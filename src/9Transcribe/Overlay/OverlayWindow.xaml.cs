using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using NineTranscribe.Settings;
using NineTranscribe.UI;

namespace NineTranscribe.Overlay;

public partial class OverlayWindow : Window
{
    // The waveform is three travelling sine curves under a sin² envelope, so both ends of
    // every curve sit on the centre line and the shape fades into the glass. Each curve has
    // its own wavelength, speed and phase; two run left-to-right and one the other way.
    private const int WavePoints = 56;
    private const double WaveWidth = 150;
    private const double WaveHeight = 30;
    private const double WaveIdleAmplitude = 1.4;
    private const double WaveMaxAmplitude = 12.5;
    private const double WaveTick = 0.033;
    private static readonly double[] WaveFrequency = { 1.0, 1.45, 0.72 };
    private static readonly double[] WaveSpeed = { 2.6, -3.3, 1.8 };
    private static readonly double[] WavePhase = { 0.0, 1.3, 2.7 };
    private static readonly double[] WaveScale = { 1.0, 0.72, 0.5 };
    private static readonly double[] WaveThickness = { 2.4, 1.7, 1.2 };
    private static readonly double[] WaveOpacity = { 1.0, 0.75, 0.5 };

    /// <summary>How far the overlay rises during its entrance, in device-independent pixels.</summary>
    private const double EntranceRise = 10;

    /// <summary>Thai stacks vowels and tone marks above the consonant; anything tighter clips them.</summary>
    private const double TranscriptLineHeightFactor = 1.6;

    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RingRevolution = TimeSpan.FromSeconds(3);

    private readonly OverlayViewModel _viewModel;
    private readonly Storyboard _pulse;
    private readonly Storyboard _spin;
    private readonly DispatcherTimer _waveTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly Polyline[] _waves = new Polyline[WaveFrequency.Length];
    private readonly Polyline _waveGlow;
    private readonly RotateTransform _ringRotation = new(0, 0.5, 0.5);
    private readonly LinearGradientBrush _accentRing;
    private readonly LinearGradientBrush _errorRing;
    private readonly DropShadowEffect _textHalo;

    private IntPtr _monitor;
    private bool _placing;
    private bool _visible;
    private double _level;
    private double _smoothedLevel;
    private double _waveClock;

    private int _baselinePercent = 90;
    private bool _showTranscript = true;
    private Color _transcriptColor = Colors.White;
    private string _transcriptShown = string.Empty;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

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

        // A soft dark halo around the letters keeps floating text legible on a white document.
        _textHalo = new DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.85,
            Color = Colors.Black,
        };

        _waveGlow = BuildWaveform();
        _pulse = BuildPulse();
        _spin = BuildSpin();

        _waveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _waveTimer.Tick += (_, _) => StepWaveform();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clockTimer.Tick += (_, _) => UpdateElapsed();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ContentChanged += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    public OverlayViewModel ViewModel => _viewModel;

    /// <summary>
    /// Applies the overlay-related settings: where the guide line is and how the transcript
    /// looks. Safe to call at any time; text already on screen is restyled in place.
    /// </summary>
    public void ApplyStyle(AppSettings settings)
    {
        TranscriptStyle style = settings.Transcript;

        _baselinePercent = settings.OverlayBaselinePercent;
        _showTranscript = style.Show;
        _transcriptColor = ColorHex.Parse(style.TextColor, Colors.White);

        TranscriptText.FontSize = style.FontSize;
        TranscriptText.LineHeight = Math.Round(style.FontSize * TranscriptLineHeightFactor);
        TranscriptText.MaxHeight = TranscriptText.LineHeight * 4;

        if (style.OpaqueBackground)
        {
            Color background = ColorHex.Parse(style.BackgroundColor, Color.FromRgb(0x1A, 0x1A, 0x24));
            TranscriptPanel.Background = new SolidColorBrush(background);
            TranscriptText.Effect = null;
        }
        else
        {
            TranscriptPanel.Background = Brushes.Transparent;
            TranscriptText.Effect = _textHalo;
        }

        SetTranscript(_transcriptShown, animate: false);
        Reposition();
    }

    /// <summary>
    /// Locks the overlay to the monitor holding the window the user is typing into. Called once
    /// when recording starts, so the overlay does not chase focus mid-dictation.
    /// </summary>
    public void AttachToMonitorOfForegroundWindow()
    {
        _monitor = OverlayPositioner.MonitorForForegroundWindow();
        TranscriptPanel.MaxWidth = OverlayPositioner.MaxContentWidthDip(_monitor);
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
            // ActualHeight is stale until layout runs, and the transcript grows with its text.
            UpdateLayout();

            double topPart = Root.Margin.Top;
            if (TranscriptPanel.Visibility == Visibility.Visible)
            {
                topPart += TranscriptPanel.ActualHeight + TranscriptPanel.Margin.Bottom;
            }

            OverlayPositioner.Place(this, _baselinePercent, topPart, _monitor);
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
                    SetTranscript(_viewModel.PreviewText, animate: true);
                }

                break;
        }
    }

    private void ApplyState()
    {
        ListeningPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        _pulse.Stop(this);
        _spin.Stop(this);
        _waveTimer.Stop();
        _clockTimer.Stop();
        _ringRotation.BeginAnimation(RotateTransform.AngleProperty, null);

        switch (_viewModel.State)
        {
            case OverlayState.Listening:
                ListeningPanel.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 1.0, error: false);
                ResetWaveform();
                UpdateElapsed();
                SetTranscript(_viewModel.PreviewText, animate: true);
                _pulse.Begin(this, true);
                SpinRing();
                _waveTimer.Start();
                _clockTimer.Start();
                ShowOverlay();
                break;

            case OverlayState.Processing:
                ProcessingText.Text = _viewModel.StatusText;
                ProcessingPanel.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 0.7, error: false);
                // The last sentence stays up while the tail of the session is transcribed.
                SetTranscript(_viewModel.PreviewText, animate: false);
                _spin.Begin(this, true);
                ShowOverlay();
                break;

            case OverlayState.Preview:
                DoneText.Text = _viewModel.StatusText;
                DonePanel.Visibility = Visibility.Visible;
                SetChrome(_accentRing, ringOpacity: 0.7, error: false);
                SetTranscript(_viewModel.PreviewText, animate: true);
                ShowOverlay();
                break;

            case OverlayState.Error:
            case OverlayState.Notice:
                bool error = _viewModel.State == OverlayState.Error;
                MessageGlyph.Text = error ? "!" : "•";
                MessageBadge.Background = (Brush)FindResource(error ? "ErrorGlyphBrush" : "NoticeGlyphBrush");
                MessageText.Text = _viewModel.StatusText;
                MessagePanel.Visibility = Visibility.Visible;
                SetChrome(error ? _errorRing : _accentRing, ringOpacity: error ? 1.0 : 0.5, error);
                SetTranscript(string.Empty, animate: false);
                ShowOverlay();
                break;

            default:
                HideOverlay();
                break;
        }
    }

    /// <summary>
    /// Puts text above the line, or takes it away when there is none or the user turned the
    /// transcript off. New text appears piece by piece; a restyle redraws it in place.
    /// </summary>
    private void SetTranscript(string text, bool animate)
    {
        _transcriptShown = text;

        if (text.Length == 0 || !_showTranscript)
        {
            TranscriptText.Inlines.Clear();
            TranscriptPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TranscriptPanel.Visibility = Visibility.Visible;
        Reveal(TranscriptText, text, _transcriptColor, animate ? 18 : 0);
    }

    private void SetChrome(Brush ring, double ringOpacity, bool error)
    {
        Ring.BorderBrush = ring;
        Ring.Opacity = ringOpacity;
        Pill.Background = (Brush)FindResource(error ? "PillErrorGlassBrush" : "PillGlassBrush");
    }

    private void UpdateElapsed()
    {
        TimeSpan elapsed = DateTime.UtcNow - _viewModel.ListeningSince;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        ElapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
    }

    /// <summary>
    /// Fills a text block with the text as a sequence of small inline pieces that fade in one
    /// after another. Pieces are cut on grapheme clusters, never inside a Thai syllable, so a
    /// vowel or tone mark can never appear before the consonant it sits on. A stagger of zero
    /// draws everything at once.
    /// </summary>
    private static void Reveal(TextBlock target, string text, Color color, int staggerMs)
    {
        target.Inlines.Clear();
        IReadOnlyList<string> chunks = TextReveal.Chunks(text);
        if (chunks.Count == 0)
        {
            return;
        }

        if (staggerMs <= 0)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target.Inlines.Add(new Run(text) { Foreground = brush });
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

    /// <summary>Creates the curves, back to front, and returns the glow behind the main one.</summary>
    private Polyline BuildWaveform()
    {
        Brush[] strokes =
        {
            (Brush)FindResource("AccentStaticBrush"),
            (Brush)FindResource("WaveSecondaryBrush"),
            (Brush)FindResource("WaveTertiaryBrush"),
        };

        // A wide, faint copy of the main curve underneath reads as light bleeding off it.
        var glow = new Polyline
        {
            Stroke = strokes[0],
            StrokeThickness = 7,
            Opacity = 0.22,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Waveform.Children.Add(glow);

        for (int k = _waves.Length - 1; k >= 0; k--)
        {
            var wave = new Polyline
            {
                Stroke = strokes[k],
                StrokeThickness = WaveThickness[k],
                Opacity = WaveOpacity[k],
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            Waveform.Children.Add(wave);
            _waves[k] = wave;
        }

        return glow;
    }

    private void ResetWaveform()
    {
        _smoothedLevel = 0;
        _level = 0;
        StepWaveform();
    }

    /// <summary>
    /// One animation frame. The points are recomputed and swapped in as a frozen collection:
    /// cheaper than animation clocks, and the canvas never changes size.
    /// </summary>
    private void StepWaveform()
    {
        _waveClock += WaveTick;

        // Fast attack so the first syllable shows; the view model already slows the decay.
        _smoothedLevel += (_level - _smoothedLevel) * 0.35;
        double amplitude = WaveIdleAmplitude + ((WaveMaxAmplitude - WaveIdleAmplitude) * _smoothedLevel);

        for (int k = 0; k < _waves.Length; k++)
        {
            // A slow swell per curve keeps the three from ever looking locked together.
            double swell = 0.86 + (0.14 * Math.Sin((_waveClock * 1.7) + (k * 2.1)));
            double curveAmplitude = amplitude * WaveScale[k] * swell;
            double drift = (_waveClock * WaveSpeed[k]) + WavePhase[k];

            var points = new PointCollection(WavePoints);
            for (int i = 0; i < WavePoints; i++)
            {
                double u = i / (double)(WavePoints - 1);
                double envelope = Math.Sin(Math.PI * u);
                envelope *= envelope;
                double y = (WaveHeight / 2) + (curveAmplitude * envelope * Math.Sin((2 * Math.PI * WaveFrequency[k] * u) + drift));
                points.Add(new Point(u * WaveWidth, y));
            }

            points.Freeze();
            _waves[k].Points = points;
        }

        _waveGlow.Points = _waves[0].Points;
    }

    private void ShowOverlay()
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

        // Rise into place from just below, growing slightly on the way.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        EntranceSlide.BeginAnimation(TranslateTransform.YProperty, Ease(EntranceRise, 0, EntranceDuration, ease));
        EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(0.96, 1, EntranceDuration, ease));
        EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(0.96, 1, EntranceDuration, ease));
        BeginAnimation(OpacityProperty, Ease(0, 1, EntranceDuration, ease));
    }

    private void HideOverlay()
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
