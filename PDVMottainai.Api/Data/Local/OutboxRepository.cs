using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PDVMottainai.Api.Data.Local;

public sealed record OfflineOperation(
    Guid OperationId,
    string IdempotencyKey,
    string EntityType,
    int LocalEntityId,
    string OperationType,
    JsonElement Payload,
    int TerminalId = 0);

public sealed record QueueReceipt(Guid OperationId, long SequenceId, string State);

public sealed class OutboxRepository(LocalStore store)
{
    public async Task<QueueReceipt> EnqueueAsync(OfflineOperation operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var receipt = await EnqueueInTransactionAsync(connection, transaction, operation, cancellationToken);
        transaction.Commit();
        return receipt;
    }

    public async Task<QueueReceipt> EnqueueInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, OfflineOperation operation, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(operation.Payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbox(
                operation_id, idempotency_key, terminal_id, entity_type, local_entity_id,
                operation_type, payload_ciphertext, payload_hash, state,
                created_at, updated_at)
            VALUES ($operation_id, $idempotency_key, $terminal_id, $entity_type, $local_entity_id,
                $operation_type, $payload_ciphertext, $payload_hash, 'PENDING',
                $created_at, $updated_at)
            ON CONFLICT(idempotency_key) DO NOTHING;
            SELECT sequence_id, operation_id, payload_hash, state
            FROM outbox WHERE idempotency_key = $idempotency_key;
            """;
        command.Parameters.AddWithValue("$operation_id", operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$idempotency_key", operation.IdempotencyKey);
        command.Parameters.AddWithValue("$terminal_id", operation.TerminalId);
        command.Parameters.AddWithValue("$entity_type", operation.EntityType);
        command.Parameters.AddWithValue("$local_entity_id", operation.LocalEntityId);
        command.Parameters.AddWithValue("$operation_type", operation.OperationType);
        command.Parameters.AddWithValue("$payload_ciphertext", store.Protect(payload));
        command.Parameters.AddWithValue("$payload_hash", payloadHash);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The queued operation could not be read back.");
        }

        var existingHash = reader.GetString(2);
        // The mutation and outbox row share a transaction. Reusing a key must
        // abort that whole transaction even when the body is identical, or a
        // retried request would create a second local entity.
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existingHash), Convert.FromHexString(payloadHash)) ||
            !string.Equals(reader.GetString(1), operation.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new IdempotencyConflictException(operation.IdempotencyKey);

        var receipt = new QueueReceipt(Guid.Parse(reader.GetString(1)), reader.GetInt64(0), reader.GetString(3));
        return receipt;
    }
}

public sealed class IdempotencyConflictException(string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' was already used with a different payload.");
