#if DEBUG
using Diary.App;

namespace Diary.AppTests;

[TestClass]
public sealed class DebugUiAutomationTests
{
    [TestMethod]
    [DataRow("1024", 1024)]
    [DataRow("9222", 9222)]
    [DataRow("65535", 65535)]
    public void TryParsePort_AcceptsValidPorts(string value, int expected)
    {
        var result = DebugUiAutomation.TryParsePort(value, out var port);

        Assert.IsTrue(result);
        Assert.AreEqual(expected, port);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1023")]
    [DataRow("65536")]
    [DataRow("not-a-port")]
    public void TryParsePort_RejectsInvalidPorts(string? value)
    {
        Assert.IsFalse(DebugUiAutomation.TryParsePort(value, out _));
    }

    [TestMethod]
    [DataRow(null, "default")]
    [DataRow("", "default")]
    [DataRow(" EXTENDED ", "extended")]
    [DataRow("survey", "survey")]
    [DataRow("database-error", "database-error")]
    [DataRow("plugins", "plugins")]
    public void NormalizeScenario_AcceptsSupportedValues(string? value, string expected)
    {
        Assert.AreEqual(expected, DebugUiAutomation.NormalizeScenario(value));
    }

    [TestMethod]
    public void NormalizeScenario_RejectsUnknownValue()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DebugUiAutomation.NormalizeScenario("unknown"));
    }

    [TestMethod]
    public void CreateIsolatedAppId_IsStableAndSeparatesProfiles()
    {
        var first = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-a");
        var repeated = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-a");
        var second = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-b");

        Assert.AreEqual(first, repeated);
        Assert.AreNotEqual(first, second);
        StringAssert.StartsWith(first, "Diary.App.UiTest.");
        Assert.AreEqual("Diary.App.UiTest.".Length + 12, first.Length);
    }
}
#endif
