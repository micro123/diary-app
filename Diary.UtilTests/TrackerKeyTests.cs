using Diary.PluginBase;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerKeyTests
{
    [TestMethod]
    public void SamePluginAndInstanceProduceEqualKeys()
    {
        var left = new TrackerKey("tracker.redmine", "redmine.company");
        var right = new TrackerKey("tracker.redmine", "redmine.company");

        Assert.AreEqual(left, right);
    }

    [TestMethod]
    public void DifferentPluginsDoNotShareAnInstanceKey()
    {
        var redmine = new TrackerKey("tracker.redmine", "default");
        var jira = new TrackerKey("tracker.jira", "default");
        var bindings = new Dictionary<TrackerKey, string>
        {
            [redmine] = "redmine-binding",
            [jira] = "jira-binding",
        };

        Assert.AreEqual(2, bindings.Count);
        Assert.AreEqual("jira-binding", bindings[jira]);
    }
}
