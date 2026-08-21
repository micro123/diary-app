using Diary.App.Services;

namespace Diary.AppTests;

[TestClass]
public sealed class AppUpdateServiceTests
{
    [TestMethod]
    public void ResolveCurrentSequence_AllowsLeavingLocalChannel()
    {
        Assert.AreEqual(0, AppUpdateService.ResolveCurrentSequence("stable", "local", 20260821091701));
        Assert.AreEqual(0, AppUpdateService.ResolveCurrentSequence("preview", "local", 20260821091701));
        Assert.AreEqual(20260821091701, AppUpdateService.ResolveCurrentSequence("local", "local", 20260821091701));
        Assert.AreEqual(491, AppUpdateService.ResolveCurrentSequence("stable", "release", 491));
    }
}
