using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController, Authorize]
public sealed class ReferenceDataController(OfflineCacheRepository cache, PostgresRequestScopeFactory postgres, IConfiguration configuration) : ControllerBase
{
    private sealed record ProductResponse(int ProductId,string Sku,string? Barcode,string Name,string UnitMeasure,decimal NormalPrice,decimal? PromotionalPrice,int? PromotionId,bool Active,int Version,DateTimeOffset CachedAt);
    [HttpGet("/terminals")]
    public async Task<IActionResult> Terminals(CancellationToken ct)
    {
        var storeId = int.Parse(User.FindFirstValue("store_id")!);
        if (postgres.IsConfigured)
        {
            try
            {
                await using var scope = await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
                if (scope is not null)
                {
                    await using var command = new NpgsqlCommand("SELECT terminal_id,store_id,terminal_code,name,active FROM mottainai.pos_terminal WHERE store_id=$1 AND active ORDER BY terminal_id", scope.Connection, scope.Transaction);
                    command.Parameters.AddWithValue(storeId); await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<CachedTerminal>(); var now = DateTimeOffset.UtcNow;
                    while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetInt32(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetBoolean(4),now)); await reader.DisposeAsync(); await scope.CommitAsync(ct); await cache.ReplaceTerminalsAsync(rows,ct); return Ok(rows);
                }
            }
            catch (PostgresException) { throw; }
            catch (NpgsqlException) { }
        }
        return Ok(await cache.GetTerminalsAsync(storeId, ct));
    }

    [HttpGet("/capabilities")]
    public IActionResult Capabilities() => Ok(new { onlineConfigured = postgres.IsConfigured && !string.IsNullOrWhiteSpace(configuration["Firebase:ProjectId"]), offline = true, offlinePromotions = false, routes = new { login = "online", offlineSessionPreparation = "online", shiftClosing = "online" } });

    [HttpGet("/products")]
    public async Task<IActionResult> Products([FromQuery] string? query, [FromQuery] int limit = 50, [FromQuery] int? customerId = null, CancellationToken ct = default)
    {
        var storeId = int.Parse(User.FindFirstValue("store_id")!);
        var eligibleForPromotions = customerId is > 0 && await cache.IsVerifiedCustomerAsync(customerId.Value, TimeSpan.FromMinutes(15), ct);
        if (postgres.IsConfigured)
        {
            try
            {
                await using var scope = await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null)
                {
                    await using var command = new NpgsqlCommand("""
                        SELECT p.product_id,p.sku,p.barcode,p.name,p.unit_measure,price.regular_price,p.active,p.version,promo.promotional_price,promo.promotion_id
                        FROM mottainai.product p
                        JOIN LATERAL (SELECT regular_price FROM mottainai.store_product_price sp
                          WHERE sp.product_id=p.product_id AND sp.store_id=$1 AND sp.active
                            AND sp.valid_from<=CURRENT_TIMESTAMP AND (sp.valid_until IS NULL OR sp.valid_until>CURRENT_TIMESTAMP)
                          ORDER BY sp.valid_from DESC LIMIT 1) price ON TRUE
                        LEFT JOIN LATERAL (SELECT pi.promotional_price,pr.promotion_id FROM mottainai.promotion_item pi JOIN mottainai.promotion pr ON pr.promotion_id=pi.promotion_id
                          WHERE pi.product_id=p.product_id AND pr.store_id=$1 AND pr.active AND pr.status='APPROVED' AND pr.deleted_at IS NULL
                            AND pr.starts_at<=CURRENT_TIMESTAMP::timestamp AND pr.ends_at>CURRENT_TIMESTAMP::timestamp
                            AND (pi.quantity_available IS NULL OR pi.quantity_available>0)
                          ORDER BY pi.promotional_price LIMIT 1) promo ON TRUE
                        WHERE p.active AND p.deleted_at IS NULL
                          AND ($2::text IS NULL OR p.sku ILIKE '%'||$2||'%' OR p.barcode=$2 OR p.name ILIKE '%'||$2||'%')
                        ORDER BY CASE WHEN p.barcode=$2 THEN 0 WHEN p.sku=$2 THEN 1 ELSE 2 END,p.name
                        LIMIT $3
                        """,scope.Connection,scope.Transaction);
                    command.Parameters.AddWithValue(storeId); command.Parameters.AddWithValue((object?)query??DBNull.Value); command.Parameters.AddWithValue(Math.Clamp(limit,1,100));
                    await using var reader=await command.ExecuteReaderAsync(ct); var rows=new List<CachedProduct>();var results=new List<ProductResponse>(); var now=DateTimeOffset.UtcNow;
                    while(await reader.ReadAsync(ct)){var product=new CachedProduct(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDecimal(5),reader.GetBoolean(6),reader.GetInt32(7),now);rows.Add(product);results.Add(ToResponse(product,eligibleForPromotions&&!reader.IsDBNull(8)?reader.GetDecimal(8):null,eligibleForPromotions&&!reader.IsDBNull(9)?reader.GetInt32(9):null));} await reader.DisposeAsync(); await scope.CommitAsync(ct); await cache.ReplaceProductsAsync(rows,ct); return Ok(results);
                }
            }
            catch(PostgresException) { throw; }
            catch(NpgsqlException) { }
        }
        return Ok((await cache.SearchProductsAsync(query,limit,ct)).Select(product=>ToResponse(product,null,null)));
    }

    [HttpGet("/products/{productId:int}")]
    public async Task<IActionResult> Product(int productId, [FromQuery] int? customerId, CancellationToken ct)
    {
        var eligibleForPromotions = customerId is > 0 && await cache.IsVerifiedCustomerAsync(customerId.Value, TimeSpan.FromMinutes(15), ct);
        if (postgres.IsConfigured)
        {
            try
            {
                var storeId = int.Parse(User.FindFirstValue("store_id")!);
                await using var scope = await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null)
                {
                    await using var command=new NpgsqlCommand("""
                        SELECT p.product_id,p.sku,p.barcode,p.name,p.unit_measure,price.regular_price,p.active,p.version,promo.promotional_price,promo.promotion_id
                        FROM mottainai.product p JOIN LATERAL(
                          SELECT regular_price FROM mottainai.store_product_price sp WHERE sp.product_id=p.product_id AND sp.store_id=$1 AND sp.active AND sp.valid_from<=CURRENT_TIMESTAMP AND (sp.valid_until IS NULL OR sp.valid_until>CURRENT_TIMESTAMP) ORDER BY sp.valid_from DESC LIMIT 1
                        ) price ON TRUE LEFT JOIN LATERAL (SELECT pi.promotional_price,pr.promotion_id FROM mottainai.promotion_item pi JOIN mottainai.promotion pr ON pr.promotion_id=pi.promotion_id WHERE pi.product_id=p.product_id AND pr.store_id=$1 AND pr.active AND pr.status='APPROVED' AND pr.deleted_at IS NULL AND pr.starts_at<=CURRENT_TIMESTAMP::timestamp AND pr.ends_at>CURRENT_TIMESTAMP::timestamp ORDER BY pi.promotional_price LIMIT 1) promo ON TRUE WHERE p.product_id=$2 AND p.active AND p.deleted_at IS NULL
                        """,scope.Connection,scope.Transaction);command.Parameters.AddWithValue(storeId);command.Parameters.AddWithValue(productId);await using var reader=await command.ExecuteReaderAsync(ct);
                    if(await reader.ReadAsync(ct)){var now=DateTimeOffset.UtcNow;var item=new CachedProduct(reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDecimal(5),reader.GetBoolean(6),reader.GetInt32(7),now);var result=ToResponse(item,eligibleForPromotions&&!reader.IsDBNull(8)?reader.GetDecimal(8):null,eligibleForPromotions&&!reader.IsDBNull(9)?reader.GetInt32(9):null);await reader.DisposeAsync();await scope.CommitAsync(ct);await cache.ReplaceProductsAsync([item],ct);return Ok(result);} await reader.DisposeAsync();await scope.CommitAsync(ct);
                }
            }
            catch(PostgresException) { throw; }
            catch(NpgsqlException) { }
        }
        var product = await cache.GetProductAsync(productId, ct);
        return product is null ? NotFound() : Ok(ToResponse(product,null,null));
    }

    public sealed record CustomerSearchRequest(string Cpf);
    [HttpPost("/customers/search")]
    public async Task<IActionResult> CustomerSearch(CustomerSearchRequest request, CancellationToken ct)
    {
        var cpfDigits = System.Text.RegularExpressions.Regex.Replace(request.Cpf ?? string.Empty, "\\D", string.Empty);
        if (!IsValidCpf(cpfDigits))
            return BadRequest(new { error = "valid_cpf_required" });

        var cpfToken = "CPF_SHA256_" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cpfDigits)));
        var tokenHash = LocalStore.Sha256(cpfToken);
        var onlineLookupUnavailable = false;
        if (postgres.IsConfigured)
        {
            try
            {
                await using var scope=await postgres.OpenStaffScopeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!,ct);
                if(scope is not null)
                {
                    await using var command=new NpgsqlCommand("SELECT mottainai.fn_pos_find_customer_by_cpf_token($1)",scope.Connection,scope.Transaction);command.Parameters.AddWithValue(cpfToken);var customerId=await command.ExecuteScalarAsync(ct);
                    await scope.CommitAsync(ct);
                    if(customerId is not null){var result=new CachedCustomer(Convert.ToInt32(customerId),$"Cliente Mottainai #{Convert.ToInt32(customerId)}",DateTimeOffset.UtcNow);await cache.CacheCustomerAsync(tokenHash,result.CustomerId,result.DisplayName,ct);return Ok(result);}
                    return NotFound();
                }
                onlineLookupUnavailable = true;
            }
            catch(PostgresException){throw;}
            catch(NpgsqlException){ onlineLookupUnavailable = true; }
        }
        var customer = await cache.GetCustomerAsync(tokenHash, ct);
        if (customer is not null)
            return Ok(customer);
        return onlineLookupUnavailable
            ? StatusCode(503, new { error = "customer_lookup_unavailable" })
            : NotFound();
    }

    private static bool IsValidCpf(string digits)
    {
        if (digits.Length != 11 || digits.All(digit => digit == digits[0]))
            return false;

        var firstSum = 0;
        for (var index = 0; index < 9; index++)
            firstSum += (digits[index] - '0') * (10 - index);
        var firstDigit = firstSum % 11 < 2 ? 0 : 11 - firstSum % 11;
        if (digits[9] - '0' != firstDigit)
            return false;

        var secondSum = 0;
        for (var index = 0; index < 10; index++)
            secondSum += (digits[index] - '0') * (11 - index);
        var secondDigit = secondSum % 11 < 2 ? 0 : 11 - secondSum % 11;
        return digits[10] - '0' == secondDigit;
    }

    private static ProductResponse ToResponse(CachedProduct product,decimal? promotionalPrice,int? promotionId)=>new(product.ProductId,product.Sku,product.Barcode,product.Name,product.UnitMeasure,product.NormalPrice,promotionalPrice,promotionId,product.Active,product.Version,product.CachedAt);
}
