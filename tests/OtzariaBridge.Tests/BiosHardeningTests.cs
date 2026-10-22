using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>הקשחת BIOS: סדר שלבים (סיסמה אחרונה), בחירת ערך מדויקת, זיהוי הגדרות, ותכנון לכל ההגדרות התואמות. בלי WMI.</summary>
public class BiosHardeningTests
{
    [Fact]
    public void Password_stage_is_always_last()
    {
        var order = BiosHardening.StageOrder(new BiosHardening.HardenRequest
        { SetPassword = true, UsbBoot = true, NetworkBoot = true, Legacy = true, BootOrder = true, BootMenu = true });
        Assert.Equal("password", order[^1]);
        Assert.Equal(6, order.Count);
        Assert.Equal(new[] { "usbBoot", "networkBoot", "legacy", "bootOrder", "bootMenuAuth", "password" }, order);
    }

    [Fact]
    public void Password_only_and_blocks_only()
    {
        Assert.Equal(new[] { "password" }, BiosHardening.StageOrder(new BiosHardening.HardenRequest { SetPassword = true }));
        Assert.Equal(new[] { "usbBoot" }, BiosHardening.StageOrder(new BiosHardening.HardenRequest { UsbBoot = true }));
        Assert.False(new BiosHardening.HardenRequest().Any);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("12345678901", false)]
    [InlineData("123456789012", true)]
    public void Password_minimum_is_12(string pw, bool ok) =>
        Assert.Equal(ok, BiosHardening.ValidateBiosPassword(pw) == null);

    [Fact]
    public void PickDisable_is_exact_and_never_picks_Normal()
    {
        Assert.Null(BiosHardening.PickDisableValue(new[] { "Normal", "Nothing", "Notify" }));
        Assert.Equal("Disabled", BiosHardening.PickDisableValue(new[] { "Normal", "Disabled", "Enabled" }));
        Assert.Equal("Disable", BiosHardening.PickDisableValue(new[] { "*Enable", "Disable" }));
        Assert.Equal("Off", BiosHardening.PickDisableValue(new[] { "On", "Off" }));
        Assert.Equal("No", BiosHardening.PickDisableValue(new[] { "Yes", "No" }));
    }

    [Fact]
    public void PickEnable_is_exact()
    {
        Assert.Null(BiosHardening.PickEnableValue(new[] { "Disabled", "Online" }));   // "on" אינו Contains של Online
        Assert.Equal("Enabled", BiosHardening.PickEnableValue(new[] { "Disabled", "Enabled" }));
    }

    [Theory]
    [InlineData("Network (PXE) Boot", true)]
    [InlineData("IPv4 PXE Support", true)]
    [InlineData("UEFI Network Stack", true)]
    [InlineData("Boot from LAN", true)]
    [InlineData("Wake on LAN", false)]
    [InlineData("WLAN Radio", false)]
    [InlineData("Network Adapter Name", false)]
    public void Network_boot_matcher(string name, bool expected) =>
        Assert.Equal(expected, BiosHardening.LooksLikeNetworkBootSetting(name));

    [Theory]
    [InlineData("Legacy Support", true)]
    [InlineData("CSM Support", true)]
    [InlineData("Launch CSM", true)]
    [InlineData("Legacy Boot", true)]
    [InlineData("Legacy USB Support", false)]
    [InlineData("Secure Boot", false)]
    public void Legacy_matcher(string name, bool expected) =>
        Assert.Equal(expected, BiosHardening.LooksLikeLegacySetting(name));

    [Theory]
    [InlineData("Lock UEFI Boot Order", true)]
    [InlineData("Prevent Boot Order Changes", true)]
    [InlineData("UEFI Boot Order", false)]
    public void Boot_order_lock_matcher(string name, bool expected) =>
        Assert.Equal(expected, BiosHardening.LooksLikeBootOrderLockSetting(name));

    [Fact]
    public void PlanStage_handles_all_matching_settings_and_skips_already_ok()
    {
        var settings = new List<(string name, string value, string[] possible)>
        {
            ("IPv4 PXE Boot", "Enable", new[] { "Enable", "Disable" }),
            ("IPv6 PXE Boot", "Disable", new[] { "Enable", "Disable" }),
            ("Fast Boot", "Enable", new[] { "Enable", "Disable" }),
            ("Network Boot Mode", "Normal", new[] { "Normal", "Special" }),
        };
        var plan = BiosHardening.PlanStage("networkBoot", settings);
        Assert.Equal(3, plan.Count);
        Assert.Equal("Disable", plan.Single(p => p.name == "IPv4 PXE Boot").newValue);
        Assert.False(plan.Single(p => p.name == "IPv4 PXE Boot").alreadyOk);
        Assert.True(plan.Single(p => p.name == "IPv6 PXE Boot").alreadyOk);
        var unknown = plan.Single(p => p.name == "Network Boot Mode");
        Assert.Null(unknown.newValue);       // "Normal" לא נבחר כ-disable
        Assert.NotNull(unknown.unknownMsg);
    }

    [Fact]
    public void PlanStage_marks_current_from_star_notation()
    {
        var plan = BiosHardening.PlanStage("usbBoot", new List<(string, string, string[])>
            { ("USB Storage Boot", "*Disable,Enable", new[] { "Disable", "Enable" }) });
        Assert.True(plan.Single().alreadyOk);
    }

    [Fact]
    public void Combine_is_failure_when_nothing_found_or_any_part_failed()
    {
        var none = BiosHardening.Combine("לא נמצא", new());
        Assert.False(none.success);
        Assert.Equal("לא נמצא", none.message);
        var mixed = BiosHardening.Combine("x", new() { (true, true, "a", "ok"), (true, false, "b", "bad") });
        Assert.False(mixed.success);
        var good = BiosHardening.Combine("x", new() { (true, true, "a", "ok") });
        Assert.True(good.success);
    }

    [Fact]
    public void Lenovo_parse_and_password_suffix()
    {
        var p = BiosHardening.ParseLenovoLine("BootOrderLock,Disable;[Optional:Disable,Enable]");
        Assert.Equal("BootOrderLock", p.name);
        Assert.Equal("Disable", p.value);
        Assert.Equal(new[] { "Disable", "Enable" }, p.possible);
        Assert.Equal("", BiosHardening.LenovoPwdSuffix(""));
        Assert.Equal(",pw1,ascii,us", BiosHardening.LenovoPwdSuffix("pw1"));
        Assert.Equal("<utf-16/>pw", BiosHardening.HpPwdArg("pw"));
        Assert.Equal("", BiosHardening.HpPwdArg(null));
    }

    [Fact]
    public void Dell_options_and_output_parsing()
    {
        Assert.Equal("uefinetworkstack=disabled", BiosHardening.DellOptionForStage("networkBoot"));
        Assert.Null(BiosHardening.DellOptionForStage("usbBoot"));
        Assert.Equal("Disabled", BiosHardening.DellParseValue("uefinetworkstack=Disabled\r\n", "uefinetworkstack"));
        Assert.False(BiosHardening.DellOutputOk("Error: password invalid"));
        Assert.True(BiosHardening.DellOutputOk("Success"));
    }
}
