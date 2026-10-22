using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>[r1] אמון לפי נתיב+חתימה (לא שם), חלון הצילום, חסד חזית, מקשי מדיה/AltGr, ניווט בדיאלוג קבצים, מנעול הפעלה</summary>
public class R1TrustTests
{
    private sealed class Env : AppTrust.ITrustEnv
    {
        public HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AppTrust.SigInfo> Sigs = new(StringComparer.OrdinalIgnoreCase);
        public bool IsTrustedLocation(string p) => Trusted.Contains(p);
        public AppTrust.SigInfo Sig(string p) => Sigs.TryGetValue(p, out var s) ? s : AppTrust.SigInfo.None;
        public string Sha256(string p) => "";
    }
    private static readonly AppTrust.SigInfo Ms = new(true, "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond");

    [Theory]
    [InlineData("osk", ShellAction.Kill)] [InlineData("perfmon", ShellAction.Kill)] [InlineData("resmon", ShellAction.Kill)]
    [InlineData("dxdiag", ShellAction.Kill)] [InlineData("msinfo32", ShellAction.Kill)] [InlineData("explorer", ShellAction.Close)]
    public void Untrusted_by_name(string name, ShellAction expected) =>
        Assert.Equal(expected, ShellPolicy.Decide(name, false, false, false, "Progman"));

    [Theory]
    [InlineData("TabTip")] [InlineData("ShellHost")] [InlineData("ImeBroker")] [InlineData("dllhost")] [InlineData("SystemSettingsBroker")]
    public void New_critical_names_need_trust(string name)
    {
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide(name, true, false, false));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide(name, false, false, false));
    }

    [Fact] public void Explorer_shell_windows_kept_run_dialog_closed_even_during_snip()
    {
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide("explorer", true, false, false, "Progman"));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("explorer", true, false, false, "#32770"));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("explorer", true, false, true, "#32770"));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("explorer", true, false, false, "CabinetWClass"));
    }

    [Fact] public void Snip_exemption_limited_to_overlay_classes_and_to_the_window()
    {
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide("explorer", true, false, true, "Windows.UI.Core.CoreWindow"));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("explorer", true, false, false, "Windows.UI.Core.CoreWindow"));
        Assert.Equal(ShellAction.Keep, ShellPolicy.Decide("ShellExperienceHost", true, false, true, "Windows.UI.Core.CoreWindow"));
        Assert.Equal(ShellAction.Kill, ShellPolicy.Decide("ShellExperienceHost", true, false, true, "#32770"));
        Assert.Equal(ShellAction.Kill, ShellPolicy.Decide("ShellExperienceHost", true, false, false, "Windows.UI.Core.CoreWindow"));
    }

    [Fact] public void ApplicationFrameHost_and_explorer_are_never_escalated_to_kill()
    {
        Assert.True(ShellPolicy.NeverKill("ApplicationFrameHost.exe"));
        Assert.True(ShellPolicy.NeverKill("explorer"));
        Assert.False(ShellPolicy.NeverKill("cmd"));
        Assert.Equal(ShellAction.Close, ShellPolicy.Decide("ApplicationFrameHost", false, false, true));
    }

    [Fact] public void Trusted_system_requires_acl_location_and_microsoft_signature()
    {
        var e = new Env();
        string good = @"C:\Windows\System32\ctfmon.exe", temp = @"C:\Windows\Temp\ctfmon.exe";
        e.Trusted.Add(good); e.Sigs[good] = Ms; e.Sigs[temp] = Ms;
        Assert.True(ShellPolicy.IsTrustedSystem(good, e));
        Assert.False(ShellPolicy.IsTrustedSystem(temp, e));
        e.Sigs[good] = new AppTrust.SigInfo(true, "CN=Evil, O=Evil Ltd");
        Assert.False(ShellPolicy.IsTrustedSystem(good, e));
        Assert.False(ShellPolicy.IsTrustedSystem(null, e));
    }

    [Fact] public void Ms_package_needs_program_files_windowsapps_name_and_signature()
    {
        var e = new Env();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pkg = pf + @"\WindowsApps\Microsoft.ScreenSketch_11.2.0.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe";
        string fake = @"C:\Users\x\WindowsApps\Microsoft.ScreenSketch_11.2.0.0_x64__8wekyb3d8bbwe\SnippingTool.exe";
        e.Sigs[pkg] = Ms; e.Sigs[fake] = Ms;
        Assert.True(AppTrust.IsMicrosoftPackage(pkg, e));
        Assert.False(AppTrust.IsMicrosoftPackage(fake, e));
        Assert.Equal(AppTrust.MatchKind.Helper, AppTrust.MatchExe(pkg, @"shell:AppsFolder\x", null, new[] { "snippingtool" }, "", "", e));
        e.Sigs[pkg] = new AppTrust.SigInfo(false, "");
        Assert.False(AppTrust.IsMicrosoftPackage(pkg, e));
    }

    [Theory]
    [InlineData(0xA6u, true)] [InlineData(0xACu, true)] [InlineData(0xB4u, true)] [InlineData(0xB5u, true)] [InlineData(0xB7u, true)]
    [InlineData(0xADu, false)] [InlineData(0xB3u, false)] [InlineData(0x41u, false)]
    public void Launch_media_keys(uint vk, bool blocked) => Assert.Equal(blocked, KioskKeys.IsLaunchMediaKey(vk));

    [Fact] public void Media_key_blocked_everywhere() =>
        Assert.True(KioskKeys.ShouldBlock(0xB4, new KioskKeys.Ctx(false, false, false, false, true, false, false, false)));

    [Fact] public void AltGr_is_not_return_hotkey()
    {
        Assert.True(KioskKeys.IsReturnHotkey(0x4F, true, true, true, lAlt: true, rAlt: false));
        Assert.False(KioskKeys.IsReturnHotkey(0x4F, true, true, true, lAlt: false, rAlt: true));
        Assert.False(KioskKeys.IsReturnHotkey(0x4F, false, true, true, true, false));
    }

    [Fact] public void Enter_blocked_in_file_dialog_only_while_entry_unsafe()
    {
        Assert.True(KioskKeys.ShouldBlock(0x0D, new KioskKeys.Ctx(false, false, false, false, false, true, false, true, true)));
        Assert.False(KioskKeys.ShouldBlock(0x0D, new KioskKeys.Ctx(false, false, false, false, false, true, false, true, false)));
        Assert.False(KioskKeys.ShouldBlock(0x0D, new KioskKeys.Ctx(false, false, false, false, false, true, false, false, true)));
    }

    [Theory]
    [InlineData("shell:Downloads", true)] [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)]
    [InlineData(@"\\server\share", true)] [InlineData("%windir%", true)] [InlineData("..", true)] [InlineData("https://x.y", true)]
    [InlineData("report.docx", false)] [InlineData(@"C:\Users\x\a.docx", false)] [InlineData("Word (*.docx)", false)] [InlineData("100%", false)]
    public void Navigation_token(string raw, bool expected) => Assert.Equal(expected, DialogGuard.IsNavigationToken(raw));

    [Theory]
    [InlineData("This PC", true)] [InlineData("Network", true)] [InlineData("Control Panel", true)]
    [InlineData(@"C:\Users\x", false)] [InlineData("", false)] [InlineData(null, false)]
    public void Address_routing(string addr, bool expected) => Assert.Equal(expected, DialogGuard.AddressNeedsRouting(addr));

    [Fact] public void Nav_violation_is_immediate() => Assert.True(DialogGuard.IsImmediate("nav:shell:x"));

    [Fact] public void Grace_only_for_microsoft_auth_and_shell_processes()
    {
        var e = new Env();
        string cub = @"C:\Windows\System32\CredentialUIBroker.exe", evil = @"C:\Users\x\CredentialUIBroker.exe", chrome = @"C:\Windows\System32\chrome.exe";
        e.Trusted.Add(cub); e.Trusted.Add(chrome); e.Sigs[cub] = Ms; e.Sigs[evil] = Ms; e.Sigs[chrome] = Ms;
        Assert.True(Programs.GraceApplies("CredentialUIBroker", cub, e));
        Assert.False(Programs.GraceApplies("CredentialUIBroker", evil, e));
        Assert.False(Programs.GraceApplies("chrome", chrome, e));
        Assert.False(Programs.GraceApplies("anything", null, e));
    }

    [Theory]
    [InlineData("msedge")] [InlineData("chrome")] [InlineData("firefox")] [InlineData("perfmon")] [InlineData("msconfig")]
    [InlineData("resmon")] [InlineData("dxdiag")] [InlineData("msinfo32")] [InlineData("taskschd")] [InlineData("mmc")]
    public void Browsers_and_admin_tools_are_blocked_names(string n) => Assert.True(Programs.IsBlockedName(n));

    [Fact] public void Snip_alias_mapped_to_package_family()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "SnippingTool.exe");
        Assert.True(Programs.IsSnipAlias(alias));
        Assert.False(Programs.IsSnipAlias(@"C:\Users\x\SnippingTool.exe"));
        Assert.Equal("Microsoft.ScreenSketch_8wekyb3d8bbwe", Programs.PfnOfShellPath(Programs.SnipPfnPath));
    }

    [Fact] public void Launch_lock_blocks_replacement_and_hashes_the_handle()
    {
        string f = Path.Combine(Path.GetTempPath(), "r1lock_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(f, "hello");
        try
        {
            using (var l = AppTrust.LaunchLock.Open(f))
            {
                Assert.NotNull(l);
                string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("hello"))).ToLowerInvariant();
                Assert.Equal(expected, l.Sha256);
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(f, "evil"));
                Assert.ThrowsAny<IOException>(() => File.Delete(f));
                Assert.True(AppTrust.LaunchAllowedLocked(f, l.Sha256, l.Sha256, "", new Env()));
                Assert.False(AppTrust.LaunchAllowedLocked(f, l.Sha256, "00", "", new Env()));
            }
            File.WriteAllText(f, "ok");
        }
        finally { try { File.Delete(f); } catch { } }
    }

    [Fact] public void Admin_only_location_rejects_user_temp_file()
    {
        string f = Path.Combine(Path.GetTempPath(), "r1loc_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(f, "x");
        try { Assert.False(AppTrust.IsAdminOnlyLocation(f)); } finally { File.Delete(f); }
    }

    [Fact] public void Windows_catalog_signed_binary_is_microsoft_signed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string svc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
        if (!File.Exists(svc)) return;
        var s = AppTrust.GetSignature(svc);
        Assert.True(s.Valid, "svchost צריך להיות חתום תקף (משובץ או קטלוג)");
        Assert.True(AppTrust.IsMicrosoftSubject(s.Subject), s.Subject);
    }
}
