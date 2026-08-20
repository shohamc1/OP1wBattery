using Xunit;

namespace OP1wBattery.Tests;

/// <summary>The tray tooltip, which NotifyIcon.Text caps at 63 characters.</summary>
public class TooltipTests
{
    const int MaxLength = 63;

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
