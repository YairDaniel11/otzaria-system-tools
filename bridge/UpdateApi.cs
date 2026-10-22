using System.Diagnostics;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// בדיקת עדכון והתקנת עדכון: משווה את גרסת הגשר לגרסה האחרונה בעמוד ההוצאות בגיטהאב,
/// ובבקשה מפורשת מוריד את OtzariaBridge.exe החדש ומריץ אותו עם --install --quiet
/// (המתקין סוגר את הגשר הישן, מחליף את הקובץ ומפעיל מחדש).
/// שרשרת האמון: SHA256SUMS.txt חתום ב-ECDSA P-256 (SHA256SUMS.txt.sig, base64 של חתימת P1363) מול מפתח
/// ציבורי שמוטמע כאן (<see cref="PublicKeySpkiBase64"/>, מפתח ייעודי לעדכונים בלבד). אימות hash בלבד
/// לא מספיק: מי ששולט בחשבון/בטוקן של GitHub שולט גם בקובץ ה-sums. בלי חתימה תקפה: דוחים, תמיד.
/// </summary>
public static class UpdateApi
{
    public const string Repo = "YairDaniel11/otzaria-system-tools";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
    public const string AllReleasesUrl = "https://github.com/" + Repo + "/releases";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";

    /// <summary>
    /// מפתח ציבורי (SPKI, base64) לאימות חתימת העדכונים. ריק = לא הוגדר מפתח, ואז כל עדכון נדחה (fail-closed).
    /// יוצרים אותו עם tools\New-UpdateKey.ps1 (הפרטי נשאר בסוד GitHub בשם UPDATE_SIGNING_KEY, לא בריפו).
    /// </summary>
    public const string PublicKeySpkiBase64 = "";
    public const string SumsAssetName = "SHA256SUMS.txt";
    public const string SigAssetName = "SHA256SUMS.txt.sig";

    private static readonly HttpClient Http = Create();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _checked = DateTime.MinValue;
    private static Info _last;

    public record Info(string Current, string Latest, string Tag, bool Newer, string Url, string ExeUrl, string SumsUrl, string SigUrl, string Notes, string BridgeVerUrl = null);

    /// <summary>
    /// תגית ה-Release בפורק האישי (RELEASE_VERSION.txt, למשל v0.0.5) היא מספור עצמאי של המשתמש,
    /// לא גרסת הגשר עצמה (Program.Version, למשל 3.5.0) - אי אפשר להשוות ביניהן ישירות. לכן ה-CI
    /// מעלה גם קובץ BRIDGE_VERSION.txt עם Program.Version בפועל, וכאן משווים מולו, לא מול התגית.
    /// הקובץ וגם התגית נכללים בחתום (SHA256SUMS.txt), וגרסת ה-exe עצמה חייבת להיות הגרסה החתומה.
    /// </summary>
    private const string BridgeVersionAssetName = "BRIDGE_VERSION.txt";

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OtzariaBridge/" + Program.Version);
        return c;
    }

    public static void Register()
    {
        Server.Route("/sys/update-check", async b => Shape(await Check(b.Bool("force"))));
        Server.Route("/sys/update-install", async _ => await Install());
    }

    private static object Shape(Info i) => new
    {
        current = i.Current,
        latest = i.Latest,
        tag = i.Tag,
        newer = i.Newer,
        url = i.Url,
        releasesUrl = ReleasesUrl,
        canInstall = i.Newer && i.ExeUrl != null && !Kiosk.IsActive,
        notes = i.Notes,
    };

    public static Version Parse(string s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>הוצאה שנבחרה מתוך תשובת GitHub</summary>
    public record Release(string Tag, Version TagVersion, string Url, string Notes, string ExeUrl, string SumsUrl, string SigUrl, string BridgeVerUrl);

    /// <summary>
    /// בחירת ההוצאה: לא draft ולא prerelease, והגבוהה ביותר לפי גרסת התגית (הסדר ב-API אינו מובטח).
    /// WHY: השדה "latest" לא קיים באובייקט release של GitHub, ולכן בחירה שנשענה עליו בחרה בפועל את
    /// הראשונה ברשימה. תגית בלי מספר גרסה נדלגת.
    /// </summary>
    public static Release SelectRelease(string json)
    {
        Release best = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.ValueKind != JsonValueKind.Object) continue;
            string tag = rel.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var tv = Parse(tag);
            if (tv == null) continue;
            if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
            if (best != null && tv <= best.TagVersion) continue;
            string exe = null, sums = null, sig = null, bv = null;
            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string dl = a.TryGetProperty("browser_download_url", out var du) ? du.GetString() : null;
                    if (name == null || dl == null) continue;
                    if (string.Equals(name, "OtzariaBridge.exe", StringComparison.OrdinalIgnoreCase)) exe = dl;
                    else if (string.Equals(name, SumsAssetName, StringComparison.OrdinalIgnoreCase)) sums = dl;
                    else if (string.Equals(name, SigAssetName, StringComparison.OrdinalIgnoreCase)) sig = dl;
                    else if (string.Equals(name, BridgeVersionAssetName, StringComparison.OrdinalIgnoreCase)) bv = dl;
                }
            best = new Release(tag, tv, rel.TryGetProperty("html_url", out var u) ? u.GetString() : null,
                rel.TryGetProperty("body", out var bd) && bd.ValueKind == JsonValueKind.String ? bd.GetString() : null, exe, sums, sig, bv);
        }
        return best;
    }

    /// <summary>אימות חתימת ECDSA P-256 (SHA-256, פורמט P1363 בגודל 64 בתים, base64) על התוכן. כל כשל = false</summary>
    public static bool VerifySignature(byte[] data, string sigText, string publicKeySpkiBase64)
    {
        try
        {
            if (data == null || string.IsNullOrWhiteSpace(sigText) || string.IsNullOrWhiteSpace(publicKeySpkiBase64)) return false;
            byte[] sig = Convert.FromBase64String(sigText.Trim());
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64.Trim()), out _);
            if (ec.KeySize != 256) return false;
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>hash של קובץ מתוך SHA256SUMS ("hash  name"), או null</summary>
    public static string FindHash(string sums, string fileName)
    {
        foreach (var line in (sums ?? "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && string.Equals(m.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>התגית שהחתום מצהיר עליה ("# tag=v0.1.5"): קושרת את הקבצים להוצאה אחת ומונעת ערבוב נכסים בין הוצאות</summary>
    public static string SignedTag(string sums)
    {
        var m = Regex.Match(sums ?? "", @"^#\s*tag=(\S+)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsPe(byte[] data)
    {
        if (data == null || data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z') return false;
        int pe = BitConverter.ToInt32(data, 0x3C);
        return pe > 0 && pe + 4 <= data.Length && data[pe] == (byte)'P' && data[pe + 1] == (byte)'E' && data[pe + 2] == 0 && data[pe + 3] == 0;
    }

    /// <summary>
    /// כל בדיקות האמון על החבילה, בלי רשת ובלי דיסק. מחזיר את הגרסה החתומה וה-hash של ה-exe, או זורק.
    /// סדר: חתימה, תגית חתומה = תגית ההוצאה, hash של BRIDGE_VERSION.txt וגרסה גבוהה מהנוכחית, hash ו-PE של ה-exe.
    /// (גרסת הקובץ של ה-exe מושווית אחר כך מול הגרסה החתומה ב-<see cref="FileVersionMatches"/>.)
    /// </summary>
    public static (Version Version, string Sha256) VerifyPackage(byte[] sums, string sigText, string tag, byte[] bridgeVerFile,
        byte[] exe, string currentVersion, string publicKeySpkiBase64)
    {
        static BridgeException Fail(string m) => new("failed", m + " העדכון בוטל.", 500);
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64)) throw Fail("לא הוגדר מפתח אימות עדכונים בגשר.");
        if (!VerifySignature(sums, sigText, publicKeySpkiBase64)) throw Fail("חתימת קובץ האימות של ההוצאה חסרה או אינה תקפה.");
        string text = System.Text.Encoding.UTF8.GetString(sums);
        if (SignedTag(text) != tag) throw Fail("התגית החתומה אינה תואמת להוצאה.");
        string bvHash = FindHash(text, BridgeVersionAssetName);
        if (bvHash == null || bridgeVerFile == null || Convert.ToHexString(SHA256.HashData(bridgeVerFile)).ToLowerInvariant() != bvHash)
            throw Fail("קובץ הגרסה אינו תואם לחתום.");
        var ver = Parse(System.Text.Encoding.ASCII.GetString(bridgeVerFile).Trim());
        var cur = Parse(currentVersion);
        // אי-downgrade: גם הוצאה חתומה ישנה (או חוזרת) לא מתקבלת
        if (ver == null || cur == null || ver <= cur) throw Fail("הגרסה בהוצאה אינה גבוהה מהגרסה המותקנת.");
        string exeHash = FindHash(text, "OtzariaBridge.exe");
        string actual = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        if (exeHash == null || exeHash != actual) throw Fail("הקובץ שהורד אינו תואם ל-SHA256 החתום.");
        if (!IsPe(exe)) throw Fail("הקובץ שהורד אינו קובץ הרצה תקין.");
        return (ver, actual);
    }

    /// <summary>גרסת הקובץ (FileVersion) של ה-exe חייבת להיות הגרסה החתומה, אחרת זו חבילה אחרת מזו שהוצהרה</summary>
    public static bool FileVersionMatches(string path, Version expected)
    {
        try
        {
            var v = Parse(FileVersionInfo.GetVersionInfo(path).FileVersion);
            return v != null && expected != null && v == expected;
        }
        catch { return false; }
    }

    /// <summary>
    /// תיקיית הורדה מוגנת: %LOCALAPPDATA%\OtzariaBridge\update (ההתקנה פר-משתמש), בלי ירושת הרשאות,
    /// רק המשתמש הנוכחי, SYSTEM ומנהלים. WHY: %TEMP% פתוח לכתיבה לכל תהליך של המשתמש (ולעיתים לאחרים).
    /// </summary>
    private static string ProtectedDir()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge", "update");
        Directory.CreateDirectory(dir);
        var di = new DirectoryInfo(dir);
        var ds = di.GetAccessControl();
        ds.SetAccessRuleProtection(true, false);
        foreach (var r in ds.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList()) ds.RemoveAccessRule(r);
        var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            ds.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(ds);
        // ניקוי שאריות מהורדה קודמת
        foreach (var f in di.GetFiles()) { try { f.Delete(); } catch { } }
        return dir;
    }

    public static async Task<Info> Check(bool force)
    {
        await Gate.WaitAsync();
        try
        {
            // תוצאה אחרונה תקפה 2 דקות בלבד, ובבדיקה ידנית (force) תמיד פונים מחדש
            if (!force && _last != null && DateTime.UtcNow - _checked < TimeSpan.FromMinutes(2)) return _last;

            string json;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                using var res = await Http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                json = await res.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                throw new BridgeException("internal", "בדיקת העדכון נכשלה (אין חיבור לאינטרנט או ש-GitHub לא זמין): " + ex.Message, 502);
            }

            var rel = SelectRelease(json);
            Version latestBridgeVer = null;
            if (rel?.BridgeVerUrl != null)
            {
                // להצגה בלבד: ההחלטה האמיתית (ב-Install) נשענת על הגרסה החתומה, לא על ההורדה הזו
                try { latestBridgeVer = Parse((await Http.GetStringAsync(rel.BridgeVerUrl)).Trim()); }
                catch { /* ההשוואה תיכשל בבטחה (לא "newer") אם זה נכשל */ }
            }
            var cur = Parse(Program.Version);
            _last = new Info(Program.Version, latestBridgeVer?.ToString(3), rel?.Tag, latestBridgeVer != null && cur != null && latestBridgeVer > cur,
                rel?.Url ?? ReleasesUrl, rel?.ExeUrl, rel?.SumsUrl, rel?.SigUrl, rel?.Notes, rel?.BridgeVerUrl);
            _checked = DateTime.UtcNow;
            return _last;
        }
        finally { Gate.Release(); }
    }

    /// <summary>הורדה, אימות והפעלת המתקין של הגרסה החדשה. הגשר הנוכחי ייסגר על ידי המתקין</summary>
    public static async Task<object> Install()
    {
        if (Kiosk.IsActive) throw BridgeException.Locked("עדכון הגשר אינו זמין בזמן נעילה. בטלו את הנעילה, עדכנו, והפעילו אותה שוב.");
        var info = await Check(true);
        if (!info.Newer) return new { started = false, message = "הגשר מעודכן (" + info.Current + ")" };
        if (info.ExeUrl == null) throw BridgeException.NotFound("בהוצאה " + info.Tag + " אין קובץ OtzariaBridge.exe");
        // fail-closed: בלי sums, חתימה וקובץ גרסה - לא מורידים בכלל
        if (info.SumsUrl == null || info.SigUrl == null || info.BridgeVerUrl == null)
            throw new BridgeException("failed", "בהוצאה " + info.Tag + " חסרים קבצי אימות חתומים (SHA256SUMS.txt, SHA256SUMS.txt.sig, BRIDGE_VERSION.txt). העדכון בוטל.", 500);

        byte[] sums, bv, data; string sig;
        try
        {
            sums = await Http.GetByteArrayAsync(info.SumsUrl);
            sig = await Http.GetStringAsync(info.SigUrl);
            bv = await Http.GetByteArrayAsync(info.BridgeVerUrl);
            data = await Http.GetByteArrayAsync(info.ExeUrl);
        }
        catch (Exception ex) { throw new BridgeException("internal", "ההורדה נכשלה: " + ex.Message, 502); }

        var (ver, hash) = VerifyPackage(sums, sig, info.Tag, bv, data, Program.Version, PublicKeySpkiBase64);

        string dir = ProtectedDir();
        string exe = Path.Combine(dir, "OtzariaBridge.exe");
        await File.WriteAllBytesAsync(exe, data);

        // מניעת TOCTOU: מחזיקים את הקובץ פתוח לקריאה בלבד (FileShare.Read) - אף תהליך אחר לא יכול לכתוב/למחוק/
        // להחליף אותו כל עוד הידית פתוחה - ומאמתים hash וגרסה דרכה, רגע לפני ההרצה.
        using (var lk = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string onDisk = Convert.ToHexString(await SHA256.HashDataAsync(lk)).ToLowerInvariant();
            if (!string.Equals(onDisk, hash, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("failed", "הקובץ שנכתב לדיסק שונה מהמאומת. העדכון בוטל.", 500);
            if (!FileVersionMatches(exe, ver))
                throw new BridgeException("failed", "גרסת הקובץ אינה הגרסה החתומה. העדכון בוטל.", 500);
            Config.Log("update: " + info.Current + " -> " + ver.ToString(3) + " tag=" + info.Tag + " sha256=" + hash);

            // המתקין החדש סוגר את הגשר הזה, מעתיק את עצמו ומפעיל מחדש
            Process.Start(new ProcessStartInfo(exe, "--install --quiet") { UseShellExecute = false, WorkingDirectory = dir });
        }
        return new { started = true, version = ver.ToString(3), sha256 = hash };
    }
}
