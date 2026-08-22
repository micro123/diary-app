using Diary.Core.Configure;
using Diary.Core.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Diary.UtilTests;

[TestClass]
public sealed class SensitiveConfigurationTests
{
    private const string FileName = "sensitive_configuration_tests.json";

    [TestCleanup]
    public void Cleanup()
    {
        var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), FileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    [TestMethod]
    public void EncryptedConfiguration_DoesNotStoreSecretAsPlaintext()
    {
        var configuration = new SensitiveConfiguration { ApiKey = "secret-value" };

        Assert.IsTrue(EasySaveLoad.Save(configuration));
        var data = File.ReadAllBytes(
            Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), FileName));
        var secret = Encoding.UTF8.GetBytes(configuration.ApiKey);

        Assert.AreEqual(-1, data.AsSpan().IndexOf(secret));
        CollectionAssert.AreEqual("DiaryGCM"u8.ToArray(), data[..8]);
        var loaded = new SensitiveConfiguration();
        Assert.IsTrue(EasySaveLoad.Load(loaded));
        Assert.AreEqual(configuration.ApiKey, loaded.ApiKey);
    }

    [TestMethod]
    public void EncryptedConfiguration_RejectsTampering()
    {
        var configuration = new SensitiveConfiguration { ApiKey = "secret-value" };
        Assert.IsTrue(EasySaveLoad.Save(configuration));
        var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), FileName);
        var data = File.ReadAllBytes(path);
        data[^1] ^= 0x01;
        File.WriteAllBytes(path, data);

        Assert.IsFalse(EasySaveLoad.Load(new SensitiveConfiguration()));
    }

    [TestMethod]
    public void EncryptedConfiguration_LoadsLegacyCbcFormat()
    {
        var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), FileName);
        File.WriteAllBytes(path, CreateLegacyPayload("{\"ApiKey\":\"legacy-secret\"}", "sensitive-test-key"));

        var loaded = new SensitiveConfiguration();

        Assert.IsTrue(EasySaveLoad.Load(loaded));
        Assert.AreEqual("legacy-secret", loaded.ApiKey);
    }

    [TestMethod]
    public void ApiKeyConfigurationProperty_IsMarkedAsPassword()
    {
        var property = typeof(SensitiveConfiguration).GetProperty(nameof(SensitiveConfiguration.ApiKey))!;
        var attribute = property.GetCustomAttributes(typeof(ConfigureTextAttribute), false)
            .Cast<ConfigureTextAttribute>()
            .Single();

        Assert.IsTrue(attribute.IsPassword);
    }

    private static byte[] CreateLegacyPayload(string text, string password)
    {
        var magic = "Salted__"u8.ToArray();
        var salt = RandomNumberGenerator.GetBytes(8);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, 100_000,
            HashAlgorithmName.SHA256, 48);
        using var aes = Aes.Create();
        using var encryptor = aes.CreateEncryptor(derived[..32], derived[32..]);
        using var output = new MemoryStream();
        using (var crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write))
        using (var writer = new StreamWriter(crypto))
            writer.Write(text);
        return [.. magic, .. salt, .. output.ToArray()];
    }

    [StorageFile(FileName, "sensitive-test-key")]
    private sealed class SensitiveConfiguration
    {
        [ConfigureText("API Key", password: true)]
        public string ApiKey { get; set; } = "";
    }
}
