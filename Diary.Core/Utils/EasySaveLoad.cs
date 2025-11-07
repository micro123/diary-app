using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Diary.Core.Configure;
using Diary.Utils;
using Newtonsoft.Json;

namespace Diary.Core.Utils;

public static class EasySaveLoad
{
    static class Pkcs7Alignment
    {
        /// <summary>
        /// 使用PKCS7填充对齐到32字节
        /// </summary>
        public static byte[] AlignWithPkcs7(byte[] data)
        {
            int blockSize = 32;
            int paddingNeeded = blockSize - (data.Length % blockSize);
        
            // 如果数据长度正好是块大小的倍数，添加一个完整的填充块
            if (paddingNeeded == 0)
                paddingNeeded = blockSize;

            byte[] result = new byte[data.Length + paddingNeeded];
        
            // 复制原始数据
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
        
            // 添加PKCS7填充
            for (int i = data.Length; i < result.Length; i++)
            {
                result[i] = (byte)paddingNeeded;
            }
        
            return result;
        }

        /// <summary>
        /// 移除PKCS7填充
        /// </summary>
        public static byte[] RemovePkcs7Padding(byte[] data)
        {
            if (data.Length == 0)
                return data;

            int paddingLength = data[^1];
        
            // 验证填充有效性
            if (paddingLength > data.Length || paddingLength == 0)
                throw new ArgumentException("Invalid PKCS7 padding");

            for (int i = data.Length - paddingLength; i < data.Length; i++)
            {
                if (data[i] != paddingLength)
                    throw new ArgumentException("Invalid PKCS7 padding");
            }

            byte[] result = new byte[data.Length - paddingLength];
            Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }
    }

    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("1478523690zzzqqq");

    private static byte[] AesEncrypt(string text, string key)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Pkcs7Alignment.AlignWithPkcs7(Encoding.UTF8.GetBytes(key));

        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using (var msEncrypt = new MemoryStream())
        {
            using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
            {
                using (var swEncrypt = new StreamWriter(csEncrypt))
                {
                    swEncrypt.Write(text);
                    swEncrypt.Flush();
                }
            }
            return msEncrypt.ToArray();
        }
    }

    private static string AesDecrypt(byte[] dat, string key)
    {
        using var aesAlg = Aes.Create();
        aesAlg.IV = Iv;
        aesAlg.KeySize = 256;
        aesAlg.Mode = CipherMode.CBC;
        aesAlg.Padding = PaddingMode.PKCS7;
        aesAlg.Key = Pkcs7Alignment.AlignWithPkcs7(Encoding.UTF8.GetBytes(key));

        var decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

        try
        {
            using (var msDecrypt = new MemoryStream(dat))
            {
                using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                {
                    using (var srDecrypt = new StreamReader(csDecrypt))
                    {
                        return srDecrypt.ReadToEnd();
                    }
                }
            }
        }
        catch (CryptographicException ex)
        {
            Debug.WriteLine(ex.Message);
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