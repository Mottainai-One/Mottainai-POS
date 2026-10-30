using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController, Authorize, Route("sales")]
public sealed class SalesController(LocalStore store, OutboxRepository outbox, OfflineCacheRepository cache, PostgresRequestScopeFactory postgres) : ControllerBase
{
    public sealed record StartSaleRequest(int? CustomerId, string? Observation, string? IdempotencyKey);
    public sealed record AddItemRequest(int ProductId, decimal Quantity, string? IdempotencyKey);
    public sealed record ChangeItemRequest(decimal Quantity, string? IdempotencyKey);
    int Employee => int.Parse(User.FindFirstValue("employee_id")!);
    int StoreId => int.Parse(User.FindFirstValue("store_id")!);
    int Terminal => int.Parse(User.FindFirstValue("terminal_id")!);

    [HttpPost]
    public async Task<IActionResult> Start(StartSaleRequest request, CancellationToken ct)
    {
        if(!Guid.TryParse(request.IdempotencyKey,out var requestKey))return BadRequest(new{error="valid_idempotency_key_required"});
        await using var db = await store.OpenConnectionAsync(ct); using var tx = db.BeginTransaction();
        await using var shift = db.CreateCommand(); shift.Transaction = tx; shift.CommandText = "SELECT local_shift_id FROM local_shift WHERE employee_id=$employee AND status='OPEN' ORDER BY opened_at DESC LIMIT 1"; shift.Parameters.AddWithValue("$employee", Employee); var shiftId = await shift.ExecuteScalarAsync(ct); if (shiftId is null) return Conflict(new { error = "open_shift_required" });
        await using var seq = db.CreateCommand(); seq.Transaction = tx; seq.CommandText = "UPDATE local_sequence SET next_value=next_value-1 WHERE sequence_name='sale' RETURNING next_value+1"; var saleId = Convert.ToInt32(await seq.ExecuteScalarAsync(ct));
        var now = DateTimeOffset.UtcNow;
        await using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO local_sale(local_sale_id,sale_date,store_id,employee_id,shift_id,customer_id,status,sync_state,updated_at,observation_ciphertext) VALUES($id,$date,$store,$employee,$shift,$customer,'DRAFT','PENDING',$date,$obs)"; insert.Parameters.AddWithValue("$id", saleId); insert.Parameters.AddWithValue("$date", now.ToString("O")); insert.Parameters.AddWithValue("$store", StoreId); insert.Parameters.AddWithValue("$employee", Employee); insert.Parameters.AddWithValue("$shift", Convert.ToInt32(shiftId)); insert.Parameters.AddWithValue("$customer", (object?)request.CustomerId ?? DBNull.Value); insert.Parameters.AddWithValue("$obs", (object?)(request.Observation is null ? null : store.Protect(request.Observation)) ?? DBNull.Value); await insert.ExecuteNonQueryAsync(ct);
        await Enqueue(db,tx,saleId, "CREATE_DRAFT", new { saleId, storeId = StoreId, employeeId = Employee, shiftId = Convert.ToInt32(shiftId), customerId = request.CustomerId, observation = request.Observation, saleDate = now }, requestKey.ToString("D"), ct); tx.Commit();
        return Created($"/sales/{saleId}", new { saleId, saleDate = now, status = "DRAFT", syncState = "PENDING" });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var rows = new List<object>(); var online = false;
        if(postgres.IsConfigured)
        {
            try
            {
                await using var scope=await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null)
                {
                    await using var command=new NpgsqlCommand("SELECT sale_id,sale_date,status::TEXT,total_amount FROM mottainai.sales_transaction WHERE employee_id=$1 AND deleted_at IS NULL ORDER BY sale_date DESC LIMIT $2",scope.Connection,scope.Transaction);command.Parameters.AddWithValue(Employee);command.Parameters.AddWithValue(Math.Clamp(limit,1,100));await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(new{saleId=reader.GetInt32(0),saleDate=reader.GetDateTime(1),status=reader.GetString(2),totalAmount=reader.GetDecimal(3),syncState="SYNCED"});await reader.DisposeAsync();await scope.CommitAsync(ct);online=true;
                }
            }
            catch(PostgresException){throw;}
            catch(NpgsqlException){ }
        }
        await using var db = await store.OpenConnectionAsync(ct); await using var local = db.CreateCommand(); local.CommandText = online ? "SELECT local_sale_id,sale_date,status,sync_state FROM local_sale WHERE employee_id=$employee AND sync_state<>'SYNCED' ORDER BY sale_date DESC LIMIT $limit" : "SELECT local_sale_id,sale_date,status,sync_state FROM local_sale WHERE employee_id=$employee ORDER BY sale_date DESC LIMIT $limit"; local.Parameters.AddWithValue("$employee", Employee); local.Parameters.AddWithValue("$limit", Math.Clamp(limit,1,100)); await using var localReader = await local.ExecuteReaderAsync(ct); while (await localReader.ReadAsync(ct)) rows.Add(new { saleId = localReader.GetInt32(0), saleDate = localReader.GetString(1), status = localReader.GetString(2), syncState = localReader.GetString(3) }); return Ok(rows);
    }

    [HttpGet("{saleId:int}")]
    public async Task<IActionResult> Get(int saleId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT sale_date,store_id,employee_id,shift_id,customer_id,status,sync_state,observation_ciphertext FROM local_sale WHERE local_sale_id=$id AND employee_id=$employee"; cmd.Parameters.AddWithValue("$id", saleId); cmd.Parameters.AddWithValue("$employee", Employee); await using var reader = await cmd.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); return await GetRemoteSaleAsync(saleId,ct); }
        var sale = new { saleId, saleDate = reader.GetString(0), storeId = reader.GetInt32(1), employeeId = reader.GetInt32(2), shiftId = reader.GetInt32(3), customerId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4), status = reader.GetString(5), syncState = reader.GetString(6), observation = reader.IsDBNull(7) ? null : store.Unprotect((byte[])reader[7]) }; await reader.DisposeAsync();
        await using var items = db.CreateCommand(); items.CommandText = "SELECT local_sale_item_id,product_id,quantity,unit_price FROM local_sale_item WHERE sale_id=$id ORDER BY local_sale_item_id"; items.Parameters.AddWithValue("$id", saleId); await using var itemReader = await items.ExecuteReaderAsync(ct); var list = new List<object>(); decimal total=0; while(await itemReader.ReadAsync(ct)){var q=decimal.Parse(itemReader.GetString(2));var p=decimal.Parse(itemReader.GetString(3));total+=q*p;list.Add(new{itemId=itemReader.GetInt32(0),productId=itemReader.GetInt32(1),quantity=q,unitPrice=p,subtotal=q*p});} return Ok(new { sale, items=list, totalAmount=total });
    }

    private async Task<IActionResult> GetRemoteSaleAsync(int saleId,CancellationToken ct)
    {
        if(!postgres.IsConfigured)return NotFound();
        try
        {
            await using var scope=await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);if(scope is null)return NotFound();
            await using var saleCommand=new NpgsqlCommand("SELECT sale_id,sale_date,store_id,employee_id,shift_id,customer_id,status::TEXT,total_amount,observation FROM mottainai.sales_transaction WHERE sale_id=$1 AND employee_id=$2 AND deleted_at IS NULL ORDER BY sale_date DESC LIMIT 1",scope.Connection,scope.Transaction);saleCommand.Parameters.AddWithValue(saleId);saleCommand.Parameters.AddWithValue(Employee);await using var reader=await saleCommand.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return NotFound();var date=reader.GetDateTime(1);var sale=new{saleId=reader.GetInt32(0),saleDate=date,storeId=reader.GetInt32(2),employeeId=reader.GetInt32(3),shiftId=reader.GetInt32(4),customerId=reader.IsDBNull(5)?(int?)null:reader.GetInt32(5),status=reader.GetString(6),syncState="SYNCED",totalAmount=reader.GetDecimal(7),observation=reader.IsDBNull(8)?null:reader.GetString(8)};await reader.DisposeAsync();
            await using var itemsCommand=new NpgsqlCommand("SELECT sale_item_id,product_id,quantity_sold,unit_price,subtotal FROM mottainai.sale_item WHERE sale_id=$1 AND sale_date=$2 ORDER BY sale_item_id",scope.Connection,scope.Transaction);itemsCommand.Parameters.AddWithValue(saleId);itemsCommand.Parameters.AddWithValue(DateTime.SpecifyKind(date,DateTimeKind.Unspecified));await using var itemsReader=await itemsCommand.ExecuteReaderAsync(ct);var items=new List<object>();while(await itemsReader.ReadAsync(ct))items.Add(new{itemId=itemsReader.GetInt32(0),productId=itemsReader.GetInt32(1),quantity=itemsReader.GetDecimal(2),unitPrice=itemsReader.GetDecimal(3),subtotal=itemsReader.GetDecimal(4)});await itemsReader.DisposeAsync();await scope.CommitAsync(ct);return Ok(new{sale,items,totalAmount=sale.totalAmount});
        }
        catch(PostgresException){throw;}
        catch(NpgsqlException){return StatusCode(503,new{error="operational_database_unavailable"});}
    }

    [HttpPost("{saleId:int}/items")]
    public async Task<IActionResult> AddItem(int saleId, AddItemRequest request, CancellationToken ct)
    {
        if(!Guid.TryParse(request.IdempotencyKey,out var requestKey))return BadRequest(new{error="valid_idempotency_key_required"});
        if (request.Quantity <= 0) return BadRequest(new { error = "quantity_must_be_positive" }); var price = await GetApplicablePriceAsync(request.ProductId,request.Quantity,ct); if (price is null || !price.Value.Product.Active) return NotFound(new { error = "product_not_in_offline_catalog" });
        await using var db=await store.OpenConnectionAsync(ct); using var tx=db.BeginTransaction(); await using var sale=db.CreateCommand(); sale.Transaction=tx; sale.CommandText="SELECT 1 FROM local_sale WHERE local_sale_id=$id AND employee_id=$employee AND status='DRAFT'"; sale.Parameters.AddWithValue("$id",saleId); sale.Parameters.AddWithValue("$employee",Employee); if(await sale.ExecuteScalarAsync(ct) is null)return NotFound();
        await using var seq=db.CreateCommand();seq.Transaction=tx;seq.CommandText="UPDATE local_sequence SET next_value=next_value-1 WHERE sequence_name='sale_item' RETURNING next_value+1";var itemId=Convert.ToInt32(await seq.ExecuteScalarAsync(ct));var now=DateTimeOffset.UtcNow;
        await using var insert=db.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO local_sale_item(local_sale_item_id,sale_id,product_id,quantity,unit_price,sync_state,updated_at) VALUES($id,$sale,$product,$quantity,$price,'PENDING',$at)";insert.Parameters.AddWithValue("$id",itemId);insert.Parameters.AddWithValue("$sale",saleId);insert.Parameters.AddWithValue("$product",request.ProductId);insert.Parameters.AddWithValue("$quantity",request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$price",price.Value.AppliedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));insert.Parameters.AddWithValue("$at",now.ToString("O"));await insert.ExecuteNonQueryAsync(ct);await Enqueue(db,tx,itemId,"ADD_SALE_ITEM",new{saleId,itemId,employeeId=Employee,productId=request.ProductId,quantity=request.Quantity,unitPrice=price.Value.AppliedPrice},requestKey.ToString("D"),ct);tx.Commit();return Accepted(new{itemId,saleId,request.ProductId,request.Quantity,unitPrice=price.Value.AppliedPrice,normalPrice=price.Value.Product.NormalPrice,promotionId=price.Value.PromotionId,syncState="PENDING"});
    }

    [HttpPatch("{saleId:int}/items/{itemId:int}")]
    public async Task<IActionResult> ChangeItem(int saleId,int itemId,ChangeItemRequest request,CancellationToken ct)
    {
        if(!Guid.TryParse(request.IdempotencyKey,out var requestKey))return BadRequest(new{error="valid_idempotency_key_required"});
        if(request.Quantity<=0)return BadRequest(new{error="quantity_must_be_positive"}); await using var db=await store.OpenConnectionAsync(ct);using var tx=db.BeginTransaction();await using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE local_sale_item SET quantity=$quantity,sync_state='PENDING',updated_at=$at WHERE local_sale_item_id=$item AND sale_id=$sale AND EXISTS(SELECT 1 FROM local_sale WHERE local_sale_id=$sale AND employee_id=$employee AND status='DRAFT')";cmd.Parameters.AddWithValue("$quantity",request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$item",itemId);cmd.Parameters.AddWithValue("$sale",saleId);cmd.Parameters.AddWithValue("$employee",Employee);if(await cmd.ExecuteNonQueryAsync(ct)==0)return NotFound();await Enqueue(db,tx,itemId,"CHANGE_SALE_ITEM",new{saleId,itemId,employeeId=Employee,quantity=request.Quantity},requestKey.ToString("D"),ct);tx.Commit();return Accepted(new{saleId,itemId,request.Quantity,syncState="PENDING"});
    }

    private async Task Enqueue(Microsoft.Data.Sqlite.SqliteConnection db,Microsoft.Data.Sqlite.SqliteTransaction tx,int id,string type,object payload,string? key,CancellationToken ct){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(payload));await outbox.EnqueueInTransactionAsync(db,tx,new OfflineOperation(Guid.NewGuid(),string.IsNullOrWhiteSpace(key)?Guid.NewGuid().ToString("D"):key,"sale",id,type,doc.RootElement.Clone(),Terminal),ct);}

    private async Task<(CachedProduct Product,decimal AppliedPrice,int? PromotionId)?> GetApplicablePriceAsync(int productId,decimal quantity,CancellationToken ct)
    {
        if(postgres.IsConfigured)
        {
            try
            {
                await using var scope=await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null)
                {
                    await using var command=new NpgsqlCommand("""
                        SELECT p.product_id,p.sku,p.barcode,p.name,p.unit_measure,price.regular_price,p.active,p.version,promo.promotional_price,promo.promotion_id
                        FROM mottainai.product p
                        JOIN LATERAL(SELECT regular_price FROM mottainai.store_product_price WHERE product_id=p.product_id AND store_id=$1 AND active AND valid_from<=CURRENT_TIMESTAMP AND (valid_until IS NULL OR valid_until>CURRENT_TIMESTAMP) ORDER BY valid_from DESC LIMIT 1) price ON TRUE
                        LEFT JOIN LATERAL(SELECT pi.promotional_price,pr.promotion_id FROM mottainai.promotion_item pi JOIN mottainai.promotion pr ON pr.promotion_id=pi.promotion_id WHERE pi.product_id=p.product_id AND pr.store_id=$1 AND pr.active AND pr.status='APPROVED' AND pr.deleted_at IS NULL AND pr.starts_at<=CURRENT_TIMESTAMP::timestamp AND pr.ends_at>CURRENT_TIMESTAMP::timestamp AND (pi.quantity_available IS NULL OR pi.quantity_available >= $3) ORDER BY pi.promotional_price LIMIT 1) promo ON TRUE
                        WHERE p.product_id=$2 AND p.active AND p.deleted_at IS NULL
                        """,scope.Connection,scope.Transaction);command.Parameters.AddWithValue(StoreId);command.Parameters.AddWithValue(productId);command.Parameters.AddWithValue(quantity);await using var reader=await command.ExecuteReaderAsync(ct);
                    if(await reader.ReadAsync(ct)){var now=DateTimeOffset.UtcNow;var product=new CachedProduct(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDecimal(5),reader.GetBoolean(6),reader.GetInt32(7),now);var promo=reader.IsDBNull(8)?(decimal?)null:reader.GetDecimal(8);var promotionId=reader.IsDBNull(9)?(int?)null:reader.GetInt32(9);await reader.DisposeAsync();await scope.CommitAsync(ct);await cache.ReplaceProductsAsync([product],ct);return(product,promo??product.NormalPrice,promotionId);}await reader.DisposeAsync();await scope.CommitAsync(ct);return null;
                }
            }
            catch(PostgresException){throw;}
            catch(NpgsqlException){ }
        }
        var cached=await cache.GetProductAsync(productId,ct);return cached is null?null:(cached,cached.NormalPrice,(int?)null);
    }
}
