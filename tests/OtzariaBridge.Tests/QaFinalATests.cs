using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>תיקוני סקירת האיכות ההגנתית (qa-final-a): עדכון עצמי אטומי, Preflight, fail-closed בהסרה, קודי גיבוי, מונה חתימה, מסלול חירום.</summary>
public class QaFinalATests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "qa-final-a-" + Guid.NewGuid().ToString("N"));
    public QaFinalATests() { Directory.CreateDirectory(_d); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }
    private string F(string name, string content = null) { string p = Path.Combine(_d, name); if (content != null) File.WriteAllText(p, content); return p; }
    private static string Sha(string s) => InstallTrust.Sha256(System.Text.Encoding.UTF8.GetBytes(s));

    // ---- #1 מגבלת קצב והרשאה ----
    [Fact]
    public void RateGate_blocks_concurrent_and_rapid_repeat()
    {
        var g = new SvcUpdate.RateGate();
        Assert.True(g.TryEnter(1000));
        Assert.False(g.TryEnter(2000));                       // רץ כרגע
        g.Exit();
        Assert.False(g.TryEnter(1000 + SvcUpdate.RateGate.MinGapMs - 1)); // סיים אך צעיר מדי
        Assert.True(g.TryEnter(1000 + SvcUpdate.RateGate.MinGapMs));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void SelfUpdate_authorization(bool admin, bool own, bool pw, bool want) => Assert.Equal(want, SvcUpdate.Authorized(admin, own, pw));

    // ---- #2 החלפה אטומית + rollback ----
    [Fact]
    public void SwapExe_success_keeps_old_and_verifies_hash()
    {
        string exe = F("svc.exe", "OLD"), neu = F("svc.exe.new", "NEW"), old = exe + ".old";
        Assert.Null(SvcUpdate.SwapExe(exe, neu, old, Sha("NEW")));
        Assert.Equal("NEW", File.ReadAllText(exe));
        Assert.Equal("OLD", File.ReadAllText(old));
        Assert.False(File.Exists(neu));
    }

    [Fact]
    public void SwapExe_hash_mismatch_changes_nothing()
    {
        string exe = F("svc.exe", "OLD"), neu = F("svc.exe.new", "NEW");
        Assert.NotNull(SvcUpdate.SwapExe(exe, neu, exe + ".old", Sha("SOMETHING-ELSE")));
        Assert.Equal("OLD", File.ReadAllText(exe));
    }

    [Fact]
    public void SwapExe_second_move_failure_restores_original()
    {
        string exe = F("svc.exe", "OLD"), neu = F("svc.exe.new", "NEW");
        using (new FileStream(neu, FileMode.Open, FileAccess.Read, FileShare.None)) // החלפה שנייה תיכשל על קובץ נעול
            Assert.NotNull(SvcUpdate.SwapExe(exe, neu, exe + ".old", null));
        Assert.Equal("OLD", File.ReadAllText(exe));          // ה-exe הקודם הוחזר, השירות לא נשאר בלי קובץ
    }

    [Fact]
    public void UpdateGuard_rolls_back_after_unconfirmed_starts_when_old_hash_matches()
    {
        string exe = F("svc.exe", "OLD"), neu = F("svc.exe.new", "NEW"), old = exe + ".old";
        SvcUpdateGuard.Begin(_d, Sha("NEW"), Sha("OLD"));
        Assert.Null(SvcUpdate.SwapExe(exe, neu, old, Sha("NEW")));
        for (int i = 0; i < SvcUpdateGuard.MaxStarts; i++) Assert.Equal(SvcUpdateGuard.Outcome.Counted, SvcUpdateGuard.OnStart(_d, exe, old));
        Assert.Equal(SvcUpdateGuard.Outcome.RolledBack, SvcUpdateGuard.OnStart(_d, exe, old));
        Assert.Equal("OLD", File.ReadAllText(exe));
        Assert.Equal(SvcUpdateGuard.Outcome.None, SvcUpdateGuard.OnStart(_d, exe, old)); // הסמן נוקה
    }

    [Fact]
    public void UpdateGuard_confirm_clears_and_tampered_old_is_not_restored()
    {
        string exe = F("svc.exe", "NEW"), old = F("svc.exe.old", "EVIL");
        SvcUpdateGuard.Begin(_d, Sha("NEW"), Sha("OLD"));
        for (int i = 0; i < SvcUpdateGuard.MaxStarts; i++) SvcUpdateGuard.OnStart(_d, exe, old);
        Assert.Equal(SvcUpdateGuard.Outcome.RollbackFailed, SvcUpdateGuard.OnStart(_d, exe, old)); // hash של .old לא תואם לרשום
        Assert.Equal("NEW", File.ReadAllText(exe));
        SvcUpdateGuard.Begin(_d, Sha("NEW"), Sha("OLD"));
        SvcUpdateGuard.Confirm(_d);
        Assert.Equal(SvcUpdateGuard.Outcome.None, SvcUpdateGuard.OnStart(_d, exe, old));
    }

    [Fact]
    public void UpdateGuard_stale_marker_for_other_exe_is_ignored()
    {
        string exe = F("svc.exe", "SOMETHING-ELSE");
        SvcUpdateGuard.Begin(_d, Sha("NEW"), Sha("OLD"));
        Assert.Equal(SvcUpdateGuard.Outcome.None, SvcUpdateGuard.OnStart(_d, exe, exe + ".old"));
    }

    // ---- #3 Preflight לפני עצירת שירות ----
    [Fact]
    public void Preflight_refuses_user_writable_source_without_hash_and_writes_nothing()
    {
        string src = F("src.exe", "X"), dst = F("dst.exe", "D");
        Assert.NotNull(InstallTrust.Preflight(src, dst, null));
        Assert.Equal("D", File.ReadAllText(dst));
        Assert.False(File.Exists(dst + ".new"));
        Assert.NotNull(InstallTrust.Preflight(src, dst, "00" + Sha("X")[2..]));   // hash שגוי
        Assert.Null(InstallTrust.Preflight(src, dst, Sha("X")));                    // hash נכון: מותר
        Assert.Null(InstallTrust.Preflight(src, src, null));                        // אותו נתיב: אין מה להעתיק
    }

    // ---- #4 הסרה כש-pipe לא עונה ----
    [Fact]
    public void Uninstall_without_pipe_reply_fails_closed_when_service_installed()
    {
        Assert.NotNull(HardLock.UninstallNoReply(true));
        Assert.Null(HardLock.UninstallNoReply(false));
    }

    // ---- #5 קודי גיבוי ----
    [Theory]
    [InlineData(null, "none")] [InlineData("", "none")] [InlineData("garbage", "none")]
    [InlineData("print", "print")] [InlineData(" FILE ", "file")]
    public void BackupCodes_default_is_not_a_user_file(string mode, string want) => Assert.Equal(want, BackupCodes.ResolveMode(mode));

    [Fact]
    public void BackupCodes_file_only_on_removable_drive()
    {
        Assert.Equal(Path.GetFullPath(@"E:\codes"), BackupCodes.RemovableTarget(@"E:\codes", _ => DriveType.Removable));
        Assert.Null(BackupCodes.RemovableTarget(@"C:\Users\x\Documents", _ => DriveType.Fixed));
        Assert.Null(BackupCodes.RemovableTarget(@"\\server\share\x", _ => DriveType.Removable));
        Assert.Null(BackupCodes.RemovableTarget(null, _ => DriveType.Removable));
        Assert.Null(BackupCodes.RemovableTarget("   ", _ => DriveType.Removable));
    }

    // ---- #7 נעילה ותפס שחרור ב-BootHardening ----
    private sealed class MiniHost : BootHardening.IBootHost
    {
        public Dictionary<string, string> Bcd = new(StringComparer.OrdinalIgnoreCase) { ["recoveryenabled"] = "No", ["bootstatuspolicy"] = "IgnoreAllFailures", ["bootmenupolicy"] = "Standard" };
        public string State; public int Runs; public bool ReOn;
        public string Run(string file, string args)
        {
            Runs++;
            if (file == "reagentc.exe")
            {
                if (args == "/enable") ReOn = true; else if (args == "/disable") ReOn = false;
                return ReOn ? @"Windows RE location: \\?\GLOBALROOT\device\x" : "Windows RE status: Disabled\r\nWindows RE location:";
            }
            if (args.StartsWith("/set {current} ")) { var p = args.Split(' '); Bcd[p[2]] = p[3]; }
            else if (args == "/enum {current}")
                return "identifier              {current}\r\n" + string.Join("", Bcd.Select(kv => kv.Key.PadRight(24) + kv.Value + "\r\n"));
            return "ok";
        }
        public bool SafeBootExists(string set, string name) => false;
        public bool SafeBootCreate(string set, string name) => true;
        public bool SafeBootDelete(string set, string name) => true;
        public string ReadState() => State;
        public void WriteState(string json) => State = json;
        public void Log(string line) { }
    }

    [Fact]
    public void BootHardening_full_release_latches_against_late_reharden_until_allowed()
    {
        BootHardening.AllowReharden();
        var h = new MiniHost();
        var rel = BootHardening.EnforceLatched(h, false, true);
        Assert.NotNull(rel);
        Assert.Null(BootHardening.EnforceLatched(h, true, false));       // Tick מאוחר לא מחזיר הקשחה אחרי שחרור מלא
        BootHardening.AllowReharden();                                    // כשל הסרה: חוזרים לאכיפה
        Assert.NotNull(BootHardening.EnforceLatched(h, true, false));
        BootHardening.AllowReharden();
    }

    [Fact]
    public void BootHardening_full_release_without_state_restores_windows_defaults()
    {
        BootHardening.AllowReharden();
        var h = new MiniHost();                                           // אין מצב שמור (אבד), הערכים הם ערכי ההקשחה
        BootHardening.Enforce(h, false, full: true);
        Assert.Equal("Yes", h.Bcd["recoveryenabled"]);
        Assert.Equal("DisplayAllFailures", h.Bcd["bootstatuspolicy"]);
        BootHardening.AllowReharden();
    }

    // ---- #7/#11 כתיבה אטומית וגיבוי ----
    [Fact]
    public void StateStore_truncated_main_falls_back_to_backup_not_empty()
    {
        string p = F("config.json");
        StateStore.WriteAtomic(p, "{\"kiosk.active\":true}");
        StateStore.WriteAtomic(p, "{\"kiosk.active\":true,\"x\":1}");     // עכשיו .bak = הקודם התקין
        File.WriteAllText(p, "");                                          // הפסקת חשמל: קובץ ריק
        var o = StateStore.Read(p, out var st, out bool restored);
        Assert.Equal(StateLoad.Ok, st); Assert.True(restored);
        Assert.True(o["kiosk.active"]!.GetValue<bool>());
        File.WriteAllText(p + ".bak", "{garbage");
        StateStore.Read(p, out st, out _);
        Assert.Equal(StateLoad.Corrupt, st);                               // לא "ריק": מדווח כפגום
    }

    // ---- #8 מונה חתימה לכל זהות ----
    [Fact]
    public void SealCounter_is_per_identity_with_legacy_fallback()
    {
        var st = new JsonObject { ["sealCounter"] = 5L };
        Assert.Equal(5, ConfigSealKey.CounterFor(st, "S-1-A"));            // חתימות ישנות עדיין תקפות
        Assert.Equal(6, ConfigSealKey.Advance(st, "S-1-A"));
        Assert.Equal(6, ConfigSealKey.CounterFor(st, "S-1-A"));
        Assert.Equal(5, ConfigSealKey.CounterFor(st, "S-1-B"));            // חשבון אחר לא הושפע
        Assert.Equal(6, ConfigSealKey.Advance(st, "S-1-B"));
        Assert.Equal(7, ConfigSealKey.Advance(st, "S-1-A"));
        Assert.Equal(6, ConfigSealKey.CounterFor(st, "S-1-B"));
        Assert.Equal(1, ConfigSealKey.Advance(new JsonObject(), null));
    }

    // ---- #9 ServiceInstalled fail-closed ----
    [Fact]
    public void ServiceProbe_only_does_not_exist_means_not_installed()
    {
        Assert.False(HardLock.ProbeFromException(new Win32Exception(1060)));
        Assert.False(HardLock.ProbeFromException(new InvalidOperationException("x", new Win32Exception(1060))));
        Assert.Null(HardLock.ProbeFromException(new Win32Exception(5)));
        Assert.Null(HardLock.ProbeFromException(new InvalidOperationException("scm busy")));
        Assert.True(HardLock.DecideInstalled(null, null));                 // לא ידוע ולא היה ערך: מותקן
        Assert.True(HardLock.DecideInstalled(null, true));
        Assert.False(HardLock.DecideInstalled(null, false));
        Assert.False(HardLock.DecideInstalled(false, true));
        Assert.True(HardLock.DecideInstalled(true, false));
    }

    // ---- #10 הצמדה מחדש של מפתח ----
    [Fact]
    public void MacPairing_needed_when_service_unpaired_or_local_key_lost()
    {
        var paired = new JsonObject { ["macPaired"] = true };
        Assert.False(HardLock.NeedsMacPairing(paired, true));
        Assert.True(HardLock.NeedsMacPairing(paired, false));              // מפתח DPAPI אבד
        Assert.True(HardLock.NeedsMacPairing(new JsonObject { ["macPaired"] = false }, true));
    }

    // ---- #12 מסלול חירום במצב פגום ----
    [Fact]
    public void RecoveryCopy_matches_only_the_real_code_and_follows_changes()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string code = "ABCD1234EFGH";
        RecoveryCopy.Sync(_d, Secret.Hash(code, salt), Convert.ToBase64String(salt));
        Assert.True(RecoveryCopy.Matches(_d, code));
        Assert.False(RecoveryCopy.Matches(_d, "WRONGWRONG12"));
        Assert.False(RecoveryCopy.Matches(_d, ""));
        byte[] salt2 = RandomNumberGenerator.GetBytes(16);
        RecoveryCopy.Sync(_d, Secret.Hash("NEWNEWNEW123", salt2), Convert.ToBase64String(salt2));
        Assert.True(RecoveryCopy.Matches(_d, "NEWNEWNEW123"));
        Assert.False(RecoveryCopy.Matches(_d, code));
        RecoveryCopy.Sync(_d, "", "");                                     // ערך ריק לא דורס
        Assert.True(RecoveryCopy.Matches(_d, "NEWNEWNEW123"));
        Assert.False(RecoveryCopy.Matches(Path.Combine(_d, "none"), code)); // אין עותק = לא מתאים
    }

    // ---- #14 UpdateRollback ----
    [Fact]
    public void UpdateRollback_failed_restore_keeps_state_with_counter_until_cap()
    {
        string root = Path.Combine(_d, "rb"); Directory.CreateDirectory(root);
        string exe = F("bridge.exe", "NEWBRIDGE");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(root, UpdateRollback.StateName),
            JsonSerializer.Serialize(new UpdateRollback.State("PREV", "1.0.0", "NEWSHA", "2.0.0", t0)));   // אין קובץ גיבוי: השחזור ייכשל
        for (int i = 1; i < UpdateRollback.MaxRestoreAttempts; i++)
        {
            var v = UpdateRollback.Watch(root, exe, 30, () => t0.AddMinutes(5), _ => { }, () => { }, () => { });
            Assert.Equal(UpdateRollback.Verdict.Rollback, v);
            Assert.Equal(i, UpdateRollback.Load(root)!.Attempts);          // המצב נשמר לניסיון חוזר
        }
        UpdateRollback.Watch(root, exe, 30, () => t0.AddMinutes(5), _ => { }, () => { }, () => { });
        Assert.Null(UpdateRollback.Load(root));                             // אחרי המכסה: מנקים (לא תקועים לנצח)
    }

    [Fact]
    public void UpdateRollback_stale_pending_for_other_version_is_ignored()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var s = new UpdateRollback.State("P", "1", "NEWSHA", "2", t0);
        Assert.True(UpdateRollback.IsStale(s, "OTHER", t0.AddHours(2)));
        Assert.False(UpdateRollback.IsStale(s, "newsha", t0.AddHours(2)));  // זו הגרסה שממתינים לה
        Assert.False(UpdateRollback.IsStale(s, "OTHER", t0.AddMinutes(10))); // עדיין טרי
        Assert.False(UpdateRollback.IsStale(null, "OTHER", t0));
    }
}
