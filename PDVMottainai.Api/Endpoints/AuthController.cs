using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Npgsql;
using PDVMottainai.Api.Data;
using PDVMottainai.Api.Data.Local;

namespace PDVMottainai.Api.Endpoints;

[ApiController]
[Route("auth")]
public sealed class AuthController(LocalStore store, PostgresRequestScopeFactory postgres, OutboxRepository outbox) : ControllerBase
{
    public sealed record OfflineLoginRequest(string FirebaseUid, string OfflineGrant, int TerminalId);
    public sealed record RefreshRequest(string RefreshToken);
    public sealed record OfflineSessionRequest(string FirebaseUid, string TerminalCode, int ValidHours = 12);
    public sealed record LocalSessionResponse(string accessToken, string refreshToken, string tokenType, DateTimeOffset expiresAt);

    [HttpPost("login"), AllowAnonymous]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        var firebase = await HttpContext.AuthenticateAsync("Firebase");
        if (!firebase.Succeeded || firebase.Principal?.FindFirst("sub")?.Value is not { Length: > 0 } uid)
            return Unauthorized(new { error = "valid_firebase_bearer_required" });
        PostgresRequestScope? scope;
        try { scope = await postgres.OpenStaffScopeAsync(uid, ct); }
        catch (NpgsqlException) { return StatusCode(503, new { error = "operational_database_unavailable" }); }
        if (scope is null) return Unauthorized(new { error = "operator_not_authorized" });
        await using (scope)
        {
            var terminalId = HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<int?>("Api:TerminalId");
            if (terminalId is null) return StatusCode(503, new { error = "terminal_not_configured" });
            await using var profileCommand = new NpgsqlCommand("""
                SELECT u.user_id, e.employee_id, e.store_id, e.name, r.name, r.permission_level
                FROM mottainai.app_user u
                JOIN mottainai.employee e ON e.employee_id=u.employee_id
                JOIN mottainai.employee_role r ON r.role_id=e.role_id
                JOIN mottainai.pos_terminal t ON t.terminal_id=$2 AND t.store_id=e.store_id AND t.active
                WHERE u.firebase_uid=$1 AND u.active AND e.active AND r.active
                  AND u.deleted_at IS NULL AND e.deleted_at IS NULL AND r.deleted_at IS NULL
                """, scope.Connection, scope.Transaction);
            profileCommand.Parameters.AddWithValue(uid); profileCommand.Parameters.AddWithValue(terminalId.Value);
            await using var reader = await profileCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Unauthorized(new { error = "operator_not_authorized" });
            var userId = reader.GetInt32(0); var employeeId = reader.GetInt32(1); var storeId = reader.GetInt32(2);
            var profileJson = JsonSerializer.Serialize(new { id = employeeId, name = reader.GetString(3), role = reader.GetString(4) });
            var permissionJson = JsonSerializer.Serialize(new { level = reader.GetInt32(5), role = reader.GetString(4) });
            await reader.DisposeAsync();

            var grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40));
            var grantExpires = DateTimeOffset.UtcNow.AddHours(12);
            var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            var sessionId = Guid.NewGuid();
            await using (var addSession = new NpgsqlCommand("INSERT INTO mottainai.staff_session(session_id,user_id,refresh_token_hash,refresh_expires_at) VALUES($1,$2,$3,$4)", scope.Connection, scope.Transaction))
            {
                addSession.Parameters.AddWithValue(sessionId); addSession.Parameters.AddWithValue(userId);
                addSession.Parameters.AddWithValue(LocalStore.Sha256(refreshToken)); addSession.Parameters.AddWithValue(grantExpires.AddDays(7));
                await addSession.ExecuteNonQueryAsync(ct);
            }
            await scope.CommitAsync(ct);
            await StoreOfflineOperatorAsync(uid, employeeId, storeId, terminalId.Value, profileJson, permissionJson, grant, grantExpires, sessionId.ToString("D"), ct);
            var localSession = await CreateLocalSessionWithRefreshAsync(uid, refreshToken, ct);
            return Ok(new { localSession.accessToken, localSession.refreshToken, tokenType = "Bearer", expiresAt = localSession.expiresAt, offlineGrant = grant, offlineGrantExpiresAt = grantExpires, operatorProfile = JsonSerializer.Deserialize<JsonElement>(profileJson), permissions = JsonSerializer.Deserialize<JsonElement>(permissionJson) });
        }
    }

    [HttpPost("offline-login"), AllowAnonymous]
    public async Task<IActionResult> OfflineLogin(OfflineLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FirebaseUid) || string.IsNullOrWhiteSpace(request.OfflineGrant)) return BadRequest();
        await using var connection = await store.OpenConnectionAsync(ct);
        var configuredTerminal = HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<int?>("Api:TerminalId");
        if (configuredTerminal is null || configuredTerminal.Value != request.TerminalId) return Unauthorized(new { error = "terminal_binding_mismatch" });
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT offline_grant_ciphertext, grant_expires_at, terminal_id FROM offline_operator WHERE firebase_uid=$uid AND revoked_at IS NULL";
        command.Parameters.AddWithValue("$uid", request.FirebaseUid);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || DateTimeOffset.Parse(reader.GetString(1)) <= DateTimeOffset.UtcNow || reader.GetInt32(2) != request.TerminalId)
            return Unauthorized(new { error = "offline_grant_invalid_or_expired" });
        string grant;
        try { grant = store.Unprotect((byte[])reader[0]); } catch (CryptographicException) { return Unauthorized(); }
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(grant), System.Text.Encoding.UTF8.GetBytes(request.OfflineGrant))) return Unauthorized();
        await reader.DisposeAsync();
        return Ok(await CreateLocalSessionAsync(connection, request.FirebaseUid, ct));
    }

    [HttpPost("refresh"), AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.RefreshToken))return Unauthorized();
        var oldHash=LocalStore.Sha256(request.RefreshToken);var now=DateTimeOffset.UtcNow;var refreshExpires=now.AddDays(7);
        var access=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));var refresh=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await using var connection=await store.OpenConnectionAsync(ct);using var tx=connection.BeginTransaction();
        await using var lookup=connection.CreateCommand();lookup.Transaction=tx;lookup.CommandText="SELECT s.firebase_uid,o.employee_id,o.terminal_id FROM local_access_session s JOIN offline_operator o USING(firebase_uid) WHERE s.refresh_token_hash=$hash AND s.revoked_at IS NULL AND s.refresh_expires_at>$now AND o.revoked_at IS NULL AND o.grant_expires_at>$now";lookup.Parameters.AddWithValue("$hash",oldHash);lookup.Parameters.AddWithValue("$now",now.ToString("O"));await using var reader=await lookup.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return Unauthorized();var uid=reader.GetString(0);var employeeId=reader.GetInt32(1);var terminalId=reader.GetInt32(2);await reader.DisposeAsync();
        await using(var revoke=connection.CreateCommand()){revoke.Transaction=tx;revoke.CommandText="UPDATE local_access_session SET revoked_at=$now WHERE refresh_token_hash=$hash";revoke.Parameters.AddWithValue("$now",now.ToString("O"));revoke.Parameters.AddWithValue("$hash",oldHash);await revoke.ExecuteNonQueryAsync(ct);}
        await using(var insert=connection.CreateCommand()){insert.Transaction=tx;insert.CommandText="INSERT INTO local_access_session(access_token_hash,refresh_token_hash,firebase_uid,expires_at,refresh_expires_at,created_at) VALUES($a,$r,$uid,$exp,$refresh_exp,$now)";insert.Parameters.AddWithValue("$a",LocalStore.Sha256(access));insert.Parameters.AddWithValue("$r",LocalStore.Sha256(refresh));insert.Parameters.AddWithValue("$uid",uid);insert.Parameters.AddWithValue("$exp",now.AddHours(12).ToString("O"));insert.Parameters.AddWithValue("$refresh_exp",refreshExpires.ToString("O"));insert.Parameters.AddWithValue("$now",now.ToString("O"));await insert.ExecuteNonQueryAsync(ct);}
        await EnqueueSessionOperationAsync(connection,tx,terminalId,employeeId,"ROTATE_SESSION",new{employeeId,oldRefreshTokenHash=oldHash,newRefreshTokenHash=LocalStore.Sha256(refresh),refreshExpiresAt=refreshExpires},ct);tx.Commit();
        return Ok(new LocalSessionResponse(access,refresh,"Bearer",now.AddHours(12)));
    }

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        await using var connection=await store.OpenConnectionAsync(ct);using var tx=connection.BeginTransaction();var now=DateTimeOffset.UtcNow;
        await using var lookup=connection.CreateCommand();lookup.Transaction=tx;lookup.CommandText="SELECT s.firebase_uid,o.employee_id,o.terminal_id FROM local_access_session s JOIN offline_operator o USING(firebase_uid) WHERE s.access_token_hash=$hash";lookup.Parameters.AddWithValue("$hash",LocalStore.Sha256(token));await using var reader=await lookup.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return NoContent();var uid=reader.GetString(0);var employeeId=reader.GetInt32(1);var terminalId=reader.GetInt32(2);await reader.DisposeAsync();
        var sessionHashes=new List<string>();await using(var hashes=connection.CreateCommand()){hashes.Transaction=tx;hashes.CommandText="SELECT refresh_token_hash FROM local_access_session WHERE firebase_uid=$uid";hashes.Parameters.AddWithValue("$uid",uid);await using var hashReader=await hashes.ExecuteReaderAsync(ct);while(await hashReader.ReadAsync(ct))sessionHashes.Add(hashReader.GetString(0));}
        await using(var command=connection.CreateCommand()){command.Transaction=tx;command.CommandText="UPDATE local_access_session SET revoked_at=$now WHERE firebase_uid=$uid";command.Parameters.AddWithValue("$now",now.ToString("O"));command.Parameters.AddWithValue("$uid",uid);await command.ExecuteNonQueryAsync(ct);}
        await using(var revokeGrant=connection.CreateCommand()){revokeGrant.Transaction=tx;revokeGrant.CommandText="UPDATE offline_operator SET revoked_at=$now WHERE firebase_uid=$uid";revokeGrant.Parameters.AddWithValue("$now",now.ToString("O"));revokeGrant.Parameters.AddWithValue("$uid",uid);await revokeGrant.ExecuteNonQueryAsync(ct);}
        await EnqueueSessionOperationAsync(connection,tx,terminalId,employeeId,"REVOKE_SESSION",new{employeeId,refreshTokenHashes=sessionHashes},ct);tx.Commit();
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new
    {
        firebaseUid = User.FindFirstValue(ClaimTypes.NameIdentifier),
        employeeId = User.FindFirstValue("employee_id"),
        storeId = User.FindFirstValue("store_id"),
        terminalId = User.FindFirstValue("terminal_id"),
        profile = User.FindFirstValue("profile"),
        permissions = User.FindFirstValue("permissions"),
        offlineGrantExpiresAt = User.FindFirstValue("offline_grant_expires_at")
    });

    [HttpPost("/offline-sessions"), Authorize]
    public async Task<IActionResult> PrepareOfflineSession(OfflineSessionRequest request, CancellationToken ct)
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (request.FirebaseUid != uid || request.ValidHours is < 1 or > 24) return BadRequest(new { error = "invalid_offline_session_request" });
        PostgresRequestScope? scope;
        try { scope = await postgres.OpenStaffScopeAsync(uid, ct); }
        catch (NpgsqlException) { return StatusCode(503, new { error = "operational_database_unavailable" }); }
        if (scope is null) return Unauthorized();
        await using (scope)
        {
            await using var command = new NpgsqlCommand("""
                SELECT e.employee_id,e.store_id,e.name,r.name,r.permission_level,t.terminal_id
                FROM mottainai.employee e
                JOIN mottainai.app_user u ON u.employee_id=e.employee_id
                JOIN mottainai.employee_role r ON r.role_id=e.role_id
                JOIN mottainai.pos_terminal t ON t.terminal_code=$1 AND t.store_id=e.store_id AND t.active
                WHERE u.firebase_uid=$2 AND e.active AND r.active
                """, scope.Connection, scope.Transaction);
            command.Parameters.AddWithValue(request.TerminalCode); command.Parameters.AddWithValue(uid);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return NotFound(new { error = "terminal_or_operator_not_authorized" });
            var employeeId = reader.GetInt32(0); var storeId = reader.GetInt32(1); var actualTerminalId = reader.GetInt32(5);
            if (actualTerminalId != int.Parse(User.FindFirstValue("terminal_id")!)) return Forbid();
            var profile = JsonSerializer.Serialize(new { id = employeeId, name = reader.GetString(2), role = reader.GetString(3) });
            var permissions = JsonSerializer.Serialize(new { level = reader.GetInt32(4), role = reader.GetString(3) });
            await reader.DisposeAsync();
            var grant = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40)); var expires = DateTimeOffset.UtcNow.AddHours(request.ValidHours);
            await scope.CommitAsync(ct);
            await StoreOfflineOperatorAsync(uid, employeeId, storeId, actualTerminalId, profile, permissions, grant, expires, Guid.NewGuid().ToString("D"), ct);
            return Ok(new { offlineGrant = grant, expiresAt = expires, terminalId = actualTerminalId });
        }
    }

    private async Task<LocalSessionResponse> CreateLocalSessionAsync(SqliteConnection connection, string uid, CancellationToken ct)
    {
        var access = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO local_access_session(access_token_hash,refresh_token_hash,firebase_uid,expires_at,refresh_expires_at,created_at) VALUES($a,$r,$uid,$exp,$refresh_exp,$now)";
        command.Parameters.AddWithValue("$a", LocalStore.Sha256(access)); command.Parameters.AddWithValue("$r", LocalStore.Sha256(refresh)); command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$exp", now.AddHours(12).ToString("O")); command.Parameters.AddWithValue("$refresh_exp",now.AddDays(7).ToString("O")); command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return new LocalSessionResponse(access, refresh, "Bearer", now.AddHours(12));
    }

    private async Task<LocalSessionResponse> CreateLocalSessionWithRefreshAsync(string uid, string refresh, CancellationToken ct)
    {
        var access = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var now = DateTimeOffset.UtcNow;
        await using var db = await store.OpenConnectionAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO local_access_session(access_token_hash,refresh_token_hash,firebase_uid,expires_at,refresh_expires_at,created_at) VALUES($a,$r,$uid,$exp,$refresh_exp,$now)";
        command.Parameters.AddWithValue("$a", LocalStore.Sha256(access)); command.Parameters.AddWithValue("$r", LocalStore.Sha256(refresh)); command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$exp", now.AddHours(12).ToString("O")); command.Parameters.AddWithValue("$refresh_exp",now.AddDays(7).ToString("O")); command.Parameters.AddWithValue("$now", now.ToString("O")); await command.ExecuteNonQueryAsync(ct);
        return new LocalSessionResponse(access, refresh, "Bearer", now.AddHours(12));
    }

    private async Task StoreOfflineOperatorAsync(string uid, int employeeId, int storeId, int terminalId, string profile, string permissions, string grant, DateTimeOffset expires, string sessionId, CancellationToken ct)
    {
        await using var db = await store.OpenConnectionAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO offline_operator(firebase_uid,employee_id,store_id,terminal_id,profile_ciphertext,permissions_ciphertext,offline_grant_ciphertext,grant_expires_at,source_session_id,cached_at)
            VALUES($uid,$employee,$store,$terminal,$profile,$permissions,$grant,$expires,$session,$now)
            ON CONFLICT(firebase_uid) DO UPDATE SET employee_id=excluded.employee_id,store_id=excluded.store_id,terminal_id=excluded.terminal_id,profile_ciphertext=excluded.profile_ciphertext,permissions_ciphertext=excluded.permissions_ciphertext,offline_grant_ciphertext=excluded.offline_grant_ciphertext,grant_expires_at=excluded.grant_expires_at,source_session_id=excluded.source_session_id,cached_at=excluded.cached_at,revoked_at=NULL
            """;
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$employee", employeeId); command.Parameters.AddWithValue("$store", storeId); command.Parameters.AddWithValue("$terminal", terminalId); command.Parameters.AddWithValue("$profile", store.Protect(profile)); command.Parameters.AddWithValue("$permissions", store.Protect(permissions)); command.Parameters.AddWithValue("$grant", store.Protect(grant)); command.Parameters.AddWithValue("$expires", expires.ToString("O")); command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnqueueSessionOperationAsync(SqliteConnection connection,SqliteTransaction tx,int terminalId,int employeeId,string type,object payload,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(payload));await outbox.EnqueueInTransactionAsync(connection,tx,new OfflineOperation(Guid.NewGuid(),Guid.NewGuid().ToString("D"),"staff_session",0,type,document.RootElement.Clone(),terminalId),ct);
    }
}
