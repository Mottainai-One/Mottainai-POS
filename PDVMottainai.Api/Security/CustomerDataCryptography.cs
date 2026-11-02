using System.Security.Cryptography;
using System.Text;

namespace PDVMottainai.Api.Security;

public sealed class CustomerDataCryptography(IConfiguration configuration)
{
    private const string AadPrefix = "mottainai:operational:customer.personal_data:";

    public string Decrypt(int customerId, byte[] ciphertext, byte[] nonce, short keyVersion)
    {
        if (customerId < 1 || nonce.Length != 12 || ciphertext.Length < 17 || keyVersion < 1)
            throw new CryptographicException("Invalid customer data envelope.");

        var keys = GetDecryptionKeys();
        if (!keys.TryGetValue(keyVersion, out var key))
            throw new InvalidOperationException("No decryption key is configured for the customer data key version.");

        var plaintext = new byte[ciphertext.Length - 16];
        var aad = Encoding.UTF8.GetBytes($"{AadPrefix}{customerId}");
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length, 16), plaintext, aad);
        return Encoding.UTF8.GetString(plaintext);
    }

    private Dictionary<short, byte[]> GetDecryptionKeys()
    {
        var encodedCurrentKey = configuration["Security:Customer:Data:EncryptionKey"];
        var currentVersionText = configuration["Security:Customer:Data:KeyVersion"];
        if (string.IsNullOrWhiteSpace(encodedCurrentKey)
            || !short.TryParse(currentVersionText, out var currentVersion)
            || currentVersion < 1)
            throw new InvalidOperationException("Customer data decryption settings are not configured.");

        var keys = new Dictionary<short, byte[]>
        {
            [currentVersion] = DecodeKey(encodedCurrentKey)
        };

        var historicalKeys = configuration["Security:Customer:Data:DecryptionKeys"];
        if (string.IsNullOrWhiteSpace(historicalKeys))
            return keys;

        foreach (var entry in historicalKeys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0 || !short.TryParse(entry[..separator], out var version) || version < 1 || keys.ContainsKey(version))
                throw new InvalidOperationException("Customer data decryption key versions are invalid.");
            keys.Add(version, DecodeKey(entry[(separator + 1)..]));
        }

        return keys;
    }

    private static byte[] DecodeKey(string encodedKey)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Customer data decryption key is invalid.", exception);
        }

        return key.Length == 32
            ? key
            : throw new InvalidOperationException("Customer data decryption key must be 32 bytes.");
    }
}
