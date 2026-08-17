using Diary.App.ViewModels;
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
