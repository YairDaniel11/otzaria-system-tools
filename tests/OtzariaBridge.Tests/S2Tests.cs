using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>[s2] הקשחת מעטפת: אמון בצאצאים, Alt+F11, סריקת תהליכים, זיהוי דיאלוג קבצים, קובץ בלי סיומת, NeverKill, Helper, זהות אוצריא, Subject של Microsoft</summary>
public class S2Tests
{
    private sealed class Env : AppTrust.ITrustEnv
    {
        public HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AdminOnly = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AppTrust.SigInfo> Sigs = new(StringComparer.OrdinalIgnoreCase);
        public bool IsTrustedLocation(string p) => Trusted.Contains(p);
        public bool IsAdminOnlyLocation(string p) => AdminOnly.Contains(p) || Trusted.Contains(p);
        public AppTrust.SigInfo Sig(string p) => Sigs.TryGetValue(p, out var s) ? s : AppTrust.SigInfo.None;
        public string Sha256(string p) => "";
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static AppTrust.ProcInfo Alive(DateTime s) => new(true, s);
    private static readonly AppTrust.ProcInfo Dead = new(false, null);
    private const string Win = @"C:\Windows";

    // ---- 1: אמון בצאצא ----
    [Fact] public void Child_in_user_dir_unsigned_is_not_trusted() =>
        Assert.False(AppTrust.TreeChildTrusted(@"C:\Users\u\evil.exe", null, Win, new Env()));

    [Fact] public void Child_under_windows_is_not_trusted_even_if_admin_only()
    {
        var env = new Env(); env.AdminOnly.Add(@"C:\Windows\hh.exe");
        Assert.False(AppTrust.TreeChildTrusted(@"C:\Windows\hh.exe", null, Win, env));
    }

    [Fact] public void Child_in_admin_only_location_is_trusted()
    {
        var env = new Env(); env.AdminOnly.Add(@"D:\Apps\x\helper.exe");
        Assert.True(AppTrust.TreeChildTrusted(@"D:\Apps\x\helper.exe", null, Win, env));
    }

    [Fact] public void Child_signed_by_pinned_publisher_is_trusted_but_other_or_invalid_not()
    {
        var env = new Env(); string f = @"C:\Users\u\AppData\x.exe";
        env.Sigs[f] = new AppTrust.SigInfo(true, "CN=Good");
        Assert.True(AppTrust.TreeChildTrusted(f, new[] { "CN=Good" }, Win, env));
        Assert.False(AppTrust.TreeChildTrusted(f, new[] { "CN=Other" }, Win, env));
        env.Sigs[f] = new AppTrust.SigInfo(false, "CN=Good");
        Assert.False(AppTrust.TreeChildTrusted(f, new[] { "CN=Good" }, Win, env));
        Assert.False(AppTrust.TreeChildTrusted(null, new[] { "CN=Good" }, Win, env));
    }

    [Fact] public void Tree_untrusted_child_of_live_root_rejected_but_root_itself_accepted()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        var procs = new Dictionary<int, AppTrust.ProcInfo> { [100] = Alive(T0), [200] = Alive(T0.AddSeconds(5)) };
        var parents = new Dictionary<int, int> { [200] = 100 };
        Assert.False(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddSeconds(30), _ => false));
        Assert.True(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddSeconds(30), _ => true));
        Assert.True(set.IsTree(100, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddSeconds(30), _ => false));
    }

    // ---- 10: תוקף לכל חיי השורש ----
    [Fact] public void Live_root_child_accepted_after_hours_dead_root_expires()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        var procs = new Dictionary<int, AppTrust.ProcInfo> { [100] = Alive(T0), [200] = Alive(T0.AddHours(3)) };
        var parents = new Dictionary<int, int> { [200] = 100 };
        Assert.True(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddHours(3).AddMinutes(1), _ => true));
        procs[100] = Dead;
        Assert.False(set.IsTree(200, p => procs.GetValueOrDefault(p, Dead), p => parents.GetValueOrDefault(p), T0.AddHours(3).AddMinutes(1), _ => true));
    }

    [Fact] public void Remember_does_not_evict_live_root_after_ten_minutes()
    {
        var set = new AppTrust.LaunchedSet();
        set.Remember(100, T0, T0);
        set.Remember(101, T0.AddMinutes(30), T0.AddMinutes(30));
        Assert.True(set.Contains(100, Alive(T0), T0.AddMinutes(31)));
    }

    // ---- Blocked / מקשים ----
    [Theory]
    [InlineData("hh")] [InlineData("msdt")] [InlineData("cmstp")] [InlineData("odbcconf")] [InlineData("wordpad")] [InlineData("charmap")]
    [InlineData("regsvr32")] [InlineData("mshta")] [InlineData("wscript")] [InlineData("cscript")] [InlineData("rundll32")] [InlineData("installutil")] [InlineData("msbuild")]
    public void Vba_children_are_blocked_names(string n)
    {
        Assert.True(Programs.IsBlockedName(n));
        Assert.True(ShellPolicy.IsHostileName(n));
        Assert.NotEqual(ShellAction.Keep, ShellPolicy.Decide(n, true, false, false));
    }

    [Fact] public void Calc_is_deliberately_not_blocked() => Assert.False(Programs.IsBlockedName("calc"));

    [Theory] [InlineData(0x7Au)] [InlineData(0x77u)]
    public void Alt_F11_and_Alt_F8_blocked_everywhere(uint vk)
    {
        Assert.True(KioskKeys.ShouldBlock(vk, new KioskKeys.Ctx(true, false, false, false, false, true, false, false)));
        Assert.True(KioskKeys.ShouldBlock(vk, new KioskKeys.Ctx(true, false, true, false, true, false, false, false)));
        Assert.False(KioskKeys.ShouldBlock(vk, new KioskKeys.Ctx(false, false, false, false, false, true, false, false)));   // F11 רגיל (מסך מלא) לא נחסם
    }

    // ---- 2: החלטת הריגת תהליך ----
    [Fact] public void Process_sweep_decisions()
    {
        // (selfTree, otzaria, grandfathered, blocked, locTrusted, byList)
        Assert.False(Kiosk.ShouldKillProcess(true, false, false, true, false, false));    // צאצא הגשר פטור גם אם cmd
        Assert.False(Kiosk.ShouldKillProcess(false, true, false, false, false, false));   // אוצריא
        Assert.False(Kiosk.ShouldKillProcess(false, false, true, true, false, false));    // baseline
        Assert.True(Kiosk.ShouldKillProcess(false, false, false, true, true, false));     // wordpad ב-Windows: נהרג למרות מיקום מהימן
        Assert.True(Kiosk.ShouldKillProcess(false, false, false, false, false, false));   // exe לא מהימן בלי חלון
        Assert.False(Kiosk.ShouldKillProcess(false, false, false, false, false, true));   // תוכנה מהרשימה
        Assert.False(Kiosk.ShouldKillProcess(false, false, false, false, true, false));   // מיקום מנהל-בלבד
    }

    // ---- 8: NeverKill לפי אמון ----
    [Fact] public void NeverKill_requires_trusted_path()
    {
        Assert.True(ShellPolicy.NeverKill("explorer", true));
        Assert.False(ShellPolicy.NeverKill("explorer", false));   // explorer.exe מזויף בתיקיה כתיבה נהרג
        Assert.False(ShellPolicy.NeverKill("ApplicationFrameHost", false));
        Assert.False(ShellPolicy.NeverKill("cmd", true));
        Assert.True(ShellPolicy.NeedsTrust("ApplicationFrameHost"));
    }

    // ---- 5: PickerHost בכל מחלקה ----
    [Fact] public void PickerHost_killed_for_any_class_even_when_trusted()
    {
        foreach (string cls in new[] { "#32770", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", null })
        {
            Assert.Equal(ShellAction.Kill, ShellPolicy.Decide("PickerHost", true, false, false, cls));
            Assert.Equal(ShellAction.Kill, ShellPolicy.Decide("PickerHost", false, false, true, cls));
        }
    }

    // ---- 3: זיהוי דיאלוג קבצים לפי ילדים ----
    [Fact] public void File_dialog_detected_by_native_children_without_uia()
    {
        Assert.True(DialogGuard.LooksLikeFileDialogByChildren(new[] { "Static", "DUIViewWndClassName", "Button" }));
        Assert.True(DialogGuard.LooksLikeFileDialogByChildren(new[] { "SHELLDLL_DefView" }));
        Assert.False(DialogGuard.LooksLikeFileDialogByChildren(new[] { "Static", "Button", "Button" }));   // MessageBox
        Assert.False(DialogGuard.LooksLikeFileDialogByChildren(Array.Empty<string>()));
        Assert.False(DialogGuard.LooksLikeFileDialogByChildren(null));
    }

    // ---- 4: קובץ בלי סיומת ----
    private static readonly Func<string, bool> NoDirs = _ => false;
    private static Func<string, bool> Dirs(params string[] d) => p => d.Contains(p.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("evil", true)] [InlineData("evil.", true)] [InlineData(@"C:\Users\u\evil", true)] [InlineData("\"a.docx\" \"b\"", true)]
    [InlineData("report.docx", false)] [InlineData("*", false)] [InlineData("*.*", false)] [InlineData("Word (*.docx)", false)] [InlineData("", false)]
    public void Extensionless_file_is_a_violation(string raw, bool expected) =>
        Assert.Equal(expected, DialogGuard.HasExtensionlessFile(raw, @"C:\Docs", NoDirs));

    [Fact] public void Extensionless_existing_directory_is_navigation_not_violation()
    {
        Assert.False(DialogGuard.HasExtensionlessFile(@"C:\Docs\Sub", @"C:\Docs", Dirs(@"C:\Docs\Sub")));
        Assert.False(DialogGuard.HasExtensionlessFile("Sub", @"C:\Docs", Dirs(@"C:\Docs\Sub")));   // יחסי לתיקיה הנוכחית
        Assert.True(DialogGuard.HasExtensionlessFile("Sub", null, Dirs(@"C:\Docs\Sub")));          // תיקיה נוכחית לא ידועה: נכשלים סגור
    }

    [Fact] public void Noext_violation_is_debounced_not_immediate() => Assert.False(DialogGuard.IsImmediate("noext:evil"));

    // ---- 9: Subject של Microsoft ----
    [Theory]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=Microsoft Corporation, O=\"Microsoft Corporation\", C=US", true)]
    [InlineData("CN=Evil, O=Microsoft Corporation Evil", false)]
    [InlineData("CN=\"x, O=Microsoft Corporation\", O=Evil", false)]
    [InlineData("CN=Evil, OU=O=Microsoft Corporation", false)]
    [InlineData("", false)]
    public void Microsoft_subject_is_parsed_not_substring(string subject, bool expected) =>
        Assert.Equal(expected, AppTrust.IsMicrosoftSubject(subject));

    [Fact] public void Helper_by_signature_requires_admin_only_location_and_full_subject()
    {
        var env = new Env(); string exe = @"D:\Apps\z\z.exe", helper = @"D:\Apps\z\h.exe";
        env.Sigs[helper] = new AppTrust.SigInfo(true, "CN=Good");
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(helper, exe, @"D:\Apps\z", new[] { "z" }, "", "CN=Good", env));
        env.AdminOnly.Add(helper);
        Assert.Equal(AppTrust.MatchKind.Helper, AppTrust.MatchExe(helper, exe, @"D:\Apps\z", new[] { "z" }, "", "CN=Good", env));
        env.Sigs[helper] = new AppTrust.SigInfo(true, "CN=Good, O=Other");   // לא Subject מלא זהה
        Assert.Equal(AppTrust.MatchKind.None, AppTrust.MatchExe(helper, exe, @"D:\Apps\z", new[] { "z" }, "", "CN=Good", env));
    }

    // ---- 7: זהות אוצריא עם שירות ----
    [Fact] public void Otzaria_with_service_has_no_tofu_and_no_config_pin()
    {
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, null, "def", serviceControlled: true));   // היה AllowAndPin
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(false, "abc", "ABC", serviceControlled: true));   // נעיצה מהשירות
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(true, null, null, serviceControlled: true));      // מיקום מנהל-בלבד
        Assert.Equal(Kiosk.LaunchVerdict.AllowAndPin, Kiosk.DecideLaunch(false, null, "def"));                       // בלי שירות: ללא שינוי
    }
}
