using Xunit;

namespace OP1wBattery.Tests;

/// <summary>The tray icon's level colours, on and around each threshold.</summary>
public class LevelColorTests
{
    const int Red = 0xF87171;
    const int Orange = 0xFB923C;
    const int Yellow = 0xFACC15;
    const int Green = 0x4ADE80;

    [Theory]
    [InlineData(0, Red)]
    [InlineData(10, Red)]    // warn threshold is inclusive
    [InlineData(11, Orange)]
    [InlineData(25, Orange)]
    [InlineData(26, Yellow)]
    [InlineData(50, Yellow)]
    [InlineData(51, Green)]
    [InlineData(100, Green)]
    [InlineData(101, Green)]   // out-of-range readings fall back to the top colour
    [InlineData(255, Green)]
    public void PercentMapsToExpectedColor(int percent, int expectedRgb)
    {
        Assert.Equal(expectedRgb, TrayApp.ColorForPercent(percent));
    }
}
