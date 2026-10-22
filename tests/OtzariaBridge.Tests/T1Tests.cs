using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>T1: נקודות חיבור שירות↔גשר (נעיצת אוצריא, חתימת config, סימון חירום, כניסה מיידית, Office). WHY בכל בדיקה: הכשל שהיא חוסמת.</summary>
public class T1Tests
{
    // ---------- 1. נעיצת אוצריא בשירות ----------

    [Fact]
    public void Policy_snapshot_carries_otzaria_sha()
    {
        // WHY: בלי שדה בתמונה הגשר לא יכול לדעת מה השירות נעץ => אוצריא במיקום כתיב לא תעלה אף פעם
        var pol = HardLock.PolicySnapshot.From(new JsonObject { ["otzariaPath"] = @"C:\x\otzaria.exe", ["otzariaSha256"] = new string('a', 64) });
        Assert.Equal(new string('a', 64), pol.OtzariaSha256);
    }

    [Fact]
    public void ServicePinned_requires_same_path_and_nonempty_sha()
    {
        // WHY: נעיצה של קובץ אחר (נתיב שונה) או ריקה לא נחשבת; אחרת החלפת הנתיב הייתה "יורשת" נעיצה של קובץ אחר
        string sha = new string('b', 64);
        var pol = HardLock.PolicySnapshot.From(new JsonObject { ["otzariaPath"] = @"C:\x\otzaria.exe", ["otzariaSha256"] = sha });
        Assert.Equal(sha, Kiosk.ServicePinned(pol, @"C:\x\OTZARIA.exe"));
        Assert.Null(Kiosk.ServicePinned(pol, @"C:\y\otzaria.exe"));
        Assert.Null(Kiosk.ServicePinned(HardLock.PolicySnapshot.From(new JsonObject { ["otzariaPath"] = @"C:\x\otzaria.exe", ["otzariaSha256"] = "" }), @"C:\x\otzaria.exe"));
        Assert.Null(Kiosk.ServicePinned(null, @"C:\x\otzaria.exe"));
    }

    [Fact]
    public void Service_pin_allows_writable_otzaria_only_when_hash_matches_and_never_tofu()
    {
        // WHY: ההחלטה הקיימת (DecideLaunch) עם serviceControlled: בלי נעיצה - סירוב (אין TOFU); נעיצה תואמת - מותר; לא תואמת - סירוב
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, null, "AB", true));
        Assert.Equal(Kiosk.LaunchVerdict.Allow, Kiosk.DecideLaunch(false, "ab", "AB", true));
        Assert.Equal(Kiosk.LaunchVerdict.Refuse, Kiosk.DecideLaunch(false, "cd", "AB", true));
    }

    // ---------- 2. חתימת config ----------

    [Fact]
    public void ConfigSeal_mac_format_matches_bridge_contract()
    {
        // WHY: הגשר מאמת מול אותו פורמט; סטייה = כל החתימות נכשלות והמפתחות יורדים לברירות מחדל מחמירות
        var key = RandomNumberGenerator.GetBytes(32);
        var data = Encoding.UTF8.GetBytes("hello");
        var expect = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("otzaria-config-seal-v1|" + Convert.ToBase64String(data)));
        Assert.Equal(expect, ConfigSealKey.Mac(key, data));
        Assert.NotEqual(expect, ConfigSealKey.Mac(RandomNumberGenerator.GetBytes(32), data));
    }

    [Fact]
    public void ConfigSeal_key_is_created_once_and_regenerated_when_corrupt()
    {
        // WHY: מפתח יציב בין קריאות (אחרת כל חתימה נפסלת), ומפתח פגום לא משאיר את השירות בלי יכולת לחתום
        string dir = Path.Combine(Path.GetTempPath(), "t1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "config-seal.key");
            var k1 = ConfigSealKey.LoadOrCreate(f);
            Assert.Equal(32, k1.Length);
            Assert.Equal(k1, ConfigSealKey.LoadOrCreate(f));
            File.WriteAllBytes(f, new byte[5]);
            var k2 = ConfigSealKey.LoadOrCreate(f);
            Assert.Equal(32, k2.Length);
            Assert.NotEqual(k1, k2);
            Assert.False(File.Exists(f + ".tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(false, true, true, false, true)]    // גשר של השירות
    [InlineData(false, true, false, false, false)]  // גשר מנתיב השירות שלא הופעל ע"י השירות וללא סיסמה: סירוב (U1: נתיב לבדו לא מספיק)
    [InlineData(false, true, false, true, true)]    // מנתיב השירות עם סיסמת נעילה נכונה בבקשה
    [InlineData(false, false, false, true, false)]  // סיסמה בלי נתיב השירות: סירוב
    [InlineData(false, false, false, false, false)] // תהליך משתמש רגיל: סירוב תמיד (הצינור פתוח לכולם: זיוף חתימה)
    [InlineData(true, false, false, false, true)]   // מנהל מורם
    public void ClientMaySign_matrix(bool admin, bool pathOk, bool own, bool passwordOk, bool expected)
    {
        // WHY: config-sign הוא אורקל חתימה; בלי הגבלה כל תהליך משתמש היה חותם config מזויף
        Assert.Equal(expected, ConfigSealKey.ClientMaySign(admin, pathOk, own, passwordOk));
    }

    [Fact]
    public void SecurityKeys_cover_the_six_reader_keys()
    {
        // WHY: מפתח שקוראי Kiosk מסתמכים עליו ולא נכלל בקבוצה החתומה = עריכה חופשית בקובץ
        foreach (var k in new[] { "kiosk.readOnly", "kiosk.openSections", "kiosk.allowOpenExtensions", "kiosk.allowExcelDocs", "kiosk.strict", "kiosk.blockTaskManager" })
            Assert.Contains(k, ConfigSeal.SecurityKeys);
    }

    // ---------- 3. סימון חירום בלי קוד גלוי ----------

    [Fact]
    public void Recovery_proof_matches_stored_hash_in_both_formats_and_rejects_wrong_code()
    {
        // WHY: ההוכחה (במקום הקוד) חייבת להשתוות לגיבוב השמור גם כשהוא בפורמט הישן, וקוד אחר לא עובר
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string saltB = Convert.ToBase64String(salt);
        var proofs = Secret.RecoveryProofs("ABCDEFGHJKLM", saltB);
        Assert.True(Secret.ProofMatches(proofs, Secret.Hash("ABCDEFGHJKLM", salt)));
        Assert.True(Secret.ProofMatches(proofs, Secret.HashLegacy("ABCDEFGHJKLM", salt)));
        Assert.False(Secret.ProofMatches(Secret.RecoveryProofs("WRONGWRONGWR", saltB), Secret.Hash("ABCDEFGHJKLM", salt)));
        Assert.False(Secret.ProofMatches(proofs, ""));
        Assert.False(Secret.ProofMatches(null, "x"));
        Assert.Null(Secret.RecoveryProofs("ABC", null));
    }

    [Fact]
    public void Emergency_marker_never_contains_plain_code_and_plaintext_is_rejected()
    {
        // WHY: הקובץ בתיקיית המשתמש; הקוד הגלוי לא נכתב, וקובץ עם קוד גלוי (זיוף/גרסה ישנה) לא מתקבל כשחרור
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        var proofs = Secret.RecoveryProofs("ABCDEFGHJKLM", Convert.ToBase64String(salt));
        string content = Kiosk.MarkerPrefix + string.Join("\n", proofs);
        Assert.DoesNotContain("ABCDEFGHJKLM", content);
        Assert.Equal(proofs, Kiosk.ParseMarker(content.Trim()));
        Assert.Null(Kiosk.ParseMarker("ABCDEFGHJKLM"));
        Assert.Null(Kiosk.ParseMarker("proof-v1\n" + new string('x', 200)));
        Assert.Null(Kiosk.ParseMarker(""));
    }

    // ---------- 6. כניסה מיידית ----------

    [Fact]
    public void Immediate_relaunch_skips_session_grace_but_not_flood()
    {
        // WHY: אחרי Sign out/כניסה הגשר חייב לעלות מיד (בלי 3 שניות חסד), אבל לא להציף הפעלות בלולאה
        var t0 = DateTime.UtcNow;
        Assert.False(RelaunchPolicy.ShouldLaunch(t0.AddMilliseconds(100), t0, null, 0));
        Assert.True(RelaunchPolicy.ShouldLaunch(t0.AddMilliseconds(100), t0, null, 0, immediate: true));
        Assert.False(RelaunchPolicy.ShouldLaunch(t0.AddMilliseconds(600), t0, t0.AddMilliseconds(100), 1, immediate: true));
        Assert.True(RelaunchPolicy.ShouldLaunch(t0.AddSeconds(2), t0, t0.AddMilliseconds(100), 1, immediate: true));
    }

    [Fact]
    public void Session_trigger_covers_logon_and_unlock_only()
    {
        // WHY: כניסה וביטול נעילת מסך מרימים גשר; ניתוק/נעילה לא
        Assert.True(SessionTrigger.IsLogonLike(SessionChangeReason.SessionLogon));
        Assert.True(SessionTrigger.IsLogonLike(SessionChangeReason.SessionUnlock));
        Assert.False(SessionTrigger.IsLogonLike(SessionChangeReason.SessionLock));
        Assert.False(SessionTrigger.IsLogonLike(SessionChangeReason.SessionLogoff));
    }

    // ---------- 7. מדיניות Office ----------

    private sealed class FakeStore : OfficePolicy.IStore
    {
        public readonly Dictionary<string, int> D = new();
        public int? Get(string a, string n) => D.TryGetValue(a + "|" + n, out int v) ? v : null;
        public void Set(string a, string n, int v) => D[a + "|" + n] = v;
        public void Delete(string a, string n) => D.Remove(a + "|" + n);
    }

    [Fact]
    public void Office_policy_applies_and_restores_original_values()
    {
        // WHY: מאקרו מושבת בנעילה, ובשחרור המצב הקודם חוזר בדיוק (כולל "לא היה קיים"), כדי לא להשאיר מדיניות תקועה
        var st = new FakeStore();
        st.D["Word|VBAWarnings"] = 2;
        var orig = new JsonObject();
        Assert.True(OfficePolicy.Apply(st, true, orig));
        foreach (var app in OfficePolicy.Apps) { Assert.Equal(4, st.D[app + "|VBAWarnings"]); Assert.Equal(0, st.D[app + "|AccessVBOM"]); }
        // טיק נוסף: המקור לא נדרס בערך שלנו
        OfficePolicy.Apply(st, true, orig);
        Assert.Equal(2, orig["Word|VBAWarnings"]!.GetValue<int>());
        Assert.True(OfficePolicy.Apply(st, false, orig));
        Assert.Equal(2, st.D["Word|VBAWarnings"]);
        Assert.False(st.D.ContainsKey("Excel|VBAWarnings"));
        Assert.Empty(orig);
    }

    [Fact]
    public void Office_policy_repairs_tampering_and_reports_unverified_write()
    {
        // WHY: משתמש שמחזיר ערך (אם הצליח) מתוקן בטיק הבא; כתיבה שלא אומתה = ממצא גלוי, לא בליעה שקטה
        var st = new FakeStore();
        var orig = new JsonObject();
        OfficePolicy.Apply(st, true, orig);
        st.D["Excel|VBAWarnings"] = 1;
        OfficePolicy.Apply(st, true, orig);
        Assert.Equal(4, st.D["Excel|VBAWarnings"]);

        var bad = new BadStore();
        var issues = new List<string>();
        OfficePolicy.Apply(bad, true, new JsonObject(), issues);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Office_policy_does_not_overwrite_value_admin_changed_while_locked()
    {
        // WHY: בשחרור מחזירים רק ערך שהוא עדיין שלנו; שינוי מנהל מכוון בזמן הנעילה נשמר
        var st = new FakeStore();
        var orig = new JsonObject();
        OfficePolicy.Apply(st, true, orig);
        st.D["Word|VBAWarnings"] = 3;
        OfficePolicy.Apply(st, false, orig);
        Assert.Equal(3, st.D["Word|VBAWarnings"]);
    }

    private sealed class BadStore : OfficePolicy.IStore
    {
        public int? Get(string a, string n) => null;           // הקריאה החוזרת לא רואה את הכתיבה
        public void Set(string a, string n, int v) { }
        public void Delete(string a, string n) { }
    }
}
