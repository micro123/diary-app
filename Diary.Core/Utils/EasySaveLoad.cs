using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Diary.Core.Configure;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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
        if (GetSaveConfig(obj, out var storageFileAttribute))
        {
            var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
            var content = JsonConvert.SerializeObject(obj);
            if (storageFileAttribute.Encrypted)
            {
                var data = AesEncrypt(content, storageFileAttribute.EncryptKey);
                IoUtils.WriteAllBytes(filePath, data);
                return true;
            }
            else
            {
                IoUtils.WriteAllText(filePath, content);
                return true;
            }
        }
        return false;
    }

    public static bool Load(object obj)
    {
        if (GetSaveConfig(obj, out var storageFileAttribute))
        {
            var filePath = Path.Combine(FsTools.GetApplicationConfigDirectory(), storageFileAttribute.FileName);
            if (File.Exists(filePath))
            {
                string? content = null;
                if (storageFileAttribute.Encrypted)
                {
                    var data = IoUtils.ReadAllBytes(filePath);
                    if (data.Length > 0)
                        content = AesDecrypt(data, storageFileAttribute.EncryptKey);
                }
                else
                {
                    content = IoUtils.ReadAllText(filePath);
                }

                if (!string.IsNullOrEmpty(content))
                {
                    JsonConvert.PopulateObject(content, obj);
                    return true;
                }
            }
        }
        return false;
    }
}