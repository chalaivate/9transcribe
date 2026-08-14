using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NineTranscribe.Settings;

namespace NineTranscribe.Overlay;

public partial class OverlayWindow : Window
{
    private static readonly double[] BarThresholds = { 0.08, 0.22, 0.40, 0.60, 0.80 };
    private const double BarMinHeight = 4;
    private const double BarMaxHeight = 18;

    private readonly OverlayViewModel _viewModel;
    private readonly Storyboard _pulse;
    private readonly Storyboard _spin;
    private readonly Rectangle[] _bars;

    private IntPtr _monitor;
    private bool _placing;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        _pulse = BuildPulse();
        _spin = BuildSpin();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ContentChanged += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    public OverlayViewModel ViewModel => _viewModel;

    public OverlayPosition Anchor { get; set; } = OverlayPosition.Bottom;

    /// <summary>
    /// Locks the overlay to the monitor holding the window the user is typing into. Called once
    /// when recording starts, so the pill does not chase focus mid-dictation.
    /// </summary>
    public void AttachToMonitorOfForegroundWindow()
    {
        _monitor = OverlayPositioner.MonitorForForegroundWindow();
        PreviewTextBlock.MaxWidth = OverlayPositioner.MaxContentWidthDip(_monitor);
    }

    public void Reposition()
    {
        if (_placing || !IsLoaded)
        {
            return;
        }

        _placing = true;
        try
        {
            // ActualWidth is stale until layout runs, and the pill resizes on every state change.
            UpdateLayout();
            OverlayPositioner.Place(this, Anchor, _monitor);
        }
        finally
        {
            _placing = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr current = WindowNative.GetWindowLongPtr(hwnd, WindowNative.GwlExStyle);
        // NoActivate is load-bearing: if this window ever takes focus, the paste lands here
        // instead of in the user's editor. ToolWindow keeps it out of Alt+Tab, and Transparent
        // makes it click-through so it never swallows a click on what is underneath.
        var style = (uint)current.ToInt64()
            | WindowNative.WsExNoActivate
            | WindowNative.WsExToolWindow
            | WindowNative.WsExTransparent;
        WindowNative.SetWindowLongPtr(hwnd, WindowNative.GwlExStyle, new IntPtr(style));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Reposition();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.State):
                ApplyState();
                break;

            case nameof(OverlayViewModel.Level):
                ApplyLevel();
                break;
        }
    }

    private void ApplyState()
    {
        ListeningPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        PreviewTextBlock.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        _pulse.Stop(this);
        _spin.Stop(this);

        switch (_viewModel.State)
        {
            case OverlayState.Listening:
                ListeningText.Text = _viewModel.StatusText;
                ListeningSegmentText.Text = _viewModel.PreviewText;
                ListeningSegmentText.Visibility = _viewModel.PreviewText.Length == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ListeningPanel.Visibility = Visibility.Visible;
                Pill.Background = (Brush)Resources["PillBackground"];
                _pulse.Begin(this, true);
                ShowPill();
                break;

            case OverlayState.Processing:
                ProcessingText.Text = _viewModel.StatusText;
                ProcessingPanel.Visibility = Visibility.Visible;
                Pill.Background = (Brush)Resources["PillBackground"];
                _spin.Begin(this, true);
                ShowPill();
                break;

            case OverlayState.Preview:
                PreviewTextBlock.Text = _viewModel.PreviewText;
                PreviewTextBlock.Visibility = Visibility.Visible;
                Pill.Background = (Brush)Resources["PillBackground"];
                ShowPill();
                break;

            case OverlayState.Error:
            case OverlayState.Notice:
                MessageGlyph.Text = _viewModel.State == OverlayState.Error ? "!" : "•";
                MessageText.Text = _viewModel.StatusText;
                MessagePanel.Visibility = Visibility.Visible;
                Pill.Background = _viewModel.State == OverlayState.Error
                    ? (Brush)Resources["PillErrorBackground"]
                    : (Brush)Resources["PillBackground"];
                ShowPill();
                break;

            default:
                HidePill();
                break;
        }
    }

    private void ApplyLevel()
    {
        double level = _viewModel.Level;
        for (int i = 0; i < _bars.Length; i++)
        {
            double reach = Math.Clamp((level - BarThresholds[i]) / 0.2, 0, 1);
            _bars[i].Height = BarMinHeight + ((BarMaxHeight - BarMinHeight) * reach);
        }
    }

    private void ShowPill()
    {
        // Other topmost windows created after ours drift above it over time.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowNative.SetWindowPos(
                hwnd,
                WindowNative.HwndTopmost,
                0,
                0,
                0,
                0,
                WindowNative.SwpNoMove | WindowNative.SwpNoSize | WindowNative.SwpNoActivate);
        }

        Reposition();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private void HidePill()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private Storyboard BuildPulse()
    {
        var grow = new DoubleAnimation(1.0, 1.25, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        var storyboard = new Storyboard();
        foreach (DependencyProperty property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            DoubleAnimation animation = grow.Clone();
            Storyboard.SetTarget(animation, RecordDotScale);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            storyboard.Children.Add(animation);
        }

        return storyboard;
    }

    private Storyboard BuildSpin()
    {
        var rotate = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(rotate, SpinnerRotation);
        Storyboard.SetTargetProperty(rotate, new PropertyPath(RotateTransform.AngleProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(rotate);
        return storyboard;
    }
}
