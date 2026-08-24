using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
[TestCategory("Unit")]
public sealed class SharedDisposableResourceTests
{
    [TestMethod]
    public void Dispose_WithActiveLease_DefersResourceDisposalUntilLeaseReleased()
    {
        var resource = new TestResource();
        var lifetime = new SharedDisposableResource<TestResource>(resource);
        var lease = lifetime.TryAcquire();
        Assert.IsNotNull(lease);

        lifetime.Dispose();

        Assert.IsFalse(resource.IsDisposed);
        Assert.IsNull(lifetime.TryAcquire());

        lease.Dispose();

        Assert.IsTrue(resource.IsDisposed);
        Assert.AreEqual(1, resource.DisposeCount);
    }

    [TestMethod]
    public void Dispose_WithoutActiveLease_DisposesResourceOnce()
    {
        var resource = new TestResource();
        var lifetime = new SharedDisposableResource<TestResource>(resource);

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.IsTrue(resource.IsDisposed);
        Assert.AreEqual(1, resource.DisposeCount);
        Assert.IsNull(lifetime.TryAcquire());
    }

    [TestMethod]
    public void Lease_DisposeTwice_ReleasesOnlyOnce()
    {
        var resource = new TestResource();
        var lifetime = new SharedDisposableResource<TestResource>(resource);
        var lease = lifetime.TryAcquire();
        Assert.IsNotNull(lease);

        lifetime.Dispose();
        lease.Dispose();
        lease.Dispose();

        Assert.AreEqual(1, resource.DisposeCount);
    }

    private sealed class TestResource : IDisposable
    {
        public int DisposeCount { get; private set; }
        public bool IsDisposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }
}
