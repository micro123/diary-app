using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Diary.Core.Configure;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Diary.Core.Utils;

public static class EasySaveLoad
{
    private static ILogger Logger => Logging.Logger;

    private const int LegacySaltSize = 8;
    private const int KeySize = 32;
    private const int LegacyIvSize = 16;
    private const int Pbkdf2Iterations = 100_000;
    private const int GcmSaltSize = 16;
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const byte EncryptedFormatVersion = 1;
    private const string MasterKeyFileName = ".diary-master-key";
    private static readonly byte[] OpenSslMagic = "Salted__"u8.ToArray();
    private static readonly byte[] EncryptedMagic = "DiaryGCM"u8.ToArray();
    private static readonly object MasterKeyLock = new();
    private static byte[]? _masterKey;

    private static byte[] EncryptAuthenticated(string text, string purpose)
    {
        var salt = RandomNumberGenerator.GetBytes(GcmSaltSize);
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];
        var key = DeriveFileKey(GetOrCreateMasterKey(), salt, purpose);
        var associatedData = Encoding.UTF8.GetBytes(purpose);
        try
        {
            using (var aes = new AesGcm(key, GcmTagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var headerLength = EncryptedMagic.Length + 1 + GcmSaltSize + GcmNonceSize + GcmTagSize;
            var result = new byte[headerLength + ciphertext.Length];
            var offset = 0;
            Buffer.BlockCopy(EncryptedMagic, 0, result, offset, EncryptedMagic.Length);
            offset += EncryptedMagic.Length;
            result[offset++] = EncryptedFormatVersion;
            Buffer.BlockCopy(salt, 0, result, offset, salt.Length);
            offset += salt.Length;
            Buffer.BlockCopy(nonce, 0, result, offset, nonce.Length);
            offset += nonce.Length;
            Buffer.BlockCopy(tag, 0, result, offset, tag.Length);
            offset += tag.Length;
            Buffer.BlockCopy(ciphertext, 0, result, offset, ciphertext.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? DecryptAuthenticated(byte[] data, string purpose)
    {
        var headerLength = EncryptedMagic.Length + 1 + GcmSaltSize + GcmNonceSize + GcmTagSize;
        if (data.Length < headerLength || data[EncryptedMagic.Length] != EncryptedFormatVersion)
        {
            Logger.LogError("Encrypted configuration has an unsupported or truncated header");
            return null;
        }

        var offset = EncryptedMagic.Length + 1;
        var salt = data.AsSpan(offset, GcmSaltSize);
        offset += GcmSaltSize;
        var nonce = data.AsSpan(offset, GcmNonceSize);
        offset += GcmNonceSize;
        var tag = data.AsSpan(offset, GcmTagSize);
        offset += GcmTagSize;
        var ciphertext = data.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveFileKey(GetOrCreateMasterKey(), salt, purpose);
        try
        {
            using var aes = new AesGcm(key, GcmTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            Logger.LogError(ex, "Authenticated configuration decryption failed");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? DecryptLegacy(byte[] data, string password)
    {
        var headerLen = OpenSslMagic.Length + LegacySaltSize;
        if (data.Length < headerLen)
        {
            Logger.LogError("Legacy encrypted configuration is too short ({Length} bytes)", data.Length);
            return null;
        }

        var salt = new byte[LegacySaltSize];
        var ciphertext = new byte[data.Length - headerLen];
        Buffer.BlockCopy(data, OpenSslMagic.Length, salt, 0, LegacySaltSize);
        Buffer.BlockCopy(data, headerLen, ciphertext, 0, ciphertext.Length);

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, KeySize + LegacyIvSize);
        var key = derived[..KeySize];
        var iv = derived[KeySize..];

        using var aes = Aes.Create();
        aes.Key = key;

        var decryptor = aes.CreateDecryptor(key, iv);
        try
        {
            using var msDecrypt = new MemoryStream(ciphertext);
            using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
            using var srDecrypt = new StreamReader(csDecrypt);
            return srDecrypt.ReadToEnd();
        }
        catch (CryptographicException ex)
        {
            Logger.LogError(ex, "Legacy configuration decryption failed");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    private static byte[] DeriveFileKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> salt, string purpose)
    {
        using var hmac = new HMACSHA256(masterKey.ToArray());
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var input = new byte[salt.Length + purposeBytes.Length];
        salt.CopyTo(input);
        purposeBytes.CopyTo(input.AsSpan(salt.Length));
        return hmac.ComputeHash(input);
    }

    private static byte[] GetOrCreateMasterKey()
    {
        lock (MasterKeyLock)
        {
            if (_masterKey is not null)
                return _masterKey;

            var path = Path.Combine(FsTools.GetApplicationConfigDirectory(), MasterKeyFileName);
            if (File.Exists(path))
            {
                _masterKey = DecodeMasterKey(IoUtils.ReadAllBytes(path));
                return _masterKey;
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            var encoded = EncodeMasterKey(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            try
            {
                IoUtils.WriteAllBytes(temporaryPath, encoded);
                RestrictMasterKeyPermissions(temporaryPath);
                File.Move(temporaryPath, path, false);
                _masterKey = key;
                return _masterKey;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                _masterKey = DecodeMasterKey(IoUtils.ReadAllBytes(path));
                return _masterKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] EncodeMasterKey(byte[] key)
        => OperatingSystem.IsWindows()
            ? ProtectedData.Protect(key, EncryptedMagic, DataProtectionScope.CurrentUser)
            : key.ToArray();

    private static byte[] DecodeMasterKey(byte[] encoded)
    {
        var key = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(encoded, EncryptedMagic, DataProtectionScope.CurrentUser)
            : encoded;
        if (key.Length != KeySize)
            throw new CryptographicException("配置主密钥长度无效。");
        return key;
    }

    private static void RestrictMasterKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static bool GetSaveConfig(object o, [NotNullWhen(true)] out StorageFileAttribute? storageFileAttribute)
    {
        storageFileAttribute = null;
        var cls = o.GetType();
        storageFileAttribute = cls.GetCustomAttribute<StorageFileAttribute>(false);
        return storageFileAttribute != null;
    }

    public static bool Save(object obj)
    {
        return SaveJson(obj, JObject.FromObject(obj));
    }

    /// <summary>读取配置文件的原始 JSON，供宿主执行 schema 迁移时保留未知字段。</summary>
    public static bool LoadJson(object obj, out JObject json)
    {
        json = new JObject();
        if (!GetSaveConfig(obj, out var storageFileAttribute))
            return false;

        var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
        var content = ReadContent(filePath, storageFileAttribute);
        if (string.IsNullOrWhiteSpace(content))
            return false;

        json = JObject.Parse(content);
        return true;
    }

    /// <summary>以配置对象声明的文件名保存原始 JSON。</summary>
    public static bool SaveJson(object obj, JObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!GetSaveConfig(obj, out var storageFileAttribute))
            return false;

        var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
        var content = json.ToString(Formatting.None);
        if (storageFileAttribute.Encrypted)
        {
            var data = EncryptAuthenticated(content, storageFileAttribute.EncryptKey);
            WriteAtomically(filePath, () => IoUtils.WriteAllBytes(filePath + ".tmp", data));
        }
        else
        {
            WriteAtomically(filePath, () => IoUtils.WriteAllText(filePath + ".tmp", content));
        }

        return true;
    }

    private static void WriteAtomically(string filePath, Action writeTemp)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            writeTemp();
            File.Move(tempPath, filePath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static bool Load(object obj)
    {
        if (!LoadJson(obj, out var json))
            return false;

        JsonConvert.PopulateObject(json.ToString(Formatting.None), obj);
        return true;
    }

    private static string? ReadContent(string filePath, StorageFileAttribute storageFileAttribute)
    {
        if (!File.Exists(filePath))
            return null;

        if (storageFileAttribute.Encrypted)
        {
            var data = IoUtils.ReadAllBytes(filePath);
            if (StartsWith(data, EncryptedMagic))
                return DecryptAuthenticated(data, storageFileAttribute.EncryptKey);
            if (StartsWith(data, OpenSslMagic))
                return DecryptLegacy(data, storageFileAttribute.EncryptKey);
            Logger.LogError("Encrypted configuration {FilePath} has an unknown format", filePath);
            return null;
        }

        return IoUtils.ReadAllText(filePath);
    }
}
