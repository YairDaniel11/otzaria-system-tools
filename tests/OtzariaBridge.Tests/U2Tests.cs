using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>[u2] תיקוני QA עוין: פטור עץ-גשר, פטור לפי שם, noext ב-Save/Open, baseline, אפליקציות Microsoft משתמש</summary>
public class U2Tests
{
    private sealed class Env : AppTrust.ITrustEnv
    {
        public HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AppTrust.SigInfo> Sigs = new(StringComparer.OrdinalIgnoreCase);
        public bool IsTrustedLocation(string p) => Trusted.Contains(p);
        public AppTrust.SigInfo Sig(string p) => Sigs.TryGetValue(p, out var s) ? s : AppTrust.SigInfo.None;
        public string Sha256(string p) => "";
    }
    private static readonly AppTrust.SigInfo Ms = new(true, "CN=Microsoft Corporation, O=Microsoft Corporation, C=US");

    // ---- 1: חסום לפני עץ; צאצא תוכנה מותרת לא "עוזר גשר" ----
    // (bridgeHelper, launchedDescendant, childTrusted, trustedSystem, otzaria, grandfathered, blocked, locTrusted, byList)
    [Fact] public void Blocked_descendant_of_launched_program_is_killed_even_in_bridge_tree() =>
        Assert.True(Kiosk.ShouldKillProcess(true, true, false, false, false, false, true, true, false));   // Word -> wscript/powershell בלי חלון

    [Fact] public void Blocked_direct_bridge_helper_is_still_exempt() =>
        Assert.False(Kiosk.ShouldKillProcess(true, false, false, false, false, false, true, false, false));   // powershell -NoWindow של הגשר עצמו

    [Fact] public void Blocked_outside_bridge_tree_is_killed() =>
        Assert.True(Kiosk.ShouldKillProcess(false, false, false, false, false, false, true, true, false));

    [Fact] public void Nonblocked_descendant_requires_child_trust_or_ms_system()
    {
        Assert.True(Kiosk.ShouldKillProcess(true, true, false, false, false, false, false, true, false));    // לא מהימן, גם אם "מיקום מהימן"
        Assert.False(Kiosk.ShouldKillProcess(true, true, true, false, false, false, false, false, false));   // TreeChildTrusted
        Assert.False(Kiosk.ShouldKillProcess(true, true, false, true, false, false, false, false, false));   // רכיב מערכת חתום Microsoft (WerFault)
    }

    [Fact] public void LaunchedSet_ancestor_detection_excludes_the_root_itself()
    {
        var set = new AppTrust.LaunchedSet();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        set.Remember(100, t0, t0);
        var procs = new Dictionary<int, (DateTime start, int parent)> { [100] = (t0, 1000), [200] = (t0.AddSeconds(5), 100), [300] = (t0.AddSeconds(9), 200), [400] = (t0.AddSeconds(2), 1000) };
        AppTrust.ProcInfo Probe(int p) => procs.TryGetValue(p, out var x) ? new(true, x.start) : new(false, null);
        int Parent(int p) => procs.TryGetValue(p, out var x) ? x.parent : 0;
        var now = t0.AddMinutes(1);
        Assert.False(set.HasLaunchedAncestor(100, Probe, Parent, now));   // השורש עצמו = התוכנה המותרת
        Assert.True(set.HasLaunchedAncestor(200, Probe, Parent, now));
        Assert.True(set.HasLaunchedAncestor(300, Probe, Parent, now));
        Assert.False(set.HasLaunchedAncestor(400, Probe, Parent, now));   // לא קשור
        Assert.True(set.AnyAlive(Probe, now));
        Assert.False(new AppTrust.LaunchedSet().AnyAlive(Probe, now));
    }

    // ---- 2: פטור סריקה לפי נתיב+חתימה, לא שם ----
    [Fact] public void Sweep_exempt_requires_trusted_microsoft_path()
    {
        var env = new Env();
        env.Trusted.Add(@"C:\Windows\System32\conhost.exe"); env.Sigs[@"C:\Windows\System32\conhost.exe"] = Ms;
        Assert.True(Kiosk.ProcSweepExemptFor("conhost", @"C:\Windows\System32\conhost.exe", env));
        Assert.False(Kiosk.ProcSweepExemptFor("conhost", @"C:\Users\u\AppData\conhost.exe", env));      // שם בלבד = התחזות
        env.Trusted.Add(@"C:\Tools\explorer.exe");                                                        // מיקום מהימן אבל לא חתום
        Assert.False(Kiosk.ProcSweepExemptFor("explorer", @"C:\Tools\explorer.exe", env));
        Assert.False(Kiosk.ProcSweepExemptFor("notepad", @"C:\Windows\System32\conhost.exe", env));    // שם לא ברשימה
    }

    // ---- 3: קובץ בלי סיומת ב-Open/Save ----
    private static readonly Func<string, bool> NoDirs = _ => false;
    private static Func<string, bool> Dirs(params string[] d) => p => d.Contains(p.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "pdf", "docx", "txt" };
    private static readonly HashSet<string> Danger = new(StringComparer.OrdinalIgnoreCase) { "exe", "bat", "ps1" };
    private static Func<string, string, IEnumerable<string>> Existing(params string[] files) =>
        (dir, stem) => files.Where(f => string.Equals(Path.GetDirectoryName(f), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(f).Equals(stem, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)));
    private static bool Noext(string raw, bool save, string typeExt, string folder, params string[] existing) =>
        DialogGuard.ExtensionlessViolation(raw, folder, save, typeExt, Allowed, Danger, NoDirs, Existing(existing));

    [Fact] public void Open_existing_extensionless_file_is_violation() => Assert.True(Noext("evil", false, null, @"C:\Docs", @"C:\Docs\evil"));
    [Fact] public void Open_nonexistent_name_is_not_violation() => Assert.False(Noext("evil", false, null, @"C:\Docs"));
    [Fact] public void Open_with_hidden_extension_resolves_real_extension_allowed() => Assert.False(Noext("report", false, null, @"C:\Docs", @"C:\Docs\report.docx"));
    [Fact] public void Open_with_hidden_extension_resolves_real_extension_dangerous() => Assert.True(Noext("report", false, null, @"C:\Docs", @"C:\Docs\report.exe"));
    [Fact] public void Open_relative_name_without_known_folder_fails_closed() => Assert.True(Noext("report", false, null, null));
    [Fact] public void Save_word_doc1_with_allowed_type_is_not_violation() => Assert.False(Noext("Doc1", true, "docx", @"C:\Docs"));
    [Fact] public void Save_without_known_or_allowed_type_is_violation()
    {
        Assert.True(Noext("Doc1", true, null, @"C:\Docs"));    // "כל הקבצים"/לא נקרא
        Assert.True(Noext("Doc1", true, "exe", @"C:\Docs"));   // סוג מסוכן
        Assert.True(Noext("Doc1", true, "xyz", @"C:\Docs"));   // לא ברשימה
    }
    [Theory] [InlineData("*")] [InlineData("*.*")] [InlineData("Word (*.docx)")] [InlineData("report.docx")] [InlineData("")]
    public void Patterns_and_explicit_extensions_are_not_noext(string raw) => Assert.False(Noext(raw, false, null, @"C:\Docs"));

    [Fact] public void Extensionless_existing_directory_is_navigation_not_violation()
    {
        Assert.False(DialogGuard.ExtensionlessViolation(@"C:\Docs\Sub", @"C:\Docs", false, null, Allowed, Danger, Dirs(@"C:\Docs\Sub"), Existing()));
        Assert.False(DialogGuard.ExtensionlessViolation("Sub", @"C:\Docs", false, null, Allowed, Danger, Dirs(@"C:\Docs\Sub"), Existing()));
        Assert.True(DialogGuard.ExtensionlessViolation("Sub", null, false, null, Allowed, Danger, Dirs(@"C:\Docs\Sub"), Existing()));
    }

    [Theory] [InlineData("Word Document (*.docx)", "docx")] [InlineData("Text (*.txt;*.log)", "txt")] [InlineData("All files (*.*)", null)] [InlineData("", null)]
    public void Filter_text_first_extension(string text, string expected) => Assert.Equal(expected, DialogGuard.FirstExtOfFilterText(text));

    [Fact] public void Save_dialog_title_detection()
    {
        Assert.True(DialogGuard.LooksLikeSaveTitle("Save As", ""));
        Assert.True(DialogGuard.LooksLikeSaveTitle("", "שמור"));
        Assert.False(DialogGuard.LooksLikeSaveTitle("Open", "Open"));
    }

    // ---- 4: זהות אוצריא = נתיב + SHA (החלטת הפעלה) ----
    [Fact] public void Writable_location_otzaria_needs_matching_pin()
    {
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, "AA", "BB", true));
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(false, "aa", "AA", true));
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, "AA", null, false));   // אין SHA (לא ניתן לנעול) = fail-closed
    }

    // ---- 6: אפליקציות Microsoft של משתמש (OneDrive/Teams) ----
    private const string Local = @"C:\Users\u\AppData\Local";
    [Fact] public void Microsoft_user_app_exempt_only_when_signed_known_dir_and_trusted_software_runs()
    {
        var env = new Env();
        string od = Local + @"\Microsoft\OneDrive\OneDrive.exe", fake = Local + @"\Temp\OneDrive.exe";
        env.Sigs[od] = Ms; env.Sigs[fake] = Ms;
        Assert.True(AppTrust.IsMicrosoftUserApp(od, Local, true, env));
        Assert.False(AppTrust.IsMicrosoftUserApp(od, Local, false, env));      // אין תוכנה מהימנה רצה
        Assert.False(AppTrust.IsMicrosoftUserApp(fake, Local, true, env));     // תיקיה לא מוכרת
        env.Sigs[od] = new AppTrust.SigInfo(true, "CN=Evil, O=Evil");
        Assert.False(AppTrust.IsMicrosoftUserApp(od, Local, true, env));       // חתימה לא של Microsoft
        env.Sigs[od] = AppTrust.SigInfo.None;
        Assert.False(AppTrust.IsMicrosoftUserApp(od, Local, true, env));       // לא חתום (שם בלבד לא מספיק)
    }
}
