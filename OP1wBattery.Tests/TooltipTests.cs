using Xunit;

namespace OP1wBattery.Tests;

/// <summary>
/// The tray tooltip, which NotifyIcon.Text caps at 127 characters on modern
/// .NET (szTip is 128 WCHARs; 63 was the .NET Framework limit).
/// </summary>
public class TooltipTests
{
    const int MaxLength = 127;

    [Theory]
    [InlineData(0)]
    [InlineData(55)]
    [InlineData(100)]
    public void FitsWithinTheNotifyIconLimit(int percent)
    {
        var tooltip = TrayApp.TooltipFor(new Reading(percent, 4200, Wired: true));
        Assert.True(tooltip.Length <= MaxLength,
                    $"tooltip was {tooltip.Length} characters: {tooltip}");
    }

    [Fact]
    public void AbsentMouseFitsToo()
    {
        Assert.True(TrayApp.TooltipFor(null).Length <= MaxLength);
    }

    [Fact]
    public void CarriesTheLevelAndVoltage()
    {
        var tooltip = TrayApp.TooltipFor(new Reading(55, 3840, Wired: false));
        Assert.Contains("55%", tooltip);
        Assert.Contains("3.84 V", tooltip);
        Assert.DoesNotContain("wired", tooltip);
    }

    [Fact]
    public void MarksAWiredMouse()
    {
        Assert.Contains("wired", TrayApp.TooltipFor(new Reading(55, 4200, Wired: true)));
    }
}
