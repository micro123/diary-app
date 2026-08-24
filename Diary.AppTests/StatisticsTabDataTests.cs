using Avalonia;
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
            Assert.AreEqual(2, viewModel.PieChart.Series.Count());
        }, CancellationToken.None);
    }

    private static StatisticsResult CreateResult(double total) => new()
    {
        DateBegin = "2026-08-01",
        DateEnd = "2026-08-22",
        Total = total,
        PrimaryTags = [],
    };

    private sealed class TestApplication : Application;
}
