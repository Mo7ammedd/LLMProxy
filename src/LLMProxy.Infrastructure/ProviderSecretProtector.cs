using System.Security.Cryptography;
using System.Text;
using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;

namespace LLMProxy.Infrastructure;

public sealed class ProviderSecretProtector : IProviderSecretProtector, IDisposable
{
    private readonly byte[]? _key;
    public bool IsConfigured => _key is not null;
    public ProviderSecretProtector(IConfiguration configuration)
    {
        var value = configuration["LLMPROXY_PROVIDER_KEY_ENCRYPTION_KEY"]
            ?? configuration["LLMProxy:Operations:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            _key = Convert.FromBase64String(value);
            if (_key.Length != 32) throw new FormatException();
        }
        catch (FormatException) { throw new InvalidOperationException("Provider key encryption requires a base64-encoded 32-byte key."); }
    }

    public string Protect(string provider, string keyId, string secret)
    {
        if (_key is null) throw new GatewayException("Configure the provider key encryption key before storing credentials.", "key_encryption_unconfigured", 409);
        var plain = Encoding.UTF8.GetBytes(secret);
        var envelope = new byte[1 + 12 + 16 + plain.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(envelope.AsSpan(1, 12), plain, envelope.AsSpan(29), envelope.AsSpan(13, 16),
                Encoding.UTF8.GetBytes(provider + "/" + keyId));
            return Convert.ToBase64String(envelope);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string Unprotect(StoredProviderKey key)
    {
        try
        {
            if (_key is null) throw new CryptographicException();
            var envelope = Convert.FromBase64String(key.Ciphertext!);
            if (envelope.Length < 30 || envelope[0] != 1) throw new CryptographicException();
            var plain = new byte[envelope.Length - 29];
            try
            {
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(29), envelope.AsSpan(13, 16), plain,
                    Encoding.UTF8.GetBytes(key.Provider + "/" + key.KeyId));
                var secret = Encoding.UTF8.GetString(plain);
                if (ProviderKeyId.FromSecret(secret) != key.KeyId) throw new CryptographicException();
                return secret;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { throw new GatewayException("Stored provider credentials could not be decrypted. Check the encryption key.", "key_decryption_failed", 503); }
    }

    public void Dispose() { if (_key is not null) CryptographicOperations.ZeroMemory(_key); }
}
