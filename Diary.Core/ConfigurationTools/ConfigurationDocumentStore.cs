using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Diary.Core.ConfigurationTools;

public sealed class ConfigurationDocumentStore
{
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

    public ConfigurationDocumentStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public bool MasterKeyExists => File.Exists(Path.Combine(RootDirectory, MasterKeyFileName));

    public ConfigurationMasterKeyDiagnostic DiagnoseMasterKey()
    {
        var path = Path.Combine(RootDirectory, MasterKeyFileName);
        if (!File.Exists(path))
        {
            return new ConfigurationMasterKeyDiagnostic(
                ConfigurationMasterKeyStatus.Missing,
                "配置主密钥不存在。首次保存加密配置时会创建。");
        }

        try
        {
            var key = DecodeMasterKey(File.ReadAllBytes(path));
            CryptographicOperations.ZeroMemory(key);
            return new ConfigurationMasterKeyDiagnostic(
                ConfigurationMasterKeyStatus.Readable,
                "配置主密钥可由当前身份读取。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new ConfigurationMasterKeyDiagnostic(
                ConfigurationMasterKeyStatus.Unreadable,
                DescribeReadError(exception));
        }
    }

    public ConfigurationMasterKeyResetResult ResetUnreadableMasterKey(
        IEnumerable<ConfigurationFileDescriptor> descriptors)
    {
        var diagnostic = DiagnoseMasterKey();
        if (diagnostic.Status != ConfigurationMasterKeyStatus.Unreadable)
            throw new InvalidOperationException("仅当现有配置主密钥无法读取时才允许重新初始化加密身份。");

        var snapshot = CreateSnapshot(descriptors);
        var path = Path.Combine(RootDirectory, MasterKeyFileName);
        var retainedPath = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Move(path, retainedPath, false);
        return new ConfigurationMasterKeyResetResult(snapshot, retainedPath);
    }

    public ConfigurationDocument Load(ConfigurationFileDescriptor descriptor, bool useBackup = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var path = ResolvePath(descriptor.RelativePath) + (useBackup ? ".bak" : string.Empty);
        var primaryPath = ResolvePath(descriptor.RelativePath);
        var hasBackup = File.Exists(primaryPath + ".bak");
        if (!File.Exists(path))
        {
            return new ConfigurationDocument(
                descriptor,
                ConfigurationDocumentStatus.Missing,
                null,
                null,
                null,
                hasBackup,
                descriptor.IsEncrypted ? "加密配置" : "JSON");
        }

        try
        {
            var data = File.ReadAllBytes(path);
            var json = descriptor.IsEncrypted
                ? Decrypt(data, descriptor.EncryptionPurpose!)
                : Encoding.UTF8.GetString(data);
            var parsed = JToken.Parse(json);
            return new ConfigurationDocument(
                descriptor,
                ConfigurationDocumentStatus.Loaded,
                parsed.ToString(Formatting.Indented),
                Fingerprint(data),
                File.GetLastWriteTimeUtc(path),
                hasBackup,
                DetectFormat(data, descriptor.IsEncrypted));
        }
        catch (Exception exception) when (IsReadException(exception))
        {
            return new ConfigurationDocument(
                descriptor,
                ConfigurationDocumentStatus.Unreadable,
                null,
                TryFingerprint(path),
                File.GetLastWriteTimeUtc(path),
                hasBackup,
                DetectFormatSafely(path, descriptor.IsEncrypted),
                DescribeReadError(exception));
        }
    }

    public ConfigurationDocument Save(
        ConfigurationFileDescriptor descriptor,
        string json,
        string? expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var parsed = JToken.Parse(json);
        if (parsed.Type != JTokenType.Object)
            throw new JsonException("配置根节点必须是 JSON 对象。");

        var path = ResolvePath(descriptor.RelativePath);
        EnsureUnchanged(path, expectedFingerprint);
        var normalized = parsed.ToString(Formatting.Indented);
        var data = descriptor.IsEncrypted
            ? Encrypt(normalized, descriptor.EncryptionPurpose!)
            : new UTF8Encoding(false).GetBytes(normalized + Environment.NewLine);
        WriteAtomically(path, data);
        return Load(descriptor);
    }

    public string RestoreBackup(ConfigurationFileDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var path = ResolvePath(descriptor.RelativePath);
        var backupPath = path + ".bak";
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("配置备份不存在。", backupPath);

        var recoveryPath = path + ".before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(path))
            File.Copy(path, recoveryPath, false);
        var temporaryPath = path + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(backupPath, temporaryPath, false);
            File.Move(temporaryPath, path, true);
            return recoveryPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public string CreateSnapshot(IEnumerable<ConfigurationFileDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var snapshotDirectory = Path.Combine(
            RootDirectory,
            "ConfigBackups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(snapshotDirectory);
        CopyIfExists(Path.Combine(RootDirectory, MasterKeyFileName), Path.Combine(snapshotDirectory, MasterKeyFileName));
        foreach (var descriptor in descriptors)
        {
            var source = ResolvePath(descriptor.RelativePath);
            var target = ResolveContainedPath(snapshotDirectory, descriptor.RelativePath);
            CopyIfExists(source, target);
            CopyIfExists(source + ".bak", target + ".bak");
        }
        return snapshotDirectory;
    }

    public string ResolvePath(string relativePath) => ResolveContainedPath(RootDirectory, relativePath);

    public static string ValidateAndNormalizeJson(string json)
    {
        var parsed = JToken.Parse(json);
        if (parsed.Type != JTokenType.Object)
            throw new JsonException("配置根节点必须是 JSON 对象。");
        return parsed.ToString(Formatting.Indented);
    }

    private byte[] Encrypt(string text, string purpose)
    {
        var salt = RandomNumberGenerator.GetBytes(GcmSaltSize);
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];
        var masterKey = GetOrCreateMasterKey();
        var key = DeriveFileKey(masterKey, salt, purpose);
        try
        {
            using var aes = new AesGcm(key, GcmTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
            var result = new byte[EncryptedMagic.Length + 1 + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
            var offset = 0;
            EncryptedMagic.CopyTo(result, offset);
            offset += EncryptedMagic.Length;
            result[offset++] = EncryptedFormatVersion;
            salt.CopyTo(result, offset);
            offset += salt.Length;
            nonce.CopyTo(result, offset);
            offset += nonce.Length;
            tag.CopyTo(result, offset);
            offset += tag.Length;
            ciphertext.CopyTo(result, offset);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string Decrypt(byte[] data, string purpose)
    {
        if (StartsWith(data, EncryptedMagic))
            return DecryptAuthenticated(data, purpose);
        if (StartsWith(data, OpenSslMagic))
            return DecryptLegacy(data, purpose);
        throw new InvalidDataException("加密配置格式未知。");
    }

    private string DecryptAuthenticated(byte[] data, string purpose)
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
        var masterKey = GetExistingMasterKey();
        var key = DeriveFileKey(masterKey, salt, purpose);
        try
        {
            using var aes = new AesGcm(key, GcmTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException("加密配置认证失败。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string DecryptLegacy(byte[] data, string password)
    {
        var headerLength = OpenSslMagic.Length + LegacySaltSize;
        if (data.Length < headerLength)
            throw new InvalidDataException("旧版加密配置长度无效。");
        var salt = data.AsSpan(OpenSslMagic.Length, LegacySaltSize);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize + LegacyIvSize);
        try
        {
            using var aes = Aes.Create();
            aes.Key = derived[..KeySize];
            using var decryptor = aes.CreateDecryptor(derived[..KeySize], derived[KeySize..]);
            using var input = new MemoryStream(data, headerLength, data.Length - headerLength);
            using var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
            using var reader = new StreamReader(crypto, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    private byte[] GetOrCreateMasterKey()
    {
        var path = Path.Combine(RootDirectory, MasterKeyFileName);
        if (File.Exists(path))
            return DecodeMasterKey(File.ReadAllBytes(path));

        Directory.CreateDirectory(RootDirectory);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var encoded = EncodeMasterKey(key);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, encoded);
            RestrictMasterKeyPermissions(temporaryPath);
            try
            {
                File.Move(temporaryPath, path, false);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return DecodeMasterKey(File.ReadAllBytes(path));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private byte[] GetExistingMasterKey()
    {
        var path = Path.Combine(RootDirectory, MasterKeyFileName);
        if (!File.Exists(path))
            throw new CryptographicException($"配置主密钥不存在：{path}");
        return DecodeMasterKey(File.ReadAllBytes(path));
    }

    private static byte[] EncodeMasterKey(byte[] key)
        => OperatingSystem.IsWindows()
            ? ProtectedData.Protect(key, EncryptedMagic, DataProtectionScope.CurrentUser)
            : key.ToArray();

    private static byte[] DecodeMasterKey(byte[] encoded)
    {
        var key = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(encoded, EncryptedMagic, DataProtectionScope.CurrentUser)
            : encoded.ToArray();
        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("配置主密钥长度无效。");
        }
        return key;
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

    private static void EnsureUnchanged(string path, string? expectedFingerprint)
    {
        if (!File.Exists(path))
        {
            if (expectedFingerprint is not null)
                throw new IOException("配置文件已被外部删除，请重新加载后再保存。");
            return;
        }

        if (expectedFingerprint is null)
            throw new IOException("配置文件已由其他进程创建，请重新加载后再保存。");
        var currentFingerprint = Fingerprint(File.ReadAllBytes(path));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(currentFingerprint),
                Convert.FromHexString(expectedFingerprint)))
        {
            throw new IOException("配置文件已被其他进程修改，请重新加载并合并更改。");
        }
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Copy(path, path + ".bak", true);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
            throw new ArgumentException("配置路径必须是相对路径。", nameof(relativePath));
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("配置路径超出目标目录。");
        }
        return path;
    }

    private static void CopyIfExists(string source, string target)
    {
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
    }

    private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string? TryFingerprint(string path)
    {
        try
        {
            return Fingerprint(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DetectFormat(byte[] data, bool encrypted)
    {
        if (!encrypted)
            return "JSON 明文";
        if (StartsWith(data, EncryptedMagic))
            return "DiaryGCM v1";
        if (StartsWith(data, OpenSslMagic))
            return "OpenSSL 旧版";
        return "未知加密格式";
    }

    private static string DetectFormatSafely(string path, bool encrypted)
    {
        try
        {
            return DetectFormat(File.ReadAllBytes(path), encrypted);
        }
        catch
        {
            return encrypted ? "加密配置" : "JSON 明文";
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static bool IsReadException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or JsonException
            or DecoderFallbackException;

    private static string DescribeReadError(Exception exception)
    {
        if (exception is CryptographicException
            && OperatingSystem.IsWindows()
            && exception.ToString().Contains("ProtectedData.Unprotect", StringComparison.Ordinal))
        {
            return "当前 Windows 用户无法解密配置主密钥。用户 SID、域身份或 DPAPI 凭据可能已变化。";
        }
        return exception.GetBaseException().Message;
    }

    private static void RestrictMasterKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
