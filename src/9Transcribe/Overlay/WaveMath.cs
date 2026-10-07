namespace NineTranscribe.Overlay;

/// <summary>
/// The maths behind the neon waveform, kept free of WPF so it can be tested. Three travelling
/// sine curves share one envelope that pins both ends to the centre line; they speed up and
/// swell with the voice, and the main curve picks up a fast ripple when the voice is loud, so
/// speech looks electric rather than like a slow sea.
/// </summary>
public sealed class WaveMath
{
    public const int CurveCount = 3;
    public const int SampleCount = 64;
    public const double Width = 168;
    public const double Height = 32;
    public const double IdleAmplitude = 1.8;
    public const double MaxAmplitude = 14;

    // Phase speed in rad/s at rest; the middle curve runs the other way.
    private static readonly double[] Speed = { 6.0, -8.5, 4.2 };

    // Whole waves across the width.
    private static readonly double[] Frequency = { 1.6, 2.3, 1.1 };

    private static readonly double[] Scale = { 1.0, 0.72, 0.5 };
    private static readonly double[] StartPhase = { 0.0, 1.3, 2.7 };

    private readonly double[] _phase = new double[CurveCount];
    private double _clock;

    public WaveMath()
    {
        Reset();
    }

    /// <summary>The voice as the waveform feels it, 0 to 1: quick to rise, slower to fall.</summary>
    public double Energy { get; private set; }

    /// <summary>Peak height of the main curve in pixels, before the envelope.</summary>
    public double Amplitude
    {
        get
        {
            // At rest the curves breathe a little so the tab never looks frozen.
            double breath = 0.6 * Math.Sin(2 * Math.PI * 1.2 * _clock) * (1 - Energy);
            return IdleAmplitude + breath + ((MaxAmplitude - IdleAmplitude) * Energy);
        }
    }

    public void Reset()
    {
        Energy = 0;
        _clock = 0;
        Array.Copy(StartPhase, _phase, CurveCount);
    }

    /// <summary>Advances the animation by <paramref name="seconds"/> with the current level (0..1).</summary>
    public void Step(double seconds, double level)
    {
        double dt = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 0.05);
        double target = Math.Pow(Math.Clamp(double.IsFinite(level) ? level : 0, 0, 1), 0.7);

        // Per-frame rates written for 60 fps, converted so a slower machine animates the same.
        double rate = target > Energy ? 0.6 : 0.12;
        double blend = 1 - Math.Pow(1 - rate, dt * 60);
        Energy += (target - Energy) * blend;

        _clock += dt;
        double boost = 1 + (2.2 * Energy);
        for (int k = 0; k < CurveCount; k++)
        {
            _phase[k] = (_phase[k] + (dt * Speed[k] * boost)) % (2 * Math.PI * 1000);
        }
    }

    /// <summary>
    /// Pins both ends to the centre line and gathers the motion in a soft hump that drifts a
    /// little left and right, so the shape never sits still.
    /// </summary>
    public double Envelope(double u)
    {
        double edge = Math.Pow(Math.Sin(Math.PI * Math.Clamp(u, 0, 1)), 1.5);
        double centre = 0.5 + (0.12 * Math.Sin(0.9 * _clock));
        double spread = (u - centre) / 0.32;
        return edge * (0.35 + (0.65 * Math.Exp(-spread * spread)));
    }

    /// <summary>Vertical position of curve <paramref name="curve"/> at <paramref name="u"/> (0..1 across).</summary>
    public double Y(int curve, double u)
    {
        double envelope = Envelope(u) * Amplitude * Scale[curve];
        double wave = Math.Sin((2 * Math.PI * Frequency[curve] * u) + _phase[curve]);

        if (curve == 0)
        {
            // Loud speech crackles: a fast ripple mixed in, never adding to the overall height.
            double crackle = 0.3 * Energy * Energy;
            wave = (wave * (1 - crackle)) + (crackle * Math.Sin((2 * Math.PI * 5.5 * u) + (3 * _phase[0])));
        }

        return (Height / 2) + (envelope * wave);
    }

    public static double X(int sample) => sample * Width / (SampleCount - 1);

    public static double U(int sample) => sample / (double)(SampleCount - 1);

    /// <summary>Where the main curve is furthest from the centre line — where sparks fly from.</summary>
    public (double X, double Y) Peak()
    {
        double bestX = Width / 2;
        double bestY = Height / 2;
        double bestDistance = -1;
        for (int i = 0; i < SampleCount; i++)
        {
            double y = Y(0, U(i));
            double distance = Math.Abs(y - (Height / 2));
            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestX = X(i);
                bestY = y;
            }
        }

        return (bestX, bestY);
    }
}
