using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController, Authorize, Route("approvals")]
public sealed class ApprovalsController(LocalStore store, OutboxRepository outbox) : ControllerBase
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

    private int Employee => int.Parse(User.FindFirstValue("employee_id")!);
    private int StoreId => int.Parse(User.FindFirstValue("store_id")!);
    private int Terminal => int.Parse(User.FindFirstValue("terminal_id")!);

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
}
