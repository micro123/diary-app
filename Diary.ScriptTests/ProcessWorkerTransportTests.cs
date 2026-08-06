using Diary.Script.Runtime;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ProcessWorkerTransportTests
{
    [TestMethod]
    public async Task Factory_RejectsRelativeExecutableAndWorkingDirectory()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("worker", [], "/tmp")).CreateAsync().AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("/bin/sh", [], "tmp")).CreateAsync().AsTask());
    }
}
