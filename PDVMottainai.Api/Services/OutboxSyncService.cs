using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Services;

public sealed class OutboxSyncService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<OutboxSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<LocalStore>();
                var postgres = scope.ServiceProvider.GetRequiredService<PostgresRequestScopeFactory>();
                var approvalCloud = scope.ServiceProvider.GetRequiredService<PosApprovalCloudClient>();
                if (postgres.IsConfigured)
                {
                    if (approvalCloud.IsConfigured)
                    {
                        await TryDispatchApprovalAsync(store, postgres, approvalCloud, stoppingToken);
                        await TrySyncApprovalDecisionsAsync(store, postgres, approvalCloud, stoppingToken);
                    }
                    var processed = await ProcessNextAsync(store, postgres, stoppingToken);
                    if (processed) continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NpgsqlException ex) { logger.LogWarning("POS outbox synchronization unavailable ({SqlState}).", ex.SqlState); }
            catch (SqliteException ex) { logger.LogError("POS local outbox error ({SqliteErrorCode}).", ex.SqliteErrorCode); }
            catch (Exception ex) { logger.LogError("POS outbox processing failed ({ExceptionType}).", ex.GetType().Name); }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private static async Task<bool> ProcessNextAsync(LocalStore store, PostgresRequestScopeFactory postgres, CancellationToken ct)
    {
        PendingOperation? operation;
        await using (var db = await store.OpenConnectionAsync(ct))
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT sequence_id,operation_id,idempotency_key,terminal_id,entity_type,local_entity_id,operation_type,payload_ciphertext,payload_hash,created_at FROM outbox WHERE state='PENDING' ORDER BY sequence_id LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            var serialized = store.Unprotect((byte[])reader[7]);
            operation = new PendingOperation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), JsonDocument.Parse(serialized), reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9)));
        }

        using (operation.Document)
        {
            if (operation.Type is not ("OPEN_SHIFT" or "SANGRIA" or "SUPRIMENTO" or "CREATE_DRAFT" or "ADD_SALE_ITEM" or "CHANGE_SALE_ITEM" or "CREATE_APPROVAL_REQUEST" or "ROTATE_SESSION" or "REVOKE_SESSION"))
                return false;

            var root = operation.Document.RootElement;
            var employeeId = root.GetProperty("employeeId").GetInt32();
            var uid = await FindUidAsync(store, operation.TerminalId, employeeId, operation.CreatedAt, ct);
            if (uid is null) return false;
            await using var scope = await postgres.OpenStaffScopeAsync(uid, ct);
            if (scope is null) return false;

            var existing = operation.Type is "ROTATE_SESSION" or "REVOKE_SESSION" ? new ReceiptMissing() : await FindReceiptAsync(scope, operation, ct);
            if (existing is ReceiptConflict)
            {
                await MarkConflictAsync(store, operation, "Idempotency key reused with a different payload.", ct);
                return true;
            }

            int remoteId;
            DateTimeOffset? partitionDate = null;
            if (existing is ReceiptFound receipt)
            {
                remoteId = receipt.EntityId;
                partitionDate = receipt.PartitionDate;
            }
            else if (operation.Type == "OPEN_SHIFT")
            {
                await using(var shiftLock=new NpgsqlCommand("SELECT pg_advisory_xact_lock($1,$2)",scope.Connection,scope.Transaction)){shiftLock.Parameters.AddWithValue(operation.TerminalId);shiftLock.Parameters.AddWithValue(employeeId);await shiftLock.ExecuteNonQueryAsync(ct);}
                await using(var openCheck=new NpgsqlCommand("SELECT shift_id FROM mottainai.pos_shift WHERE terminal_id=$1 AND employee_id=$2 AND status='OPEN' LIMIT 1",scope.Connection,scope.Transaction)){openCheck.Parameters.AddWithValue(operation.TerminalId);openCheck.Parameters.AddWithValue(employeeId);if(await openCheck.ExecuteScalarAsync(ct)is not null){await MarkConflictAsync(store,operation,"An open shift already exists for this terminal and employee.",ct);return true;}}
                await using var insert = new NpgsqlCommand("INSERT INTO mottainai.pos_shift(terminal_id,employee_id,opening_amount,observation) VALUES($1,$2,$3,$4) RETURNING shift_id", scope.Connection, scope.Transaction);
                insert.Parameters.AddWithValue(operation.TerminalId); insert.Parameters.AddWithValue(employeeId);
                insert.Parameters.AddWithValue(root.GetProperty("openingAmount").GetDecimal());
                var observation = root.TryGetProperty("observation", out var obs) && obs.ValueKind != JsonValueKind.Null ? obs.GetString() : null;
                insert.Parameters.AddWithValue((object?)observation ?? DBNull.Value);
                remoteId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
                await AddReceiptAsync(scope, operation, "shift", remoteId, null, ct);
            }
            else if (operation.Type is "SANGRIA" or "SUPRIMENTO")
            {
                var localShiftId = root.GetProperty("shiftId").GetInt32();
                var remoteShiftId = await FindRemoteShiftIdAsync(store, localShiftId, ct);
                if (remoteShiftId is null) return false;
                var type = operation.Type;
                var amount = root.GetProperty("amount").GetDecimal();
                var reason = root.GetProperty("reason").GetString()!;
                var observation = root.TryGetProperty("observation", out var obs) && obs.ValueKind != JsonValueKind.Null ? obs.GetString() : null;
                await using var insert = new NpgsqlCommand("INSERT INTO mottainai.pos_cash_movement(shift_id,employee_id,movement_type,amount,reason,observation,movement_date) VALUES($1,$2,$3,$4,$5,$6,$7) RETURNING cash_movement_id", scope.Connection, scope.Transaction);
                insert.Parameters.AddWithValue(remoteShiftId.Value); insert.Parameters.AddWithValue(employeeId); insert.Parameters.AddWithValue(type);
                insert.Parameters.AddWithValue(amount); insert.Parameters.AddWithValue(reason); insert.Parameters.AddWithValue((object?)observation ?? DBNull.Value);
                insert.Parameters.AddWithValue(root.GetProperty("movementDate").GetDateTimeOffset());
                remoteId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
                await AddReceiptAsync(scope, operation, "cash_movement", remoteId, null, ct);
            }
            else if (operation.Type == "ROTATE_SESSION")
            {
                await using var rotate=new NpgsqlCommand("UPDATE mottainai.staff_session SET refresh_token_hash=$1,refresh_expires_at=$2,last_used_at=CURRENT_TIMESTAMP WHERE refresh_token_hash IN ($3,$1) AND revoked_at IS NULL",scope.Connection,scope.Transaction);
                rotate.Parameters.AddWithValue(root.GetProperty("newRefreshTokenHash").GetString()!);rotate.Parameters.AddWithValue(root.GetProperty("refreshExpiresAt").GetDateTimeOffset());rotate.Parameters.AddWithValue(root.GetProperty("oldRefreshTokenHash").GetString()!);await rotate.ExecuteNonQueryAsync(ct);remoteId=0;
            }
            else if (operation.Type == "REVOKE_SESSION")
            {
                var hashes=root.GetProperty("refreshTokenHashes").EnumerateArray().Select(item=>item.GetString()!).ToArray();
                if(hashes.Length>0){await using var revoke=new NpgsqlCommand("UPDATE mottainai.staff_session SET revoked_at=CURRENT_TIMESTAMP,revoked_reason='POS_LOGOUT' WHERE refresh_token_hash=ANY($1) AND revoked_at IS NULL",scope.Connection,scope.Transaction);revoke.Parameters.AddWithValue(hashes);await revoke.ExecuteNonQueryAsync(ct);}remoteId=0;
            }
            else if (operation.Type == "CREATE_DRAFT")
            {
                var localShiftId = root.GetProperty("shiftId").GetInt32();
                var remoteShiftId = await FindRemoteShiftIdAsync(store, localShiftId, ct);
                if (remoteShiftId is null) return false;
                var date = root.GetProperty("saleDate").GetDateTimeOffset();
                var timestamp = DateTime.SpecifyKind(date.UtcDateTime, DateTimeKind.Unspecified);
                var customer = root.TryGetProperty("customerId", out var customerValue) && customerValue.ValueKind != JsonValueKind.Null ? customerValue.GetInt32() : (int?)null;
                var observation = root.TryGetProperty("observation", out var obs) && obs.ValueKind != JsonValueKind.Null ? obs.GetString() : null;
                await using var insert = new NpgsqlCommand("INSERT INTO mottainai.sales_transaction(store_id,employee_id,shift_id,customer_id,sale_date,status,observation) VALUES($1,$2,$3,$4,$5,'DRAFT',$6) RETURNING sale_id,sale_date", scope.Connection, scope.Transaction);
                insert.Parameters.AddWithValue(root.GetProperty("storeId").GetInt32()); insert.Parameters.AddWithValue(employeeId); insert.Parameters.AddWithValue(remoteShiftId.Value);
                insert.Parameters.AddWithValue((object?)customer ?? DBNull.Value); insert.Parameters.AddWithValue(timestamp); insert.Parameters.AddWithValue((object?)observation ?? DBNull.Value);
                await using var reader = await insert.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); remoteId = reader.GetInt32(0); var remoteDate = reader.GetDateTime(1); await reader.DisposeAsync();
                partitionDate = new DateTimeOffset(DateTime.SpecifyKind(remoteDate, DateTimeKind.Utc));
                await AddReceiptAsync(scope, operation, "sale", remoteId, partitionDate, ct);
            }
            else if (operation.Type == "CREATE_APPROVAL_REQUEST")
            {
                var localSaleId = root.GetProperty("saleId").GetInt32();
                var remoteSale = await FindRemoteSaleAsync(store, localSaleId, ct);
                if (remoteSale is null) return false;
                partitionDate = remoteSale.SaleDate;
                var requestId = Guid.Parse(root.GetProperty("requestId").GetString()!);
                var requestType = root.GetProperty("requestType").GetString()!;
                int? remoteItemId = null;
                if (root.TryGetProperty("saleItemId", out var itemValue) && itemValue.ValueKind != JsonValueKind.Null)
                {
                    remoteItemId = await FindRemoteSaleItemIdAsync(store, itemValue.GetInt32(), ct);
                    if (remoteItemId is null) return false;
                }

                int? cancellationRequestId = null;
                if (requestType is "CANCEL_PURCHASE" or "CANCEL_PRODUCT")
                {
                    await using var cancellation = new NpgsqlCommand(
                        "SELECT mottainai.fn_request_pos_cancellation($1,$2,$3,$4,$5)",
                        scope.Connection, scope.Transaction);
                    cancellation.Parameters.AddWithValue(remoteSale.SaleId);
                    cancellation.Parameters.AddWithValue(DateTime.SpecifyKind(remoteSale.SaleDate.UtcDateTime, DateTimeKind.Unspecified));
                    cancellation.Parameters.AddWithValue((object?)remoteItemId ?? DBNull.Value);
                    cancellation.Parameters.AddWithValue(employeeId);
                    cancellation.Parameters.AddWithValue(root.GetProperty("reason").GetString()!);
                    cancellationRequestId = Convert.ToInt32(await cancellation.ExecuteScalarAsync(ct));
                }

                await using var insert = new NpgsqlCommand("""
                    INSERT INTO mottainai.pos_manager_approval_request(
                        request_id,store_id,terminal_id,employee_id,sale_id,sale_date,sale_item_id,
                        cancellation_request_id,request_type,reason,requested_amount,discount_percent,
                        idempotency_key)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                    """, scope.Connection, scope.Transaction);
                insert.Parameters.AddWithValue(requestId);
                insert.Parameters.AddWithValue(root.GetProperty("storeId").GetInt32());
                insert.Parameters.AddWithValue(operation.TerminalId);
                insert.Parameters.AddWithValue(employeeId);
                insert.Parameters.AddWithValue(remoteSale.SaleId);
                insert.Parameters.AddWithValue(DateTime.SpecifyKind(remoteSale.SaleDate.UtcDateTime, DateTimeKind.Unspecified));
                insert.Parameters.AddWithValue((object?)remoteItemId ?? DBNull.Value);
                insert.Parameters.AddWithValue((object?)cancellationRequestId ?? DBNull.Value);
                insert.Parameters.AddWithValue(requestType);
                insert.Parameters.AddWithValue(root.GetProperty("reason").GetString()!);
                insert.Parameters.AddWithValue(root.TryGetProperty("requestedAmount", out var amount) && amount.ValueKind != JsonValueKind.Null ? amount.GetDecimal() : (object)DBNull.Value);
                insert.Parameters.AddWithValue(root.TryGetProperty("discountPercent", out var discount) && discount.ValueKind != JsonValueKind.Null ? discount.GetDecimal() : (object)DBNull.Value);
                insert.Parameters.AddWithValue(root.GetProperty("idempotencyKey").GetString()!);
                await insert.ExecuteNonQueryAsync(ct);

                await using var dispatch = new NpgsqlCommand(
                    "INSERT INTO mottainai.pos_manager_approval_dispatch(request_id,store_id) VALUES($1,$2)",
                    scope.Connection, scope.Transaction);
                dispatch.Parameters.AddWithValue(requestId);
                dispatch.Parameters.AddWithValue(root.GetProperty("storeId").GetInt32());
                await dispatch.ExecuteNonQueryAsync(ct);
                remoteId = operation.LocalEntityId;
                await AddReceiptAsync(scope, operation, "approval", remoteId, partitionDate, ct);
            }
            else
            {
                var localSaleId = root.GetProperty("saleId").GetInt32();
                var remoteSale = await FindRemoteSaleAsync(store, localSaleId, ct);
                if (remoteSale is null) return false;
                partitionDate = remoteSale.SaleDate;
                if (operation.Type == "ADD_SALE_ITEM")
                {
                    var quantity = root.GetProperty("quantity").GetDecimal(); var unitPrice = root.GetProperty("unitPrice").GetDecimal();
                    await using var insert = new NpgsqlCommand("INSERT INTO mottainai.sale_item(sale_id,product_id,quantity_sold,unit_price,status,sale_date) VALUES($1,$2,$3,$4,'DRAFT',$5) RETURNING sale_item_id", scope.Connection, scope.Transaction);
                    insert.Parameters.AddWithValue(remoteSale.SaleId); insert.Parameters.AddWithValue(root.GetProperty("productId").GetInt32()); insert.Parameters.AddWithValue(quantity); insert.Parameters.AddWithValue(unitPrice); insert.Parameters.AddWithValue(DateTime.SpecifyKind(remoteSale.SaleDate.UtcDateTime,DateTimeKind.Unspecified));
                    remoteId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
                    await RecalculateDraftTotalAsync(scope,remoteSale.SaleId,remoteSale.SaleDate,ct);
                    await AddReceiptAsync(scope,operation,"sale_item",remoteId,null,ct);
                }
                else
                {
                    var localItemId=root.GetProperty("itemId").GetInt32();var remoteItemId=await FindRemoteSaleItemIdAsync(store,localItemId,ct);if(remoteItemId is null)return false;
                    await using var update=new NpgsqlCommand("UPDATE mottainai.sale_item SET quantity_sold=$1 WHERE sale_item_id=$2 AND sale_id=$3 AND sale_date=$4 AND status='DRAFT'",scope.Connection,scope.Transaction);update.Parameters.AddWithValue(root.GetProperty("quantity").GetDecimal());update.Parameters.AddWithValue(remoteItemId.Value);update.Parameters.AddWithValue(remoteSale.SaleId);update.Parameters.AddWithValue(DateTime.SpecifyKind(remoteSale.SaleDate.UtcDateTime,DateTimeKind.Unspecified));if(await update.ExecuteNonQueryAsync(ct)==0)return false;
                    await RecalculateDraftTotalAsync(scope,remoteSale.SaleId,remoteSale.SaleDate,ct);remoteId=remoteItemId.Value;await AddReceiptAsync(scope,operation,"sale_item",remoteId,null,ct);
                }
            }

            await scope.CommitAsync(ct);
            await MarkSyncedAsync(store, operation, remoteId, partitionDate, ct);
            return true;
        }
    }

    private async Task TryDispatchApprovalAsync(LocalStore store, PostgresRequestScopeFactory postgres,
        PosApprovalCloudClient cloud, CancellationToken ct)
    {
        try
        {
            var uid = await FindActiveUidAsync(store, ct);
            if (uid is null) return;
            await using var scope = await postgres.OpenStaffScopeAsync(uid, ct);
            if (scope is null) return;
            Guid requestId;
            PosApprovalNotification notification;
            await using (var command = new NpgsqlCommand("""
                SELECT r.request_id,r.store_id,r.employee_id,r.request_type,r.sale_id,r.sale_item_id,r.reason,
                       r.requested_amount,r.discount_percent
                FROM mottainai.pos_manager_approval_dispatch d
                JOIN mottainai.pos_manager_approval_request r USING(request_id)
                WHERE d.dispatched_at IS NULL AND r.status='PENDING'
                ORDER BY d.created_at
                LIMIT 1
                FOR UPDATE OF d SKIP LOCKED
                """, scope.Connection, scope.Transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return;
                requestId = reader.GetGuid(0);
                notification = new PosApprovalNotification(
                    requestId, reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8));
            }

            string? errorCode = null;
            var sent = false;
            try
            {
                await cloud.DispatchAsync(notification, ct);
                sent = true;
            }
            catch (HttpRequestException ex)
            {
                errorCode = ex.StatusCode is { } status ? $"HTTP_{(int)status}" : "HTTP_TRANSPORT";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                errorCode = "HTTP_TIMEOUT";
            }

            await using var update = new NpgsqlCommand("""
                UPDATE mottainai.pos_manager_approval_dispatch
                SET attempts=attempts+1,last_attempt_at=CURRENT_TIMESTAMP,
                    dispatched_at=CASE WHEN $2 THEN CURRENT_TIMESTAMP ELSE dispatched_at END,
                    last_error_code=$3
                WHERE request_id=$1
                """, scope.Connection, scope.Transaction);
            update.Parameters.AddWithValue(requestId);
            update.Parameters.AddWithValue(sent);
            update.Parameters.AddWithValue((object?)errorCode ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
            await scope.CommitAsync(ct);
            if (!sent) logger.LogWarning("Manager approval notification dispatch deferred ({ErrorCode}).", errorCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Manager approval notification dispatch unavailable ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private async Task TrySyncApprovalDecisionsAsync(LocalStore store, PostgresRequestScopeFactory postgres,
        PosApprovalCloudClient cloud, CancellationToken ct)
    {
        try
        {
            var uid = await FindActiveUidAsync(store, ct);
            if (uid is null) return;
            await using var scope = await postgres.OpenStaffScopeAsync(uid, ct);
            if (scope is null) return;
            var pending = new List<PendingApproval>();
            await using (var command = new NpgsqlCommand("""
                SELECT request_id,store_id,request_type,sale_id,sale_date,sale_item_id,
                       cancellation_request_id,discount_percent
                FROM mottainai.pos_manager_approval_request
                WHERE status='PENDING'
                ORDER BY created_at
                LIMIT 50
                FOR UPDATE SKIP LOCKED
                """, scope.Connection, scope.Transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    pending.Add(new PendingApproval(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
                        reader.GetInt32(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7)));
            }
            if (pending.Count == 0) return;

            var decisions = await cloud.GetDecisionsAsync(pending[0].StoreId,
                pending.Select(item => item.RequestId).ToArray(), ct);
            var byId = decisions.ToDictionary(item => item.RequestId);
            var appliedStatuses = new List<(Guid RequestId, string Status)>();
            foreach (var request in pending)
            {
                if (!byId.TryGetValue(request.RequestId, out var decision)
                    || decision.Status is not ("APPROVED" or "REJECTED")) continue;
                var managerId = await ResolveManagerEmployeeAsync(scope, decision.DecisionUid,
                    request.StoreId, ct);
                if (managerId is null)
                {
                    logger.LogWarning("Approval decision ignored because its actor is not an active manager in the request store.");
                    continue;
                }

                var approved = decision.Status == "APPROVED";
                if (request.RequestType is "CANCEL_PURCHASE" or "CANCEL_PRODUCT")
                {
                    if (request.CancellationRequestId is null) continue;
                    await using var decide = new NpgsqlCommand(
                        "CALL mottainai.sp_decide_pos_cancellation($1,$2,$3,$4)",
                        scope.Connection, scope.Transaction);
                    decide.Parameters.AddWithValue(request.CancellationRequestId.Value);
                    decide.Parameters.AddWithValue(managerId.Value);
                    decide.Parameters.AddWithValue(approved);
                    decide.Parameters.AddWithValue((object?)decision.Observation ?? DBNull.Value);
                    await decide.ExecuteNonQueryAsync(ct);
                }
                else if (request.RequestType == "SPECIAL_DISCOUNT" && approved)
                {
                    if (request.SaleItemId is null || request.DiscountPercent is null) continue;
                    await using var discount = new NpgsqlCommand("""
                        UPDATE mottainai.sale_item
                        SET unit_price=ROUND(unit_price*(1-$1/100),2)
                        WHERE sale_item_id=$2 AND sale_id=$3 AND sale_date=$4 AND status='DRAFT'
                        """, scope.Connection, scope.Transaction);
                    discount.Parameters.AddWithValue(request.DiscountPercent.Value);
                    discount.Parameters.AddWithValue(request.SaleItemId.Value);
                    discount.Parameters.AddWithValue(request.SaleId);
                    discount.Parameters.AddWithValue(request.SaleDate);
                    if (await discount.ExecuteNonQueryAsync(ct) == 0) continue;
                    await RecalculateDraftTotalAsync(scope, request.SaleId,
                        new DateTimeOffset(DateTime.SpecifyKind(request.SaleDate, DateTimeKind.Utc)), ct);
                }

                var persistedStatus = request.RequestType is "CANCEL_PURCHASE" or "CANCEL_PRODUCT"
                    ? approved ? "EXECUTED" : "REJECTED"
                    : decision.Status;
                await using var update = new NpgsqlCommand("""
                    UPDATE mottainai.pos_manager_approval_request
                    SET status=$1,decided_by_employee_id=$2,decided_by_firebase_uid=$3,
                        decision_observation=$4,decided_at=$5,updated_at=CURRENT_TIMESTAMP
                    WHERE request_id=$6 AND status='PENDING'
                    """, scope.Connection, scope.Transaction);
                update.Parameters.AddWithValue(persistedStatus);
                update.Parameters.AddWithValue(managerId.Value);
                update.Parameters.AddWithValue(decision.DecisionUid);
                update.Parameters.AddWithValue((object?)decision.Observation ?? DBNull.Value);
                update.Parameters.AddWithValue(decision.DecidedAt);
                update.Parameters.AddWithValue(request.RequestId);
                if (await update.ExecuteNonQueryAsync(ct) == 1)
                    appliedStatuses.Add((request.RequestId, persistedStatus));
            }
            await scope.CommitAsync(ct);
            if (appliedStatuses.Count > 0)
            {
                await using var local = await store.OpenConnectionAsync(ct);
                using var localTransaction = local.BeginTransaction();
                foreach (var applied in appliedStatuses)
                {
                    await using var update = local.CreateCommand();
                    update.Transaction = localTransaction;
                    update.CommandText = "UPDATE local_approval_request SET status=$status WHERE request_id=$requestId";
                    update.Parameters.AddWithValue("$status", applied.Status);
                    update.Parameters.AddWithValue("$requestId", applied.RequestId.ToString("D"));
                    await update.ExecuteNonQueryAsync(ct);
                }
                localTransaction.Commit();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Manager approval decisions are not synchronized ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private static async Task<int?> ResolveManagerEmployeeAsync(PostgresRequestScope scope, string firebaseUid,
        int storeId, CancellationToken ct)
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

    private static async Task<string?> FindActiveUidAsync(LocalStore store, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT firebase_uid FROM offline_operator
            WHERE revoked_at IS NULL AND grant_expires_at > $now
            ORDER BY cached_at DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string?> FindUidAsync(LocalStore store, int terminalId, int employeeId, DateTimeOffset operationCreatedAt, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = "SELECT firebase_uid,grant_expires_at FROM offline_operator WHERE terminal_id=$terminal AND employee_id=$employee ORDER BY cached_at DESC";
        command.Parameters.AddWithValue("$terminal", terminalId); command.Parameters.AddWithValue("$employee", employeeId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var grantExpires=DateTimeOffset.Parse(reader.GetString(1));
            // Operations queued while the grant was valid may synchronize after logout;
            // authorization checks on new HTTP requests still reject the revoked grant.
            if(grantExpires>=operationCreatedAt)return reader.GetString(0);
        }
        return null;
    }

    private static async Task<int?> FindRemoteShiftIdAsync(LocalStore store, int localId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var command = db.CreateCommand(); command.CommandText = "SELECT remote_shift_id FROM local_shift WHERE local_shift_id=$id"; command.Parameters.AddWithValue("$id", localId);
        var value = await command.ExecuteScalarAsync(ct); return value is long id ? checked((int)id) : value is int intId ? intId : null;
    }

    private static async Task<RemoteSale?> FindRemoteSaleAsync(LocalStore store,int localId,CancellationToken ct)
    {
        await using var db=await store.OpenConnectionAsync(ct);await using var command=db.CreateCommand();command.CommandText="SELECT remote_id,remote_partition_date FROM id_mapping WHERE entity_type='sale' AND local_id=$id";command.Parameters.AddWithValue("$id",localId);await using var reader=await command.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct)||reader.IsDBNull(1))return null;return new RemoteSale(reader.GetInt32(0),DateTimeOffset.Parse(reader.GetString(1)));
    }

    private static async Task<int?> FindRemoteSaleItemIdAsync(LocalStore store,int localItemId,CancellationToken ct)
    {
        await using var db=await store.OpenConnectionAsync(ct);await using var command=db.CreateCommand();command.CommandText="SELECT remote_sale_item_id FROM local_sale_item WHERE local_sale_item_id=$id";command.Parameters.AddWithValue("$id",localItemId);var value=await command.ExecuteScalarAsync(ct);return value is long id?checked((int)id):value is int intId?intId:null;
    }

    private static async Task RecalculateDraftTotalAsync(PostgresRequestScope scope,int saleId,DateTimeOffset saleDate,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("UPDATE mottainai.sales_transaction s SET total_amount=COALESCE((SELECT SUM(i.subtotal) FROM mottainai.sale_item i WHERE i.sale_id=s.sale_id AND i.sale_date=s.sale_date),0),updated_at=CURRENT_TIMESTAMP WHERE s.sale_id=$1 AND s.sale_date=$2 AND s.status='DRAFT'",scope.Connection,scope.Transaction);command.Parameters.AddWithValue(saleId);command.Parameters.AddWithValue(DateTime.SpecifyKind(saleDate.UtcDateTime,DateTimeKind.Unspecified));await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ReceiptLookup> FindReceiptAsync(PostgresRequestScope scope, PendingOperation operation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT payload_hash,entity_id,entity_partition_date FROM mottainai.pos_api_idempotency WHERE terminal_id=$1 AND idempotency_key=$2", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue(operation.TerminalId); command.Parameters.AddWithValue(operation.Key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new ReceiptMissing();
        if (!string.Equals(reader.GetString(0), operation.Hash, StringComparison.Ordinal)) return new ReceiptConflict();
        DateTimeOffset? date=reader.IsDBNull(2)?null:new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2),DateTimeKind.Utc));
        return new ReceiptFound(reader.GetInt32(1),date);
    }

    private static async Task AddReceiptAsync(PostgresRequestScope scope, PendingOperation operation, string entityType, int entityId, DateTimeOffset? partitionDate, CancellationToken ct)
    {
        var storeId = operation.Document.RootElement.TryGetProperty("storeId", out var store) ? store.GetInt32() : await GetStoreId(scope, ct);
        await using var command = new NpgsqlCommand("INSERT INTO mottainai.pos_api_idempotency(store_id,terminal_id,idempotency_key,payload_hash,entity_type,entity_id,entity_partition_date) VALUES($1,$2,$3,$4,$5,$6,$7)", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue(storeId); command.Parameters.AddWithValue(operation.TerminalId); command.Parameters.AddWithValue(operation.Key); command.Parameters.AddWithValue(operation.Hash); command.Parameters.AddWithValue(entityType); command.Parameters.AddWithValue(entityId); command.Parameters.AddWithValue((object?)partitionDate ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetStoreId(PostgresRequestScope scope, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT NULLIF(current_setting('app.current_store_id',true),'')::INTEGER", scope.Connection, scope.Transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task MarkSyncedAsync(LocalStore store, PendingOperation operation, int remoteId, DateTimeOffset? partitionDate, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); using var tx = db.BeginTransaction();
        if(operation.Type is "ROTATE_SESSION" or "REVOKE_SESSION")
        {
            // Session mutations are idempotent updates; only the outbox receipt changes locally.
        }
        else if (operation.Type == "OPEN_SHIFT")
        {
            await using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE local_shift SET remote_shift_id=$remote,sync_state='SYNCED',updated_at=$now WHERE local_shift_id=$local; INSERT INTO id_mapping(entity_type,local_id,remote_id) VALUES('shift',$local,$remote) ON CONFLICT(entity_type,local_id) DO UPDATE SET remote_id=excluded.remote_id;";
            update.Parameters.AddWithValue("$remote", remoteId); update.Parameters.AddWithValue("$local", operation.LocalEntityId); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await update.ExecuteNonQueryAsync(ct);
        }
        else if (operation.Type is "SANGRIA" or "SUPRIMENTO")
        {
            await using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE local_cash_movement SET remote_movement_id=$remote,sync_state='SYNCED' WHERE local_movement_id=$local; INSERT INTO id_mapping(entity_type,local_id,remote_id) VALUES('cash_movement',$local,$remote) ON CONFLICT(entity_type,local_id) DO UPDATE SET remote_id=excluded.remote_id;";
            update.Parameters.AddWithValue("$remote", remoteId); update.Parameters.AddWithValue("$local", operation.LocalEntityId); await update.ExecuteNonQueryAsync(ct);
        }
        else if(operation.Type=="CREATE_DRAFT")
        {
            if(partitionDate is null)throw new InvalidOperationException("Synced draft sale receipt is missing its partition date.");
            await using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE local_sale SET remote_sale_id=$remote,sync_state='SYNCED' WHERE local_sale_id=$local; INSERT INTO id_mapping(entity_type,local_id,remote_id,remote_partition_date) VALUES('sale',$local,$remote,$date) ON CONFLICT(entity_type,local_id) DO UPDATE SET remote_id=excluded.remote_id,remote_partition_date=excluded.remote_partition_date;";update.Parameters.AddWithValue("$remote",remoteId);update.Parameters.AddWithValue("$local",operation.LocalEntityId);update.Parameters.AddWithValue("$date",partitionDate.Value.ToString("O"));await update.ExecuteNonQueryAsync(ct);
        }
        else if(operation.Type=="CREATE_APPROVAL_REQUEST")
        {
            await using var update=db.CreateCommand();update.Transaction=tx;
            update.CommandText="UPDATE local_approval_request SET status='PENDING' WHERE local_approval_request_id=$id";
            update.Parameters.AddWithValue("$id",operation.LocalEntityId);await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE local_sale_item SET remote_sale_item_id=$remote,sync_state='SYNCED' WHERE local_sale_item_id=$local; INSERT INTO id_mapping(entity_type,local_id,remote_id) VALUES('sale_item',$local,$remote) ON CONFLICT(entity_type,local_id) DO UPDATE SET remote_id=excluded.remote_id;";update.Parameters.AddWithValue("$remote",remoteId);update.Parameters.AddWithValue("$local",operation.LocalEntityId);await update.ExecuteNonQueryAsync(ct);
        }
        await using var queue = db.CreateCommand(); queue.Transaction = tx; queue.CommandText = "UPDATE outbox SET state='SYNCED',updated_at=$now WHERE sequence_id=$sequence"; queue.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); queue.Parameters.AddWithValue("$sequence", operation.Sequence); await queue.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    private static async Task MarkConflictAsync(LocalStore store, PendingOperation operation, string reason, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); using var tx=db.BeginTransaction();await using var command = db.CreateCommand();command.Transaction=tx; command.CommandText = "UPDATE outbox SET state='CONFLICT',last_error_ciphertext=$error,updated_at=$now WHERE sequence_id=$sequence"; command.Parameters.AddWithValue("$error", store.Protect(reason)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$sequence", operation.Sequence); await command.ExecuteNonQueryAsync(ct);
        if(operation.EntityType=="shift"){await using var entity=db.CreateCommand();entity.Transaction=tx;entity.CommandText="UPDATE local_shift SET sync_state='CONFLICT',updated_at=$now WHERE local_shift_id=$id";entity.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));entity.Parameters.AddWithValue("$id",operation.LocalEntityId);await entity.ExecuteNonQueryAsync(ct);}
        if(operation.EntityType=="approval"){await using var entity=db.CreateCommand();entity.Transaction=tx;entity.CommandText="UPDATE local_approval_request SET status='CONFLICT' WHERE local_approval_request_id=$id";entity.Parameters.AddWithValue("$id",operation.LocalEntityId);await entity.ExecuteNonQueryAsync(ct);}
        tx.Commit();
    }

    private sealed record PendingOperation(long Sequence, string OperationId, string Key, int TerminalId, string EntityType, int LocalEntityId, string Type, JsonDocument Document, string Hash, DateTimeOffset CreatedAt);
    private sealed record RemoteSale(int SaleId,DateTimeOffset SaleDate);
    private sealed record PendingApproval(Guid RequestId,int StoreId,string RequestType,int SaleId,
        DateTime SaleDate,int? SaleItemId,int? CancellationRequestId,decimal? DiscountPercent);
    private abstract record ReceiptLookup;
    private sealed record ReceiptMissing : ReceiptLookup;
    private sealed record ReceiptConflict : ReceiptLookup;
    private sealed record ReceiptFound(int EntityId,DateTimeOffset? PartitionDate) : ReceiptLookup;
}
