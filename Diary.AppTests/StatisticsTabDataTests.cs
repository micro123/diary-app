using Avalonia.Headless;
using Diary.App.Models;
using Diary.Core.Data.Statistics;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class StatisticsTabDataTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
    }

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public async Task ConcurrentRefresh_DoesNotLetOlderRequestOverwriteLatestResult()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var callCount = 0;

        await _session.Dispatch(async () =>
        {
            var viewModel = new StatisticsTabData(
                StatisticsType.Custom,
                (_, _) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    if (call == 1)
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    }
                    return CreateResult(call);
                },
                loadImmediately: false);

            var first = viewModel.RefreshAsync();
            Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            var second = viewModel.RefreshAsync();
            releaseFirst.Set();

            await Task.WhenAll(first, second);

            Assert.AreEqual(2.0, viewModel.StatisticsTotal);
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task ChartMode_DefaultsToBar_AndCanSwitchToPie()
    {
        await _session.Dispatch(async () =>
        {
            var viewModel = new StatisticsTabData(
                StatisticsType.Custom,
                (_, _) => new StatisticsResult
                {
                    DateBegin = "2026-08-01",
                    DateEnd = "2026-08-22",
                    Total = 3,
                    PrimaryTags =
                    [
                        new TagTime { TagId = 1, TagName = "开发", Time = 2 },
                        new TagTime { TagId = 2, TagName = "会议", Time = 1 },
                    ],
                },
                loadImmediately: false);

            Assert.IsFalse(viewModel.IsPieChart);

            await viewModel.RefreshAsync();

            viewModel.IsPieChart = true;

            Assert.IsTrue(viewModel.IsPieChart);
            Assert.IsNotNull(viewModel.PieChart);
            Assert.AreEqual(2, viewModel.PieChart.Series.Count());
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task Initialization_IsLazyAndRunsOnlyOnce()
    {
        var callCount = 0;
        await _session.Dispatch(async () =>
        {
            var viewModel = new StatisticsTabData(
                StatisticsType.Custom,
                (_, _) =>
                {
                    Interlocked.Increment(ref callCount);
                    return CreateResult(4);
                },
                loadImmediately: false);

            Assert.IsFalse(viewModel.IsInitialized);
            Assert.IsNull(viewModel.Chart);
            Assert.IsNull(viewModel.PieChart);
            Assert.IsNull(viewModel.TimeDetails);
            Assert.AreEqual(0, callCount);

            await viewModel.EnsureInitializedAsync();
            await viewModel.EnsureInitializedAsync();

            Assert.IsTrue(viewModel.IsInitialized);
            Assert.IsNotNull(viewModel.Chart);
            Assert.IsNotNull(viewModel.PieChart);
            Assert.IsNotNull(viewModel.TimeDetails);
            Assert.AreEqual(1, callCount);
            Assert.AreEqual(4.0, viewModel.StatisticsTotal);
        }, CancellationToken.None);
    }

    private static StatisticsResult CreateResult(double total) => new()
    {
        DateBegin = "2026-08-01",
        DateEnd = "2026-08-22",
        Total = total,
        PrimaryTags = [],
    };

    private sealed class TestApplication : TestBaseApplication;
}
