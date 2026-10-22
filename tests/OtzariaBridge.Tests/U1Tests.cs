using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// סוכן U1 (הקשחת שירות, סבב QA עוין): טוקן חירום חד-פעמי (במקום pass-the-hash), מונה לחתימת ההגדרות, סיווג שגיאות ConfigSeal,
/// הגנת מפתח, ToolGuard לפי OriginalFilename, enable דרך ParsePolicy, סיבוב יומן, ושמירת מקורות Office לפני כתיבה.
/// </summary>
public class U1Tests
{
    // ---------- 1. טוקן שחרור חירום ----------

    [Fact]
    public void RecoveryToken_is_single_use()
    {
        // WHY: טוקן שנצרך פעם אחת לא ישחרר שוב (קובץ סימון שהועתק/הוקלט לא עובד פעמיים)
        var t = new RecoveryTokens();
        string tok = t.Mint(1000);
        Assert.True(t.Consume(tok, 2000));
        Assert.False(t.Consume(tok, 2001));
    }

    [Fact]
    public void RecoveryToken_expires_and_rejects_garbage()
    {
        var t = new RecoveryTokens();
        string tok = t.Mint(0);
        Assert.False(t.Consume("not-a-token", 10));
        Assert.False(t.Consume("", 10));
        Assert.False(t.Consume(null, 10));
        Assert.False(t.Consume(new string('x', 500), 10));
        Assert.False(t.Consume(tok, RecoveryTokens.TtlMs + 1)); // פג
    }

    [Fact]
    public void RecoveryToken_pool_is_bounded_and_tokens_are_random()
    {
        var t = new RecoveryTokens();
        var all = Enumerable.Range(0, RecoveryTokens.Max + 3).Select(_ => t.Mint(0)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.False(t.Consume(all[0], 1));   // הישן נדחק החוצה: הצפה לא מצטברת בלי גבול
        Assert.True(t.Consume(all[^1], 1));
    }

    [Fact]
    public void Hash_as_proof_is_not_accepted_while_service_runs()
    {
        // WHY (pass-the-hash): הוכחה = hash שמור. מי שקרא את ה-hash לא יכול לשחרר כשהשירות מותקן ורץ; רק במצב רך או כשהשירות מופסק בפועל
        Assert.True(Kiosk.ProofAcceptable(false, false, () => true));    // בלי שירות
        Assert.False(Kiosk.ProofAcceptable(false, false, () => false));
        Assert.False(Kiosk.ProofAcceptable(true, false, () => true));    // שירות מותקן ורץ: נדחה גם כשה-hash תואם
        Assert.True(Kiosk.ProofAcceptable(true, true, () => true));      // שירות מופסק
        Assert.False(Kiosk.ProofAcceptable(true, true, () => false));
    }

    [Fact]
    public void Token_marker_parses_and_proof_parser_rejects_it()
    {
        Assert.Equal("abc+/=", Kiosk.ParseTokenMarker(Kiosk.TokenMarkerPrefix + "abc+/="));
        Assert.Null(Kiosk.ParseTokenMarker("proof-v1\nabc"));
        Assert.Null(Kiosk.ParseTokenMarker("token-v1\na\nb"));
        Assert.Null(Kiosk.ParseTokenMarker("token-v1\n" + new string('x', 101)));
        Assert.Null(Kiosk.ParseTokenMarker(""));
        Assert.Null(Kiosk.ParseMarker(Kiosk.TokenMarkerPrefix + "abc"));
    }

    // ---------- 2. config-sign: הרשאות ומונה ----------

    [Fact]
    public void Seal_counter_rejects_rollback_and_forged_counter()
    {
        // WHY: config.json ישן שהיה חתום כדין לא יתקבל אחרי חתימה חדשה; מונה עתידי/אפס = זיוף; בלי מונה = רק לפני שהונפק מונה ראשון
        Assert.True(ConfigSealKey.CounterAccepts(0, null));
        Assert.False(ConfigSealKey.CounterAccepts(1, null));
        Assert.True(ConfigSealKey.CounterAccepts(5, 5));
        Assert.False(ConfigSealKey.CounterAccepts(5, 4));
        Assert.False(ConfigSealKey.CounterAccepts(5, 6));
        Assert.False(ConfigSealKey.CounterAccepts(0, 0));
    }

    [Fact]
    public void Seal_mac_is_bound_to_counter()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), data = { 1, 2, 3 };
        var m1 = ConfigSealKey.MacWithCounter(key, data, 1);
        Assert.NotEqual(m1, ConfigSealKey.MacWithCounter(key, data, 2));
        Assert.NotEqual(m1, ConfigSealKey.Mac(key, data));   // חתימה בלי מונה לא עוברת כחתימה עם מונה
        Assert.Equal(m1, ConfigSealKey.MacWithCounter(key, data, 1));
    }

    // ---------- 3. סיווג שגיאות ConfigSeal ----------

    [Theory]
    [InlineData("forbidden", "x", ConfigSeal.SealState.Down)]
    [InlineData("internal", "x", ConfigSeal.SealState.Down)]
    [InlineData("too_many", "x", ConfigSeal.SealState.Down)]
    [InlineData("wrong_password", "x", ConfigSeal.SealState.Down)]
    [InlineData("unknown_op", "פקודה לא מוכרת: config-sign", ConfigSeal.SealState.Unsupported)]
    [InlineData("bad_request", "פקודה לא מוכרת: config-sign", ConfigSeal.SealState.Unsupported)]   // שירות ישן: הקוד הכללי + ההודעה
    [InlineData("bad_request", "data אינו Base64 תקין", ConfigSeal.SealState.Down)]
    public void ConfigSeal_only_unknown_op_is_unsupported(string code, string message, ConfigSeal.SealState expected)
    {
        // WHY (fail-open): כל ok:false נחשב "שירות ישן" ופתח את האכיפה למשך 30 שניות; עכשיו רק פקודה לא מוכרת
        var res = new JsonObject { ["ok"] = false, ["code"] = code, ["message"] = message };
        Assert.Equal(expected, ConfigSeal.ClassifyError(res));
    }

    // ---------- 5. מפתח החתימה ----------

    [Fact]
    public void SealKey_is_not_overwritten_on_transient_io_error()
    {
        // WHY: נעילת קובץ זמנית (AV/גיבוי) דרסה מפתח תקין וביטלה את כל החתימות; עכשיו נזרק, והמפתח נשאר
        string dir = Path.Combine(Path.GetTempPath(), "u1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "config-seal.key");
            byte[] k = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(f, k);
            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => ConfigSealKey.LoadOrCreate(f));
            Assert.Equal(k, File.ReadAllBytes(f));
            Assert.Equal(k, ConfigSealKey.LoadOrCreate(f));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SealKey_is_created_when_missing_or_wrong_length()
    {
        string dir = Path.Combine(Path.GetTempPath(), "u1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "k");
            Assert.Equal(32, ConfigSealKey.LoadOrCreate(f).Length);
            File.WriteAllBytes(f, new byte[7]);
            Assert.Equal(32, ConfigSealKey.LoadOrCreate(f).Length);
            Assert.Equal(32, File.ReadAllBytes(f).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- 6. ToolGuard לפי OriginalFilename ----------

    [Theory]
    [InlineData("calc", @"C:\Users\kid\AppData\calc.exe", false, "Taskmgr.exe", true)]   // העתק בשם אחר
    [InlineData("zz", @"D:\x\zz.exe", false, "REGEDIT.EXE", true)]
    [InlineData("zz", @"D:\x\zz.exe", false, "MMC.EXE", true)]
    [InlineData("zz", @"D:\x\zz.exe", true, "Taskmgr.exe", false)]                         // מורם: נחסך
    [InlineData("zz", @"D:\x\zz.exe", false, "notepad.exe", false)]
    [InlineData("zz", @"D:\x\zz.exe", false, null, false)]
    [InlineData("taskmgr", @"C:\Windows\System32\taskmgr.exe", false, null, true)]         // הכלל הקיים לפי שם+נתיב נשמר
    public void ToolGuard_original_filename(string name, string path, bool elevated, string orig, bool expected) =>
        Assert.Equal(expected, ToolGuard.ShouldKill(name, path, elevated, @"C:\Windows", orig));

    // ---------- 7. enable עובר דרך ParsePolicy ----------

    [Fact]
    public void Enable_rejects_blocked_or_relative_otzaria_path()
    {
        var st = new JsonObject { ["otzariaPath"] = @"C:\Otz\otzaria.exe", ["otzariaSha256"] = new string('a', 64) };
        Assert.NotNull(HardLockService.ApplyEnableOtzariaPath(st, @"C:\Windows\System32\cmd.exe"));
        Assert.NotNull(HardLockService.ApplyEnableOtzariaPath(st, @"otzaria.exe"));
        Assert.NotNull(HardLockService.ApplyEnableOtzariaPath(st, @"C:\Otz\readme.txt"));
        Assert.Equal(@"C:\Otz\otzaria.exe", st["otzariaPath"]!.ToString());   // נדחה: המצב לא השתנה
        Assert.Equal(new string('a', 64), st["otzariaSha256"]!.ToString());
    }

    [Fact]
    public void Enable_clears_pin_only_when_path_changes()
    {
        // WHY: נעיצת SHA-256 שייכת לקובץ מסוים; נתיב אחר עם נעיצה ישנה = אימות מול הקובץ הלא נכון (או נעיצה שתאפשר החלפה)
        string sha = new('b', 64);
        var st = new JsonObject { ["otzariaPath"] = @"C:\Otz\otzaria.exe", ["otzariaSha256"] = sha };
        Assert.Null(HardLockService.ApplyEnableOtzariaPath(st, @"C:\Otz\otzaria.exe"));   // אותו נתיב
        Assert.Equal(sha, st["otzariaSha256"]!.ToString());
        Assert.Null(HardLockService.ApplyEnableOtzariaPath(st, @"C:\Other\otzaria.exe")); // נתיב שונה
        Assert.Equal(@"C:\Other\otzaria.exe", st["otzariaPath"]!.ToString());
        Assert.Equal("", st["otzariaSha256"]!.ToString());
        Assert.Null(HardLockService.ApplyEnableOtzariaPath(st, ""));                      // ריק: לא נוגעים
        Assert.Equal(@"C:\Other\otzaria.exe", st["otzariaPath"]!.ToString());
    }

    // ---------- 8. יומן השירות ----------

    [Fact]
    public void ServiceLog_rotates_instead_of_deleting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "u1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "service.log");
            File.WriteAllText(f, new string('x', 100));
            LogRotator.RotateIfLarge(f, 1000);
            Assert.True(File.Exists(f));   // קטן: לא מסובבים
            File.WriteAllText(f, new string('y', 2000));
            LogRotator.RotateIfLarge(f, 1000);
            Assert.False(File.Exists(f));
            Assert.Equal(2000, File.ReadAllText(f + ".1").Length);   // הראיות נשמרו
            File.WriteAllText(f, new string('z', 2000));
            LogRotator.RotateIfLarge(f, 1000);                       // סיבוב נוסף דורס .1 ולא נכשל
            Assert.Equal('z', File.ReadAllText(f + ".1")[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ServiceLog_throttles_duplicates_and_floods()
    {
        var t = new LogThrottle(dupWindowMs: 10_000, perMinute: 5);
        Assert.True(t.Allow("a", 0, out _));
        Assert.False(t.Allow("a", 100, out _));            // כפילות בחלון
        Assert.True(t.Allow("b", 200, out int sup));
        Assert.Equal(1, sup);                              // השורה הבאה מדווחת כמה נבלעו
        Assert.True(t.Allow("a", 300, out _));             // שונה מהאחרונה
        Assert.True(t.Allow("c", 400, out _));
        Assert.True(t.Allow("d", 500, out _));
        Assert.False(t.Allow("e", 600, out _));           // מעבר למכסת הדקה
        Assert.True(t.Allow("e", 61_000, out _));         // חלון חדש
        Assert.True(t.Allow("e", 80_000, out _));         // אותה שורה אחרי חלון הכפילות
    }

    // ---------- 9. Office: מקורות נשמרים לפני כתיבה ----------

    private sealed class OrderStore : OfficePolicy.IStore
    {
        public readonly Dictionary<string, int> D = new();
        public bool AnySet;
        public int? Get(string a, string n) => D.TryGetValue(a + "|" + n, out int v) ? v : null;
        public void Set(string a, string n, int v) { AnySet = true; D[a + "|" + n] = v; }
        public void Delete(string a, string n) => D.Remove(a + "|" + n);
    }

    [Fact]
    public void Office_originals_are_persisted_before_first_write()
    {
        // WHY: קריסה אחרי ה-Set הראשון ולפני שמירת המקורות השאירה את הערך שלנו כ"מקור"; עכשיו persist רץ לפני כל כתיבה
        var st = new OrderStore();
        st.D["Word|VBAWarnings"] = 2;
        var orig = new JsonObject();
        bool persisted = false;
        OfficePolicy.Apply(st, true, orig, null, () =>
        {
            Assert.False(st.AnySet);                                     // עוד לא נכתב כלום
            Assert.Equal(2, orig["Word|VBAWarnings"]!.GetValue<int>());  // המקור כבר רשום
            Assert.Equal(OfficePolicy.Absent, orig["Excel|AccessVBOM"]!.GetValue<int>());
            persisted = true;
        });
        Assert.True(persisted);
        Assert.Equal(4, st.D["Word|VBAWarnings"]);
    }

    [Fact]
    public void Office_does_not_write_when_persist_fails()
    {
        var st = new OrderStore();
        st.D["Word|VBAWarnings"] = 2;
        var issues = new List<string>();
        OfficePolicy.Apply(st, true, new JsonObject(), issues, () => throw new IOException("disk full"));
        Assert.False(st.AnySet);
        Assert.Equal(2, st.D["Word|VBAWarnings"]);   // הערך המקורי לא נדרס בלי מקור שמור
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Office_persist_is_not_called_again_once_originals_are_known()
    {
        var st = new OrderStore();
        var orig = new JsonObject();
        int n = 0;
        OfficePolicy.Apply(st, true, orig, null, () => n++);
        OfficePolicy.Apply(st, true, orig, null, () => n++);
        Assert.Equal(1, n);
    }
}
