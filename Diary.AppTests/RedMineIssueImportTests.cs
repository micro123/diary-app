using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Data.Base;
using Diary.GUIBase.Events;
using Diary.RedMine;
using Diary.RedMine.Models;
using Diary.RedMine.Response;
using Diary.RedMine.UI;
using Diary.RedMine.UI.ViewModels.Pages;

namespace Diary.AppTests;

[TestClass]
public sealed class RedMineIssueImportTests
{
    [TestMethod]
    public async Task ImportCommand_NotifiesIssueDataChanged()
    {
        var database = new RecordingDatabase();
        var viewModel = new RedMineIssueManageViewModel(new ProjectApi(), database);
        var recipient = new object();
        uint changed = 0;
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(recipient, (_, message) => changed = message.Value);
        var issue = new IssueInfo
        {
            Id = 10,
            Subject = "Imported issue",
            Project = new CommonInfo { Id = 1, Name = "Test project" },
            AssignedTo = new CommonInfo { Id = 1, Name = "admin" },
        };

        try
        {
            await viewModel.ImportCommand.ExecuteAsync(issue);

            Assert.AreEqual(1, database.ProjectId);
            Assert.AreEqual(10, database.IssueId);
            Assert.AreEqual(RedMineUiEvents.IssueChanged, changed);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    private sealed class ProjectApi : IRedMineApi
    {
        public int PageSize => 50;

        public bool GetProject([NotNullWhen(true)] out ProjectInfo? project, int id)
        {
            project = new ProjectInfo { Id = id, Name = "Test project", Description = "Imported" };
            return true;
        }

        public bool SearchProject([NotNullWhen(true)] out IEnumerable<ProjectInfo>? projects, out int total, int page = 0, string keyword = "")
            => throw new NotSupportedException();
        public bool SearchIssueByKeywords([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string keywords = "")
            => throw new NotSupportedException();
        public bool SearchIssueByIds([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string ids = "")
            => throw new NotSupportedException();
        public bool GetIssue([NotNullWhen(true)] out IssueInfo? issue, int id) => throw new NotSupportedException();
        public bool CreateIssue([NotNullWhen(true)] out IssueInfo? issue, int projectId, string subject, string description = "", bool assignedToSelf = true)
            => throw new NotSupportedException();
        public bool CloseIssue(int id) => throw new NotSupportedException();
        public bool CreateTimeEntry([NotNullWhen(true)] out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment)
            => throw new NotSupportedException();
        public bool GetMyTimeEntries([NotNullWhen(true)] out IEnumerable<TimeInfo>? timeInfos, out int total, string dateStart = "", string dateEnd = "", int page = 0)
            => throw new NotSupportedException();
        public bool GetActivities([NotNullWhen(true)] out IEnumerable<ActivityInfo>? activities) => throw new NotSupportedException();
        public bool GetUserInfo([NotNullWhen(true)] out UserInfo? userInfo) => throw new NotSupportedException();
    }

    private sealed class RecordingDatabase : IRedMineDb
    {
        public int ProjectId { get; private set; }
        public int IssueId { get; private set; }
        public string InstanceId => RedMinePluginConstants.DefaultInstanceId;
        public uint SchemaVersion => 1;

        public RedMineProject AddRedMineProject(int id, string title, string description)
        {
            ProjectId = id;
            return new RedMineProject { Id = id, Title = title, Description = description };
        }

        public RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project, bool closed = false)
        {
            IssueId = id;
            return new RedMineIssue { Id = id, Title = title, AssignedTo = assignedTo, ProjectId = project, IsClosed = closed };
        }

        public RedMineActivity AddRedMineActivity(int id, string title) => throw new NotSupportedException();
        public void UpdateRedMineIssueStatus(int id, bool closed) => throw new NotSupportedException();
        public void UpdateRedMineProjectStatus(int id, bool closed) => throw new NotSupportedException();
        public WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item) => throw new NotSupportedException();
        public IDictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date) => throw new NotSupportedException();
        public bool WorkItemWasUploaded(WorkItem item) => throw new NotSupportedException();
        public ICollection<RedMineActivity> GetRedMineActivities() => throw new NotSupportedException();
        public ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project) => throw new NotSupportedException();
        public ICollection<RedMineProject> GetRedMineProjects() => throw new NotSupportedException();
        public WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus) => throw new NotSupportedException();
        public bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry) => throw new NotSupportedException();
        public bool ClearData() => throw new NotSupportedException();
        public uint GetSchemaVersion() => SchemaVersion;
    }
}
