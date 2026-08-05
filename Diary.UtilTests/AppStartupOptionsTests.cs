using Diary.App;

namespace Diary.UtilTests;

[TestClass]
public sealed class AppStartupOptionsTests
{
    [TestMethod]
    public void ParseCoreOnlyArgument_DisablesTrackerLoading()
    {
        var options = AppStartupOptions.Parse(new[] { AppStartupOptions.CoreOnlyArgument });

        Assert.IsTrue(options.CoreOnly);
    }

    [TestMethod]
    public void ParseWithoutCoreOnlyArgument_PreservesDefaultStartup()
    {
        var options = AppStartupOptions.Parse(new[] { "--some-other-option" });

        Assert.IsFalse(options.CoreOnly);
    }
}
