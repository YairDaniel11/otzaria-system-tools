using System.Security.AccessControl;
using System.Security.Principal;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>הקשחות S1 (QA עוין): limiter, מצב אטומי/פגום, כלים, אתחול, סודות בשורת פקודה, סיסמה, ACL של התיקייה</summary>
public class S1Tests
{
    private static string Tmp()
    {
        string d = Path.Combine(Path.GetTempPath(), "s1t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- ממצא 5: probe לא נשרף ע"י בדיקה בלבד ----
    [Fact]
    public void CanTry_does_not_consume_probe_window()
    {
        var l = new AttemptLimiter();
        long t = 1_000_000;
        for (int i = 0; i < 5; i++) l.Fail("u", t);
        long later = t + 20_000;
        // תוקף שרק "בודק" בלולאה לא תופס את החלון: סיסמה נכונה (שלא מתועדת כ-Fail) עדיין עוברת
        for (int i = 0; i < 10; i++) Assert.True(l.CanTry("u", later, out _));
        l.Fail("u", later); // ניסיון שגוי בפועל צורך את החלון
        Assert.False(l.CanTry("u", later + 50, out _));
    }

    [Fact]
    public void Bridge_bucket_is_separate_from_raw_client_bucket()
    {
        string raw = ClientBuckets.Key("S-1-5-21-1", false, false);
        string bridge = ClientBuckets.Key("S-1-5-21-1", false, true);
        Assert.NotEqual(raw, bridge);
        var l = new AttemptLimiter();
        for (int i = 0; i < 20; i++) l.Fail(raw, 1000);
        Assert.True(l.CanTry(bridge, 1001, out _)); // תוקף גולמי לא חוסם את הגשר
        Assert.True(l.IsLocked(raw, 1001, out _));
        Assert.Contains("|elevated", ClientBuckets.Key("S", true, false));
    }

    // ---- ממצא 8 ----
    [Fact]
    public void Password_policy_matches_bridge_minimum()
    {
        Assert.Equal(Kiosk.MinPasswordLength, PasswordPolicy.MinLength);
        Assert.Equal(10, PasswordPolicy.MinLength);
        Assert.False(PasswordPolicy.IsValid("123456789"));
        Assert.True(PasswordPolicy.IsValid("1234567890"));
        Assert.False(PasswordPolicy.IsValid(null));
    }

    // ---- ממצא 6: מצב אטומי / פגום ----
    [Fact]
    public void State_missing_is_reported_as_missing()
    {
        string d = Tmp();
        try
        {
            var o = StateStore.Read(Path.Combine(d, "s.json"), out var st, out _);
            Assert.Null(o); Assert.Equal(StateLoad.Missing, st);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void State_corrupt_without_backup_is_corrupt_not_missing()
    {
        string d = Tmp();
        try
        {
            string f = Path.Combine(d, "s.json");
            File.WriteAllText(f, "{ not json");
            StateStore.Read(f, out var st, out _);
            Assert.Equal(StateLoad.Corrupt, st); // לא "Missing" (שהיה פותח)
            File.WriteAllText(f, ""); // קובץ ריק (קריסה באמצע כתיבה)
            StateStore.Read(f, out st, out _);
            Assert.Equal(StateLoad.Corrupt, st);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void State_write_is_atomic_and_keeps_last_good_backup()
    {
        string d = Tmp();
        try
        {
            string f = Path.Combine(d, "s.json");
            StateStore.WriteAtomic(f, "{\"v\":1}");
            StateStore.WriteAtomic(f, "{\"v\":2}");
            Assert.False(File.Exists(f + ".tmp"));
            Assert.Equal(2, StateStore.Read(f, out _, out _)["v"].GetValue<int>());
            // פגיעה בקובץ הראשי => משחזרים מהגיבוי (הערך הקודם התקין)
            File.WriteAllText(f, "garbage");
            var o = StateStore.Read(f, out var st, out bool restored);
            Assert.Equal(StateLoad.Ok, st); Assert.True(restored);
            Assert.Equal(1, o["v"].GetValue<int>());
            // כתיבה על קובץ פגום לא דורסת גיבוי טוב בפגום
            StateStore.WriteAtomic(f, "{\"v\":3}");
            Assert.Equal(1, JsonOf(f + ".bak"));
        }
        finally { Directory.Delete(d, true); }
    }

    private static int JsonOf(string path) => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))["v"].GetValue<int>();

    [Fact]
    public void State_main_deleted_restores_from_backup()
    {
        string d = Tmp();
        try
        {
            string f = Path.Combine(d, "s.json");
            StateStore.WriteAtomic(f, "{\"v\":1}");
            StateStore.WriteAtomic(f, "{\"v\":2}");
            File.Delete(f);
            var o = StateStore.Read(f, out var st, out bool restored);
            Assert.Equal(StateLoad.Ok, st); Assert.True(restored); Assert.NotNull(o);
        }
        finally { Directory.Delete(d, true); }
    }

    // ---- ממצא 3 ----
    [Fact]
    public void Boot_hardening_stays_while_configured_and_released_only_on_uninstall()
    {
        Assert.True(BootPolicy.ShouldHarden(configured: true, uninstalling: false));  // גם בפתיחה זמנית/מחוץ לחלון
        Assert.False(BootPolicy.ShouldHarden(configured: true, uninstalling: true));  // הסרה מלאה
        Assert.False(BootPolicy.ShouldHarden(configured: false, uninstalling: false)); // שירות שלא הוגדר: לא נוגעים
    }

    // ---- ממצא 2 ----
    [Theory]
    [InlineData("taskmgr", @"C:\Windows\System32\Taskmgr.exe", false, true)]
    [InlineData("regedit", @"C:\Windows\regedit.exe", false, true)]
    [InlineData("mmc", @"C:\Windows\SysWOW64\mmc.exe", false, true)]
    [InlineData("taskmgr", @"C:\Windows\System32\Taskmgr.exe", true, false)]   // מורם נחסך (חירום)
    [InlineData("mmc", null, false, false)]                                     // נתיב לא ידוע: לא הורגים
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", false, false)]  // לא ברשימה
    [InlineData("taskmgr", @"C:\Users\u\Downloads\taskmgr.exe", false, false)] // מחוץ ל-System32: לא נוגעים (false-positive נמוך)
    public void ToolGuard_decision(string name, string path, bool elevated, bool expected) =>
        Assert.Equal(expected, ToolGuard.ShouldKill(name, path, elevated, @"C:\Windows"));

    // ---- ממצא 4 ----
    [Fact]
    public void CliSecret_inline_value_parsing()
    {
        Assert.Null(CliSecret.InlineValue(new[] { "--unlock" }, "--unlock"));
        Assert.Null(CliSecret.InlineValue(new[] { "--unlock", "--quiet" }, "--unlock"));
        Assert.Null(CliSecret.InlineValue(new[] { "--unlock", "-" }, "--unlock"));
        Assert.Null(CliSecret.InlineValue(new[] { "--other" }, "--unlock"));
        Assert.Equal("ABC", CliSecret.InlineValue(new[] { "--unlock", "ABC" }, "--unlock"));
        Assert.Equal("pw", CliSecret.InlineValue(new[] { "--uninstall", "--password", "pw" }, "--password"));
    }

    // ---- ממצא 1 ----
    [Fact]
    public void Thread_hardening_interval_is_short_and_token_dacl_stays_gated()
    {
        Assert.True(ProcessDacl.ThreadHardenIntervalMs <= 250);
        Assert.False(ProcessDacl.UseTokenDefaultDacl); // כבוי עד בדיקה חיה (תהליכי-בן/אובייקטי סנכרון)
        // המסכה של חוט למשתמש לא כוללת TERMINATE/SUSPEND/SET_CONTEXT
        Assert.Equal(0u, ProcessDacl.UserThreadMask & (0x1 | 0x2 | 0x10));
    }

    // ---- ממצא 7 ----
    [Fact]
    public void DirSecurity_is_protected_admin_owned_and_without_user_access()
    {
        var ds = HardLock.BuildDirSecurity();
        Assert.True(ds.AreAccessRulesProtected);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            ds.GetOwner(typeof(SecurityIdentifier)));
        var sids = ds.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference)).ToList();
        Assert.Equal(2, sids.Count);
        Assert.Contains(sids, x => x.IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(sids, x => x.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    [Fact]
    public void Trusted_owner_is_system_or_admins_only()
    {
        Assert.True(HardLock.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(HardLock.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(HardLock.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(HardLock.IsTrustedOwner(null));
    }
}
