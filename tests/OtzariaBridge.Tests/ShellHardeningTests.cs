using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>הקשחת מעטפת: החלטות מקשים, מדיניות חלונות, ונרמול שמות קבצים בדיאלוג (חלק טהור בלבד; דיאלוג חי נבדק ידנית)</summary>
public class ShellHardeningTests
{
    private static KioskKeys.Ctx C(bool alt = false, bool ctrl = false, bool shift = false, bool win = false,
        bool otz = false, bool allowed = false, bool browser = false, bool dlg = false) =>
        new(alt, ctrl, shift, win, otz, allowed, browser, dlg);

    [Theory]
    [InlineData(0x09)] [InlineData(0x1B)] [InlineData(0x73)] [InlineData(0x20)]
    public void Alt_combos_blocked_in_otzaria(uint vk) => Assert.True(KioskKeys.ShouldBlock(vk, C(alt: true, otz: true)));

    [Fact] public void Alt_shift_tab_blocked() => Assert.True(KioskKeys.ShouldBlock(0x09, C(alt: true, shift: true)));
    [Fact] public void Ctrl_shift_esc_blocked() => Assert.True(KioskKeys.ShouldBlock(0x1B, C(ctrl: true, shift: true, otz: true)));
    [Fact] public void Win_any_blocked() => Assert.True(KioskKeys.ShouldBlock(0x41, C(win: true, otz: true)));
    [Fact] public void Win_key_blocked() => Assert.True(KioskKeys.ShouldBlock(0x5B, C(otz: true)));
    [Fact] public void Alt_f4_allowed_only_in_allowed_program()
    {
        Assert.False(KioskKeys.ShouldBlock(0x73, C(alt: true, allowed: true)));
        Assert.True(KioskKeys.ShouldBlock(0x73, C(alt: true, otz: true)));
    }
    [Fact] public void Plain_typing_not_blocked() => Assert.False(KioskKeys.ShouldBlock(0x41, C(otz: true)));
    [Fact] public void Alt_shift_language_switch_not_blocked() => Assert.False(KioskKeys.ShouldBlock(0x10, C(alt: true)));

    [Fact] public void Menu_keys_blocked_outside_legit_programs()
    {
        Assert.True(KioskKeys.ShouldBlock(0x70, C()));
        Assert.True(KioskKeys.ShouldBlock(0x5D, C()));
        Assert.True(KioskKeys.ShouldBlock(0x79, C(shift: true)));
        Assert.False(KioskKeys.ShouldBlock(0x70, C(otz: true)));
        Assert.False(KioskKeys.ShouldBlock(0x5D, C(allowed: true)));
    }
    [Fact] public void Menu_keys_blocked_in_file_dialog_even_if_allowed_program() =>
        Assert.True(KioskKeys.ShouldBlock(0x5D, C(allowed: true, dlg: true)));
    [Fact] public void Ctrl_o_p_blocked_in_browser_only()
    {
        Assert.True(KioskKeys.ShouldBlock(0x4F, C(ctrl: true, allowed: true, browser: true)));
        Assert.True(KioskKeys.ShouldBlock(0x50, C(ctrl: true, allowed: true, browser: true)));
        Assert.False(KioskKeys.ShouldBlock(0x4F, C(ctrl: true, allowed: true)));
    }

    [Theory]
    [InlineData("osk", ShellAction.Kill)] [InlineData("SystemSettings", ShellAction.Kill)] [InlineData("WerFault.exe", ShellAction.Kill)]
    [InlineData("msedge", ShellAction.Kill)] [InlineData("ApplicationFrameHost", ShellAction.Close)]
    [InlineData("somethingnew", ShellAction.Close)]
    // [r1] Decide(name, trusted, pathUnknown, snip, cls): אמון נקבע מבחוץ (נתיב+ACL+חתימה); explorer בלי אמון = התחזות = Close
    public void Decide_by_name(string name, ShellAction expected) =>
        Assert.Equal(expected, ShellPolicy.Decide(name, false, false, false, "Progman"));

    [Fact] public void Critical_kept_only_when_trusted_or_elevated()
    {
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide("ctfmon", true, false, false));
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide("ctfmon", false, true, false));   // נתיב לא קריא = מוגבה מאיתנו
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("ctfmon", false, false, false)); // התחזות לפי שם
    }

    [Theory]
    [InlineData("evil.exe.", "exe")]
    [InlineData("evil.exe...  ", "exe")]
    [InlineData("evil.exe:stream", "exe")]
    [InlineData("evil.exe::$DATA", "exe")]
    [InlineData(@"C:\x\evil.exe.", "exe")]
    [InlineData("report.docx", "docx")]
    public void Extension_normalized(string raw, string ext) => Assert.Contains(ext, DialogGuard.ExtensionsOf(raw));

    [Fact] public void Multi_select_checks_every_item() =>
        Assert.Equal(new[] { "docx", "exe" }, DialogGuard.ExtensionsOf("\"a.docx\" \"b.exe.\""));
    [Fact] public void Spaced_name_with_dots_not_misblocked() =>
        Assert.Equal(new[] { "docx" }, DialogGuard.ExtensionsOf("v1.2 notes.docx"));
    [Fact] public void Dangerous_token_in_spaced_name_found() =>
        Assert.Contains("exe", DialogGuard.ExtensionsOf("a.docx evil.exe."));
    [Fact] public void Filter_pattern_ignored() => Assert.Empty(DialogGuard.ExtensionsOf("Word (*.docx)"));

    [Theory]
    [InlineData("locked", true)] [InlineData("path:c:/x", true)] [InlineData("danger:exe", true)] [InlineData("ext:d", false)]
    public void Debounce_only_for_unlisted_extension(string v, bool immediate) => Assert.Equal(immediate, DialogGuard.IsImmediate(v));

    [Fact] public void Baseline_denies_windows_unc_and_config_dir()
    {
        Assert.True(DialogGuard.KioskBaselineDenied(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\System32"));
        Assert.True(DialogGuard.KioskBaselineDenied(@"\server\share\x"));
        Assert.True(DialogGuard.KioskBaselineDenied(Config.Dir));
    }

    [Fact] public void Minimum_password_is_ten() => Assert.Equal(10, Kiosk.MinPasswordLength);
}
