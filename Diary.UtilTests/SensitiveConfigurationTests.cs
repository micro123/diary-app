using Diary.Core.Configure;
using Diary.Core.Utils;

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
        var content = File.ReadAllText(
            Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), FileName));

        Assert.IsFalse(content.Contains(configuration.ApiKey, StringComparison.Ordinal));
        var loaded = new SensitiveConfiguration();
        Assert.IsTrue(EasySaveLoad.Load(loaded));
        Assert.AreEqual(configuration.ApiKey, loaded.ApiKey);
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

    [StorageFile(FileName, "sensitive-test-key")]
    private sealed class SensitiveConfiguration
    {
        [ConfigureText("API Key", password: true)]
        public string ApiKey { get; set; } = "";
    }
}
