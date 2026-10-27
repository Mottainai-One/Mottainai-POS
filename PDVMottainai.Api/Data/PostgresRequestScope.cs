using Npgsql;

namespace PDVMottainai.Api.Data;

public sealed class PostgresRequestScope(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;
    private bool _committed;

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try { await Transaction.RollbackAsync(); } catch (NpgsqlException) { }
        }
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
