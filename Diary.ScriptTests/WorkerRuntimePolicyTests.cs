using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerRuntimePolicyTests
{
    [TestMethod]
    public void SharedPolicy_AllowsWorkerReuseWithinConfiguredBudget()
    {
        var policy = WorkerRuntimePolicy.Shared;

        Assert.AreEqual(WorkerIsolationMode.Shared, policy.IsolationMode);
        Assert.IsFalse(policy.IsDedicated);
        Assert.AreEqual(1000, policy.MaxRequestsPerWorker);
    }

    [TestMethod]
    public void DedicatedPolicy_RecyclesWorkerAfterEachRequest()
    {
        var policy = WorkerRuntimePolicy.Dedicated;

        Assert.AreEqual(WorkerIsolationMode.Dedicated, policy.IsolationMode);
        Assert.IsTrue(policy.IsDedicated);
        Assert.AreEqual(1, policy.MaxRequestsPerWorker);
    }

    [TestMethod]
    public void WorkerRuntime_StoresPolicyAlongsideSupervisor()
    {
        var supervisor = new WorkerSupervisor(new NoopTransportFactory(), maxRequestsPerWorker: 1);
        var runtime = new WorkerRuntime(
            "python",
            supervisor,
            new WorkerHandshakeOptions("python", [ScriptApiVersion.V1], []),
            WorkerRuntimePolicy.Dedicated);

        Assert.AreSame(WorkerRuntimePolicy.Dedicated, runtime.Policy);
        Assert.AreEqual(runtime.Policy.MaxRequestsPerWorker, runtime.Supervisor.MaxRequestsPerWorker);
    }

    private sealed class NoopTransportFactory : IWorkerTransportFactory
    {
        public ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
