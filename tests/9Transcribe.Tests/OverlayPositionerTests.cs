using NineTranscribe.Overlay;
using Xunit;

namespace NineTranscribe.Tests;

public sealed class OverlayPositionerTests
{
    // A 1920x1080 monitor with a 40 px taskbar along the bottom.
    private const int Left = 0;
    private const int Top = 0;
    private const int Right = 1920;
    private const int Bottom = 1040;

    private const int Width = 400;
    private const int Height = 120;
    private const int TopPart = 30;

    [Fact]
    public void Compute_CentresHorizontallyAndHangsTheTopPartAboveTheLine()
    {
        (int x, int y) = OverlayPositioner.Compute(Left, Top, Right, Bottom, Width, Height, TopPart, 90);

        // 90% of 1040 is 936; the top part ends there, so the window starts 30 px higher.
        Assert.Equal(760, x);
        Assert.Equal(906, y);
    }

    [Theory]
    [InlineData(50, 520 - TopPart)]
    [InlineData(75, 780 - TopPart)]
    public void Compute_FollowsTheBaselinePercent(int percent, int expectedY)
    {
        (_, int y) = OverlayPositioner.Compute(Left, Top, Right, Bottom, Width, Height, TopPart, percent);

        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void Compute_WithNoTopPart_PutsTheWindowTopOnTheLine()
    {
        // A tab short enough to fit in the 104 px below the line.
        (_, int y) = OverlayPositioner.Compute(Left, Top, Right, Bottom, Width, 100, 0, 90);

        Assert.Equal(936, y);
    }

    [Fact]
    public void Compute_NearTheBottom_KeepsTheWholeWindowOnScreen()
    {
        // A line at 96% leaves 42 px below it, less than the window's height.
        (_, int y) = OverlayPositioner.Compute(Left, Top, Right, Bottom, Width, Height, TopPart, 96);

        Assert.Equal(Bottom - Height, y);
    }

    [Fact]
    public void Compute_WithATopPartTallerThanTheLineHeight_ClampsToTheTop()
    {
        (_, int y) = OverlayPositioner.Compute(Left, Top, Right, Bottom, Width, Height, 700, 40);

        Assert.Equal(Top, y);
    }

    [Fact]
    public void Compute_OnAMonitorAtNegativeCoordinates_StaysRelativeToThatRectangle()
    {
        // A second display to the left of the primary one starts at a negative X.
        (int x, int y) = OverlayPositioner.Compute(-1920, 0, 0, 1080, Width, Height, TopPart, 90);

        Assert.Equal(-1920 + ((1920 - Width) / 2), x);
        Assert.Equal(972 - TopPart, y);
    }

    [Fact]
    public void Compute_WithAWindowWiderThanTheScreen_StillReturnsTheLeftEdge()
    {
        (int x, _) = OverlayPositioner.Compute(0, 0, 300, 300, 900, Height, TopPart, 90);

        // Negative is correct here: the window is centred, so it overhangs both sides equally.
        Assert.Equal((300 - 900) / 2, x);
    }
}
