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

public enum ConfigurationLoadStatus
{
    Missing,
    Loaded,
    Unreadable,
}

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

    private static string DecryptAuthenticated(byte[] data, string purpose)
    {
        var headerLength = EncryptedMagic.Length + 1 + GcmSaltSize + GcmNonceSize + GcmTagSize;
        if (data.Length < headerLength || data[EncryptedMagic.Length] != EncryptedFormatVersion)
            throw new InvalidDataException("加密配置头不完整或版本不受支持。");

        var offset = EncryptedMagic.Length + 1;
        var salt = data.AsSpan(offset, GcmSaltSize);
        offset += GcmSaltSize;
        var nonce = data.AsSpan(offset, GcmNonceSize);
        offset += GcmNonceSize;
        var tag = data.AsSpan(offset, GcmTagSize);
        offset += GcmTagSize;
        var ciphertext = data.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        byte[]? key = null;
        try
        {
            key = DeriveFileKey(GetExistingMasterKey(), salt, purpose);
            using var aes = new AesGcm(key, GcmTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("加密配置认证失败。", ex);
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string DecryptLegacy(byte[] data, string password)
    {
        var headerLen = OpenSslMagic.Length + LegacySaltSize;
        if (data.Length < headerLen)
            throw new InvalidDataException($"旧版加密配置长度无效：{data.Length} 字节。");

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
            throw new CryptographicException("旧版加密配置解密失败。", ex);
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
            var path = GetMasterKeyPath();
            if (_masterKey is not null)
            {
                if (!File.Exists(path))
                    WriteMasterKey(path, _masterKey);
                return _masterKey;
            }

            if (File.Exists(path))
            {
                _masterKey = DecodeMasterKey(IoUtils.ReadAllBytes(path));
                return _masterKey;
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            try
            {
                WriteMasterKey(path, key);
                _masterKey = key;
                return _masterKey;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                _masterKey = DecodeMasterKey(IoUtils.ReadAllBytes(path));
                return _masterKey;
            }
        }
    }

    private static byte[] GetExistingMasterKey()
    {
        lock (MasterKeyLock)
        {
            if (_masterKey is not null)
                return _masterKey;

            var path = GetMasterKeyPath();
            if (!File.Exists(path))
                throw new CryptographicException($"配置主密钥不存在：{path}");

            _masterKey = DecodeMasterKey(IoUtils.ReadAllBytes(path));
            return _masterKey;
        }
    }

    private static string GetMasterKeyPath()
        => Path.Combine(FsTools.GetApplicationConfigDirectory(), MasterKeyFileName);

    private static void WriteMasterKey(string path, byte[] key)
    {
        var encoded = EncodeMasterKey(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            if (!IoUtils.WriteAllBytes(temporaryPath, encoded))
                throw new IOException($"无法写入配置主密钥临时文件：{temporaryPath}");
            RestrictMasterKeyPermissions(temporaryPath);
            File.Move(temporaryPath, path, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
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

    internal static void ResetMasterKeyCacheForTests()
    {
        lock (MasterKeyLock)
        {
            if (_masterKey is not null)
                CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
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
        => LoadJson(obj, out json, out _) == ConfigurationLoadStatus.Loaded;

    /// <summary>读取配置并区分首次缺失与不可读取，防止调用方用默认值覆盖损坏配置。</summary>
    public static ConfigurationLoadStatus LoadJson(object obj, out JObject json, out Exception? error)
    {
        json = new JObject();
        error = null;
        if (!GetSaveConfig(obj, out var storageFileAttribute))
            return ConfigurationLoadStatus.Missing;

        var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
        var backupPath = filePath + ".bak";
        if (!File.Exists(filePath))
        {
            if (!File.Exists(backupPath))
                return ConfigurationLoadStatus.Missing;

            try
            {
                json = ParseContent(backupPath, storageFileAttribute);
                File.Copy(backupPath, filePath, false);
                Logger.LogWarning("配置文件 {FilePath} 缺失，已从备份恢复", filePath);
                return ConfigurationLoadStatus.Loaded;
            }
            catch (Exception ex) when (IsConfigurationReadException(ex))
            {
                error = ex;
                Logger.LogError(ex, "配置文件 {FilePath} 及其备份无法恢复", filePath);
                return ConfigurationLoadStatus.Unreadable;
            }
        }

        try
        {
            json = ParseContent(filePath, storageFileAttribute);
            return ConfigurationLoadStatus.Loaded;
        }
        catch (Exception ex) when (IsConfigurationReadException(ex))
        {
            error = ex;
            Logger.LogError(ex, "配置文件 {FilePath} 无法读取，已阻止覆盖", filePath);
            return ConfigurationLoadStatus.Unreadable;
        }
    }

    /// <summary>以配置对象声明的文件名保存原始 JSON。</summary>
    public static bool SaveJson(object obj, JObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!GetSaveConfig(obj, out var storageFileAttribute))
            return false;

        var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
        var loadStatus = LoadJson(obj, out _, out _);
        if (loadStatus == ConfigurationLoadStatus.Unreadable)
            return false;

        var content = json.ToString(Formatting.None);
        if (storageFileAttribute.Encrypted)
        {
            var data = EncryptAuthenticated(content, storageFileAttribute.EncryptKey);
            WriteAtomically(filePath, temporaryPath =>
            {
                if (!IoUtils.WriteAllBytes(temporaryPath, data))
                    throw new IOException($"无法写入配置临时文件：{temporaryPath}");
            });
        }
        else
        {
            WriteAtomically(filePath, temporaryPath =>
            {
                if (!IoUtils.WriteAllText(temporaryPath, content))
                    throw new IOException($"无法写入配置临时文件：{temporaryPath}");
            });
        }

        return true;
    }

    private static void WriteAtomically(string filePath, Action<string> writeTemp)
    {
        var tempPath = filePath + ".tmp";
        var backupPath = filePath + ".bak";
        try
        {
            writeTemp(tempPath);
            if (File.Exists(filePath))
                File.Copy(filePath, backupPath, true);
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

    private static JObject ParseContent(string filePath, StorageFileAttribute storageFileAttribute)
    {
        var content = ReadContent(filePath, storageFileAttribute);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("配置内容为空。");
        return JObject.Parse(content);
    }

    private static string ReadContent(string filePath, StorageFileAttribute storageFileAttribute)
    {
        if (storageFileAttribute.Encrypted)
        {
            var data = IoUtils.ReadAllBytes(filePath);
            if (StartsWith(data, EncryptedMagic))
                return DecryptAuthenticated(data, storageFileAttribute.EncryptKey);
            if (StartsWith(data, OpenSslMagic))
                return DecryptLegacy(data, storageFileAttribute.EncryptKey);
            throw new InvalidDataException($"加密配置格式未知：{filePath}");
        }

        return IoUtils.ReadAllText(filePath);
    }

    private static bool IsConfigurationReadException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or JsonException;
}
