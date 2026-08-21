using System.ComponentModel;
using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class UpdateFileAccessTests
{
    [TestMethod]
    public async Task ExecuteWithSharingRetryAsync_WhenViolationIsTransient_RetriesOperation()
    {
        var attempts = 0;

        var result = await UpdateFileAccess.ExecuteWithSharingRetryAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new Win32Exception(32);
                return ValueTask.FromResult("ok");
            },
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.AreEqual("ok", result);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ExecuteWithSharingRetryAsync_WhenErrorIsNotSharingViolation_DoesNotRetry()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await UpdateFileAccess.ExecuteWithSharingRetryAsync<string>(
                () =>
                {
                    attempts++;
                    throw new IOException("disk error");
                },
                retryDelays: [TimeSpan.Zero]));

        Assert.AreEqual("disk error", exception.Message);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    [DataRow(32)]
    [DataRow(33)]
    public void IsSharingViolation_ForWindowsSharingErrors_ReturnsTrue(int nativeErrorCode)
    {
        Assert.IsTrue(UpdateFileAccess.IsSharingViolation(new Win32Exception(nativeErrorCode)));
        Assert.IsTrue(UpdateFileAccess.IsSharingViolation(new TestIOException(nativeErrorCode)));
    }

    private sealed class TestIOException : IOException
    {
        public TestIOException(int nativeErrorCode)
        {
            HResult = unchecked((int)0x80070000) | nativeErrorCode;
        }
    }
}
