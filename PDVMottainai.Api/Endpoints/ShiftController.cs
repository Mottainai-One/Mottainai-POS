using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController, Authorize, Route("shifts")]
public sealed class ShiftController(LocalStore store, OutboxRepository outbox, PostgresRequestScopeFactory postgres) : ControllerBase
{
    public sealed record OpenShiftRequest(decimal OpeningAmount, string? Observation, string? IdempotencyKey);
    public sealed record CloseShiftRequest(decimal ClosingAmount, string? Observation);
    public sealed record CashMovementRequest(decimal Amount, string Reason, string? Observation, string? IdempotencyKey);
    int Employee => int.Parse(User.FindFirstValue("employee_id")!);
    int Terminal => int.Parse(User.FindFirstValue("terminal_id")!);

    [HttpPost]
    public async Task<IActionResult> Open(OpenShiftRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.IdempotencyKey, out var requestKey)) return BadRequest(new { error = "valid_idempotency_key_required" });
        if (request.OpeningAmount < 0) return BadRequest(new { error = "opening_amount_must_be_nonnegative" });
        var operationPayload=new{terminalId=Terminal,employeeId=Employee,openingAmount=request.OpeningAmount,observation=request.Observation};
        var operationHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(operationPayload)))).ToLowerInvariant();
        if (postgres.IsConfigured)
        {
            try
            {
                await using var scope = await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if (scope is not null)
                {
                    await using(var shiftLock=new NpgsqlCommand("SELECT pg_advisory_xact_lock($1,$2)",scope.Connection,scope.Transaction)){shiftLock.Parameters.AddWithValue(Terminal);shiftLock.Parameters.AddWithValue(Employee);await shiftLock.ExecuteNonQueryAsync(ct);}
                    await using var receipt=new NpgsqlCommand("SELECT payload_hash,entity_id FROM mottainai.pos_api_idempotency WHERE terminal_id=$1 AND idempotency_key=$2",scope.Connection,scope.Transaction);receipt.Parameters.AddWithValue(Terminal);receipt.Parameters.AddWithValue(requestKey.ToString("D"));await using var receiptReader=await receipt.ExecuteReaderAsync(ct);
                    if(await receiptReader.ReadAsync(ct)){if(!string.Equals(receiptReader.GetString(0),operationHash,StringComparison.Ordinal)){await receiptReader.DisposeAsync();return Conflict(new{error="idempotency_key_conflict"});}var replayId=receiptReader.GetInt32(1);await receiptReader.DisposeAsync();await scope.CommitAsync(ct);return Ok(new{shiftId=replayId,idempotentReplay=true});}await receiptReader.DisposeAsync();
                    await using var exists=new NpgsqlCommand("SELECT shift_id FROM mottainai.pos_shift WHERE terminal_id=$1 AND employee_id=$2 AND status='OPEN' LIMIT 1",scope.Connection,scope.Transaction); exists.Parameters.AddWithValue(Terminal); exists.Parameters.AddWithValue(Employee); var existing=await exists.ExecuteScalarAsync(ct); if(existing is not null)return Conflict(new{error="shift_already_open",shiftId=Convert.ToInt32(existing)});
                    await using var insert=new NpgsqlCommand("INSERT INTO mottainai.pos_shift(terminal_id,employee_id,opening_amount,observation) VALUES($1,$2,$3,$4) RETURNING shift_id,opened_at",scope.Connection,scope.Transaction); insert.Parameters.AddWithValue(Terminal);insert.Parameters.AddWithValue(Employee);insert.Parameters.AddWithValue(request.OpeningAmount);insert.Parameters.AddWithValue((object?)request.Observation??DBNull.Value);await using var reader=await insert.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);var remoteId=reader.GetInt32(0);var opened=reader.GetDateTime(1);await reader.DisposeAsync();
                    await using(var addReceipt=new NpgsqlCommand("INSERT INTO mottainai.pos_api_idempotency(store_id,terminal_id,idempotency_key,payload_hash,entity_type,entity_id) VALUES($1,$2,$3,$4,'shift',$5)",scope.Connection,scope.Transaction)){addReceipt.Parameters.AddWithValue(int.Parse(User.FindFirstValue("store_id")!));addReceipt.Parameters.AddWithValue(Terminal);addReceipt.Parameters.AddWithValue(requestKey.ToString("D"));addReceipt.Parameters.AddWithValue(operationHash);addReceipt.Parameters.AddWithValue(remoteId);await addReceipt.ExecuteNonQueryAsync(ct);}await scope.CommitAsync(ct);
                    await SaveOnlineShift(remoteId,request.OpeningAmount,request.Observation,opened,ct);
                    return Created($"/shifts/{remoteId}",new{shiftId=remoteId,openedAt=opened,status="OPEN",syncState="SYNCED"});
                }
            }
            catch(PostgresException) { throw; }
            catch(NpgsqlException) { }
        }
        await using var db = await store.OpenConnectionAsync(ct);
        using var tx = db.BeginTransaction();
        await using (var check = db.CreateCommand())
        {
            check.Transaction = tx; check.CommandText = "SELECT local_shift_id FROM local_shift WHERE terminal_id=$terminal AND employee_id=$employee AND status='OPEN' LIMIT 1";
            check.Parameters.AddWithValue("$terminal", Terminal); check.Parameters.AddWithValue("$employee", Employee);
            if (await check.ExecuteScalarAsync(ct) is not null) return Conflict(new { error = "shift_already_open" });
        }
        var id = await NextId(db, tx, "shift", ct);
        var now = DateTimeOffset.UtcNow;
        await using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO local_shift(local_shift_id,terminal_id,employee_id,opened_at,opening_amount,status,observation_ciphertext,sync_state,updated_at) VALUES($id,$terminal,$employee,$at,$amount,'OPEN',$obs,'PENDING',$at)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$terminal", Terminal); cmd.Parameters.AddWithValue("$employee", Employee); cmd.Parameters.AddWithValue("$at", now.ToString("O")); cmd.Parameters.AddWithValue("$amount", request.OpeningAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$obs", (object?) (request.Observation is null ? null : store.Protect(request.Observation)) ?? DBNull.Value); await cmd.ExecuteNonQueryAsync(ct);
        }
        await Enqueue(db, tx, "shift", id, "OPEN_SHIFT", operationPayload, requestKey.ToString("D"), ct);
        tx.Commit();
        return Created($"/shifts/{id}", new { shiftId = id, openedAt = now, status = "OPEN", syncState = "PENDING" });
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT local_shift_id,opened_at,opening_amount,status,sync_state FROM local_shift WHERE terminal_id=$terminal AND employee_id=$employee AND status='OPEN' ORDER BY opened_at DESC LIMIT 1"; cmd.Parameters.AddWithValue("$terminal", Terminal); cmd.Parameters.AddWithValue("$employee", Employee);
        await using var reader = await cmd.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return NoContent();
        return Ok(new { shiftId = reader.GetInt32(0), openedAt = reader.GetString(1), openingAmount = decimal.Parse(reader.GetString(2)), status = reader.GetString(3), syncState = reader.GetString(4) });
    }

    [HttpGet("{shiftId:int}")]
    public async Task<IActionResult> Get(int shiftId, CancellationToken ct) => await ReadShift(shiftId, ct);

    [HttpGet("{shiftId:int}/summary")]
    public async Task<IActionResult> Summary(int shiftId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct);
        await using var shiftCommand = db.CreateCommand(); shiftCommand.CommandText = "SELECT opening_amount,sync_state,remote_shift_id FROM local_shift WHERE local_shift_id=$id AND employee_id=$employee"; shiftCommand.Parameters.AddWithValue("$id", shiftId); shiftCommand.Parameters.AddWithValue("$employee", Employee);
        await using var shiftReader = await shiftCommand.ExecuteReaderAsync(ct); if (!await shiftReader.ReadAsync(ct)) return NotFound();
        var opening = decimal.Parse(shiftReader.GetString(0)); var syncState = shiftReader.GetString(1); var remoteShiftId=shiftReader.IsDBNull(2)?(int?)null:shiftReader.GetInt32(2); await shiftReader.DisposeAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT movement_type,amount FROM local_cash_movement WHERE shift_id=$id"; cmd.Parameters.AddWithValue("$id", shiftId);
        await using var reader = await cmd.ExecuteReaderAsync(ct); decimal deposits = 0, withdrawals = 0;
        while (await reader.ReadAsync(ct)) { var amount = decimal.Parse(reader.GetString(1)); if (reader.GetString(0) == "SUPRIMENTO") deposits += amount; else withdrawals += amount; }
        decimal cashPayments=0;var cashPaymentsComplete=false;
        if(remoteShiftId is not null&&postgres.IsConfigured)
        {
            try
            {
                await using var scope=await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null){await using var payments=new NpgsqlCommand("SELECT COALESCE(SUM(p.amount),0) FROM mottainai.sale_payment p JOIN mottainai.sales_transaction s ON s.sale_id=p.sale_id AND s.sale_date=p.sale_date WHERE s.shift_id=$1 AND s.status='COMPLETED' AND p.payment_method='CASH'",scope.Connection,scope.Transaction);payments.Parameters.AddWithValue(remoteShiftId.Value);cashPayments=Convert.ToDecimal(await payments.ExecuteScalarAsync(ct));await scope.CommitAsync(ct);cashPaymentsComplete=true;}
            }
            catch(PostgresException){throw;}
            catch(NpgsqlException){ }
        }
        var expected = opening + deposits - withdrawals+cashPayments;
        return Ok(new { shiftId, openingAmount = opening, cashDeposits = deposits, cashWithdrawals = withdrawals,cashPayments,cashPaymentsComplete, expectedAmount = expected, syncState });
    }

    [HttpPost("{shiftId:int}/closing")]
    public async Task<IActionResult> Close(int shiftId,CloseShiftRequest request,CancellationToken ct)
    {
        if(request.ClosingAmount<0)return BadRequest(new{error="closing_amount_must_be_nonnegative"});
        if(!postgres.IsConfigured)return StatusCode(503,new{error="online_database_required",message="Fechamento exige conexão operacional; turno permanece aberto."});
        var uid=User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        try
        {
            await using var scope=await postgres.OpenStaffScopeAsync(uid,ct);if(scope is null)return Unauthorized();
            var local=await GetExpectedAmount(shiftId,ct);if(local is null)return NotFound();
            await using(var pending=await store.OpenConnectionAsync(ct))await using(var pendingCheck=pending.CreateCommand())
            {pendingCheck.CommandText="SELECT COUNT(*) FROM local_cash_movement WHERE shift_id=$id AND sync_state<>'SYNCED'";pendingCheck.Parameters.AddWithValue("$id",shiftId);if(Convert.ToInt32(await pendingCheck.ExecuteScalarAsync(ct))>0)return StatusCode(503,new{error="pending_cash_movements",message="Sincronize movimentos pendentes antes de fechar o caixa."});}
            await using var payments=new NpgsqlCommand("SELECT COALESCE(SUM(p.amount),0) FROM mottainai.sale_payment p JOIN mottainai.sales_transaction s ON s.sale_id=p.sale_id AND s.sale_date=p.sale_date WHERE s.shift_id=$1 AND s.status='COMPLETED' AND p.payment_method='CASH'",scope.Connection,scope.Transaction);payments.Parameters.AddWithValue(shiftId);var cashPayments=Convert.ToDecimal(await payments.ExecuteScalarAsync(ct));var expected=local.Value+cashPayments;
            await using var update=new NpgsqlCommand("UPDATE mottainai.pos_shift SET closed_at=CURRENT_TIMESTAMP,closing_amount=$1,expected_amount=$2,status='CLOSED',observation=COALESCE($3,observation),updated_at=CURRENT_TIMESTAMP WHERE shift_id=$4 AND employee_id=$5 AND status='OPEN' RETURNING closed_at",scope.Connection,scope.Transaction);update.Parameters.AddWithValue(request.ClosingAmount);update.Parameters.AddWithValue(expected);update.Parameters.AddWithValue((object?)request.Observation??DBNull.Value);update.Parameters.AddWithValue(shiftId);update.Parameters.AddWithValue(Employee);var closed=await update.ExecuteScalarAsync(ct);if(closed is null)return Conflict(new{error="shift_not_open_or_not_owned"});await scope.CommitAsync(ct);
            await using var db=await store.OpenConnectionAsync(ct);await using var localUpdate=db.CreateCommand();localUpdate.CommandText="UPDATE local_shift SET closed_at=$closed,closing_amount=$counted,expected_amount=$expected,status='CLOSED',sync_state='SYNCED',updated_at=$closed WHERE local_shift_id=$id";localUpdate.Parameters.AddWithValue("$closed",Convert.ToDateTime(closed).ToUniversalTime().ToString("O"));localUpdate.Parameters.AddWithValue("$counted",request.ClosingAmount.ToString(System.Globalization.CultureInfo.InvariantCulture));localUpdate.Parameters.AddWithValue("$expected",expected.ToString(System.Globalization.CultureInfo.InvariantCulture));localUpdate.Parameters.AddWithValue("$id",shiftId);await localUpdate.ExecuteNonQueryAsync(ct);return Ok(new{shiftId,status="CLOSED",closingAmount=request.ClosingAmount,expectedAmount=expected,cashPayments});
        }
        catch(NpgsqlException){return StatusCode(503,new{error="online_database_required",message="Fechamento exige conexão operacional; turno permanece aberto."});}
    }

    [HttpGet("{shiftId:int}/cash-movements")]
    public async Task<IActionResult> Movements(int shiftId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT local_movement_id,movement_type,amount,movement_date,sync_state,reason_ciphertext FROM local_cash_movement WHERE shift_id=$id ORDER BY local_movement_id"; cmd.Parameters.AddWithValue("$id", shiftId);
        await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<object>(); while (await reader.ReadAsync(ct)) rows.Add(new { movementId = reader.GetInt32(0), type = reader.GetString(1), amount = decimal.Parse(reader.GetString(2)), date = reader.GetString(3), syncState = reader.GetString(4), reason = store.Unprotect((byte[])reader[5]) }); return Ok(rows);
    }

    [HttpPost("{shiftId:int}/cash-withdrawals")]
    public Task<IActionResult> Withdraw(int shiftId, CashMovementRequest request, CancellationToken ct) => AddMovement(shiftId, request, "SANGRIA", ct);
    [HttpPost("{shiftId:int}/cash-deposits")]
    public Task<IActionResult> Deposit(int shiftId, CashMovementRequest request, CancellationToken ct) => AddMovement(shiftId, request, "SUPRIMENTO", ct);

    private async Task<IActionResult> AddMovement(int shiftId, CashMovementRequest request, string type, CancellationToken ct)
    {
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 200) return BadRequest(new { error = "positive_amount_and_reason_required" });
        if (!Guid.TryParse(request.IdempotencyKey,out var requestKey)) return BadRequest(new { error = "valid_idempotency_key_required" });
        await using var db = await store.OpenConnectionAsync(ct); using var tx = db.BeginTransaction();
        await using var check = db.CreateCommand(); check.Transaction = tx; check.CommandText = "SELECT 1 FROM local_shift WHERE local_shift_id=$id AND employee_id=$employee AND status='OPEN'"; check.Parameters.AddWithValue("$id", shiftId); check.Parameters.AddWithValue("$employee", Employee); if (await check.ExecuteScalarAsync(ct) is null) return NotFound();
        var at = DateTimeOffset.UtcNow; await using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO local_cash_movement(shift_id,employee_id,movement_type,amount,reason_ciphertext,observation_ciphertext,movement_date,sync_state) VALUES($shift,$employee,$type,$amount,$reason,$obs,$at,'PENDING'); SELECT last_insert_rowid();"; insert.Parameters.AddWithValue("$shift", shiftId); insert.Parameters.AddWithValue("$employee", Employee); insert.Parameters.AddWithValue("$type", type); insert.Parameters.AddWithValue("$amount", request.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$reason", store.Protect(request.Reason)); insert.Parameters.AddWithValue("$obs", (object?)(request.Observation is null ? null : store.Protect(request.Observation)) ?? DBNull.Value); insert.Parameters.AddWithValue("$at", at.ToString("O")); var movementId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
        await Enqueue(db,tx,"cash_movement", movementId, type, new { shiftId, employeeId = Employee, amount = request.Amount, reason = request.Reason, observation = request.Observation, movementDate = at }, requestKey.ToString("D"), ct); tx.Commit();
        return Accepted(new { movementId, shiftId, type, request.Amount, movementDate = at, syncState = "PENDING" });
    }

    private async Task<IActionResult> ReadShift(int id, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT local_shift_id,terminal_id,employee_id,opened_at,opening_amount,closed_at,closing_amount,expected_amount,status,sync_state,observation_ciphertext FROM local_shift WHERE local_shift_id=$id AND employee_id=$employee"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$employee", Employee); await using var reader = await cmd.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return NotFound();
        return Ok(new { shiftId = reader.GetInt32(0), terminalId = reader.GetInt32(1), employeeId = reader.GetInt32(2), openedAt = reader.GetString(3), openingAmount = decimal.Parse(reader.GetString(4)), closedAt = reader.IsDBNull(5) ? null : reader.GetString(5), closingAmount = reader.IsDBNull(6) ? (decimal?)null : decimal.Parse(reader.GetString(6)), expectedAmount = reader.IsDBNull(7) ? (decimal?)null : decimal.Parse(reader.GetString(7)), status = reader.GetString(8), syncState = reader.GetString(9), observation = reader.IsDBNull(10) ? null : store.Unprotect((byte[])reader[10]) });
    }

    private async Task<decimal?> GetExpectedAmount(int shiftId,CancellationToken ct)
    {
        await using var db=await store.OpenConnectionAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT opening_amount FROM local_shift WHERE local_shift_id=$id AND employee_id=$employee AND status='OPEN'";cmd.Parameters.AddWithValue("$id",shiftId);cmd.Parameters.AddWithValue("$employee",Employee);var raw=await cmd.ExecuteScalarAsync(ct);if(raw is not string openingText)return null;var expected=decimal.Parse(openingText);await using var movements=db.CreateCommand();movements.CommandText="SELECT movement_type,amount FROM local_cash_movement WHERE shift_id=$id";movements.Parameters.AddWithValue("$id",shiftId);await using var reader=await movements.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){var amount=decimal.Parse(reader.GetString(1));expected+=reader.GetString(0)=="SUPRIMENTO"?amount:-amount;}return expected;
    }

    private async Task SaveOnlineShift(int shiftId,decimal opening,string? observation,DateTime openedAt,CancellationToken ct)
    {
        await using var db=await store.OpenConnectionAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO local_shift(local_shift_id,remote_shift_id,terminal_id,employee_id,opened_at,opening_amount,status,observation_ciphertext,sync_state,updated_at) VALUES($id,$id,$terminal,$employee,$at,$amount,'OPEN',$observation,'SYNCED',$at) ON CONFLICT(local_shift_id) DO UPDATE SET remote_shift_id=excluded.remote_shift_id,status='OPEN',sync_state='SYNCED',updated_at=excluded.updated_at";cmd.Parameters.AddWithValue("$id",shiftId);cmd.Parameters.AddWithValue("$terminal",Terminal);cmd.Parameters.AddWithValue("$employee",Employee);cmd.Parameters.AddWithValue("$at",new DateTimeOffset(DateTime.SpecifyKind(openedAt,DateTimeKind.Utc)).ToString("O"));cmd.Parameters.AddWithValue("$amount",opening.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$observation",(object?)(observation is null?null:store.Protect(observation))??DBNull.Value);await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task Enqueue(Microsoft.Data.Sqlite.SqliteConnection db,Microsoft.Data.Sqlite.SqliteTransaction tx,string entity, int id, string operation, object payload, string key, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload)); await outbox.EnqueueInTransactionAsync(db,tx,new OfflineOperation(Guid.NewGuid(), key, entity, id, operation, doc.RootElement.Clone(), Terminal), ct);
    }

    private static async Task<int> NextId(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx, string name, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE local_sequence SET next_value=next_value-1 WHERE sequence_name=$name RETURNING next_value+1"; cmd.Parameters.AddWithValue("$name", name); return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
