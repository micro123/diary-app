using System.Diagnostics;
using Diary.RedMine;
using Diary.RedMine.Response;

namespace Diary.RedMineTests;

[TestClass]
[TestCategory("External")]
public sealed class RedMineTests
{
    private static readonly IRedMineApi Api = new RedMineApi();

    [TestMethod]
    public void GetCurrentUser()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.GetUserInfo(out var userInfo));
        Debug.WriteLine($"user is {userInfo.LastName}{userInfo.FirstName}, Id = {userInfo.Id}");
    }

    [TestMethod]
    public void GetUserActivities()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.GetActivities(out var activities));
        var activityInfos = activities as ActivityInfo[] ?? activities.ToArray();
        Debug.WriteLine($"total activity counts: {activityInfos.Count()}");
        foreach (var activity in activityInfos)
        {
            Debug.WriteLine($"{activity.Id} - {activity.Name}");
        }
    }

    [TestMethod]
    public void SearchProject()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.SearchProject(out var projects, out var total, 0));
        Debug.WriteLine($"search results has {total} projects");
        foreach (var project in projects)
        {
            Debug.WriteLine($"{project.Id} - {project.Name}");
            Debug.WriteLine($"{project.Description}");
        }
    }

    [TestMethod]
    public void GetProject()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.GetProject(out var project, 1));
        Debug.WriteLine($"{project.Id} - {project.Name}");
        Debug.WriteLine($"{project.Description}");
    }

    [TestMethod]
    public void SearchIssues()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.SearchIssueByKeywords(out var issues, out var total, myIssues: false, openOnly: false));
        Debug.WriteLine($"search results has {total} issues");
        foreach (var issue in issues)
        {
            Debug.WriteLine($"issue #{issue.Id}: {issue.Subject}");
        }
    }

    [TestMethod]
    public void GetIssue()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.GetIssue(out var issue, 3));
        Debug.WriteLine($"{issue.Id} - {issue.Subject}");
    }

    [TestMethod]
    public void GetTimeEntries()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.GetMyTimeEntries(out var timeEntries, out var total, "2025-11-17", "2025-11-23"));
        Debug.WriteLine($"total results has {total} timeEntries");
        foreach (var timeEntry in timeEntries)
        {
            Debug.WriteLine($"#{timeEntry.Id}: project = {timeEntry.Project.Name}, issue = {timeEntry.Issue.Name}, time = {timeEntry.Hours}, comment = {timeEntry.Comment}");
        }
    }

    [TestMethod]
    public void CreateIssue()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.CreateIssue(out var issue, 1, "Diary.App.ApiTest", "Diary.App.ApiTest", true));
        Debug.WriteLine($"issue created! id = {issue.Id}, subject = {issue.Subject}, description = {issue.Description}, assignedTo = {issue.AssignedTo.Id}");
    }

    [TestMethod]
    public void CreateTimeEntry()
    {
        EnsureExternalTestsEnabled();
        Assert.IsTrue(Api.CreateTimeEntry(out var info, 3, 1, "2025-11-25", 6.5, "你好"));
        Debug.WriteLine($"time created, id = {info.Id}");
    }

    private static void EnsureExternalTestsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DIARY_RUN_REDMINE_EXTERNAL_TESTS"),
                "1",
                StringComparison.Ordinal))
            Assert.Inconclusive("设置 DIARY_RUN_REDMINE_EXTERNAL_TESTS=1 后才运行外部 Redmine API 测试。");
    }
}
