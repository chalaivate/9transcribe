using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NineTranscribe.Settings;
using NineTranscribe.UI;

namespace NineTranscribe.Overlay;

public partial class OverlayWindow : Window
{
    /// <summary>How far the overlay rises during its entrance, in device-independent pixels.</summary>
    private const double EntranceRise = 10;

    /// <summary>Thai stacks vowels and tone marks above the consonant; anything tighter clips them.</summary>
    private const double TranscriptLineHeightFactor = 1.6;

    private const int SparkPoolSize = 10;
    private const double SparksPerSecondAtFullVoice = 25;

    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RingRevolution = TimeSpan.FromSeconds(1.8);

    private static readonly Color NeonCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color NeonBlue = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Color NeonViolet = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color NeonMagenta = Color.FromRgb(0xEC, 0x48, 0x99);
    private static readonly Color ProblemColor = Color.FromRgb(0xFC, 0xA5, 0xA5);

    private readonly OverlayViewModel _viewModel;
    private readonly Storyboard _pulse;
    private readonly Storyboard _spin;
    private readonly WaveMath _wave = new();
    private readonly Polyline[][] _curves = new Polyline[WaveMath.CurveCount][];
    private readonly Polyline _hotCore;
    private readonly Spark[] _sparks = new Spark[SparkPoolSize];
    private readonly Random _random = new();
    private readonly RotateTransform _ringRotation = new(0, 0.5, 0.5);
    private readonly LinearGradientBrush _neonRing;
    private readonly LinearGradientBrush _errorRing;
    private readonly DropShadowEffect _textHalo;
    private readonly List<Inline> _transcriptTail = new();
    private readonly List<Ellipse> _thinkingDots = new();

    private IntPtr _monitor;
    private bool _placing;
    private bool _visible;
    private bool _rendering;
    private TimeSpan _lastRenderTime;
    private double _level;
    private double _sparkBudget;
    private string _elapsedShown = string.Empty;
    private bool _hasTranscriptText;

    private int _baselinePercent = 90;
    private bool _showTranscript = true;
    private Color _transcriptColor = Colors.White;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _neonRing = BuildRing(NeonCyan, NeonBlue, NeonViolet, NeonMagenta, NeonCyan);
        _errorRing = BuildRing(
            Color.FromRgb(0xF8, 0x71, 0x71),
            Color.FromRgb(0xEF, 0x44, 0x44),
            Color.FromRgb(0xB9, 0x1C, 0x1C),
            Color.FromRgb(0xF8, 0x71, 0x71));
        Ring.BorderBrush = _neonRing;

        // A soft dark halo around the letters keeps floating text legible on a white document.
        _textHalo = new DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.85,
            Color = Colors.Black,
        };

        _hotCore = BuildWaveform();
        _pulse = BuildPulse();
        _spin = BuildSpin();

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

        RenderTranscript(animateNewest: false);
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

    protected override void OnClosed(EventArgs e)
    {
        StopWave();
        base.OnClosed(e);
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

            case nameof(OverlayViewModel.TranscriptRevision):
                // A new piece of text: redraw it, with the newest piece fading in word by word.
                RenderTranscript(animateNewest: true);
                break;

            case nameof(OverlayViewModel.PendingSegments):
            case nameof(OverlayViewModel.ProblemText):
                // Only the end of the transcript changes, so a reveal still running is left alone.
                UpdateTranscriptTail();
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
        StopWave();
        _ringRotation.BeginAnimation(RotateTransform.AngleProperty, null);

        switch (_viewModel.State)
        {
            case OverlayState.Listening:
                ListeningPanel.Visibility = Visibility.Visible;
                SetChrome(_neonRing, ringOpacity: 0.55, error: false);
                RenderTranscript(animateNewest: false);
                _pulse.Begin(this, true);
                SpinRing();
                StartWave();
                ShowOverlay();
                break;

            case OverlayState.Processing:
                ProcessingText.Text = _viewModel.StatusText;
                ProcessingPanel.Visibility = Visibility.Visible;
                SetChrome(_neonRing, ringOpacity: 0.7, error: false);
                UpdateTranscriptTail();
                _spin.Begin(this, true);
                ShowOverlay();
                break;

            case OverlayState.Preview:
                DoneText.Text = _viewModel.StatusText;
                DonePanel.Visibility = Visibility.Visible;
                SetChrome(_neonRing, ringOpacity: 0.7, error: false);
                UpdateTranscriptTail();
                ShowOverlay();
                break;

            case OverlayState.Error:
            case OverlayState.Notice:
                bool error = _viewModel.State == OverlayState.Error;
                MessageGlyph.Text = error ? "!" : "•";
                MessageBadge.Background = (Brush)FindResource(error ? "ErrorGlyphBrush" : "NoticeGlyphBrush");
                MessageText.Text = _viewModel.StatusText;
                MessagePanel.Visibility = Visibility.Visible;
                SetChrome(error ? _errorRing : _neonRing, ringOpacity: error ? 1.0 : 0.5, error);
                RenderTranscript(animateNewest: false);
                ShowOverlay();
                break;

            default:
                HideOverlay();
                break;
        }
    }

    private void SetChrome(Brush ring, double ringOpacity, bool error)
    {
        Ring.BorderBrush = ring;
        Ring.Opacity = ringOpacity;
        Pill.Background = (Brush)FindResource(error ? "PillErrorGlassBrush" : "PillGlassBrush");
    }

    // ------------------------------------------------------------------ transcript

    private bool SessionOnScreen => _viewModel.State is OverlayState.Listening
        or OverlayState.Processing
        or OverlayState.Preview;

    /// <summary>
    /// Redraws the text above the line: the earlier pieces dimmed, the newest one in full colour
    /// (fading in when it has just arrived), then the transcribing dots and any warning.
    /// </summary>
    private void RenderTranscript(bool animateNewest)
    {
        StopThinkingDots();
        _transcriptTail.Clear();
        TranscriptText.Inlines.Clear();
        _hasTranscriptText = false;

        if (_showTranscript && SessionOnScreen)
        {
            string older = _viewModel.TranscriptOlder;
            string newest = _viewModel.TranscriptNewest;

            if (older.Length > 0)
            {
                var dim = new SolidColorBrush(_transcriptColor) { Opacity = 0.62 };
                dim.Freeze();
                TranscriptText.Inlines.Add(new Run(older) { Foreground = dim });
            }

            if (newest.Length > 0)
            {
                AppendReveal(TranscriptText.Inlines, newest, _transcriptColor, animateNewest ? 18 : 0);
            }

            _hasTranscriptText = older.Length > 0 || newest.Length > 0;
        }

        UpdateTranscriptTail();
    }

    /// <summary>Rebuilds only what follows the text, leaving the text runs and their animations alone.</summary>
    private void UpdateTranscriptTail()
    {
        foreach (Inline inline in _transcriptTail)
        {
            TranscriptText.Inlines.Remove(inline);
        }

        _transcriptTail.Clear();
        StopThinkingDots();

        OverlayState state = _viewModel.State;
        bool thinking = _showTranscript
            && (state == OverlayState.Processing
                || (state == OverlayState.Listening && _viewModel.PendingSegments > 0));
        string problem = SessionOnScreen ? _viewModel.ProblemText : string.Empty;

        if (thinking)
        {
            if (_hasTranscriptText)
            {
                AddTail(new Run(" "));
            }

            AddTail(new InlineUIContainer(BuildThinkingDots()) { BaselineAlignment = BaselineAlignment.Center });
        }

        if (problem.Length > 0)
        {
            if (_hasTranscriptText || thinking)
            {
                AddTail(new LineBreak());
            }

            var brush = new SolidColorBrush(ProblemColor);
            brush.Freeze();
            AddTail(new Run(problem)
            {
                Foreground = brush,
                FontSize = Math.Max(13, TranscriptText.FontSize * 0.5),
            });
        }

        bool anything = _hasTranscriptText || thinking || problem.Length > 0;
        TranscriptPanel.Visibility = anything ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddTail(Inline inline)
    {
        TranscriptText.Inlines.Add(inline);
        _transcriptTail.Add(inline);
    }

    /// <summary>Three neon dots pulsing in turn where the next words will appear.</summary>
    private FrameworkElement BuildThinkingDots()
    {
        double size = Math.Max(5, TranscriptText.FontSize * 0.22);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) };
        Color[] colors = { NeonCyan, NeonViolet, NeonMagenta };

        for (int i = 0; i < colors.Length; i++)
        {
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness(size * 0.35, 0, size * 0.35, 0),
                Fill = new SolidColorBrush(colors[i]),
                Opacity = 0.25,
            };

            var pulse = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(420))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            dot.BeginAnimation(OpacityProperty, pulse);
            _thinkingDots.Add(dot);
            panel.Children.Add(dot);
        }

        return panel;
    }

    /// <summary>Repeating animations keep ticking until stopped, even once their element is gone.</summary>
    private void StopThinkingDots()
    {
        foreach (Ellipse dot in _thinkingDots)
        {
            dot.BeginAnimation(OpacityProperty, null);
        }

        _thinkingDots.Clear();
    }

    /// <summary>
    /// Appends the text as a sequence of small inline pieces that fade in one after another.
    /// Pieces are cut on grapheme clusters, never inside a Thai syllable, so a vowel or tone mark
    /// can never appear before the consonant it sits on. A stagger of zero draws it at once.
    /// </summary>
    private static void AppendReveal(InlineCollection inlines, string text, Color color, int staggerMs)
    {
        IReadOnlyList<string> chunks = TextReveal.Chunks(text);
        if (chunks.Count == 0)
        {
            return;
        }

        if (staggerMs <= 0)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            inlines.Add(new Run(text) { Foreground = brush });
            return;
        }

        // Long text still finishes appearing quickly; the stagger shrinks to fit the cap.
        double stagger = Math.Min(staggerMs, TextReveal.MaxRevealMs / (double)chunks.Count);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < chunks.Count; i++)
        {
            var brush = new SolidColorBrush(color) { Opacity = 0 };
            inlines.Add(new Run(chunks[i]) { Foreground = brush });

            var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * stagger),
                EasingFunction = ease,
            };
            brush.BeginAnimation(Brush.OpacityProperty, fade);
        }
    }

    // ------------------------------------------------------------------ waveform

    /// <summary>
    /// Three curves, each drawn three times on top of itself — a wide faint stroke, a narrower
    /// brighter one and a thin core — which reads as neon glow without the cost of a blur
    /// effect on something that changes every frame. A white-hot line runs inside the main one.
    /// </summary>
    private Polyline BuildWaveform()
    {
        LinearGradientBrush forward = BuildFlowBrush(TimeSpan.FromSeconds(1.6), reverse: false,
            NeonCyan, NeonBlue, NeonViolet, NeonMagenta, NeonCyan);
        LinearGradientBrush backward = BuildFlowBrush(TimeSpan.FromSeconds(2.3), reverse: true,
            NeonMagenta, NeonViolet, NeonCyan, NeonBlue, NeonMagenta);
        Brush[] strokes = { forward, backward, forward };

        double[] coreThickness = { 1.9, 1.4, 1.1 };
        double[] coreOpacity = { 1.0, 0.8, 0.6 };
        (double Thickness, double Opacity)[] glow = { (7.0, 0.10), (3.6, 0.26) };

        for (int k = 0; k < WaveMath.CurveCount; k++)
        {
            _curves[k] = new Polyline[glow.Length + 1];
        }

        // Back to front: every glow layer of every curve, then the cores, so no core is dimmed
        // by another curve's haze.
        for (int layer = 0; layer < glow.Length; layer++)
        {
            for (int k = WaveMath.CurveCount - 1; k >= 0; k--)
            {
                Polyline line = NewStroke(strokes[k], glow[layer].Thickness, glow[layer].Opacity * coreOpacity[k]);
                Waveform.Children.Add(line);
                _curves[k][layer] = line;
            }
        }

        for (int k = WaveMath.CurveCount - 1; k >= 0; k--)
        {
            Polyline core = NewStroke(strokes[k], coreThickness[k], coreOpacity[k]);
            Waveform.Children.Add(core);
            _curves[k][glow.Length] = core;
        }

        Polyline hot = NewStroke(Brushes.White, 0.7, 0.85);
        Waveform.Children.Add(hot);

        for (int i = 0; i < SparkPoolSize; i++)
        {
            _sparks[i] = new Spark(Waveform, NeonCyan);
        }

        // The ends of every curve melt into the glass instead of stopping at a hard edge.
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.14));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.86));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        mask.Freeze();
        Waveform.OpacityMask = mask;

        return hot;
    }

    private static Polyline NewStroke(Brush brush, double thickness, double opacity) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        Opacity = opacity,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
    };

    /// <summary>A repeating gradient whose colours stream along the curve forever.</summary>
    private static LinearGradientBrush BuildFlowBrush(TimeSpan period, bool reverse, params Color[] colors)
    {
        var shift = new TranslateTransform();
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Repeat,
            RelativeTransform = shift,
        };

        for (int i = 0; i < colors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Length - 1)));
        }

        shift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(reverse ? 1 : 0, reverse ? 0 : 1, period) { RepeatBehavior = RepeatBehavior.Forever });
        return brush;
    }

    private void StartWave()
    {
        _wave.Reset();
        _level = 0;
        _sparkBudget = 0;
        _elapsedShown = string.Empty;
        foreach (Spark spark in _sparks)
        {
            spark.Kill();
        }

        DrawWave();
        UpdateElapsed();

        if (!_rendering)
        {
            _rendering = true;
            _lastRenderTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopWave()
    {
        if (!_rendering)
        {
            return;
        }

        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// One frame, in step with WPF's own render loop rather than a timer, and timed by the clock
    /// so the motion is the same speed on a slow machine as on a fast one.
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        if (now == _lastRenderTime && now != TimeSpan.Zero)
        {
            return;
        }

        double dt = _lastRenderTime == TimeSpan.Zero ? 1 / 60.0 : (now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;

        _wave.Step(dt, _level);
        DrawWave();
        StepSparks(dt);

        // The ring glows brighter with the voice.
        Ring.Opacity = 0.55 + (0.45 * _wave.Energy);
        UpdateElapsed();
    }

    private void DrawWave()
    {
        for (int k = 0; k < WaveMath.CurveCount; k++)
        {
            var points = new PointCollection(WaveMath.SampleCount);
            for (int i = 0; i < WaveMath.SampleCount; i++)
            {
                points.Add(new Point(WaveMath.X(i), _wave.Y(k, WaveMath.U(i))));
            }

            points.Freeze();
            foreach (Polyline line in _curves[k])
            {
                line.Points = points;
            }

            if (k == 0)
            {
                _hotCore.Points = points;
            }
        }
    }

    private void StepSparks(double dt)
    {
        double loud = Math.Max(0, (_wave.Energy - 0.35) / 0.65);
        _sparkBudget = Math.Min(3, _sparkBudget + (dt * SparksPerSecondAtFullVoice * loud));

        while (_sparkBudget >= 1)
        {
            _sparkBudget -= 1;
            Spark? free = Array.Find(_sparks, s => !s.Alive);
            if (free is null)
            {
                break;
            }

            (double x, double y) = _wave.Peak();
            double away = y >= WaveMath.Height / 2 ? 1 : -1;
            free.Launch(
                x + ((_random.NextDouble() - 0.5) * 12),
                y,
                (_random.NextDouble() - 0.5) * 30,
                away * (20 + (_random.NextDouble() * 20)),
                0.35 + (_random.NextDouble() * 0.25));
        }

        foreach (Spark spark in _sparks)
        {
            spark.Step(dt);
        }
    }

    private void UpdateElapsed()
    {
        TimeSpan elapsed = DateTime.UtcNow - _viewModel.ListeningSince;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        string text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");

        // Every frame asks; the text block only hears about it once a second.
        if (text != _elapsedShown)
        {
            _elapsedShown = text;
            ElapsedText.Text = text;
        }
    }

    // ------------------------------------------------------------------ show / hide

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
        StopThinkingDots();
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

    /// <summary>
    /// A spark thrown off the top of the main curve when the voice is loud: a white-hot point in
    /// a small cyan glow that flies outwards and fades. Pooled, so loud speech allocates nothing.
    /// </summary>
    private sealed class Spark
    {
        private const double GlowSize = 7;
        private const double CoreSize = 2.6;

        private readonly Ellipse _glow;
        private readonly Ellipse _core;
        private double _x;
        private double _y;
        private double _vx;
        private double _vy;
        private double _age;
        private double _life;

        public Spark(Canvas canvas, Color glowColor)
        {
            var glowBrush = new RadialGradientBrush(Color.FromArgb(0xCC, glowColor.R, glowColor.G, glowColor.B), Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B));
            glowBrush.Freeze();
            _glow = new Ellipse { Width = GlowSize, Height = GlowSize, Fill = glowBrush, Opacity = 0, IsHitTestVisible = false };
            _core = new Ellipse { Width = CoreSize, Height = CoreSize, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Add(_glow);
            canvas.Children.Add(_core);
        }

        public bool Alive => _age < _life;

        public void Launch(double x, double y, double vx, double vy, double life)
        {
            _x = x;
            _y = y;
            _vx = vx;
            _vy = vy;
            _age = 0;
            _life = life;
            Place();
        }

        public void Kill()
        {
            _age = _life = 0;
            _glow.Opacity = 0;
            _core.Opacity = 0;
        }

        public void Step(double dt)
        {
            if (!Alive)
            {
                return;
            }

            _age += dt;
            _x += _vx * dt;
            _y += _vy * dt;

            if (!Alive)
            {
                Kill();
                return;
            }

            Place();
        }

        private void Place()
        {
            double fade = 1 - (_age / _life);
            Canvas.SetLeft(_glow, _x - (GlowSize / 2));
            Canvas.SetTop(_glow, _y - (GlowSize / 2));
            Canvas.SetLeft(_core, _x - (CoreSize / 2));
            Canvas.SetTop(_core, _y - (CoreSize / 2));
            _glow.Opacity = 0.9 * fade;
            _core.Opacity = fade;
        }
    }
}
