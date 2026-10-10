using Diary.App.Services;

namespace Diary.AppTests;

[TestClass]
public sealed class McpServiceDiscoveryCoordinatorTests
{
    [DataRow("workstation-01", "workstation-01", true)]
    [DataRow("WORKSTATION-01", "workstation-01", true)]
    [DataRow(" workstation-01 ", "workstation-01", true)]
    [DataRow("workstation-02", "workstation-01", false)]
    [DataRow("", "workstation-01", false)]
    [TestMethod]
    public void IsLocalHostMatchesHostnameWithoutCaseSensitivity(
        string responseHostname,
        string localHostname,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            McpServiceDiscoveryCoordinator.IsLocalHost(responseHostname, localHostname));
    }
}
