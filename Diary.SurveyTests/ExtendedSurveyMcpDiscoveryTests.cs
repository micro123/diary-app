using System.Net;
using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
public sealed class ExtendedSurveyMcpDiscoveryTests
{
    [TestMethod]
    public void McpServiceRequestAndResponseUseStableContract()
    {
        var request = ExtendedSurveyProtocol.SerializeMcpServicesRequest("request-1");
        Assert.IsTrue(ExtendedSurveyProtocol.TryDeserializeRequest(request, out var parsed));
        Assert.AreEqual(ExtendedSurveyProtocol.McpServicesKind, parsed?.Kind);

        var response = ExtendedSurveyProtocol.SerializeMcpServicesSuccess(
            "request-1",
            "host",
            "user",
            "instance-1",
            [CreateService()]);
        Assert.IsTrue(ExtendedSurveyProtocol.TryDeserializeMcpServicesResponse(
            response,
            out var envelope,
            out var data));
        Assert.AreEqual("request-1", envelope?.RequestId);
        Assert.AreEqual("instance-1", data?.InstanceId);
        Assert.AreEqual("diary.readonly", data?.Services.Single().ServiceId);
        CollectionAssert.Contains(data?.Services.Single().Capabilities.ToArray(), "diary_query_work_items");
    }

    [TestMethod]
    public void RegistryRegistrationCanBeReplacedAndDisposedSafely()
    {
        var registry = new ExtendedSurveyServiceRegistry();
        using var first = registry.Register(CreateService("first"));
        using var second = registry.Register(CreateService("second"));

        first.Dispose();
        Assert.AreEqual("second", registry.Snapshot().Single().DisplayName);
        second.Dispose();
        Assert.IsEmpty(registry.Snapshot());
    }

    [TestMethod]
    public void DirectoryRefreshesTtlWithoutPublishingFalseChangeAndPrunesExpiredService()
    {
        var directory = new ExtendedSurveyServiceDirectory();
        var changes = 0;
        directory.Changed += (_, _) => changes++;
        var now = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
        var response = new ExtendedSurveyMcpServices
        {
            InstanceId = "instance-1",
            Hostname = "host",
            Username = "user",
            Services = [CreateService()],
        };

        directory.Apply(response, now);
        directory.Apply(response, now.AddSeconds(30));
        Assert.AreEqual(1, changes);
        Assert.AreEqual(now.AddSeconds(120), directory.Snapshot().Single().ExpiresAtUtc);

        Assert.AreEqual(1, directory.Prune(now.AddSeconds(121)));
        Assert.AreEqual(2, changes);
        Assert.IsEmpty(directory.Snapshot());
    }

    [TestMethod]
    public void PeerPolicyAllowsLoopbackLocalAddressAndConfiguredInvestigatorOnly()
    {
        var local = IPAddress.Parse("192.168.10.20");
        var policy = new SurveyMcpPeerAccessPolicy(
            () => "192.168.10.10",
            () => [local]);

        Assert.IsTrue(policy.IsAllowed(IPAddress.Loopback));
        Assert.IsTrue(policy.IsAllowed(local));
        Assert.IsTrue(policy.IsAllowed(IPAddress.Parse("192.168.10.10")));
        Assert.IsFalse(policy.IsAllowed(IPAddress.Parse("192.168.10.99")));
        Assert.IsFalse(policy.IsAllowed(null));
    }

    private static ExtendedSurveyMcpService CreateService(string displayName = "DiaryApp 只读数据") => new()
    {
        ServiceId = "diary.readonly",
        DisplayName = displayName,
        Endpoints = ["http://192.168.10.20:12345/mcp"],
        Capabilities = ["diary_query_work_items"],
        StartedAt = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero),
        ExpiresInSeconds = 90,
    };
}
