using Xunit;

namespace OP1wBattery.Tests;

/// <summary>Parsing of the 0xB4 battery payload (see MouseBattery's doc comment).</summary>
public class BatteryPayloadTests
{
    [Fact]
    public void PercentPassesThrough()
    {
        var reading = MouseBattery.ParseBatteryPayload([55, 0, 0], wired: false);
        Assert.Equal(55, reading.Percent);
    }

    [Fact]
    public void PercentIsClampedAt100()
    {
        var reading = MouseBattery.ParseBatteryPayload([0xFF, 0, 0], wired: false);
        Assert.Equal(100, reading.Percent);
    }

    [Fact]
    public void MillivoltsAreLittleEndian()
    {
        var reading = MouseBattery.ParseBatteryPayload([0, 0x0C, 0x0E], wired: false);
        Assert.Equal(0x0E0C, reading.Millivolts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WiredFlagPassesThrough(bool wired)
    {
        var reading = MouseBattery.ParseBatteryPayload([50, 0, 0], wired);
        Assert.Equal(wired, reading.Wired);
    }
}
