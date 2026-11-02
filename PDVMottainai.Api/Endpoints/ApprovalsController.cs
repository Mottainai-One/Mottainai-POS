using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController, Route("approvals")]
public sealed class ApprovalsController(
    LocalStore store,
    OutboxRepository outbox,
    PostgresRequestScopeFactory postgres,
    ILogger<ApprovalsController> logger) : ControllerBase
{
    private static readonly HashSet<string> RequestTypes =
    ["CANCEL_PURCHASE", "CANCEL_PRODUCT", "REFUND", "RETURN", "SPECIAL_DISCOUNT"];

    public sealed record CreateApprovalRequest(
        int SaleId,
        int? SaleItemId,
        string RequestType,
        string Reason,
        decimal? RequestedAmount,
        decimal? DiscountPercent,
        string? IdempotencyKey);

    public sealed record ManagerDecisionRequest(bool? Approved, string? Observation);

    private int Employee => int.Parse(User.FindFirstValue("employee_id")!);
    private int StoreId => int.Parse(User.FindFirstValue("store_id")!);
    private int Terminal => int.Parse(User.FindFirstValue("terminal_id")!);

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create(CreateApprovalRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.IdempotencyKey, out var idempotencyKey))
            return BadRequest(new { error = "valid_idempotency_key_required" });
        var type = request.RequestType?.Trim().ToUpperInvariant();
        if (type is null || !RequestTypes.Contains(type))
            return BadRequest(new { error = "unsupported_approval_type" });
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 250)
            return BadRequest(new { error = "reason_required_max_250" });
        if (type == "CANCEL_PURCHASE" && request.SaleItemId is not null)
            return BadRequest(new { error = "purchase_request_cannot_target_item" });
        if (type is "CANCEL_PRODUCT" or "SPECIAL_DISCOUNT" && request.SaleItemId is null)
            return BadRequest(new { error = "item_required_for_request_type" });
        if (type is "REFUND" or "RETURN" && request.RequestedAmount is not (> 0))
            return BadRequest(new { error = "positive_amount_required" });
        if (type == "SPECIAL_DISCOUNT" && request.DiscountPercent is not (> 0 and <= 100))
            return BadRequest(new { error = "discount_percent_must_be_between_0_and_100" });
        if (type != "SPECIAL_DISCOUNT" && request.DiscountPercent is not null)
            return BadRequest(new { error = "discount_percent_only_valid_for_special_discount" });

        await using var db = await store.OpenConnectionAsync(ct);
        using var tx = db.BeginTransaction();
        await using var sale = db.CreateCommand();
        sale.Transaction = tx;
        sale.CommandText = "SELECT sale_date,status FROM local_sale WHERE local_sale_id=$sale AND employee_id=$employee AND store_id=$store";
        sale.Parameters.AddWithValue("$sale", request.SaleId);
        sale.Parameters.AddWithValue("$employee", Employee);
        sale.Parameters.AddWithValue("$store", StoreId);
        await using var reader = await sale.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return NotFound(new { error = "sale_not_found" });
        var saleDate = reader.GetString(0);
        var saleStatus = reader.GetString(1);
        await reader.DisposeAsync();

        if (type == "SPECIAL_DISCOUNT" && saleStatus != "DRAFT")
            return Conflict(new { error = "special_discount_requires_draft_sale" });
        if (request.SaleItemId is { } itemId)
        {
            await using var item = db.CreateCommand();
            item.Transaction = tx;
            item.CommandText = "SELECT 1 FROM local_sale_item WHERE local_sale_item_id=$item AND sale_id=$sale";
            item.Parameters.AddWithValue("$item", itemId);
            item.Parameters.AddWithValue("$sale", request.SaleId);
            if (await item.ExecuteScalarAsync(ct) is null)
                return BadRequest(new { error = "sale_item_mismatch" });
        }

        var requestId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO local_approval_request(request_id,sale_id,sale_item_id,request_type,reason_ciphertext,
                requested_amount,discount_percent,status,created_at)
            VALUES($id,$sale,$item,$type,$reason,$amount,$discount,'PENDING_SYNC',$created)
            RETURNING local_approval_request_id;
            """;
        insert.Parameters.AddWithValue("$id", requestId.ToString("D"));
        insert.Parameters.AddWithValue("$sale", request.SaleId);
        insert.Parameters.AddWithValue("$item", (object?)request.SaleItemId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$reason", store.Protect(request.Reason.Trim()));
        insert.Parameters.AddWithValue("$amount", request.RequestedAmount is null ? DBNull.Value : request.RequestedAmount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$discount", request.DiscountPercent is null ? DBNull.Value : request.DiscountPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        var localId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));

        var payload = new
        {
            requestId,
            saleId = request.SaleId,
            saleDate,
            saleItemId = request.SaleItemId,
            requestType = type,
            reason = request.Reason.Trim(),
            requestedAmount = request.RequestedAmount,
            discountPercent = request.DiscountPercent,
            employeeId = Employee,
            storeId = StoreId,
            terminalId = Terminal,
            idempotencyKey = idempotencyKey.ToString("D"),
            createdAt
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await outbox.EnqueueInTransactionAsync(db, tx, new OfflineOperation(
            Guid.NewGuid(), idempotencyKey.ToString("D"), "approval", localId,
            "CREATE_APPROVAL_REQUEST", document.RootElement.Clone(), Terminal), ct);
        tx.Commit();
        return Accepted(new { requestId, request.SaleId, request.SaleItemId, requestType = type, status = "PENDING_SYNC", createdAt });
    }

    [Authorize]
    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.sale_id,r.sale_item_id,r.request_type,r.reason_ciphertext,r.requested_amount,
                   r.discount_percent,r.status,r.created_at
            FROM local_approval_request r
            JOIN local_sale s ON s.local_sale_id=r.sale_id
            WHERE r.request_id=$id AND s.employee_id=$employee AND s.store_id=$store;
            """;
        command.Parameters.AddWithValue("$id", requestId.ToString("D"));
        command.Parameters.AddWithValue("$employee", Employee);
        command.Parameters.AddWithValue("$store", StoreId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return NotFound();
        return Ok(new
        {
            requestId,
            saleId = reader.GetInt32(0),
            saleItemId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
            requestType = reader.GetString(2),
            reason = store.Unprotect((byte[])reader[3]),
            requestedAmount = reader.IsDBNull(4) ? (decimal?)null : decimal.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            discountPercent = reader.IsDBNull(5) ? (decimal?)null : decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            status = reader.GetString(6),
            createdAt = DateTimeOffset.Parse(reader.GetString(7))
        });
    }

    [Authorize(AuthenticationSchemes = "Firebase")]
    [HttpPost("{requestId:guid}/manager-decision")]
    public async Task<IActionResult> DecideAsManager(
        Guid requestId,
        ManagerDecisionRequest request,
        CancellationToken ct)
    {
        var managerUid = User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(managerUid))
            return Unauthorized(new { error = "firebase_user_required" });
        if (request.Approved is null)
            return BadRequest(new { error = "approved_decision_required" });
        if (request.Observation?.Trim().Length > 250)
            return BadRequest(new { error = "observation_max_250_characters" });
        if (!postgres.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "operational_database_unavailable" });

        PostgresRequestScope? scope;
        try
        {
            scope = await postgres.OpenStaffScopeAsync(managerUid, ct);
        }
        catch (NpgsqlException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "operational_database_unavailable" });
        }

        if (scope is null)
            return Forbid();

        string requestType;
        int storeId;
        int saleId;
        DateTime saleDate;
        int? saleItemId;
        int? cancellationRequestId;
        decimal? discountPercent;
        await using (scope)
        {
            await using (var approval = new NpgsqlCommand("""
                SELECT store_id,request_type,sale_id,sale_date,sale_item_id,
                       cancellation_request_id,discount_percent,status
                FROM mottainai.pos_manager_approval_request
                WHERE request_id=$1
                FOR UPDATE
                """, scope.Connection, scope.Transaction))
            {
                approval.Parameters.AddWithValue(requestId);
                await using var reader = await approval.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return NotFound(new { error = "approval_request_not_found" });

                storeId = reader.GetInt32(0);
                requestType = reader.GetString(1);
                saleId = reader.GetInt32(2);
                saleDate = reader.GetDateTime(3);
                saleItemId = reader.IsDBNull(4) ? null : reader.GetInt32(4);
                cancellationRequestId = reader.IsDBNull(5) ? null : reader.GetInt32(5);
                discountPercent = reader.IsDBNull(6) ? null : reader.GetDecimal(6);
                if (reader.GetString(7) != "PENDING")
                    return Conflict(new { error = "approval_request_already_decided" });
            }

            var managerEmployeeId = await FindActiveManagerEmployeeIdAsync(
                scope, managerUid, storeId, ct);
            if (managerEmployeeId is null)
                return Forbid();

            var observation = request.Observation?.Trim();
            var approved = request.Approved.Value;
            if (requestType is "CANCEL_PURCHASE" or "CANCEL_PRODUCT")
            {
                if (cancellationRequestId is null)
                    return Conflict(new { error = "cancellation_request_not_ready" });

                await using var cancellation = new NpgsqlCommand(
                    "CALL mottainai.sp_decide_pos_cancellation($1,$2,$3,$4)",
                    scope.Connection, scope.Transaction);
                cancellation.Parameters.AddWithValue(cancellationRequestId.Value);
                cancellation.Parameters.AddWithValue(managerEmployeeId.Value);
                cancellation.Parameters.AddWithValue(approved);
                cancellation.Parameters.AddWithValue((object?)observation ?? DBNull.Value);
                await cancellation.ExecuteNonQueryAsync(ct);
            }
            else if (requestType == "SPECIAL_DISCOUNT" && approved)
            {
                if (saleItemId is null || discountPercent is null)
                    return Conflict(new { error = "discount_request_not_ready" });

                await using var discount = new NpgsqlCommand("""
                    UPDATE mottainai.sale_item
                    SET unit_price=ROUND(unit_price*(1-$1/100),2)
                    WHERE sale_item_id=$2 AND sale_id=$3 AND sale_date=$4 AND status='DRAFT'
                    """, scope.Connection, scope.Transaction);
                discount.Parameters.AddWithValue(discountPercent.Value);
                discount.Parameters.AddWithValue(saleItemId.Value);
                discount.Parameters.AddWithValue(saleId);
                discount.Parameters.AddWithValue(saleDate);
                if (await discount.ExecuteNonQueryAsync(ct) == 0)
                    return Conflict(new { error = "sale_item_is_not_a_draft" });

                await RecalculateDraftTotalAsync(scope, saleId, saleDate, ct);
            }

            var status = requestType is "CANCEL_PURCHASE" or "CANCEL_PRODUCT"
                ? approved ? "EXECUTED" : "REJECTED"
                : approved ? "APPROVED" : "REJECTED";
            var decidedAt = DateTimeOffset.UtcNow;
            await using var update = new NpgsqlCommand("""
                UPDATE mottainai.pos_manager_approval_request
                SET status=$1,decided_by_employee_id=$2,decided_by_firebase_uid=$3,
                    decision_observation=$4,decided_at=$5,updated_at=CURRENT_TIMESTAMP
                WHERE request_id=$6 AND status='PENDING'
                """, scope.Connection, scope.Transaction);
            update.Parameters.AddWithValue(status);
            update.Parameters.AddWithValue(managerEmployeeId.Value);
            update.Parameters.AddWithValue(managerUid);
            update.Parameters.AddWithValue((object?)observation ?? DBNull.Value);
            update.Parameters.AddWithValue(decidedAt);
            update.Parameters.AddWithValue(requestId);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                return Conflict(new { error = "approval_request_already_decided" });

            await scope.CommitAsync(ct);

            try
            {
                await using var local = await store.OpenConnectionAsync(ct);
                await using var updateLocal = local.CreateCommand();
                updateLocal.CommandText = "UPDATE local_approval_request SET status=$status WHERE request_id=$requestId";
                updateLocal.Parameters.AddWithValue("$status", status);
                updateLocal.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
                await updateLocal.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex)
            {
                logger.LogWarning("Manager decision was saved remotely but the local approval cache was not updated ({SqliteErrorCode}).", ex.SqliteErrorCode);
            }

            return Ok(new
            {
                requestId,
                status,
                approvedByEmployeeId = managerEmployeeId.Value,
                decidedAt,
                paymentProcessed = false
            });
        }
    }

    private static async Task<int?> FindActiveManagerEmployeeIdAsync(
        PostgresRequestScope scope,
        string firebaseUid,
        int storeId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT e.employee_id
            FROM mottainai.app_user u
            JOIN mottainai.employee e ON e.employee_id=u.employee_id
            JOIN mottainai.employee_role r ON r.role_id=e.role_id
            WHERE u.firebase_uid=$1 AND e.store_id=$2
              AND u.active AND e.active AND r.active
              AND u.deleted_at IS NULL AND e.deleted_at IS NULL AND r.deleted_at IS NULL
              AND UPPER(r.name) IN ('GERENTE','DONO','MANAGER','OWNER')
            LIMIT 1
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue(firebaseUid);
        command.Parameters.AddWithValue(storeId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async Task RecalculateDraftTotalAsync(
        PostgresRequestScope scope,
        int saleId,
        DateTime saleDate,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE mottainai.sales_transaction s
            SET total_amount=COALESCE((
                    SELECT SUM(i.subtotal)
                    FROM mottainai.sale_item i
                    WHERE i.sale_id=s.sale_id AND i.sale_date=s.sale_date),0),
                updated_at=CURRENT_TIMESTAMP
            WHERE s.sale_id=$1 AND s.sale_date=$2 AND s.status='DRAFT'
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue(saleId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(saleDate, DateTimeKind.Unspecified));
        await command.ExecuteNonQueryAsync(ct);
    }
}
