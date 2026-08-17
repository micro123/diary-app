using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.Survey;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class SurveyViewModelTests
{
    [TestMethod]
    public void QueryModeIndex_ControlsExtendedQueryEligibilityAndDescription()
    {
        var viewModel = new SurveyViewModel(NullLogger.Instance, EmptyServiceProvider.Instance);

        Assert.IsFalse(viewModel.IsExtendedQuery);
        StringAssert.Contains(viewModel.QueryModeDescription, "9721");
        StringAssert.Contains(viewModel.QueryModes[0], "兼容查询");

        viewModel.QueryModeIndex = 1;

        Assert.IsTrue(viewModel.IsExtendedQuery);
        StringAssert.Contains(viewModel.QueryModeDescription, "9722");
        StringAssert.Contains(viewModel.QueryModes[1], "扩展查询");
    }

    [TestMethod]
    public void CapabilityResult_UsesUserFacingChineseLabels()
    {
        var result = new SurveyCapabilityResult(new ExtendedSurveyCapabilities
        {
            Hostname = "network",
            Username = "tang",
            Kinds =
            [
                ExtendedSurveyProtocol.CapabilitiesKind,
                ExtendedSurveyProtocol.CustomStatisticsKind,
            ],
            GroupDimensions =
            [
                ExtendedSurveyProtocol.GroupByTag,
                ExtendedSurveyProtocol.GroupByDate,
                ExtendedSurveyProtocol.GroupByPriority,
            ],
            SupportsDetails = true,
        });

        Assert.AreEqual("tang@network", result.NodeName);
        Assert.AreEqual("能力发现、扩展统计", result.KindsText);
        Assert.AreEqual("标签、日期、优先级", result.GroupDimensionsText);
        Assert.AreEqual("支持明细", result.DetailsText);

        var page = new SurveyViewModel(NullLogger.Instance, EmptyServiceProvider.Instance);
        Assert.IsFalse(page.CanViewCapabilities);
        page.PeerCapabilities.Add(result);
        Assert.IsTrue(page.CanViewCapabilities);

        var dialog = new SurveyCapabilitiesViewModel(page.PeerCapabilities, "已发现 1 个新版节点");
        Assert.IsFalse(dialog.IsEmpty);
        Assert.AreEqual("已发现 1 个新版节点", dialog.Status);
        Assert.AreEqual("tang@network", dialog.Capabilities.Single().NodeName);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
