using Avalonia.Controls;
using Avalonia.Headless;
using Diary.App.Controls;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class ClockTimePickerTests
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
    public Task SelectedTimeUsesCompactTwentyFourHourDisplay() => _session.Dispatch(() =>
    {
        var picker = new ClockTimePicker
        {
            SelectedTime = new TimeSpan(9, 5, 47),
        };

        Assert.AreEqual(
            "09:05",
            picker.FindControl<TextBlock>("ClockTimePickerDisplayText")?.Text);
    }, CancellationToken.None);

    [TestMethod]
    public Task EmptyAndReadOnlyStatesUpdateTheButton() => _session.Dispatch(() =>
    {
        var picker = new ClockTimePicker
        {
            Watermark = "不设置默认值",
            IsReadOnly = true,
        };

        Assert.AreEqual(
            "不设置默认值",
            picker.FindControl<TextBlock>("ClockTimePickerDisplayText")?.Text);
        Assert.IsFalse(picker.FindControl<Button>("ClockTimePickerButton")?.IsEnabled);
    }, CancellationToken.None);

    private sealed class TestApplication : TestBaseApplication;
}