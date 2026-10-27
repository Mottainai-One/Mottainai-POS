using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Security;

public sealed class PosBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var hash = LocalStore.Sha256(header[7..].Trim());
        await using var connection = await store.OpenConnectionAsync(Context.RequestAborted);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.firebase_uid, o.employee_id, o.store_id, o.terminal_id, s.expires_at,
                   o.profile_ciphertext, o.permissions_ciphertext, o.grant_expires_at
            FROM local_access_session s JOIN offline_operator o USING(firebase_uid)
            WHERE s.access_token_hash = $hash AND s.revoked_at IS NULL AND o.revoked_at IS NULL
              AND datetime(o.grant_expires_at) > datetime('now');
            """;
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(Context.RequestAborted);
        if (!await reader.ReadAsync(Context.RequestAborted) || DateTimeOffset.Parse(reader.GetString(4)) <= DateTimeOffset.UtcNow)
            return AuthenticateResult.Fail("Sessão local inválida ou expirada.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, reader.GetString(0)),
            new("employee_id", reader.GetInt32(1).ToString()),
            new("store_id", reader.GetInt32(2).ToString()),
            new("terminal_id", reader.GetInt32(3).ToString()),
            new("profile", store.Unprotect((byte[])reader[5])),
            new("permissions", store.Unprotect((byte[])reader[6])),
            new("offline_grant_expires_at", reader.GetString(7))
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
