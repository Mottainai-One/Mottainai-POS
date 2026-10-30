using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace PDVMottainai.Api.Data.Local;

public sealed class LocalStore
{
    private readonly string _connectionString;
    private readonly string _schemaPath;
    private readonly IDataProtector _protector;

    public LocalStore(IConfiguration configuration, IDataProtectionProvider protectionProvider)
    {
        var configuredPath = configuration["LocalStore:Path"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mottainai", "PDV", "pos-local.db")
            : Environment.ExpandEnvironmentVariables(configuredPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        _schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "Local", "001_local_state.sql");
        _protector = protectionProvider.CreateProtector("Mottainai.PDV.LocalStore.v1");
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_schemaPath))
        {
            throw new FileNotFoundException("Local POS schema was not copied to the application output.", _schemaPath);
        }

        var schema = await File.ReadAllTextAsync(_schemaPath, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public byte[] Protect(string value) => _protector.Protect(Encoding.UTF8.GetBytes(value));

    public string Unprotect(byte[] value) => Encoding.UTF8.GetString(_protector.Unprotect(value));

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
