using Npgsql;

namespace PDVMottainai.Api.Data;

public sealed class PostgresRequestScopeFactory(IConfiguration configuration)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Operational"));

    public async Task<PostgresRequestScope?> OpenStaffScopeAsync(string firebaseUid, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("Operational");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var role = new NpgsqlCommand("SET LOCAL ROLE mottainai_api", connection, transaction))
                    await role.ExecuteNonQueryAsync(cancellationToken);
                await using var bootstrap = new NpgsqlCommand("SELECT mottainai.fn_bootstrap_staff_context($1, 'FIREBASE_UID')", connection, transaction);
                bootstrap.Parameters.AddWithValue(firebaseUid);
                if (!Equals(await bootstrap.ExecuteScalarAsync(cancellationToken), true))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    await connection.DisposeAsync();
                    return null;
                }
                return new PostgresRequestScope(connection, transaction);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
