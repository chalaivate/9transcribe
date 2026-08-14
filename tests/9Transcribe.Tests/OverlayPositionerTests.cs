using NineTranscribe.Overlay;
using NineTranscribe.Settings;
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
    private const int Height = 80;
    private const int Margin = 16;

    [Theory]
    [InlineData(OverlayPosition.Top, 760, 16)]
    [InlineData(OverlayPosition.Bottom, 760, 944)]
    [InlineData(OverlayPosition.Left, 16, 480)]
    [InlineData(OverlayPosition.Right, 1504, 480)]
    [InlineData(OverlayPosition.TopLeft, 16, 16)]
    [InlineData(OverlayPosition.TopRight, 1504, 16)]
    [InlineData(OverlayPosition.BottomLeft, 16, 944)]
    [InlineData(OverlayPosition.BottomRight, 1504, 944)]
    public void Compute_PlacesEveryAnchorOnThePrimaryWorkArea(OverlayPosition anchor, int expectedX, int expectedY)
    {
        (int x, int y) = OverlayPositioner.Compute(
            anchor, Left, Top, Right, Bottom, Width, Height, Margin);

        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void Compute_OnAMonitorAtNegativeCoordinates_StaysRelativeToThatRectangle()
    {
        // A second display to the left of the primary one starts at a negative X.
        (int x, int y) = OverlayPositioner.Compute(
            OverlayPosition.BottomRight, -1920, 0, 0, 1080, Width, Height, Margin);

        Assert.Equal(-1920 + 1920 - Width - Margin, x);
        Assert.Equal(1080 - Height - Margin, y);
    }

    [Fact]
    public void Compute_CentresHorizontallyWithinTheWorkArea()
    {
        (int x, _) = OverlayPositioner.Compute(
            OverlayPosition.Bottom, 100, 0, 1100, 1000, Width, Height, Margin);

        Assert.Equal(100 + ((1000 - Width) / 2), x);
    }

    [Fact]
    public void Compute_WithAWindowWiderThanTheScreen_StillReturnsTheLeftEdge()
    {
        (int x, _) = OverlayPositioner.Compute(
            OverlayPosition.Bottom, 0, 0, 300, 300, 900, Height, Margin);

        // Negative is correct here: the pill is centred, so it overhangs both sides equally.
        Assert.Equal((300 - 900) / 2, x);
    }
}
