using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NineTranscribe.Settings;

namespace NineTranscribe.Overlay;

public partial class OverlayWindow : Window
{
    private const int BarCount = 24;
    private const double BarWidth = 2.5;
    private const double BarGap = 1.5;
    private const double BarMinHeight = 3;
    private const double BarMaxHeight = 22;

    /// <summary>How far the pill travels during its entrance, in device-independent pixels.</summary>
    private const double EntranceDistance = 12;

    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RingRevolution = TimeSpan.FromSeconds(3);

    private readonly OverlayViewModel _viewModel;
    private readonly Storyboard _pulse;
    private readonly Storyboard _spin;
    private readonly DispatcherTimer _waveTimer;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _barWeights = new double[BarCount];
    private readonly double[] _barPhases = new double[BarCount];
    private readonly double[] _barHeights = new double[BarCount];
    private readonly RotateTransform _ringRotation = new(0, 0.5, 0.5);
    private readonly LinearGradientBrush _accentRing;
    private readonly LinearGradientBrush _errorRing;
    private readonly Color _foreground;
    private readonly Color _foregroundDim;

    private IntPtr _monitor;
    private bool _placing;
    private bool _visible;
    private double _level;
    private double _waveClock;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _foreground = ((SolidColorBrush)FindResource("PillForeground")).Color;
        _foregroundDim = ((SolidColorBrush)FindResource("PillForegroundDim")).Color;

        _accentRing = BuildRing(
            Color.FromRgb(0x7C, 0x3A, 0xED),
            Color.FromRgb(0xEC, 0x48, 0x99),
            Color.FromRgb(0xF9, 0x73, 0x16),
            Color.FromRgb(0x7C, 0x3A, 0xED));
        _errorRing = BuildRing(
            Color.FromRgb(0xF8, 0x71, 0x71),
            Color.FromRgb(0xEF, 0x44, 0x44),
            Color.FromRgb(0xB9, 0x1C, 0x1C),
            Color.FromRgb(0xF8, 0x71, 0x71));
        Ring.BorderBrush = _accentRing;

        BuildWaveform();
        _pulse = BuildPulse();
        _spin = BuildSpin();

        _waveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _waveTimer.Tick += (_, _) => StepWaveform();

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
        double maxWidth = OverlayPositioner.MaxContentWidthDip(_monitor);
        PreviewTextBlock.MaxWidth = maxWidth;
        ListeningSegmentText.MaxWidth = maxWidth * 0.55;
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
                _level = _viewModel.Level;
                break;

            case nameof(OverlayViewModel.PreviewText):
                // A sentence typed mid-session arrives as a text change with no state change.
                if (_viewModel.State == OverlayState.Listening)
                {
                    ApplySegmentText();
                }

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
        _waveTimer.Stop();
        _ringRotation.BeginAnimation(RotateTransform.AngleProperty, null);

        switch (_viewModel.State)
        {
            case OverlayState.Listening:
                ListeningText.Text = _viewModel.StatusText;
                ApplySegmentText();
                ListeningPanel.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 1.0, error: false);
                ResetWaveform();
                _pulse.Begin(this, true);
                SpinRing();
                _waveTimer.Start();
                ShowPill();
                break;

            case OverlayState.Processing:
                ProcessingText.Text = _viewModel.StatusText;
                ProcessingPanel.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 0.7, error: false);
                _spin.Begin(this, true);
                ShowPill();
                break;

            case OverlayState.Preview:
                PreviewTextBlock.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 0.7, error: false);
                Reveal(PreviewTextBlock, _viewModel.PreviewText, _foreground, staggerMs: 18);
                ShowPill();
                break;

            case OverlayState.Error:
            case OverlayState.Notice:
                bool error = _viewModel.State == OverlayState.Error;
                MessageGlyph.Text = error ? "!" : "•";
                MessageBadge.Background = (Brush)FindResource(error ? "ErrorGlyphBrush" : "NoticeGlyphBrush");
                MessageText.Text = _viewModel.StatusText;
                MessagePanel.Visibility = Visibility.Visible;
                SetChrome(error ? _errorRing : _accentRing, ringOpacity: error ? 1.0 : 0.5, error);
                ShowPill();
                break;

            default:
                HidePill();
                break;
        }
    }

    private void ApplySegmentText()
    {
        string text = _viewModel.PreviewText;
        if (text.Length == 0)
        {
            ListeningSegmentText.Inlines.Clear();
            ListeningSegmentText.Visibility = Visibility.Collapsed;
            return;
        }

        ListeningSegmentText.Visibility = Visibility.Visible;
        Reveal(ListeningSegmentText, text, _foregroundDim, staggerMs: 10);
    }

    private void SetChrome(Brush ring, double ringOpacity, bool error)
    {
        Ring.BorderBrush = ring;
        Ring.Opacity = ringOpacity;
        Pill.Background = (Brush)FindResource(error ? "PillErrorGlassBrush" : "PillGlassBrush");
    }

    /// <summary>
    /// Fills a text block with the text as a sequence of small inline pieces that fade in one
    /// after another. Pieces are cut on grapheme clusters, never inside a Thai syllable, so a
    /// vowel or tone mark can never appear before the consonant it sits on.
    /// </summary>
    private static void Reveal(TextBlock target, string text, Color color, int staggerMs)
    {
        target.Inlines.Clear();
        IReadOnlyList<string> chunks = TextReveal.Chunks(text);
        if (chunks.Count == 0)
        {
            return;
        }

        // Long text still finishes appearing quickly; the stagger shrinks to fit the cap.
        double stagger = Math.Min(staggerMs, TextReveal.MaxRevealMs / (double)chunks.Count);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < chunks.Count; i++)
        {
            var brush = new SolidColorBrush(color) { Opacity = 0 };
            target.Inlines.Add(new Run(chunks[i]) { Foreground = brush });

            var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * stagger),
                EasingFunction = ease,
            };
            brush.BeginAnimation(Brush.OpacityProperty, fade);
        }
    }

    private void BuildWaveform()
    {
        var fill = (Brush)FindResource("WaveBarBrush");
        double centre = (BarCount - 1) / 2.0;

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = BarMinHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = fill,
            };
            Canvas.SetLeft(bar, i * (BarWidth + BarGap));
            Canvas.SetTop(bar, (BarMaxHeight - BarMinHeight) / 2);
            Waveform.Children.Add(bar);
            _bars[i] = bar;
            _barHeights[i] = BarMinHeight;

            // A bell curve keeps the middle bars tallest so the shape reads as a waveform rather
            // than a row of equal sticks; the phase offsets stop neighbours moving in lockstep.
            double distance = (i - centre) / 6.5;
            _barWeights[i] = 0.3 + (0.7 * Math.Exp(-distance * distance));
            _barPhases[i] = i * 0.9;
        }
    }

    private void ResetWaveform()
    {
        for (int i = 0; i < BarCount; i++)
        {
            _barHeights[i] = BarMinHeight;
            _bars[i].Height = BarMinHeight;
            Canvas.SetTop(_bars[i], (BarMaxHeight - BarMinHeight) / 2);
        }
    }

    /// <summary>
    /// One animation frame of the waveform. Heights are eased towards their targets by hand
    /// rather than through animation clocks: twenty-four clocks restarted thirty times a
    /// second is churn for nothing.
    /// </summary>
    private void StepWaveform()
    {
        _waveClock += 0.033;
        double level = _level;

        for (int i = 0; i < BarCount; i++)
        {
            // A faint ripple keeps the bars alive in silence; a quicker wobble makes speech look
            // like speech instead of a meter needle.
            double ripple = 0.10 * (0.5 + (0.5 * Math.Sin((_waveClock * 2.2) + _barPhases[i])));
            double wobble = 0.78 + (0.22 * Math.Sin((_waveClock * 9.0) + (_barPhases[i] * 1.7)));
            double drive = Math.Clamp((level * _barWeights[i] * wobble) + (ripple * _barWeights[i]), 0, 1);
            double target = BarMinHeight + ((BarMaxHeight - BarMinHeight) * drive);

            double height = _barHeights[i] + ((target - _barHeights[i]) * 0.38);
            _barHeights[i] = height;
            _bars[i].Height = height;
            Canvas.SetTop(_bars[i], (BarMaxHeight - height) / 2);
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

        bool entering = !_visible;
        _visible = true;

        if (!entering)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }

        // Slide in from the edge the pill is anchored to, growing slightly as it arrives.
        (double dx, double dy) = OverlayPositioner.EntranceOffset(Anchor, EntranceDistance);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        EntranceSlide.BeginAnimation(TranslateTransform.XProperty, Ease(dx, 0, EntranceDuration, ease));
        EntranceSlide.BeginAnimation(TranslateTransform.YProperty, Ease(dy, 0, EntranceDuration, ease));
        EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(0.96, 1, EntranceDuration, ease));
        EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(0.96, 1, EntranceDuration, ease));
        BeginAnimation(OpacityProperty, Ease(0, 1, EntranceDuration, ease));
    }

    private void HidePill()
    {
        _visible = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fade = new DoubleAnimation(0, ExitDuration)
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = ease,
        };
        BeginAnimation(OpacityProperty, fade);
        EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(1, 0.97, ExitDuration, ease));
        EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(1, 0.97, ExitDuration, ease));
    }

    private void SpinRing()
    {
        var spin = new DoubleAnimation(0, 360, RingRevolution)
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _ringRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    private LinearGradientBrush BuildRing(params Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _ringRotation,
        };

        for (int i = 0; i < colors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Length - 1)));
        }

        return brush;
    }

    private static DoubleAnimation Ease(double from, double to, TimeSpan duration, IEasingFunction ease) =>
        new(from, to, duration) { EasingFunction = ease };

    private Storyboard BuildPulse()
    {
        var storyboard = new Storyboard();

        // The dot breathes; the halo behind it ripples outwards and fades, like a sonar ping.
        var breathe = new DoubleAnimation(1.0, 1.18, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        foreach (DependencyProperty property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            DoubleAnimation animation = breathe.Clone();
            Storyboard.SetTarget(animation, RecordDotScale);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            storyboard.Children.Add(animation);
        }

        var ping = new DoubleAnimation(1.0, 1.9, TimeSpan.FromMilliseconds(1400))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        foreach (DependencyProperty property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            DoubleAnimation animation = ping.Clone();
            Storyboard.SetTarget(animation, RecordHaloScale);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            storyboard.Children.Add(animation);
        }

        var fadeHalo = new DoubleAnimation(0.45, 0.0, TimeSpan.FromMilliseconds(1400))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(fadeHalo, RecordHalo);
        Storyboard.SetTargetProperty(fadeHalo, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(fadeHalo);

        return storyboard;
    }

    private Storyboard BuildSpin()
    {
        var rotate = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1000))
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
