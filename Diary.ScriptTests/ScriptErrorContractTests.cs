using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptErrorContractTests
{
    [TestMethod]
    public void QueryAndLogResultsExposeStableApiErrorCodes()
    {
        var query = ScriptWorkItemQueryResult.Failure(
            ScriptQueryErrorCode.InvalidInput,
            "查询参数无效。");
        var log = ScriptLogItemResult.Failure(
            ScriptLogItemErrorCode.DatabaseUnavailable,
            "数据库尚未连接。");

        Assert.AreEqual(ScriptApiErrorCodes.InvalidArgument, query.ApiError!.Code);
        Assert.AreEqual(ScriptErrorCategory.Validation, query.ApiError.Category);
        Assert.AreEqual(ScriptApiErrorCodes.HostNotConfigured, log.ApiError!.Code);
        Assert.IsTrue(log.ApiError.Retryable);
    }

    [TestMethod]
    public void TrackerResultExposesStableApiErrorCodes()
    {
        var invalid = TrackerScriptResult.Failure(
            TrackerScriptErrorCode.InvalidInput,
            "Tracker 身份无效。");
        var unavailable = TrackerScriptResult.Failure(
            TrackerScriptErrorCode.InstanceUnavailable,
            "Tracker 实例不可用。");

        Assert.AreEqual(ScriptApiErrorCodes.InvalidArgument, invalid.ApiError!.Code);
        Assert.AreEqual(ScriptApiErrorCodes.InstanceUnavailable, unavailable.ApiError!.Code);
    }
}
