using System.Data.Common;
using Diary.App.Services;

namespace Diary.AppTests;

[TestClass]
public sealed class ApplicationFailurePolicyTests
{
    [TestMethod]
    public async Task RetryableOperation_AllowsRetryAfterFailure()
    {
        var attempts = 0;
        var operation = new RetryableAsyncOperation();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation.RunAsync(() =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("首次失败"))
                : Task.CompletedTask;
        }));
        await operation.RunAsync(() =>
        {
            attempts++;
            return Task.CompletedTask;
        });
        await operation.RunAsync(() =>
        {
            attempts++;
            return Task.CompletedTask;
        });

        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public void GlobalExceptionPolicy_OnlyContinuesForDatabaseExceptions()
    {
        Assert.IsTrue(GlobalExceptionPolicy.CanContinue(new TestDbException()));
        Assert.IsFalse(GlobalExceptionPolicy.CanContinue(new InvalidOperationException()));
    }

    private sealed class TestDbException : DbException;
}
