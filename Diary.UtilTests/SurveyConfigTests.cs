using Diary.Core.Data.AppConfig;

namespace Diary.UtilTests;

[TestClass]
public sealed class SurveyConfigTests
{
    [TestMethod]
    public void SurveyorUsesLocalhostAsItsRespondentAddress()
    {
        var config = new SurveyConfig
        {
            Enabled = true,
            AsServer = true,
            ServerAddress = " 192.0.2.10 ",
        };

        Assert.IsTrue(config.IsServerEnabled);
        Assert.IsFalse(config.IsRespondentEnabled);
        Assert.IsTrue(config.TryGetRespondentAddress(out var address));
        Assert.AreEqual("127.0.0.1", address);
    }

    [TestMethod]
    public void RespondentRequiresTrimmedSurveyorAddress()
    {
        var config = new SurveyConfig
        {
            Enabled = true,
            AsServer = false,
            ServerAddress = " 192.0.2.10 ",
        };

        Assert.IsTrue(config.IsRespondentEnabled);
        Assert.IsTrue(config.TryGetRespondentAddress(out var address));
        Assert.AreEqual("192.0.2.10", address);
    }

    [TestMethod]
    public void DisabledSurveyHasNoActiveRole()
    {
        var config = new SurveyConfig { Enabled = false, AsServer = true };

        Assert.IsFalse(config.IsServerEnabled);
        Assert.IsFalse(config.IsRespondentEnabled);
        Assert.IsFalse(config.TryGetRespondentAddress(out _));
    }
}
