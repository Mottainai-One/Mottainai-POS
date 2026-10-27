using Microsoft.Data.Sqlite;

namespace PDVMottainai.Api.Data.Local;

public sealed record CachedTerminal(int TerminalId, int StoreId, string TerminalCode, string Name, bool Active, DateTimeOffset CachedAt);

public sealed record CachedProduct(
    int ProductId,
    string Sku,
    string? Barcode,
    string Name,
    string UnitMeasure,
    decimal NormalPrice,
    bool Active,
    int Version,
    DateTimeOffset CachedAt);

public sealed record CachedCustomer(int CustomerId, string DisplayName);

public sealed class OfflineCacheRepository(LocalStore store)
{
    public async Task ReplaceTerminalsAsync(IEnumerable<CachedTerminal> terminals, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM terminal_cache;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var terminal in terminals)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO terminal_cache(terminal_id, store_id, terminal_code, name, active, cached_at)
                VALUES ($terminal_id, $store_id, $terminal_code, $name, $active, $cached_at);
                """;
            insert.Parameters.AddWithValue("$terminal_id", terminal.TerminalId);
            insert.Parameters.AddWithValue("$store_id", terminal.StoreId);
            insert.Parameters.AddWithValue("$terminal_code", terminal.TerminalCode);
            insert.Parameters.AddWithValue("$name", terminal.Name);
            insert.Parameters.AddWithValue("$active", terminal.Active ? 1 : 0);
            insert.Parameters.AddWithValue("$cached_at", terminal.CachedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<CachedTerminal>> GetTerminalsAsync(int storeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT terminal_id, store_id, terminal_code, name, active, cached_at
            FROM terminal_cache WHERE store_id = $store_id AND active = 1
            ORDER BY terminal_id;
            """;
        command.Parameters.AddWithValue("$store_id", storeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CachedTerminal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CachedTerminal(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4) == 1, DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }

    public async Task ReplaceProductsAsync(IEnumerable<CachedProduct> products, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var product in products)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO product_cache(
                    product_id, sku, barcode, name, unit_measure, normal_price, active, version, cached_at)
                VALUES ($product_id, $sku, $barcode, $name, $unit_measure, $normal_price, $active, $version, $cached_at)
                ON CONFLICT(product_id) DO UPDATE SET
                    sku = excluded.sku,
                    barcode = excluded.barcode,
                    name = excluded.name,
                    unit_measure = excluded.unit_measure,
                    normal_price = excluded.normal_price,
                    active = excluded.active,
                    version = excluded.version,
                    cached_at = excluded.cached_at;
                """;
            command.Parameters.AddWithValue("$product_id", product.ProductId);
            command.Parameters.AddWithValue("$sku", product.Sku);
            command.Parameters.AddWithValue("$barcode", (object?)product.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", product.Name);
            command.Parameters.AddWithValue("$unit_measure", product.UnitMeasure);
            command.Parameters.AddWithValue("$normal_price", product.NormalPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$active", product.Active ? 1 : 0);
            command.Parameters.AddWithValue("$version", product.Version);
            command.Parameters.AddWithValue("$cached_at", product.CachedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<CachedProduct>> SearchProductsAsync(string? query, int limit, CancellationToken cancellationToken = default)
    {
        var normalized = query?.Trim();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrEmpty(normalized)
            ? """
              SELECT product_id, sku, barcode, name, unit_measure, normal_price, active, version, cached_at
              FROM product_cache WHERE active = 1 ORDER BY name LIMIT $limit;
              """
            : """
              SELECT product_id, sku, barcode, name, unit_measure, normal_price, active, version, cached_at
              FROM product_cache
              WHERE active = 1 AND (sku LIKE $query OR barcode = $exact OR name LIKE $query)
              ORDER BY CASE WHEN barcode = $exact THEN 0 WHEN sku = $exact THEN 1 ELSE 2 END, name
              LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        if (!string.IsNullOrEmpty(normalized))
        {
            command.Parameters.AddWithValue("$query", $"%{EscapeLike(normalized)}%");
            command.Parameters.AddWithValue("$exact", normalized);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CachedProduct>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadProduct(reader));
        }
        return result;
    }

    public async Task<CachedProduct?> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT product_id, sku, barcode, name, unit_measure, normal_price, active, version, cached_at
            FROM product_cache WHERE product_id = $product_id;
            """;
        command.Parameters.AddWithValue("$product_id", productId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    public async Task CacheCustomerAsync(string authTokenHash, int customerId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_lookup_cache(auth_token_hash, customer_id, display_ciphertext, cached_at)
            VALUES ($auth_hash, $customer_id, $display, $cached_at)
            ON CONFLICT(auth_token_hash) DO UPDATE SET
                customer_id = excluded.customer_id,
                display_ciphertext = excluded.display_ciphertext,
                cached_at = excluded.cached_at;
            """;
        command.Parameters.AddWithValue("$auth_hash", authTokenHash);
        command.Parameters.AddWithValue("$customer_id", customerId);
        command.Parameters.AddWithValue("$display", store.Protect(displayName));
        command.Parameters.AddWithValue("$cached_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CachedCustomer?> GetCustomerAsync(string authTokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT customer_id, display_ciphertext FROM customer_lookup_cache WHERE auth_token_hash = $auth_hash;
            """;
        command.Parameters.AddWithValue("$auth_hash", authTokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new CachedCustomer(reader.GetInt32(0), store.Unprotect((byte[])reader[1]));
    }

    public async Task<DateTimeOffset?> GetLatestProductCacheTimeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(cached_at) FROM product_cache;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string timestamp ? DateTimeOffset.Parse(timestamp) : null;
    }

    private static CachedProduct ReadProduct(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3), reader.GetString(4),
        decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetInt32(6) == 1, reader.GetInt32(7), DateTimeOffset.Parse(reader.GetString(8)));

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
