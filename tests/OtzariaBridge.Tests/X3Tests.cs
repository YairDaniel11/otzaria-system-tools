using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>X3: שחזור אוטומטי לעדכון כושל + נעילת handle. WHY בכל בדיקה: הכשל שהיא מונעת.</summary>
public class X3Tests : IDisposable
{
    private readonly string _t = Path.Combine(Path.GetTempPath(), "x3-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_t, "rb");
    private string Exe => Path.Combine(_t, "OtzariaBridge.exe");
    public X3Tests() { Directory.CreateDirectory(_t); }
    public void Dispose() { HandleLock.Release(); try { Directory.Delete(_t, true); } catch { } }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private string Hash(string p) => UpdateRollback.Sha(p);

    private void Setup(out string newSha)
    {
        File.WriteAllText(Exe, "OLD-BRIDGE");
        newSha = "AB" + new string('0', 62);
        Assert.True(UpdateRollback.Prepare(Root, Exe, newSha, "9.9.9", "1.0.0", T0));
        File.WriteAllText(Exe, "NEW-BROKEN"); // המתקין החליף
    }

    [Fact] // WHY: בלי גיבוי אין חזרה; הגיבוי חייב להיות זהה למקור
    public void Prepare_backs_up_and_records_hash()
    {
        File.WriteAllText(Exe, "OLD");
        Assert.True(UpdateRollback.Prepare(Root, Exe, "AA", "2.0.0", "1.0.0", T0));
        var s = UpdateRollback.Load(Root);
        Assert.Equal(Hash(Exe), s.PrevSha);
    }

    [Fact] // WHY: התקנה ראשונה (אין exe) לא יוצרת מצב שחזור שמצביע לכלום
    public void Prepare_without_installed_exe_is_noop() =>
        Assert.False(UpdateRollback.Prepare(Root, Exe, "AA", "2.0.0", "1.0.0", T0));

    [Fact] // WHY: סימן תקינות חייב להתאים ל-hash של הגרסה החדשה, לא סתם קיום קובץ
    public void Evaluate_requires_matching_health_and_times_out()
    {
        var s = new UpdateRollback.State("P", "1", "NEWSHA", "2", T0);
        Assert.Equal(UpdateRollback.Verdict.Waiting, UpdateRollback.Evaluate(s, null, T0.AddSeconds(10), 60));
        Assert.Equal(UpdateRollback.Verdict.Waiting, UpdateRollback.Evaluate(s, "OTHER", T0.AddSeconds(10), 60));
        Assert.Equal(UpdateRollback.Verdict.Healthy, UpdateRollback.Evaluate(s, "newsha\n", T0.AddSeconds(10), 60));
        Assert.Equal(UpdateRollback.Verdict.Rollback, UpdateRollback.Evaluate(s, "OTHER", T0.AddSeconds(60), 60));
        Assert.Equal(UpdateRollback.Verdict.None, UpdateRollback.Evaluate(null, null, T0, 60));
    }

    [Fact] // WHY: גשר שאינו הגרסה הממתינה (hash אחר) לא יכול "לאשר" את העדכון
    public void ReportHealthy_only_for_expected_hash()
    {
        Setup(out var nsha);
        Assert.False(UpdateRollback.ReportHealthy(Root, "FF"));
        Assert.True(UpdateRollback.ReportHealthy(Root, nsha));
    }

    [Fact] // WHY: עדכון שלא עולה חייב לחזור אוטומטית לגרסה הקודמת
    public void Watch_rolls_back_when_no_health()
    {
        Setup(out _);
        var now = T0; bool stopped = false, started = false;
        var v = UpdateRollback.Watch(Root, Exe, 30, () => now, ms => now = now.AddMilliseconds(ms), () => stopped = true, () => started = true);
        Assert.Equal(UpdateRollback.Verdict.Rollback, v);
        Assert.True(stopped && started);
        Assert.Equal("OLD-BRIDGE", File.ReadAllText(Exe));
        Assert.Null(UpdateRollback.Load(Root));
    }

    [Fact] // WHY: עדכון תקין לא משוחזר ומצב נוקה
    public void Watch_keeps_new_when_healthy()
    {
        Setup(out var nsha);
        UpdateRollback.ReportHealthy(Root, nsha);
        var v = UpdateRollback.Watch(Root, Exe, 30, () => T0.AddSeconds(1), _ => { }, () => Assert.Fail("stop"), () => Assert.Fail("start"));
        Assert.Equal(UpdateRollback.Verdict.Healthy, v);
        Assert.Equal("NEW-BROKEN", File.ReadAllText(Exe));
        Assert.Null(UpdateRollback.Load(Root));
    }

    [Fact] // WHY: אי-downgrade זדוני: גיבוי שהוחלף בקובץ אחר (hash שונה מהנרשם) לא משוחזר
    public void Restore_refuses_tampered_backup()
    {
        Setup(out _);
        File.WriteAllText(Path.Combine(Root, UpdateRollback.BackupName), "EVIL-OLD-VULNERABLE");
        Assert.NotNull(UpdateRollback.Restore(Root, Exe));
        Assert.Equal("NEW-BROKEN", File.ReadAllText(Exe));
    }

    [Fact] // WHY: אין מצב = אין שחזור (לא משחזרים "סתם" מקובץ prev שנשאר)
    public void Restore_without_state_fails()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, UpdateRollback.BackupName), "x");
        Assert.NotNull(UpdateRollback.Restore(Root, Exe));
    }

    [Fact] // WHY: נעילה מונעת שינוי שם/מחיקה של ה-exe ושל התיקייה, ושחרור (uninstall/update) מחזיר את האפשרות
    public void HandleLock_blocks_rename_and_delete_until_released()
    {
        string dir = Path.Combine(_t, "data"); Directory.CreateDirectory(dir);
        File.WriteAllText(Exe, "exe");
        HandleLock.Acquire(Exe, dir);
        Assert.True(HandleLock.Held);
        Assert.ThrowsAny<IOException>(() => File.Move(Exe, Exe + ".x"));
        Assert.ThrowsAny<IOException>(() => File.Delete(Exe));
        Assert.ThrowsAny<IOException>(() => Directory.Move(dir, dir + "2"));
        File.WriteAllText(Path.Combine(dir, "cfg.json"), "ok"); // יצירת קבצים בתיקייה עדיין עובדת
        HandleLock.Release();
        Assert.False(HandleLock.Held);
        File.Move(Exe, Exe + ".x");
        Directory.Move(dir, dir + "2");
    }

    [Fact] // WHY: נועלים רק בנעילה פעילה, אחרת עדכון/הסרה לגיטימיים נחסמים
    public void ShouldHold_only_when_kiosk_active()
    {
        Assert.True(HandleLock.ShouldHold(true));
        Assert.False(HandleLock.ShouldHold(false));
    }
}
