using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>בדיקות יושרת השירות והגשר (HARDENING w1): זיהוי לפי נתיב, DACL, fail-closed, שעון, אזור זמן, סביבה והתקנה מאומתת</summary>
public class ServiceHardeningTests
{
    private const string Exe = @"C:\Program Files\OtzariaBridge\OtzariaBridge.exe";

    // ---------- 1. זיהוי הגשר לפי נתיב ----------

    [Fact]
    public void SessionsRunning_ignores_same_name_from_other_path()
    {
        var procs = new[]
        {
            new ProcInfo(10, 1, Exe),
            new ProcInfo(11, 2, @"C:\Users\x\AppData\Local\Temp\OtzariaBridge.exe"), // אותו שם, נתיב אחר
            new ProcInfo(12, 3, null),                                                // נתיב לא ידוע
        };
        var running = BridgeProcs.SessionsRunning(procs, Exe);
        Assert.Equal(new[] { 1 }, running.ToArray());
    }

    [Fact]
    public void SessionsRunning_path_compare_is_case_and_dot_insensitive()
    {
        var procs = new[] { new ProcInfo(1, 5, @"c:\program files\OTZARIABRIDGE\.\otzariabridge.EXE") };
        Assert.Contains(5, BridgeProcs.SessionsRunning(procs, Exe));
    }

    [Fact]
    public void Impostors_are_known_other_paths_only()
    {
        var procs = new[] { new ProcInfo(1, 1, Exe), new ProcInfo(2, 1, @"D:\fake\OtzariaBridge.exe"), new ProcInfo(3, 1, null) };
        var imp = BridgeProcs.Impostors(procs, Exe);
        Assert.Single(imp);
        Assert.Equal(2, imp[0].Pid);
    }

    // ---------- 2. חלון הרמה מחדש ----------

    [Fact]
    public void Relaunch_window_is_short_then_backs_off()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(2), t0, null, 0));        // חסד קצר
        Assert.True(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(3), t0, null, 0));         // 3 שניות (היה 15)
        Assert.False(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(5), t0, t0.AddSeconds(3), 1));
        Assert.True(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(6), t0, t0.AddSeconds(3), 1)); // 3 שניות בין ניסיונות (היה 20)
        Assert.False(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(20), t0, t0.AddSeconds(13), 5)); // אחרי 5 ניסיונות רצופים — איטי
        Assert.True(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(23), t0, t0.AddSeconds(13), 5));
        Assert.True(RelaunchPolicy.FastRetry < TimeSpan.FromSeconds(5));
    }

    // ---------- 3. DACL על תהליך הגשר ----------

    [Fact]
    public void User_masks_never_contain_dangerous_rights()
    {
        Assert.Equal(0u, ProcessDacl.UserProcessMask & ProcessDacl.ForbiddenProcessBits);
        Assert.Equal(0u, ProcessDacl.UserThreadMask & 0x1 /*THREAD_TERMINATE*/ & 0x2 /*SUSPEND_RESUME*/ & 0x10 /*SET_CONTEXT*/);
        Assert.Equal(0u, ProcessDacl.UserThreadMask & (0x1u | 0x2u | 0x10u | 0x40000u | 0x80000u));
        Assert.Equal(0x121400u, ProcessDacl.UserProcessMask);
    }

    [Fact]
    public void Sddl_is_protected_and_system_has_full_control()
    {
        string h = ProcessDacl.Sddl(ProcessDacl.UserProcessMask, true);
        Assert.StartsWith("D:P(A;;GA;;;SY)", h);
        Assert.Contains("(A;;GA;;;BA)", h);
        Assert.Contains("(A;;RC;;;OW)", h); // בלי זה הבעלים מקבל WRITE_DAC במובלע
        Assert.Contains("(A;;0x121400;;;AU)", h);
        Assert.Contains("(A;;GA;;;AU)", ProcessDacl.Sddl(ProcessDacl.UserProcessMask, false)); // מצב משוחרר
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);

    private static bool Denied(int pid, uint right)
    {
        IntPtr h = OpenProcess(right, false, pid);
        if (h != IntPtr.Zero) { CloseHandle(h); return false; }
        return Marshal.GetLastWin32Error() == 5;
    }

    [Fact]
    public void Protected_child_denies_terminate_write_thread_and_dac_but_allows_query()
    {
        // includeAdmins:false — כך שהבדיקה תקפה גם כשהיא רצה מורמת (אחרת ACE של Administrators נותן גישה למי שמריץ)
        string sddl = ProcessDacl.Sddl(ProcessDacl.UserProcessMask, true, includeAdmins: false);
        bool ok = Native.Launch(IntPtr.Zero, "cmd.exe /c ping -n 8 127.0.0.1 >nul", null, true, out int pid, out IntPtr hProc,
            closeHandles: false, procSddl: sddl);
        Assert.True(ok);
        try
        {
            Assert.True(Denied(pid, 0x0001), "PROCESS_TERMINATE");
            Assert.True(Denied(pid, 0x0002), "PROCESS_CREATE_THREAD");
            Assert.True(Denied(pid, 0x0020), "PROCESS_VM_WRITE");
            Assert.True(Denied(pid, 0x0008), "PROCESS_VM_OPERATION");
            Assert.True(Denied(pid, 0x40000), "WRITE_DAC");
            Assert.True(Denied(pid, 0x0800), "PROCESS_SUSPEND_RESUME");
            Assert.False(Denied(pid, 0x1000), "QUERY_LIMITED_INFORMATION חייב לעבוד (מנהל המשימות/סריקות)");
            Assert.False(Denied(pid, 0x100000), "SYNCHRONIZE חייב לעבוד (המתנה לתהליך)");
            // Process.Kill הרגיל נכשל
            using var p = Process.GetProcessById(pid);
            Assert.ThrowsAny<Exception>(() => p.Kill());
            Assert.False(p.HasExited);
        }
        finally
        {
            TerminateProcess(hProc, 0); // ה-handle שנוצר עם התהליך נושא הרשאה מלאה — מסלול ה"יציאה המורשית"
            CloseHandle(hProc);
        }
    }

    [Fact]
    public void Unprotected_child_can_be_killed_normally()
    {
        bool ok = Native.Launch(IntPtr.Zero, "cmd.exe /c ping -n 8 127.0.0.1 >nul", null, false, out int pid, out IntPtr hProc, closeHandles: false);
        Assert.True(ok);
        try
        {
            Assert.False(Denied(pid, 0x0001));
            using var p = Process.GetProcessById(pid);
            p.Kill();
            Assert.True(p.WaitForExit(5000));
        }
        finally { CloseHandle(hProc); }
    }

    [Fact]
    public void ApplyToSelf_hardens_then_releases_so_exit_is_not_stuck()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return; // מורם: ACE של Administrators נותן גישה
        int self = Environment.ProcessId;
        try
        {
            Assert.True(ProcessDacl.ApplyToSelf(true));
            Assert.True(Denied(self, 0x0001));
        }
        finally { Assert.True(ProcessDacl.ApplyToSelf(false)); }
        Assert.False(Denied(self, 0x0001)); // אחרי השחרור אפשר להרוג/לצאת שוב
        Assert.False(Denied(self, 0x40000));
    }

    [Fact]
    public void DecideDacl_hardens_only_when_enforced_and_fails_closed()
    {
        var active = new System.Text.Json.Nodes.JsonObject { ["active"] = true };
        var inactive = new System.Text.Json.Nodes.JsonObject { ["active"] = false };
        Assert.Null(HardLock.DecideDacl(false, active, true));      // לא גשר-שירות — לא נוגעים
        Assert.True(HardLock.DecideDacl(true, active, true));
        Assert.False(HardLock.DecideDacl(true, inactive, true));    // לא נאכף → משחררים (יציאה/הסרה עובדים)
        Assert.Null(HardLock.DecideDacl(true, null, true));         // שירות מותקן ולא עונה → נשארים כמו שהיינו
        Assert.False(HardLock.DecideDacl(true, null, false));       // שירות הוסר (sc delete) → משחררים, אין נעילה תקועה
    }

    // ---------- 4. fail-closed, מגבלות, lockout לפי לקוח ----------

    [Theory]
    [InlineData(true, true, true)]     // ה-pipe ענה: מוגדר
    [InlineData(false, true, false)]   // ענה, מותקן אך לא מוגדר (התקנה שנקטעה) → מקומי
    [InlineData(null, true, true)]     // לא ענה והשירות מותקן → נעול (fail-closed), לא hash מקומי
    [InlineData(null, false, false)]   // לא ענה ולא מותקן → מקומי
    public void Available_falls_closed_when_installed_but_pipe_silent(bool? configured, bool installed, bool expected)
    {
        Assert.Equal(expected, HardLock.DecideAvailable(configured, installed));
    }

    [Fact]
    public void Lockout_is_per_client_not_global()
    {
        var l = new AttemptLimiter();
        long t = 1_000_000;
        for (int i = 0; i < 5; i++) l.Fail("attacker", t + i);
        Assert.True(l.IsLocked("attacker", t + 10, out int secs));
        Assert.InRange(secs, 55, 60);
        Assert.False(l.IsLocked("S-1-5-21-admin|elevated", t + 10, out _)); // מנהל מורם לא חסום
        Assert.False(l.IsLocked("S-1-5-21-other", t + 10, out _));
        Assert.False(l.IsLocked("attacker", t + 61_000, out _)); // פג אחרי דקה
    }

    [Fact]
    public void Lockout_success_resets_and_old_failures_are_forgotten()
    {
        var l = new AttemptLimiter();
        long t = 5_000;
        for (int i = 0; i < 4; i++) l.Fail("u", t);
        l.Success("u");
        l.Fail("u", t + 1);
        Assert.False(l.IsLocked("u", t + 2, out _));
        for (int i = 0; i < 4; i++) l.Fail("v", t);
        l.Fail("v", t + 11 * 60_000); // 4 ישנים נשכחו, זה הראשון
        Assert.False(l.IsLocked("v", t + 11 * 60_000 + 1, out _));
    }

    [Fact]
    public void Lockout_table_is_bounded()
    {
        var l = new AttemptLimiter(cap: 16);
        for (int i = 0; i < 1000; i++) l.Fail("k" + i, i);
        Assert.False(l.IsLocked("k0", 5000, out _)); // לא התפוצץ; אין צמיחה ללא גבול
    }

    [Fact]
    public void ConnLimiter_caps_total_and_per_pid()
    {
        var c = new ConnLimiter(total: 5, perPid: 2);
        Assert.True(c.TryEnter(1)); Assert.True(c.TryEnter(1));
        Assert.False(c.TryEnter(1));                 // לפי PID
        Assert.True(c.TryEnter(2)); Assert.True(c.TryEnter(3)); Assert.True(c.TryEnter(4));
        Assert.False(c.TryEnter(5));                 // סך הכול
        c.Leave(1);
        Assert.True(c.TryEnter(5));
        c.Leave(99); // שחרור לא מוכר לא שובר ספירה
        Assert.False(c.TryEnter(6));
    }

    // ---------- 5. שעון ואזור זמן ----------

    [Fact]
    public void ClockGuard_detects_jump_over_two_minutes_only()
    {
        var g = new ClockGuard();
        Assert.False(g.Sample(1_000_000, 50_000));              // ראשונה
        Assert.False(g.Sample(1_005_000, 55_000));              // תקין
        Assert.False(g.Sample(1_005_000 + 5_000 + 119_000, 60_000)); // סטייה 119 שניות — בתוך הסובלנות
        Assert.True(g.Sample(1_005_000 - 600_000, 65_000));     // שעון אחורה ב-10 דקות
        Assert.True(g.Sample(9_000_000, 70_000));               // קדימה
        Assert.False(g.Sample(9_005_000, 75_000));              // אחרי הקפיצה חוזר לנורמלי
    }

    [Fact]
    public void Mono_seconds_left_is_independent_of_wall_clock_and_zero_when_unset()
    {
        long now = 10_000_000;
        long end = TempUnlock.EndTick(now, 30);
        Assert.Equal(1800, TempUnlock.MonoSecondsLeft(end, now));
        Assert.Equal(1, TempUnlock.MonoSecondsLeft(end, end - 200));
        Assert.Equal(0, TempUnlock.MonoSecondsLeft(end, end));
        Assert.Equal(0, TempUnlock.MonoSecondsLeft(0, now));                       // אחרי הפעלת שירות: נעול
        Assert.Equal(0, TempUnlock.MonoSecondsLeft(now + 3 * 3600_000L, now));     // רחוק מדי = פג
        Assert.Equal(3600, TempUnlock.MonoSecondsLeft(TempUnlock.EndTick(now, 999), now)); // מוגבל ל-60
    }

    private static TimeZoneInfo Tz(string id) { try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return null; } }

    [Fact]
    public void Schedule_window_follows_stored_timezone_not_machine_timezone()
    {
        var il = Tz("Israel Standard Time");
        var ny = Tz("Eastern Standard Time");
        if (il == null || ny == null) return;
        // 21:00 בישראל ב-1.12.2026 = 19:00 UTC = 14:00 בניו יורק
        var now = new DateTimeOffset(2026, 12, 1, 19, 0, 0, TimeSpan.Zero);
        Assert.True(Sched.InWindow("20:00", "23:00", now, il));
        Assert.False(Sched.InWindow("20:00", "23:00", now, ny)); // משתמש שהחליף אזור זמן לא יוצא מהחלון: החישוב נשאר לפי השמור
    }

    [Fact]
    public void Window_end_is_absolute_instant_in_schedule_timezone()
    {
        var il = Tz("Israel Standard Time");
        if (il == null) return;
        var now = new DateTimeOffset(2026, 12, 1, 19, 0, 0, TimeSpan.Zero); // 21:00 IST
        var end = Sched.CurrentWindowEnd("20:00", "02:00", now, il);       // חוצה חצות
        Assert.NotNull(end);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 2, 0, 0, TimeSpan.FromHours(2)), end.Value);
        Assert.Null(Sched.CurrentWindowEnd("20:00", "23:00", now.AddHours(5), il));
    }

    [Fact]
    public void ResolveTz_falls_back_to_local_for_unknown_or_empty_id()
    {
        Assert.NotNull(Sched.ResolveTz(null));
        Assert.NotNull(Sched.ResolveTz("No Such Zone"));
        Assert.False(string.IsNullOrEmpty(Sched.LocalTzId()));
    }

    // ---------- 6. סביבה והתקנה מאומתת ----------

    [Fact]
    public void Environment_filter_drops_dotnet_and_clr_injection_vars_only()
    {
        var src = new[]
        {
            "PATH=C:\\Windows", "DOTNET_STARTUP_HOOKS=C:\\evil.dll", "dotnet_gcServer=1", "COMPlus_ETWEnabled=0",
            "COR_ENABLE_PROFILING=1", "COR_PROFILER={X}", "CORECLR_PROFILER={Y}", "=C:=C:\\work", "CORPORATE=1", "MY_DOTNET_X=1",
        };
        var f = EnvBlock.Filter(src);
        Assert.Equal(new[] { "PATH=C:\\Windows", "=C:=C:\\work", "CORPORATE=1", "MY_DOTNET_X=1" }, f);
    }

    [Fact]
    public void Environment_block_roundtrips_through_native_format()
    {
        var entries = new[] { "A=1", "שם=ערך", "=C:=C:\\x" };
        IntPtr p = EnvBlock.Build(entries);
        try { Assert.Equal(entries, EnvBlock.Parse(p)); }
        finally { Marshal.FreeHGlobal(p); }
        IntPtr e = EnvBlock.Build(Array.Empty<string>());
        try { Assert.Empty(EnvBlock.Parse(e)); } finally { Marshal.FreeHGlobal(e); }
    }

    [Fact]
    public void Real_user_environment_block_has_no_blocked_vars_after_filter()
    {
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "x");
        try
        {
            var launched = new List<string>();
            // CreateEnvironmentBlock (token==Zero → משתמשים בסביבה הנוכחית כאן) — בודקים רק את הפילטר על הסביבה בפועל
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables()) launched.Add(kv.Key + "=" + kv.Value);
            Assert.DoesNotContain(EnvBlock.Filter(launched), e => e.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase));
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null); }
    }

    [Fact]
    public void CopyVerified_requires_matching_hash_for_user_writable_sources()
    {
        string dir = Path.Combine(Path.GetTempPath(), "otz-w1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.exe"), dst = Path.Combine(dir, "dst.exe");
            File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
            string good = InstallTrust.Sha256File(src);

            Assert.NotNull(InstallTrust.CopyVerified(src, dst, null));         // בלי גיבוב, מקור כתיב-משתמש → סירוב
            Assert.False(File.Exists(dst));
            Assert.NotNull(InstallTrust.CopyVerified(src, dst, new string('0', 64))); // גיבוב שגוי (הקובץ הוחלף)
            Assert.False(File.Exists(dst));
            Assert.Null(InstallTrust.CopyVerified(src, dst, good));            // גיבוב תואם → הועתק
            Assert.Equal(good, InstallTrust.Sha256File(dst));
            Assert.False(File.Exists(dst + ".new"));

            // החלפת הקובץ אחרי חישוב הגיבוב נתפסת
            File.WriteAllBytes(src, new byte[] { 9, 9, 9 });
            Assert.NotNull(InstallTrust.CopyVerified(src, dst, good));
            Assert.Equal(good, InstallTrust.Sha256File(dst)); // היעד לא נפגע
            // אותו נתיב — אין מה להעתיק
            Assert.Null(InstallTrust.CopyVerified(dst, dst, null));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void User_writable_location_detection()
    {
        Assert.True(InstallTrust.IsUserWritableLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge", "OtzariaBridge.exe")));
        Assert.False(InstallTrust.IsUserWritableLocation(Exe));
        Assert.False(InstallTrust.IsUserWritableLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "x.exe")));
        // מסלול עם ".." שמנסה להיראות כ-Program Files
        Assert.True(InstallTrust.IsUserWritableLocation(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\..\Users\x\a.exe"));
    }
}
