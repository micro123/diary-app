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

    private const int SaltSize = 8;
    private const int KeySize = 32;
    private const int IvSize = 16;
    private const int Pbkdf2Iterations = 100_000;
    private static readonly byte[] OpenSslMagic = "Salted__"u8.ToArray();

    private static byte[] AesEncrypt(string text, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, KeySize + IvSize);
        var key = derived[..KeySize];
        var iv = derived[KeySize..];

        using var aes = Aes.Create();
        aes.Key = key;

        var encryptor = aes.CreateEncryptor(key, iv);
        using var msEncrypt = new MemoryStream();
        using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (var swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(text);
            swEncrypt.Flush();
        }

        var ciphertext = msEncrypt.ToArray();
        var result = new byte[OpenSslMagic.Length + SaltSize + ciphertext.Length];
        Buffer.BlockCopy(OpenSslMagic, 0, result, 0, OpenSslMagic.Length);
        Buffer.BlockCopy(salt, 0, result, OpenSslMagic.Length, SaltSize);
        Buffer.BlockCopy(ciphertext, 0, result, OpenSslMagic.Length + SaltSize, ciphertext.Length);
        return result;
    }

    private static string AesDecrypt(byte[] data, string password)
    {
        var headerLen = OpenSslMagic.Length + SaltSize;
        if (data.Length < headerLen)
        {
            Logger.LogError("AesDecrypt: data too short ({Length} bytes)", data.Length);
            return string.Empty;
        }

        var salt = new byte[SaltSize];
        var ciphertext = new byte[data.Length - headerLen];
        Buffer.BlockCopy(data, OpenSslMagic.Length, salt, 0, SaltSize);
        Buffer.BlockCopy(data, headerLen, ciphertext, 0, ciphertext.Length);

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, KeySize + IvSize);
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
            Logger.LogError(ex, "AesDecrypt error");
            return string.Empty;
        }
    }

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
            var data = AesEncrypt(content, storageFileAttribute.EncryptKey);
            IoUtils.WriteAllBytes(filePath, data);
        }
        else
        {
            IoUtils.WriteAllText(filePath, content);
        }

        return true;
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
            return data.Length > 0
                ? AesDecrypt(data, storageFileAttribute.EncryptKey)
                : null;
        }

        return IoUtils.ReadAllText(filePath);
    }
}
