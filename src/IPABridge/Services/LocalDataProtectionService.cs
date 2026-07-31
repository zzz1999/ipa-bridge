using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPABridge.Services;

internal interface ILocalDataKeyProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] protectedData);
}

internal interface IRandomByteGenerator
{
    byte[] GetBytes(int count);
}

internal sealed class WindowsCurrentUserKeyProtector : ILocalDataKeyProtector
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("IPA Bridge|local data key|v1");

    public byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, OptionalEntropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        return ProtectedData.Unprotect(protectedData, OptionalEntropy, DataProtectionScope.CurrentUser);
    }
}

internal sealed class CryptographicRandomByteGenerator : IRandomByteGenerator
{
    public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
}

internal sealed class LocalDataProtectionService
{
    internal const string EnvelopeFormat = "ipa-bridge-encrypted-settings";
    internal const string EnvelopeAlgorithm = "AES-256-GCM";
    internal const int EnvelopeVersion = 1;
    internal const int MasterKeySize = 32;
    internal const int NonceSize = 12;
    internal const int AuthenticationTagSize = 16;

    private static readonly byte[] AssociatedData =
        Encoding.UTF8.GetBytes("IPA Bridge|settings|v1|AES-256-GCM");

    private static readonly JsonSerializerOptions EnvelopeSerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _keyFile;
    private readonly ILocalDataKeyProtector _keyProtector;
    private readonly IRandomByteGenerator _randomByteGenerator;
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    public LocalDataProtectionService(string keyFile)
        : this(keyFile, new WindowsCurrentUserKeyProtector(), new CryptographicRandomByteGenerator())
    {
    }

    internal LocalDataProtectionService(
        string keyFile,
        ILocalDataKeyProtector keyProtector,
        IRandomByteGenerator randomByteGenerator)
    {
        _keyFile = Path.GetFullPath(keyFile);
        _keyProtector = keyProtector;
        _randomByteGenerator = randomByteGenerator;
    }

    public async Task EnsureKeyExistsAsync()
    {
        var key = await GetKeyAsync(allowCreation: true);
        CryptographicOperations.ZeroMemory(key);
    }

    public async Task<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, bool allowKeyCreation)
    {
        var key = await GetKeyAsync(allowKeyCreation);
        var nonce = GetRandomBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var authenticationTag = new byte[AuthenticationTagSize];

        try
        {
            using var aes = new AesGcm(key, AuthenticationTagSize);
            aes.Encrypt(
                nonce,
                plaintext.Span,
                ciphertext,
                authenticationTag,
                AssociatedData);

            var envelope = new EncryptedSettingsEnvelope
            {
                Format = EnvelopeFormat,
                Version = EnvelopeVersion,
                Algorithm = EnvelopeAlgorithm,
                Nonce = Convert.ToBase64String(nonce),
                AuthenticationTag = Convert.ToBase64String(authenticationTag),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };

            return JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeSerializerOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(authenticationTag);
        }
    }

    public async Task<byte[]> DecryptAsync(ReadOnlyMemory<byte> encryptedEnvelope)
    {
        var envelope = DeserializeAndValidateEnvelope(encryptedEnvelope.Span);
        byte[] nonce;
        byte[] authenticationTag;
        byte[] ciphertext;

        try
        {
            nonce = Convert.FromBase64String(envelope.Nonce);
            authenticationTag = Convert.FromBase64String(envelope.AuthenticationTag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The encrypted settings file contains invalid encoded data.",
                exception);
        }

        if (nonce.Length != NonceSize ||
            authenticationTag.Length != AuthenticationTagSize ||
            ciphertext.Length == 0)
        {
            throw new InvalidDataException("The encrypted settings file has invalid field lengths.");
        }

        var key = await GetKeyAsync(allowCreation: false);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, AuthenticationTagSize);
            aes.Decrypt(
                nonce,
                ciphertext,
                authenticationTag,
                plaintext,
                AssociatedData);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                "IPA Bridge could not authenticate or decrypt its saved settings. " +
                "The settings or local encryption key may be damaged or belong to another Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(authenticationTag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private async Task<byte[]> GetKeyAsync(bool allowCreation)
    {
        await _keyGate.WaitAsync();
        try
        {
            if (File.Exists(_keyFile))
            {
                return await ReadExistingKeyAsync();
            }

            if (!allowCreation)
            {
                throw new InvalidDataException(
                    "The encrypted settings file exists, but its local encryption key is missing. " +
                    "Restore master-key.v1 from the same Windows user profile or remove the encrypted settings to start over.");
            }

            var key = GetRandomBytes(MasterKeySize);
            byte[]? protectedKey = null;
            try
            {
                protectedKey = _keyProtector.Protect(key);
                if (protectedKey.Length == 0)
                {
                    throw new InvalidDataException("Windows returned an empty protected encryption key.");
                }

                try
                {
                    await AtomicFile.WriteAllBytesAsync(_keyFile, protectedKey, overwrite: false);
                    return key;
                }
                catch (IOException) when (File.Exists(_keyFile))
                {
                    // Another app instance completed first-launch key creation.
                    CryptographicOperations.ZeroMemory(key);
                    return await ReadExistingKeyAsync();
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
            finally
            {
                if (protectedKey is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedKey);
                }
            }
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private async Task<byte[]> ReadExistingKeyAsync()
    {
        var protectedKey = await File.ReadAllBytesAsync(_keyFile);
        try
        {
            byte[] key;
            try
            {
                key = _keyProtector.Unprotect(protectedKey);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "Windows could not unlock the IPA Bridge local encryption key for the current user.",
                    exception);
            }

            if (key.Length != MasterKeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidDataException("The IPA Bridge local encryption key has an invalid length.");
            }

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private byte[] GetRandomBytes(int count)
    {
        var bytes = _randomByteGenerator.GetBytes(count);
        if (bytes.Length != count)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException(
                $"The random byte generator returned {bytes.Length} bytes instead of {count}.");
        }

        return bytes;
    }

    private static EncryptedSettingsEnvelope DeserializeAndValidateEnvelope(
        ReadOnlySpan<byte> encryptedEnvelope)
    {
        EncryptedSettingsEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EncryptedSettingsEnvelope>(
                           encryptedEnvelope,
                           EnvelopeSerializerOptions)
                       ?? throw new InvalidDataException("The encrypted settings file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The encrypted settings file is not valid JSON.", exception);
        }

        if (!string.Equals(envelope.Format, EnvelopeFormat, StringComparison.Ordinal) ||
            envelope.Version != EnvelopeVersion ||
            !string.Equals(envelope.Algorithm, EnvelopeAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The encrypted settings file uses an unsupported format, version, or algorithm.");
        }

        return envelope;
    }

    private sealed class EncryptedSettingsEnvelope
    {
        [JsonPropertyName("format")]
        public string Format { get; init; } = string.Empty;

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("algorithm")]
        public string Algorithm { get; init; } = string.Empty;

        [JsonPropertyName("nonce")]
        public string Nonce { get; init; } = string.Empty;

        [JsonPropertyName("authenticationTag")]
        public string AuthenticationTag { get; init; } = string.Empty;

        [JsonPropertyName("ciphertext")]
        public string Ciphertext { get; init; } = string.Empty;
    }
}

internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, byte[] contents, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryFile = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryFile,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                             }))
            {
                await stream.WriteAsync(contents);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryFile, fullPath, overwrite);
        }
        catch
        {
            TryDeleteFile(temporaryFile);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original write failure if cleanup is also blocked.
        }
    }
}
