using Diary.GUIBase.ViewModels;

namespace Diary.UtilTests;

[TestClass]
public sealed class SettingChoiceTests
{
    [TestMethod]
    public void Load_UsesConfiguredValueWithoutWritingDefaultOption()
    {
        var configuration = new ChoiceConfiguration { DatabaseDriver = "PostgreSQL" };
        var setting = CreateSetting(configuration);

        setting.Load();

        Assert.AreEqual(1, setting.SelectedIndex);
        Assert.AreEqual("PostgreSQL", configuration.DatabaseDriver);
    }

    [TestMethod]
    public void ChangingSelection_UpdatesConfigurationImmediately()
    {
        var configuration = new ChoiceConfiguration { DatabaseDriver = "SQLite" };
        var setting = CreateSetting(configuration);

        setting.Load();
        setting.SelectedIndex = 1;

        Assert.AreEqual("PostgreSQL", configuration.DatabaseDriver);
    }

    [TestMethod]
    public void Save_WritesSelectedOptionToConfiguration()
    {
        var configuration = new ChoiceConfiguration { DatabaseDriver = "SQLite" };
        var setting = CreateSetting(configuration);

        setting.SelectedIndex = 1;
        setting.Save();

        Assert.AreEqual("PostgreSQL", configuration.DatabaseDriver);
    }

    private static SettingChoice CreateSetting(ChoiceConfiguration configuration)
        => new(
            "数据库驱动",
            "",
            new[] { "SQLite", "PostgreSQL" },
            configuration,
            typeof(ChoiceConfiguration).GetProperty(nameof(ChoiceConfiguration.DatabaseDriver))!);

    private sealed class ChoiceConfiguration
    {
        public string DatabaseDriver { get; set; } = "SQLite";
    }
}
