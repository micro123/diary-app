using System.Diagnostics;
using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
public sealed class RedMineTests
{
    [TestMethod]
    public void GetCurrentUser()
    {
        Assert.IsTrue(RedMineApis.GetUserInfo(out var userInfo));
        Debug.WriteLine($"user is {userInfo.LastName}{userInfo.FirstName}, Id = {userInfo.Id}");
    }

    [TestMethod]
    public void GetUserActivities()
    {
        Assert.IsTrue(RedMineApis.GetActivities(out var activities));
        foreach (var activity in activities)
        {
            Debug.WriteLine($"{activity.Id} - {activity.Name}");
        }
    }

    [TestMethod]
    public void SearchProject()
    {
        Assert.IsTrue(RedMineApis.SearchProject(out var projects, out var total, 0));
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
        Assert.IsTrue(RedMineApis.GetProject(out var project, 171));
        Debug.WriteLine($"{project.Id} - {project.Name}");
        Debug.WriteLine($"{project.Description}");
    }

    [TestMethod]
    public void SearchIssues()
    {
        Assert.IsTrue(RedMineApis.SearchIssueByKeywords(out var issues, out var total, myIssues:false, openOnly:false));
        Debug.WriteLine($"search results has {total} issues");
        foreach (var issue in issues)
        {
            Debug.WriteLine($"issue #{issue.Id}: {issue.Subject}");
        }
    }

    [TestMethod]
    public void GetIssue()
    {
        Assert.IsTrue(RedMineApis.GetIssue(out var issue, 4129));
        Debug.WriteLine($"{issue.Id} - {issue.Subject}");
    }

    [TestMethod]
    public void GetTimeEntries()
    {
        Assert.IsTrue(RedMineApis.GetMyTimeEntries(out var timeEntries,  out var total, "2025-11-17", "2025-11-23"));
        Debug.WriteLine($"total results has {total} timeEntries");
        foreach (var timeEntry in timeEntries)
        {
            
        }
    }
}