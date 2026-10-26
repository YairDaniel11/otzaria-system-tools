using System.Text.Json.Nodes;
using System.Text.Json;

namespace OtzariaBridge;

/// <summary>
/// זמני היום לשומר המסך.
/// המידע ההלכתי עצמו (זמני היום, תאריך עברי, שבת/חג) מגיע מלוח השנה המובנה של אוצריא עצמה
/// (ה-API ‏calendar.* בתוסף "הגדרות מערכת", החל מגרסת אוצריא 0.9.97) ולא מחושב כאן מחדש.
/// תוסף "הגדרות מערכת" (js/app.js) שולח POST ‏/kiosk/zmanim-cache מדי דקה עם מה שקיבל מאוצריא;
/// הגשר רק שומר את זה בזיכרון (לא בקובץ ההגדרות — זה מידע זמני שמתעדכן כל דקה) ומציג אותו.
/// אם הנתונים חסרים או ישנים מדי (מעל שעתיים — כנראה אוצריא סגורה או התוסף הושעה), מוצג "גיבוי"
/// המחושב כאן מקומית, בלי שבת/חג/עומר/יום-השנה (אלה דורשים לוח עברי אמיתי שאיננו רוצים לשכפל כאן).
///
/// לגיבוי יש שני מנועי חישוב אפשריים, לבחירת המנהל (kiosk.zmanimFallbackEngine, גם ב-js/sections/kiosk.js
/// בתוסף "הגדרות מערכת"): "noaa" (ברירת מחדל, ללא שינוי התנהגות למתקינים קיימים) — קירוב אסטרונומי
/// פשוט, הנץ/שקיעה בגובה פני הים בלבד; ו-"kosherjava" (חדש) — זמנים הלכתיים מלאים (גר"א/מג"א) כולל
/// תיקון גובה (issue #33, "תיקון גובה לזמנים"), אבל קוד צד-שלישי שעלול להיבדל מעט בזמנים גבוליים.
///
/// קרדיט: מנוע ה-kosherjava משתמש בחבילת ה-NuGet ‏"Zmanim" גרסה 1.5.0 — הפורט הרשמי ל-.NET/.NET Standard
/// של פרויקט KosherJava מאת Eliyahu Hershfeld (http://www.kosherjava.com/zmanim-project/),
/// ברישיון LGPL-2.1. זהו PackageReference רגיל (קישור דינמי), לא קוד מועתק/משוכתב לתוך הגשר —
/// ולכן אינו הופך את הקוד שלנו ל-LGPL, אך הקרדיט מוצג גם בחלון "אודות" וב-README.md כנדרש ברישיון.
/// </summary>
internal static class Zmanim
{
    private static readonly object Gate = new();
    private static string _cacheJson;
    private static DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    /// <summary>חודשים עבריים תקינים לשדה "יום השנה" בהגדרות (כולל אדר א/ב לבחירה מפורשת)</summary>
    internal static readonly string[] YahrzeitMonths =
        { "תשרי", "חשון", "כסלו", "טבת", "שבט", "אדר", "אדר א", "אדר ב", "ניסן", "אייר", "סיון", "תמוז", "אב", "אלול" };

    public static void Register() => Server.Route("/kiosk/zmanim-cache", CacheSet);

    /// <summary>תוסף "הגדרות מערכת" דוחף לכאן את מה שקיבל מ-calendar.getDailyTimes/getJewishDate באוצריא</summary>
    /// <summary>מבטל את מטמון הזמנים מאוצריא — אחרי שינוי עיר/מיקום/חו"ל, כדי שמסך המנוחה לא ימשיך
    /// להציג את זמני המיקום הקודם עד שהמטמון יפוג (באג #59: "ההגדרות לא נשמרות")</summary>
    public static void InvalidateCache() { lock (Gate) { _cacheJson = null; } }

    private static object CacheSet(JsonElement b)
    {
        string raw = b.GetRawText();
        if (raw.Length > 262144) throw BridgeException.BadRequest("מטמון הזמנים גדול מדי");   // QA-B 15: במקום עד 64MB בזיכרון
        lock (Gate) { _cacheJson = raw; _cacheUtc = DateTime.UtcNow; }
        return new { ok = true };
    }

    // ---------- מיקום מותאם אישית (קואורדינטות): נשאר כאפשרות מתקדמת גם כשנבחרה עיר מרשימה ----------

    public static (double lat, double lon)? ParseLocation(string s)
    {
        var parts = (s ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out double lat) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double lon)) return null;
        if (lat < -66 || lat > 66 || lon < -180 || lon > 180) return null; // מעבר לחוג הקוטב אין הנץ/שקיעה בכל יום
        return (lat, lon);
    }

    /// <summary>שורות לשומר המסך: זמני היום, כניסת שבת/חג, ספירת העומר, יום השנה, פרשת השבוע ודף יומי — או מערך ריק</summary>
    // #33: התראה לפני זמן נבחר (kiosk.zmanimAlert = מזהה זמן, kiosk.zmanimAlertMinutes = דקות לפני)
    private static System.Threading.Timer _alertTimer;
    public static void StartAlerts() => _alertTimer ??= new System.Threading.Timer(_ => { try { CheckAlert(DateTime.Now); } catch (Exception ex) { Config.Log("zmanim alert: " + ex.Message); } }, null, 15_000, 60_000);

    private static readonly HashSet<string> _alerted = new();
    private static void CheckAlert(DateTime now)
    {
        // משוב 15.10: כמה זמנים מופרדים בפסיקים
        string ids = Config.Get("kiosk.zmanimAlert", "");
        if (string.IsNullOrWhiteSpace(ids)) return;
        int mins = Math.Clamp(Config.Get("kiosk.zmanimAlertMinutes", 10), 1, 180);
        var cached = TryGetCache();
        Dictionary<string, string> loc = null;
        foreach (var raw in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var z = KnownZmanim.FirstOrDefault(k => k.id.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (z.id == null) continue;
            string val = null;
            if (cached != null) TryPick(WithMissing(cached.Value.Item1, now), out val, z.keys);
            if (val == null) { try { loc ??= KosherJavaTimes(now); foreach (var k in z.keys) if (loc.TryGetValue(k, out val)) break; } catch { } }
            if (val == null || !TimeSpan.TryParse(Hm(val), System.Globalization.CultureInfo.InvariantCulture, out var t)) continue;
            var at = now.Date + t;
            string key = z.id + "|" + at.ToString("yyyy-MM-dd HH:mm");
            if (now < at && now >= at.AddMinutes(-mins) && _alerted.Add(key))
            {
                int left = (int)Math.Ceiling((at - now).TotalMinutes);
                Tray.Notify(Lang.T("זמני היום", "Zmanim"), Lang.T($"{z.he} בעוד {left} דקות ({Hm(val)})", $"{z.en} in {left} minutes ({Hm(val)})"), ToolTipIcon.Info);
            }
        }
        if (_alerted.Count > 200) _alerted.Clear();
    }

    public static string[] Sections(DateTime now)
    {
        var list = new List<string>();
        var cached = TryGetCache();
        if (cached != null)
        {
            var (dailyTimes, jdToday, jdTomorrow) = cached.Value;
            string cal = CalendarLine(jdToday);
            if (cal != null) list.Add(cal);
            string zm = ZmanimLine(WithMissing(dailyTimes, now));
            if (zm != null) list.Add(zm);
            string ch = CandleHavdalaLine(now, dailyTimes, jdToday, jdTomorrow);
            if (ch != null) list.Add(ch);
            string omer = OmerLine(jdToday);
            if (omer != null) list.Add(omer);
            string yz = YahrzeitLine(jdToday);
            if (yz != null) list.Add(yz);
        }
        else
        {
            string fb = FallbackLine(now);
            if (fb != null) list.Add(fb);
        }
        // פרשת השבוע ודף יומי: לא תלויים במטמון מאוצריא (וגם לא בהגדרת מיקום) — מחושבים תמיד ישירות
        // דרך ספריית Zmanim/KosherJava (קרדיט למעלה) מהתאריך הנוכחי בלבד. כל אחד עם מתג נפרד, כבוי כברירת מחדל.
        string ps = ParshaLine(now);
        if (ps != null) list.Add(ps);
        string df = DafYomiLine(now);
        if (df != null) list.Add(df);
        return list.ToArray();
    }

    // ---------- פרשת השבוע: מחושבת דרך JewishCalendar.GetParshah (API ציבורי של ספריית Zmanim/KosherJava) ----------
    // הספרייה (פורט .NET, גרסה 1.5.0) אינה חושפת method ציבורי לשם הפרשה בעברית (יש רק TransliteratedParshiosList
    // לאנגלית, ושדה hebrewParshiyos פנימי/פרטי בלבד) — ולכן שמות התצוגה כאן הם טבלה קבועה שלנו (כמו DayWords/WeekWords
    // למעלה), לא חישוב עצמאי של הפרשה עצמה. את הפרשה עצמה (איזה שבוע) מחשבת הספרייה, לא אנחנו.

    private static readonly Dictionary<string, (string he, string en)> ParshaNames = new()
    {
        ["BERESHIS"] = ("בראשית", "Bereshis"), ["NOACH"] = ("נח", "Noach"), ["LECH_LECHA"] = ("לך לך", "Lech Lecha"),
        ["VAYERA"] = ("וירא", "Vayera"), ["CHAYEI_SARA"] = ("חיי שרה", "Chayei Sara"), ["TOLDOS"] = ("תולדות", "Toldos"),
        ["VAYETZEI"] = ("ויצא", "Vayetzei"), ["VAYISHLACH"] = ("וישלח", "Vayishlach"), ["VAYESHEV"] = ("וישב", "Vayeshev"),
        ["MIKETZ"] = ("מקץ", "Miketz"), ["VAYIGASH"] = ("ויגש", "Vayigash"), ["VAYECHI"] = ("ויחי", "Vayechi"),
        ["SHEMOS"] = ("שמות", "Shemos"), ["VAERA"] = ("וארא", "Vaera"), ["BO"] = ("בא", "Bo"),
        ["BESHALACH"] = ("בשלח", "Beshalach"), ["YISRO"] = ("יתרו", "Yisro"), ["MISHPATIM"] = ("משפטים", "Mishpatim"),
        ["TERUMAH"] = ("תרומה", "Terumah"), ["TETZAVEH"] = ("תצוה", "Tetzaveh"), ["KI_SISA"] = ("כי תשא", "Ki Sisa"),
        ["VAYAKHEL"] = ("ויקהל", "Vayakhel"), ["PEKUDEI"] = ("פקודי", "Pekudei"), ["VAYIKRA"] = ("ויקרא", "Vayikra"),
        ["TZAV"] = ("צו", "Tzav"), ["SHMINI"] = ("שמיני", "Shmini"), ["TAZRIA"] = ("תזריע", "Tazria"),
        ["METZORA"] = ("מצרע", "Metzora"), ["ACHREI_MOS"] = ("אחרי מות", "Achrei Mos"), ["KEDOSHIM"] = ("קדושים", "Kedoshim"),
        ["EMOR"] = ("אמור", "Emor"), ["BEHAR"] = ("בהר", "Behar"), ["BECHUKOSAI"] = ("בחקתי", "Bechukosai"),
        ["BAMIDBAR"] = ("במדבר", "Bamidbar"), ["NASSO"] = ("נשא", "Nasso"), ["BEHAALOSCHA"] = ("בהעלתך", "Beha'aloscha"),
        ["SHLACH"] = ("שלח לך", "Shlach"), ["KORACH"] = ("קרח", "Korach"), ["CHUKAS"] = ("חוקת", "Chukas"),
        ["BALAK"] = ("בלק", "Balak"), ["PINCHAS"] = ("פינחס", "Pinchas"), ["MATOS"] = ("מטות", "Matos"),
        ["MASEI"] = ("מסעי", "Masei"), ["DEVARIM"] = ("דברים", "Devarim"), ["VAESCHANAN"] = ("ואתחנן", "Vaeschanan"),
        ["EIKEV"] = ("עקב", "Eikev"), ["REEH"] = ("ראה", "Re'eh"), ["SHOFTIM"] = ("שופטים", "Shoftim"),
        ["KI_SEITZEI"] = ("כי תצא", "Ki Seitzei"), ["KI_SAVO"] = ("כי תבוא", "Ki Savo"), ["NITZAVIM"] = ("ניצבים", "Nitzavim"),
        ["VAYEILECH"] = ("וילך", "Vayeilech"), ["HAAZINU"] = ("האזינו", "Haazinu"), ["VZOS_HABERACHA"] = ("וזאת הברכה", "Vezos Haberacha"),
        ["VAYAKHEL_PEKUDEI"] = ("ויקהל פקודי", "Vayakhel-Pekudei"), ["TAZRIA_METZORA"] = ("תזריע מצרע", "Tazria-Metzora"),
        ["ACHREI_MOS_KEDOSHIM"] = ("אחרי מות קדושים", "Achrei Mos-Kedoshim"), ["BEHAR_BECHUKOSAI"] = ("בהר בחקתי", "Behar-Bechukosai"),
        ["CHUKAS_BALAK"] = ("חוקת בלק", "Chukas-Balak"), ["MATOS_MASEI"] = ("מטות מסעי", "Matos-Masei"),
        ["NITZAVIM_VAYEILECH"] = ("ניצבים וילך", "Nitzavim-Vayeilech"), ["SHKALIM"] = ("שקלים", "Shekalim"),
        ["ZACHOR"] = ("זכור", "Zachor"), ["PARA"] = ("פרה", "Para"), ["HACHODESH"] = ("החודש", "HaChodesh"),
    };

    /// <summary>
    /// פרשת השבת הקרובה (היום עצמו אם שבת, אחרת השבת הבאה): JewishCalendar.GetParshah מחשב איזו פרשה זו
    /// לפי התאריך העברי האמיתי (כולל הבדלי ארץ/חו"ל סביב פסח); kiosk.zmanimDiaspora הקיים קובע את inIsrael,
    /// כדי להישאר עקבי עם חישוב כניסת/יציאת חג למעלה.
    /// </summary>
    private static string ParshaLine(DateTime now)
    {
        if (!Config.Get("kiosk.showParsha", true)) return null;
        try
        {
            var jc = new global::Zmanim.JewishCalendar.JewishCalendar();
            int diff = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
            DateTime sat = now.Date.AddDays(diff);
            bool inIsrael = !Config.Get("kiosk.zmanimDiaspora", false);
            var parsha = jc.GetParshah(sat, inIsrael);
            if (!ParshaNames.TryGetValue(parsha.ToString(), out var name)) return null; // NONE או פרשה לא ידועה בטבלה
            return Lang.T("פרשת השבוע: " + name.he, "Parashat Hashavua: " + name.en);
        }
        catch (Exception ex) { Config.Log("parsha: " + ex.Message); return null; }
    }

    // ---------- דף יומי: YomiCalculator.GetDafYomiBavli + HebrewDateFormatter.FormatDafYomiBavli (שניהם API ציבורי של הספרייה) ----------

    /// <summary>מסכת ודף של דף היומי הנוכחי, בעברית (עם גימטריה, "כ״ה") ובאנגלית, ישירות משני ה-API הציבוריים של הספרייה</summary>
    private static string DafYomiLine(DateTime now)
    {
        if (!Config.Get("kiosk.showDafYomi", true)) return null;
        try
        {
            var daf = global::Zmanim.JewishCalendar.YomiCalculator.GetDafYomiBavli(now.Date);
            if (daf == null) return null;
            string he = new global::Zmanim.JewishCalendar.HebrewDateFormatter { HebrewFormat = true }.FormatDafYomiBavli(daf);
            string en = new global::Zmanim.JewishCalendar.HebrewDateFormatter { HebrewFormat = false }.FormatDafYomiBavli(daf);
            return Lang.T("דף יומי: " + he, "Daf Yomi: " + en);
        }
        // לפני תחילת מחזור הדף היומי (י"א תשרי תרפ"ד / 11.9.1923) הספרייה זורקת — לא אמור לקרות בתאריך נוכחי
        catch (Exception ex) { Config.Log("daf yomi: " + ex.Message); return null; }
    }

    private static (JsonElement dailyTimes, JsonElement jewishDate, JsonElement jewishDateTomorrow)? TryGetCache()
    {
        string json; DateTime at;
        lock (Gate) { json = _cacheJson; at = _cacheUtc; }
        if (json == null || DateTime.UtcNow - at > MaxAge) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone(); // עותק עצמאי שנשאר תקף אחרי Dispose של ה-JsonDocument
            var dt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("dailyTimes", out var a) ? a : default;
            var jd = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jewishDate", out var b) ? b : default;
            var jt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jewishDateTomorrow", out var c) ? c : default;
            return (dt, jd, jt);
        }
        catch { return null; }
    }

    // ---------- זמני היום: מפתחות דינמיים (לא תקן קבוע באוצריא) לשמות מוכרים ----------

    private static readonly (string id, string[] keys, string he, string en)[] KnownZmanim =
    {
        ("alot", new[] { "alotHaShachar", "alotHashachar", "alot", "dawn" }, "עלות השחר", "Dawn"),
        ("netz", new[] { "sunrise", "netz", "hanetz", "netzHachama" }, "הנץ החמה", "Sunrise"),
        ("shemaMga", new[] { "sofZmanShmaMGA", "sofZmanShemaMGA", "sofZmanKriatShemaMGA", "sofZmanShmaMga" }, "סוף ק\"ש (מג\"א)", "Shema (MGA)"),
        ("shemaGra", new[] { "sofZmanShmaGRA", "sofZmanShemaGRA", "sofZmanKriatShemaGRA", "sofZmanShmaGra" }, "סוף ק\"ש (גר\"א)", "Shema (GRA)"),
        ("tefilaMga", new[] { "sofZmanTfilaMGA", "sofZmanTefilaMGA", "sofZmanTfilaMga" }, "סוף תפילה (מג\"א)", "Tefila (MGA)"),
        ("tefilaGra", new[] { "sofZmanTfilaGRA", "sofZmanTefilaGRA", "sofZmanTfilaGra" }, "סוף תפילה (גר\"א)", "Tefila (GRA)"),
        ("chatzot", new[] { "chatzot", "midday", "chatzos" }, "חצות", "Midday"),
        ("minchaGedola", new[] { "minchaGedola" }, "מנחה גדולה", "Mincha Gedola"),
        ("minchaKetana", new[] { "minchaKetana" }, "מנחה קטנה", "Mincha Ketana"),
        ("plag", new[] { "plagHamincha", "plagHaMincha" }, "פלג המנחה", "Plag HaMincha"),
        ("shkia", new[] { "sunset", "shkia", "shkiah" }, "שקיעה", "Sunset"),
        ("tzeit", new[] { "tzeit", "tzet", "tzeithakochavim", "nightfall" }, "צאת הכוכבים", "Nightfall"),
    };

    private static readonly string[] DefaultShow = { "alot", "netz", "shemaGra", "chatzot", "shkia", "tzeit" };

    private static bool TryPick(JsonElement obj, out string value, params string[] keys)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in obj.EnumerateObject())
            foreach (var k in keys)
                if (string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) { value = prop.Value.GetString(); return true; }
                    if (prop.Value.ValueKind == JsonValueKind.Number) { value = prop.Value.ToString(); return true; }
                }
        return false;
    }

    /// <summary>#1 (13.10.2026): ערך זמן מאוצריא (ISO / AM-PM / מספר אפוק) → "H:mm" בלי אותיות לועזיות</summary>
    private static string Hm(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // באג #59: אותיות/סימנים באנגלית סביב השעה — מסירים סימני כיוון ותווי עיצוב (LRM/RLM/LRI…, קטגוריה Cf)
        // ורווחים קשיחים לפני הפענוח, ומפענחים גם 12 שעות (AM/PM) כדי שהשעה תוצג תמיד כ-H:mm נקי.
        var sb = new System.Text.StringBuilder(v.Length);
        foreach (char c in v)
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            sb.Append(c is '\u00A0' or '\u202F' or '\u2009' ? ' ' : c);
        }
        v = sb.ToString().Trim();
        if (long.TryParse(v, out long ms) && ms > 100000000000L)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("H:mm", inv);
        bool tz = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(v, @"T.*[+-]\d\d:?\d\d$");
        if (tz && DateTimeOffset.TryParse(v, inv, System.Globalization.DateTimeStyles.None, out var o)) return o.ToLocalTime().ToString("H:mm", inv);
        if (DateTime.TryParse(v, inv, System.Globalization.DateTimeStyles.None, out var d)) return d.ToString("H:mm", inv);
        // "6:12 PM" / "6:12PM" / "18:12:05" וכו' בתוך טקסט: לוקחים רק את השעה ומתרגמים AM/PM ל-24 שעות
        var m = System.Text.RegularExpressions.Regex.Match(v, @"(\d{1,2}):(\d{2})(?::\d{2})?\s*([AaPp])?\.?\s*[Mm]?\.?");
        if (m.Success)
        {
            int h = int.Parse(m.Groups[1].Value, inv), mi = int.Parse(m.Groups[2].Value, inv);
            if (m.Groups[3].Success)
            {
                bool pm = m.Groups[3].Value is "P" or "p";
                if (pm && h < 12) h += 12;
                if (!pm && h == 12) h = 0;
            }
            if (h < 24 && mi < 60) return h.ToString(inv) + ":" + mi.ToString("00", inv);
        }
        return System.Text.RegularExpressions.Regex.Replace(v, "[A-Za-z]", "").Trim();
    }

    /// <summary>#3: חגים ודף יומי מלוח השנה של אוצריא (getJewishDate), אם קיימים</summary>
    private static string CalendarLine(JsonElement jd)
    {
        if (jd.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        if (jd.TryGetProperty("holidays", out var hol) && hol.ValueKind == JsonValueKind.Array)
            foreach (var h in hol.EnumerateArray())
            {
                string n = h.ValueKind == JsonValueKind.String ? h.GetString()
                    : h.ValueKind == JsonValueKind.Object && h.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                if (!string.IsNullOrWhiteSpace(n)) parts.Add(n.Trim());
            }
        if (TryPick(jd, out string parasha, "parasha", "parsha", "parashat", "weeklyParasha")) parts.Add(Lang.T("פרשת ", "Parasha ") + parasha);
        if (TryPick(jd, out string daf, "dafYomi", "daf_yomi", "dafYomiBavli")) parts.Add(Lang.T("דף יומי: ", "Daf Yomi: ") + daf);
        else if (jd.TryGetProperty("dafYomi", out var dy) && dy.ValueKind == JsonValueKind.Object &&
                 TryPick(dy, out string mas, "masechta", "tractate", "name") && TryPick(dy, out string page, "daf", "page"))
            parts.Add(Lang.T("דף יומי: ", "Daf Yomi: ") + mas + " " + page);
        return parts.Count > 0 ? string.Join("   ·   ", parts) : null;
    }

    /// <summary>זמנים שנבחרו בהגדרות אבל לא חזרו מלוח השנה של אוצריא — משלימים מחישוב מקומי (KosherJava)
    /// כשיש מיקום, כדי שכל הזמנים שנבחרו יוצגו (באג #59)</summary>
    private static JsonElement WithMissing(JsonElement dailyTimes, DateTime now)
    {
        if (dailyTimes.ValueKind != JsonValueKind.Object) return dailyTimes;
        var show = Config.HasList("kiosk.zmanimShow") ? Config.GetList("kiosk.zmanimShow") : DefaultShow;
        var missing = KnownZmanim.Where(z => show.Contains(z.id, StringComparer.OrdinalIgnoreCase) && !TryPick(dailyTimes, out _, z.keys)).ToList();
        if (missing.Count == 0) return dailyTimes;
        Dictionary<string, string> local;
        try { local = KosherJavaTimes(now); } catch { return dailyTimes; }
        if (local.Count == 0) return dailyTimes;
        var merged = JsonNode.Parse(dailyTimes.GetRawText()) as JsonObject ?? new JsonObject();
        foreach (var z in missing)
            foreach (var k in z.keys)
                if (local.TryGetValue(k, out var v)) { merged[k] = v; break; }
        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string ZmanimLine(JsonElement dailyTimes)
    {
        if (dailyTimes.ValueKind != JsonValueKind.Object) return null;
        var show = Config.HasList("kiosk.zmanimShow") ? Config.GetList("kiosk.zmanimShow") : DefaultShow;
        var parts = new List<string>();
        foreach (var z in KnownZmanim)
        {
            if (!show.Contains(z.id, StringComparer.OrdinalIgnoreCase)) continue;
            if (!TryPick(dailyTimes, out string val, z.keys)) continue;
            parts.Add(Lang.T(z.he + " ", z.en + " ") + Hm(val));
        }
        if (parts.Count == 0) return null;
        int half = (parts.Count + 1) / 2;
        const string sep = "   ·   ";
        string line1 = string.Join(sep, parts.Take(half));
        string line2 = string.Join(sep, parts.Skip(half));
        return line2.Length > 0 ? line1 + "\n" + line2 : line1;
    }

    // ---------- כניסת שבת/חג והבדלה: מבוססים על isShabbat/holidays שמחזיר getJewishDate ----------

    private static bool HasYomTov(JsonElement jd)
    {
        if (jd.ValueKind != JsonValueKind.Object) return false;
        if (jd.TryGetProperty("holidays", out var hol) && hol.ValueKind == JsonValueKind.Array)
            foreach (var h in hol.EnumerateArray())
                if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("kind", out var k) &&
                    k.ValueKind == JsonValueKind.String && string.Equals(k.GetString(), "yomTov", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static bool IsHoly(JsonElement jd)
    {
        bool shabbat = jd.ValueKind == JsonValueKind.Object && jd.TryGetProperty("isShabbat", out var s) &&
            (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False) && s.GetBoolean();
        return shabbat || HasYomTov(jd);
    }

    private static DateTime? ParseTimeOnDate(string hm, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(hm)) return null;
        if (DateTime.TryParse(hm, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t))
            return date.Date + t.TimeOfDay;
        return null;
    }

    private static string CandleHavdalaLine(DateTime now, JsonElement dailyTimes, JsonElement jdToday, JsonElement jdTomorrow)
    {
        bool tomorrowHoly = IsHoly(jdTomorrow);
        bool todayHoly = IsHoly(jdToday);
        if (tomorrowHoly)
        {
            // כניסת שבת/חג: שקיעה פחות מספר דקות הניתן להגדרה (ברירת מחדל 18). אם אין שקיעה במטמון,
            // משתמשים בשדה candleLighting שאוצריא עצמה מחזירה (אז מספר הדקות שהוגדר כאן לא משפיע).
            int minutes = Config.Get("kiosk.candleLightingMinutes", 18);
            DateTime? t = null;
            if (TryPick(dailyTimes, out string sunset, "sunset", "shkia", "shkiah"))
                t = ParseTimeOnDate(sunset, now)?.AddMinutes(-minutes);
            if (t == null && TryPick(dailyTimes, out string cl, "candleLighting", "candleLightingTime", "hadlakatNerot", "candlelighting"))
                t = ParseTimeOnDate(cl, now);
            if (t != null) return Lang.T("הדלקת נרות ", "Candle lighting ") + t.Value.ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (todayHoly)
        {
            DateTime? t = null;
            if (TryPick(dailyTimes, out string tz, "tzeit", "tzet", "tzeithakochavim", "nightfall"))
                t = ParseTimeOnDate(tz, now);
            if (t == null && TryPick(dailyTimes, out string hv, "havdalah", "havdala", "motzaeiShabbat"))
                t = ParseTimeOnDate(hv, now);
            if (t != null) return Lang.T("צאת שבת/חג (הבדלה) ", "Havdala ") + t.Value.ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    // ---------- ספירת העומר: חישוב טהור מתאריך עברי (16 בניסן עד 5 בסיון) ----------

    private static string Norm(string s) => (s ?? "").Replace("\"", "").Replace("'", "").Replace("׳", "").Replace("״", "").Replace(" ", "").Trim();

    private static bool NameIs(string name, params string[] candidates) =>
        candidates.Any(c => string.Equals(Norm(name), Norm(c), StringComparison.OrdinalIgnoreCase));

    private static int OmerDayNumber(JsonElement jd)
    {
        if (jd.ValueKind != JsonValueKind.Object) return 0;
        if (!jd.TryGetProperty("monthName", out var mv) || mv.ValueKind != JsonValueKind.String) return 0;
        if (!jd.TryGetProperty("day", out var dv) || dv.ValueKind != JsonValueKind.Number) return 0;
        string month = mv.GetString();
        int day = dv.GetInt32();
        if (day <= 0) return 0;
        if (NameIs(month, "ניסן", "Nisan", "Nissan") && day >= 16) return day - 15;
        if (NameIs(month, "אייר", "Iyar", "Iyyar")) return 15 + day;
        if (NameIs(month, "סיון", "סיוון", "Sivan") && day <= 5) return 44 + day;
        return 0;
    }

    private static readonly string[] DayWords =
    {
        "", "יום אחד", "שני ימים", "שלושה ימים", "ארבעה ימים", "חמישה ימים", "ששה ימים", "שבעה ימים", "שמונה ימים", "תשעה ימים", "עשרה ימים",
        "אחד עשר יום", "שנים עשר יום", "שלושה עשר יום", "ארבעה עשר יום", "חמישה עשר יום", "ששה עשר יום", "שבעה עשר יום", "שמונה עשר יום", "תשעה עשר יום", "עשרים יום",
        "אחד ועשרים יום", "שנים ועשרים יום", "שלושה ועשרים יום", "ארבעה ועשרים יום", "חמישה ועשרים יום", "ששה ועשרים יום", "שבעה ועשרים יום", "שמונה ועשרים יום", "תשעה ועשרים יום", "שלושים יום",
        "אחד ושלושים יום", "שנים ושלושים יום", "שלושה ושלושים יום", "ארבעה ושלושים יום", "חמישה ושלושים יום", "ששה ושלושים יום", "שבעה ושלושים יום", "שמונה ושלושים יום", "תשעה ושלושים יום", "ארבעים יום",
        "אחד וארבעים יום", "שנים וארבעים יום", "שלושה וארבעים יום", "ארבעה וארבעים יום", "חמישה וארבעים יום", "ששה וארבעים יום", "שבעה וארבעים יום", "שמונה וארבעים יום", "תשעה וארבעים יום",
    };
    private static readonly string[] WeekWords = { "", "שבוע אחד", "שני שבועות", "שלושה שבועות", "ארבעה שבועות", "חמישה שבועות", "ששה שבועות", "שבעה שבועות" };
    private static readonly string[] RemDayWords = { "", "יום אחד", "שני ימים", "שלושה ימים", "ארבעה ימים", "חמישה ימים", "ששה ימים" };

    private static string OmerLine(JsonElement jd)
    {
        int day = OmerDayNumber(jd);
        if (day <= 0 || day > 49) return null;
        string hePhrase;
        int weeks = day / 7, rem = day % 7;
        if (day < 7) hePhrase = "היום " + DayWords[day] + " לעומר";
        else hePhrase = "היום " + DayWords[day] + ", שהם " + WeekWords[weeks] + (rem > 0 ? " ו" + RemDayWords[rem] : "") + " לעומר";
        string enPhrase = "Today is day " + day + " of the Omer" +
            (day >= 7 ? " (" + weeks + " week" + (weeks != 1 ? "s" : "") + (rem > 0 ? " and " + rem + " day" + (rem != 1 ? "s" : "") : "") + ")" : "") + ".";
        return Lang.T(hePhrase, enPhrase);
    }

    // ---------- יום השנה: תאריך עברי כללי שהמנהל מגדיר (לא קשור לתאריך פטירה ספציפי כלשהו) ----------

    private static bool MonthMatches(string a, string b)
    {
        string na = Norm(a), nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return true;
        // אדר א/אדר ב/אדר (בשנה פשוטה): מקלים ומתאימים כל שילוב, כדי לא להחמיץ את יום השנה
        // בגלל הבדל בין שנה מעוברת לפשוטה. ראו הערה בתיעוד ה-PR.
        bool aAdar = na.StartsWith("אדר") || na.StartsWith("Adar", StringComparison.OrdinalIgnoreCase);
        bool bAdar = nb.StartsWith("אדר") || nb.StartsWith("Adar", StringComparison.OrdinalIgnoreCase);
        return aAdar && bAdar;
    }

    private static string YahrzeitLine(JsonElement jd)
    {
        string month = Config.Get("kiosk.yahrzeitMonth", "");
        int day = Config.Get("kiosk.yahrzeitDay", 0);
        if (string.IsNullOrWhiteSpace(month) || day <= 0) return null;
        if (jd.ValueKind != JsonValueKind.Object) return null;
        if (!jd.TryGetProperty("monthName", out var mv) || mv.ValueKind != JsonValueKind.String) return null;
        if (!jd.TryGetProperty("day", out var dv) || dv.ValueKind != JsonValueKind.Number) return null;
        if (dv.GetInt32() != day || !MonthMatches(month, mv.GetString())) return null;
        string label = Config.Get("kiosk.yahrzeitLabel", "");
        string who = string.IsNullOrWhiteSpace(label) ? "" : " " + label;
        return Lang.T("יום השנה היום" + who, "Yahrzeit today" + who);
    }

    // ---------- גיבוי: שני מנועי חישוב אפשריים, נבחר ב-kiosk.zmanimFallbackEngine ----------
    // ---------- רק כשאין מטמון טרי מאוצריא, ורק אם הוגדר מיקום ----------

    /// <summary>
    /// בוחר בין שני מנועי החישוב לפי kiosk.zmanimFallbackEngine: "noaa" (ברירת המחדל — ההתנהגות
    /// שהייתה כאן עד כה, לא משתנה למתקינים קיימים אלא אם יבחרו אחרת) או "kosherjava" (חדש).
    /// שני המנועים משמשים רק גיבוי כשאין נתונים טריים מאוצריא עצמה.
    /// </summary>
    private static string FallbackLine(DateTime now)
    {
        string engine = Config.Get("kiosk.zmanimFallbackEngine", "noaa");
        return string.Equals(engine, "kosherjava", StringComparison.OrdinalIgnoreCase)
            ? FallbackLineKosherJava(now)
            : FallbackLineNoaa(now);
    }

    /// <summary>
    /// מנוע חדש: מחשב זמני יום דרך ‏ComplexZmanimCalendar (חבילת Zmanim/KosherJava, LGPL — ראו קרדיט
    /// למעלה) ומעביר אותם דרך אותו ZmanimLine/KnownZmanim ששולף מהמטמון של אוצריא — כך הגיבוי יוצא
    /// באותה צורה ואותה בחירת זמנים (kiosk.zmanimShow) כמו הנתיב הרגיל, רק עם מקור חישוב שונה.
    /// זמנים הלכתיים מלאים (גר"א/מג"א) ותיקון גובה אמיתי, אבל קוד צד-שלישי — ייתכנו הבדלים קלים
    /// מהרגיל בזמנים גבוליים מסוימים לעומת מה שהמנהל מכיר.
    /// </summary>
    /// <summary>זמני היום מחושבים מקומית (KosherJava) לפי kiosk.zmanimLocation; ריק כשאין מיקום</summary>
    private static Dictionary<string, string> KosherJavaTimes(DateTime now)
    {
        var dict = new Dictionary<string, string>();
        string loc = Config.Get("kiosk.zmanimLocation", "");
        if (string.IsNullOrWhiteSpace(loc)) return dict;
        var p = ParseLocation(loc);
        if (p == null) return dict;
        double lat = p.Value.lat, lon = p.Value.lon;
        // תיקון גובה (issue #33): מטרים מעל פני הים; 0 כברירת מחדל אם לא הוגדר. הספרייה דורשת ≥ 0
        double elevation = Math.Max(0, Config.Get("kiosk.zmanimElevation", 0.0));

        global::Zmanim.ComplexZmanimCalendar cal;
        try
        {
            var tz = new global::Zmanim.TimeZone.WindowsTimeZone(TimeZoneInfo.Local);
            var geo = new global::Zmanim.Utilities.GeoLocation("kiosk", lat, lon, elevation, tz);
            cal = new global::Zmanim.ComplexZmanimCalendar(now.Date, geo);
        }
        catch { return dict; } // קואורדינטות לא תקינות לספרייה (לא אמור לקרות אחרי ParseLocation)

        string H(DateTime? d) => d?.ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture);
        void Add(string key, DateTime? v) { string s = H(v); if (s != null) dict[key] = s; }
        try
        {
            // מפתחות כמו ב-KnownZmanim למעלה (האליאס הראשון בכל שורה), כדי לעבור דרך אותו ZmanimLine
            Add("alotHaShachar", cal.GetAlosHashachar());
            Add("sunrise", cal.GetSunrise());
            Add("sofZmanShmaMGA", cal.GetSofZmanShmaMGA());
            Add("sofZmanShmaGRA", cal.GetSofZmanShmaGRA());
            Add("sofZmanTfilaMGA", cal.GetSofZmanTfilaMGA());
            Add("sofZmanTfilaGRA", cal.GetSofZmanTfilaGRA());
            Add("chatzot", cal.GetChatzos());
            Add("minchaGedola", cal.GetMinchaGedola());
            Add("minchaKetana", cal.GetMinchaKetana());
            Add("plagHamincha", cal.GetPlagHamincha());
            Add("sunset", cal.GetSunset());
            Add("tzeit", cal.GetTzais());
        }
        catch { /* למשל קו רוחב קיצוני עם יום/לילה מתמשכים חלקית — מה שכבר חושב מוצג */ }
        return dict;
    }

    private static string FallbackLineKosherJava(DateTime now)
    {
        var dict = KosherJavaTimes(now);
        if (dict.Count == 0) return null;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict));
        string zm = ZmanimLine(doc.RootElement);
        if (zm == null) return null;
        return zm + "\n" + Lang.T(
            "(חישוב גיבוי מקומי — ספריית Zmanim/KosherJava, ללא חיבור ללוח השנה של אוצריא)",
            "(local fallback calculation — Zmanim/KosherJava library, not from Otzaria's calendar)");
    }

    /// <summary>
    /// מנוע ישן (ברירת מחדל, ללא שינוי התנהגות למתקינים קיימים): קירוב אסטרונומי פשוט של NOAA,
    /// הנץ/שקיעה בגובה פני הים בלבד, בלי תיקון גובה וקבוצת זמנים מצומצמת.
    /// </summary>
    private static string FallbackLineNoaa(DateTime now)
    {
        string loc = Config.Get("kiosk.zmanimLocation", "");
        if (string.IsNullOrWhiteSpace(loc)) return null;
        var p = ParseLocation(loc);
        if (p == null) return null;
        double lat = p.Value.lat, lon = p.Value.lon;
        DateTime? T(bool rise, double zenith) => SunTime(now.Date, lat, lon, rise, zenith);
        var rise = T(true, 90.833); var set = T(false, 90.833);
        if (rise == null || set == null) return null;
        var alot = T(true, 90 + 16.1); var tzeit = T(false, 90 + 8.5);
        string H(DateTime? d) => d?.ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
        var chatzot = rise.Value + TimeSpan.FromTicks((set.Value - rise.Value).Ticks / 2);
        const string sep = "   ·   ";
        return
            Lang.T("עלות השחר ", "Dawn ") + H(alot) + sep + Lang.T("הנץ ", "Sunrise ") + H(rise) + sep +
            Lang.T("חצות ", "Midday ") + H(chatzot) + "\n" +
            Lang.T("שקיעה ", "Sunset ") + H(set) + sep + Lang.T("צאת הכוכבים ", "Nightfall ") + H(tzeit) + sep +
            Lang.T("(חישוב גיבוי, בלי חיבור ללוח השנה של אוצריא)", "(fallback estimate, not from Otzaria's calendar)");
    }

    /// <summary>זמן הנץ/השקיעה בשעון המקומי של המחשב (זנית 90.833°). אלגוריתם NOAA, בלי תיקון גובה</summary>
    private static DateTime? SunTime(DateTime date, double lat, double lon, bool rise, double zenith)
    {
        static double Rad(double d) => d * Math.PI / 180;
        static double Deg(double r) => r * 180 / Math.PI;
        int n = date.DayOfYear;
        double lngHour = lon / 15;
        double t = n + ((rise ? 6 : 18) - lngHour) / 24;
        double M = 0.9856 * t - 3.289;
        double L = M + 1.916 * Math.Sin(Rad(M)) + 0.020 * Math.Sin(Rad(2 * M)) + 282.634;
        L = (L % 360 + 360) % 360;
        double RA = Deg(Math.Atan(0.91764 * Math.Tan(Rad(L))));
        RA = (RA % 360 + 360) % 360;
        RA += Math.Floor(L / 90) * 90 - Math.Floor(RA / 90) * 90;
        RA /= 15;
        double sinDec = 0.39782 * Math.Sin(Rad(L));
        double cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Math.Cos(Rad(zenith)) - sinDec * Math.Sin(Rad(lat))) / (cosDec * Math.Cos(Rad(lat)));
        if (cosH > 1 || cosH < -1) return null;
        double H = rise ? 360 - Deg(Math.Acos(cosH)) : Deg(Math.Acos(cosH));
        H /= 15;
        double T = H + RA - 0.06571 * t - 6.622;
        double UT = ((T - lngHour) % 24 + 24) % 24;
        var utc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(UT);
        var local = utc.ToLocalTime();
        // תיקון יום: הזמן המקומי צריך ליפול על התאריך המבוקש
        if (local.Date > date.Date) local = local.AddDays(-1);
        else if (local.Date < date.Date) local = local.AddDays(1);
        return local;
    }
}
