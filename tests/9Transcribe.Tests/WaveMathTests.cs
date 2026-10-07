using NineTranscribe.Overlay;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class WaveMathTests
{
    [Fact]
    public void EveryCurve_StartsAndEndsOnTheCentreLine()
    {
        var wave = new WaveMath();
        wave.Step(0.3, 1.0);

        for (int k = 0; k < WaveMath.CurveCount; k++)
        {
            Assert.Equal(WaveMath.Height / 2, wave.Y(k, 0), 9);
            Assert.Equal(WaveMath.Height / 2, wave.Y(k, 1), 9);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Curves_StayInsideTheCanvas(double level)
    {
        var wave = new WaveMath();

        for (int frame = 0; frame < 600; frame++)
        {
            wave.Step(1 / 60.0, level);
            for (int k = 0; k < WaveMath.CurveCount; k++)
            {
                for (int i = 0; i < WaveMath.SampleCount; i++)
                {
                    double y = wave.Y(k, WaveMath.U(i));
                    Assert.True(double.IsFinite(y));
                    Assert.InRange(y, 0, WaveMath.Height);
                }
            }
        }
    }

    [Fact]
    public void Energy_RisesQuicklyAndFallsMoreSlowly()
    {
        var wave = new WaveMath();
        for (int i = 0; i < 10; i++)
        {
            wave.Step(1 / 60.0, 1.0);
        }

        double afterAttack = wave.Energy;
        Assert.True(afterAttack > 0.95, $"energy {afterAttack}");

        for (int i = 0; i < 10; i++)
        {
            wave.Step(1 / 60.0, 0.0);
        }

        Assert.InRange(wave.Energy, 0.1, 0.5);
    }

    [Fact]
    public void Energy_DoesNotDependOnTheFrameRate()
    {
        var fast = new WaveMath();
        var slow = new WaveMath();

        for (int i = 0; i < 12; i++)
        {
            fast.Step(1 / 120.0, 0.6);
        }

        for (int i = 0; i < 3; i++)
        {
            slow.Step(1 / 30.0, 0.6);
        }

        Assert.Equal(fast.Energy, slow.Energy, 6);
    }

    [Fact]
    public void Peak_IsOnTheMainCurve()
    {
        var wave = new WaveMath();
        wave.Step(0.2, 0.8);

        (double x, double y) = wave.Peak();

        Assert.InRange(x, 0, WaveMath.Width);
        Assert.Equal(wave.Y(0, x / WaveMath.Width), y, 6);
    }
}
