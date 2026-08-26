using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Diary.App.Controls;

public partial class ClockTimePicker : UserControl
{
    private const double FaceCenter = 110;
    private const double MarkerRadius = 88;
    private ClockSelectionStep _step = ClockSelectionStep.Hour;
    private int _pendingHour;
    private int _pendingMinute;
    private bool _isDragging;
    private bool _isInitialized;

    public static readonly StyledProperty<TimeSpan?> SelectedTimeProperty =
        AvaloniaProperty.Register<ClockTimePicker, TimeSpan?>(
            nameof(SelectedTime),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<ClockTimePicker, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<ClockTimePicker, string>(nameof(Watermark), "未填写");

    public TimeSpan? SelectedTime
    {
        get => GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public ClockTimePicker()
    {
        InitializeComponent();
        _isInitialized = true;
        UpdateDisplay();
        UpdateReadOnlyState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_isInitialized)
            return;

        if (change.Property == SelectedTimeProperty || change.Property == WatermarkProperty)
            UpdateDisplay();
        else if (change.Property == IsReadOnlyProperty)
            UpdateReadOnlyState();
    }

    private void OnFlyoutOpened(object? sender, EventArgs e)
    {
        var initial = SelectedTime ?? DateTime.Now.TimeOfDay;
        _pendingHour = Normalize(initial.Hours, 24);
        _pendingMinute = Normalize(initial.Minutes, 60);
        SetStep(ClockSelectionStep.Hour);
        ClockTimePickerFace.Focus();
    }

    private void OnHourStepClick(object? sender, RoutedEventArgs e)
        => SetStep(ClockSelectionStep.Hour);

    private void OnMinuteStepClick(object? sender, RoutedEventArgs e)
        => SetStep(ClockSelectionStep.Minute);

    private void OnClockFacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ClockTimePickerFace).Properties.IsLeftButtonPressed)
            return;

        _isDragging = true;
        e.Pointer.Capture(ClockTimePickerFace);
        ClockTimePickerFace.Focus();
        UpdatePendingValue(e.GetPosition(ClockTimePickerFace));
        e.Handled = true;
    }

    private void OnClockFacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
            return;

        UpdatePendingValue(e.GetPosition(ClockTimePickerFace));
        e.Handled = true;
    }

    private void OnClockFacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        e.Pointer.Capture(null);
        UpdatePendingValue(e.GetPosition(ClockTimePickerFace));
        if (_step == ClockSelectionStep.Hour)
            SetStep(ClockSelectionStep.Minute);
        else
            CommitSelection();
        e.Handled = true;
    }

    private void OnClockFaceKeyDown(object? sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left or Key.Down => -1,
            Key.Right or Key.Up => 1,
            Key.PageDown => -5,
            Key.PageUp => 5,
            _ => 0,
        };
        if (delta != 0)
        {
            if (_step == ClockSelectionStep.Hour)
                _pendingHour = Normalize(_pendingHour + delta, 24);
            else
                _pendingMinute = Normalize(_pendingMinute + delta, 60);
            RefreshFace();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_step == ClockSelectionStep.Hour)
                SetStep(ClockSelectionStep.Minute);
            else
                CommitSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClockTimePickerButton.Flyout?.Hide();
            e.Handled = true;
        }
    }


    private void CommitSelection()
    {
        SelectedTime = new TimeSpan(_pendingHour, _pendingMinute, 0);
        ClockTimePickerButton.Flyout?.Hide();
    }

    private void SetStep(ClockSelectionStep step)
    {
        _step = step;
        ClockTimePickerStepHint.Text = step == ClockSelectionStep.Hour
            ? "选择小时（00–23）"
            : "选择分钟（00–59）";
        ClockTimePickerInteractionHint.Text = step == ClockSelectionStep.Hour
            ? "拖动选择，松开后进入分钟"
            : "拖动选择，松开后立即应用";
        RefreshFace();
    }

    private void UpdatePendingValue(Point point)
    {
        var count = _step == ClockSelectionStep.Hour ? 24 : 60;
        var angle = Math.Atan2(point.Y - FaceCenter, point.X - FaceCenter) + (Math.PI / 2);
        if (angle < 0)
            angle += Math.PI * 2;
        var value = Normalize((int)Math.Round(angle / (Math.PI * 2) * count), count);
        if (_step == ClockSelectionStep.Hour)
            _pendingHour = value;
        else
            _pendingMinute = value;
        RefreshFace();
    }

    private void RefreshFace()
    {
        ClockTimePickerHourText.Text = _pendingHour.ToString("00", CultureInfo.InvariantCulture);
        ClockTimePickerMinuteText.Text = _pendingMinute.ToString("00", CultureInfo.InvariantCulture);
        ClockTimePickerHourStepButton.Classes.Set("Primary", _step == ClockSelectionStep.Hour);
        ClockTimePickerMinuteStepButton.Classes.Set("Primary", _step == ClockSelectionStep.Minute);

        var count = _step == ClockSelectionStep.Hour ? 24 : 60;
        var selected = _step == ClockSelectionStep.Hour ? _pendingHour : _pendingMinute;
        ClockTimePickerMarkers.Children.Clear();
        for (var value = 0; value < count; value++)
            AddMarker(value, count, selected);

        var selectedPoint = PointOnRing(selected, count, MarkerRadius);
        var markerSize = _step == ClockSelectionStep.Hour ? 24d : 28d;
        ClockTimePickerSelectedMarker.Width = markerSize;
        ClockTimePickerSelectedMarker.Height = markerSize;
        Canvas.SetLeft(ClockTimePickerSelectedMarker, selectedPoint.X - (markerSize / 2));
        Canvas.SetTop(ClockTimePickerSelectedMarker, selectedPoint.Y - (markerSize / 2));
        ClockTimePickerHand.EndPoint = selectedPoint;
    }

    private void AddMarker(int value, int count, int selected)
    {
        var point = PointOnRing(value, count, MarkerRadius);
        var showLabel = value == selected || IsReferenceLabel(value, count);
        if (!showLabel)
        {
            var dot = new Ellipse
            {
                Width = 2.5,
                Height = 2.5,
                Fill = value == selected ? Brushes.White : new SolidColorBrush(Color.Parse("#7A7A7A")),
            };
            Canvas.SetLeft(dot, point.X - 1.25);
            Canvas.SetTop(dot, point.Y - 1.25);
            ClockTimePickerMarkers.Children.Add(dot);
            return;
        }

        var markerSize = _step == ClockSelectionStep.Hour ? 22d : 28d;
        var label = new TextBlock
        {
            Text = value.ToString("00", CultureInfo.InvariantCulture),
            FontSize = _step == ClockSelectionStep.Hour ? 9 : 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = value == selected ? Brushes.White : Foreground ?? Brushes.Gray,
        };
        var container = new Border
        {
            Width = markerSize,
            Height = markerSize,
            CornerRadius = new CornerRadius(markerSize / 2),
            Child = label,
        };
        Canvas.SetLeft(container, point.X - (markerSize / 2));
        Canvas.SetTop(container, point.Y - (markerSize / 2));
        ClockTimePickerMarkers.Children.Add(container);
    }

    private void UpdateDisplay()
    {
        ClockTimePickerDisplayText.Text = SelectedTime is { } time
            ? $"{Normalize(time.Hours, 24):00}:{Normalize(time.Minutes, 60):00}"
            : Watermark;
        ClockTimePickerDisplayText.Opacity = SelectedTime.HasValue ? 1 : 0.62;
    }

    private void UpdateReadOnlyState()
        => ClockTimePickerButton.IsEnabled = !IsReadOnly;

    private static bool IsReferenceLabel(int value, int count)
        => count == 24 ? value % 3 == 0 : value % 5 == 0;
    private static Point PointOnRing(int value, int count, double radius)
    {
        var angle = (value / (double)count * Math.PI * 2) - (Math.PI / 2);
        return new Point(
            FaceCenter + (Math.Cos(angle) * radius),
            FaceCenter + (Math.Sin(angle) * radius));
    }
    private static int Normalize(int value, int modulus)
        => ((value % modulus) + modulus) % modulus;

    private enum ClockSelectionStep
    {
        Hour,
        Minute,
    }
}