using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace OtzariaBridge;

/// <summary>
/// זהות תהליכים ואמון באפליקציות — לא לפי שם תהליך. (מדיניות ההקשחה, עיקרון 3):
///  * קובץ מותר מזוהה לפי נתיב מלא + (חתימת Authenticode של מפרסם מקובע, או SHA-256 מקובע), או לפי מיקום
///    קבוע שהמשתמש הרגיל לא יכול לכתוב אליו (בדיקת ACL על הקובץ ועל כל התיקיות מעליו);
///  * זיהוי "מעטפת חסומה" לפי OriginalFilename מתוך VersionInfo ולא רק ProcessName (שינוי שם cmd לא עוקף);
///  * רישום תהליכים שנפתחו דרך הגשר כולל זמן יצירה (PID reuse) ותוקף 10 דקות.
/// הלוגיקה ההתאמה כתובה כפונקציות טהורות (ITrustEnv מוזרק) כדי שאפשר לבדוק אותה ב-xunit.
/// </summary>
internal static class AppTrust
{
    // ---------- מודלים ----------

    internal sealed record SigInfo(bool Valid, string Subject)
    {
        public static readonly SigInfo None = new(false, "");
    }

    internal interface ITrustEnv
    {
        /// <summary>מיקום קבוע (Program Files/אוצריא) שרק מנהל/SYSTEM יכולים לכתוב אליו</summary>
        bool IsTrustedLocation(string path);
        /// <summary>חתימת Authenticode תקפה (WinVerifyTrust) והמפרסם (Subject)</summary>
        SigInfo Sig(string path);
        string Sha256(string path);
    }

    internal enum MatchKind { None, Exact, Helper }

    // ---------- התאמה (טהור) ----------

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool HasPin(string pinSha, string pinSigner) => !string.IsNullOrEmpty(pinSha) || !string.IsNullOrEmpty(pinSigner);

    /// <summary>הקובץ תואם את הנעיצה: מפרסם חתום-תקף זהה, או SHA-256 זהה. בלי נעיצה — false (לעולם לא "פתוח")</summary>
    internal static bool PinMatches(string path, string pinSha, string pinSigner, ITrustEnv env)
    {
        if (!string.IsNullOrEmpty(pinSigner))
        {
            var s = env.Sig(path);
            if (s.Valid && Eq(s.Subject, pinSigner)) return true;
        }
        if (!string.IsNullOrEmpty(pinSha) && Eq(env.Sha256(path), pinSha)) return true;
        return false;
    }

    /// <summary>
    /// האם קובץ ההפעלה של תהליך רץ הוא התוכנה של הרשומה.
    ///  Exact: אותו נתיב מלא כמו ברשומה, וגם (נעיצה תואמת, או מיקום קבוע לא-כתיב-למשתמש).
    ///  Helper: תהליך עזר (שם מרשימת Procs או אותה תיקיית התקנה לא-רחבה) — רק אם הוא במיקום קבוע לא-כתיב,
    ///          או חתום-תקף באותו מפרסם שנעוץ ברשומה. "כל exe בתיקיית ההתקנה" בלי אימות — לא מתקבל.
    /// </summary>
    internal static MatchKind MatchExe(string exe, string entryPath, string entryDirOrNull, IEnumerable<string> procNamesLower,
                                       string pinSha, string pinSigner, ITrustEnv env)
    {
        if (string.IsNullOrEmpty(exe)) return MatchKind.None;   // fail-closed: אין נתיב (תהליך מוגבה) = אין זהות
        if (Eq(exe, entryPath))
            return PinMatches(exe, pinSha, pinSigner, env) || env.IsTrustedLocation(exe) ? MatchKind.Exact : MatchKind.None;

        string stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        bool nameHit = procNamesLower != null && procNamesLower.Contains(stem);
        bool dirHit = !string.IsNullOrEmpty(entryDirOrNull) &&
                      exe.StartsWith(entryDirOrNull.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (!nameHit && !dirHit) return MatchKind.None;
        if (env.IsTrustedLocation(exe)) return MatchKind.Helper;
        if (!string.IsNullOrEmpty(pinSigner))
        {
            var s = env.Sig(exe);
            if (s.Valid && Eq(s.Subject, pinSigner)) return MatchKind.Helper;
        }
        return MatchKind.None;
    }

    /// <summary>לפני הפעלה: הקובץ נבדק מחדש מול הנעיצה (או מיקום קבוע). קובץ שהוחלף — לא מופעל</summary>
    internal static bool LaunchAllowed(string path, string pinSha, string pinSigner, ITrustEnv env) =>
        PinMatches(path, pinSha, pinSigner, env) || env.IsTrustedLocation(path);

    /// <summary>
    /// זיהוי מעטפת חסומה לפי שם התהליך, שם הקובץ בדיסק, או OriginalFilename מה-VersionInfo.
    /// עותק של cmd.exe ששמו שונה ל-calc.exe עדיין נחשב cmd (OriginalFilename="Cmd.Exe").
    /// </summary>
    internal static bool IsBlockedIdentity(string processName, string exePath, string originalFilename, ISet<string> blocked)
    {
        static string Stem(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            int i = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (i >= 0) s = s.Substring(i + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }
        foreach (var c in new[] { Stem(processName), Stem(exePath), Stem(originalFilename) })
            if (c.Length > 0 && blocked.Contains(c)) return true;
        return false;
    }

    // ---------- ACL (טהור: כללים מוזרקים) ----------

    internal readonly record struct AclRule(string Sid, FileSystemRights Rights, bool Allow, bool InheritOnly);

    // מי שמורשה לכתוב: SYSTEM, Administrators, TrustedInstaller (וגם OWNER RIGHTS/CREATOR OWNER כשאינם חלים על האובייקט עצמו)
    internal static readonly HashSet<string> AdminSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    private const FileSystemRights WriteMask =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // תיקיית אב: אין צורך לחסום "יצירת קובץ חדש" (שורש הכונן מאפשר יצירת תיקיות), רק שינוי/מחיקה/שינוי הרשאות של ילדים קיימים
    private const FileSystemRights AncestorMask =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// האם רק מנהלים כותבים לאובייקט. רשימה לבנה של SID: כל הרשאת כתיבה (Allow) ל-SID שאינו מנהל/SYSTEM/TI
    /// (Users, Authenticated Users, Everyone, משתמש ספציפי, חבילות אפליקציה) = כתיב למשתמש. גם בעלים שאינו מנהל נחשב כתיב (WRITE_DAC).
    /// כללי Deny מתעלמים מהם בכוונה (שמרני).
    /// </summary>
    internal static bool IsAdminOnlyWritable(IEnumerable<AclRule> rules, string ownerSid, bool ancestor = false)
    {
        if (string.IsNullOrEmpty(ownerSid) || !AdminSids.Contains(ownerSid)) return false;
        var mask = ancestor ? AncestorMask : WriteMask;
        foreach (var r in rules)
        {
            if (!r.Allow || (r.Rights & mask) == 0) continue;
            if (AdminSids.Contains(r.Sid)) continue;
            // CREATOR OWNER (S-1-3-0): כלל תורשה בלבד, לא חל על האובייקט עצמו
            if (r.InheritOnly && (r.Sid == "S-1-3-0" || r.Sid == "S-1-3-4")) continue;
            if (r.InheritOnly) continue;   // לא חל על האובייקט הזה (רק על ילדים עתידיים שלא קיימים)
            return false;
        }
        return true;
    }

    /// <summary>האם path נמצא תחת אחת מהשורשים (אחרי נרמול). שורש = תיקיה, לא קידומת מחרוזת ("C:\Program Files X" לא תחת "C:\Program Files")</summary>
    internal static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC/רשת — לא אמין
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string root;
            try { root = Path.GetFullPath(r).TrimEnd('\\'); } catch { continue; }
            if (root.Length <= 3) continue;                       // שורש כונן — לעולם לא
            if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------- מיקום קבוע לא-כתיב (מימוש אמיתי) ----------

    private sealed record Cached<T>(long Len, long Ticks, T Val);
    private static readonly ConcurrentDictionary<string, (DateTime at, bool val)> TrustLoc = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FixedRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);   // System32: תהליכי עזר של Windows (ScreenClippingHost וכו')
        string otz = null;
        try { otz = Path.GetDirectoryName(Kiosk.OtzariaPath()); } catch { }
        if (!string.IsNullOrEmpty(otz)) yield return otz;
    }

    private static IEnumerable<AclRule> RulesOf(FileSystemSecurity sec, out string owner)
    {
        owner = (sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var list = new List<AclRule>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            list.Add(new AclRule(((SecurityIdentifier)r.IdentityReference).Value, r.FileSystemRights,
                r.AccessControlType == AccessControlType.Allow, (r.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        return list;
    }

    private static bool ObjectAdminOnly(string path, bool isDir, bool ancestor)
    {
        FileSystemSecurity sec = isDir ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var rules = RulesOf(sec, out string owner);
        return IsAdminOnlyWritable(rules, owner, ancestor);
    }

    /// <summary>
    /// הקובץ נמצא תחת Program Files / תיקיית אוצריא, ואינו ניתן לכתיבה למשתמש רגיל: לא הקובץ עצמו, לא התיקיה שלו
    /// ולא אף תיקיית אב (אחרת אפשר להחליף/לשנות שם לתיקיה). שגיאה כלשהי = לא מהימן (fail-closed). תוצאה נשמרת 60 שניות.
    /// </summary>
    internal static bool IsTrustedLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (TrustLoc.TryGetValue(path, out var c) && (DateTime.UtcNow - c.at).TotalSeconds < 60) return c.val;
        bool ok = false;
        try
        {
            string full = Path.GetFullPath(path);
            if (IsUnderAnyRoot(full, FixedRoots()) && File.Exists(full)
                && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
            {
                ok = ObjectAdminOnly(full, false, false);
                string dir = Path.GetDirectoryName(full);
                bool first = true;
                while (ok && !string.IsNullOrEmpty(dir))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { ok = false; break; }
                    ok = ObjectAdminOnly(dir, true, !first ? true : false);
                    first = false;
                    dir = Path.GetDirectoryName(dir);   // עד השורש (null)
                }
            }
        }
        catch { ok = false; }
        TrustLoc[path] = (DateTime.UtcNow, ok);
        return ok;
    }

    // ---------- Authenticode ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static readonly ConcurrentDictionary<string, Cached<SigInfo>> SigCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Cached<string>> ShaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Cached<string>> OrigCache = new(StringComparer.OrdinalIgnoreCase);

    private static (long, long) Stamp(string path)
    {
        var fi = new FileInfo(path);
        return (fi.Length, fi.LastWriteTimeUtc.Ticks);
    }

    /// <summary>זורקים מהמטמון (לפני Launch/Add: בדיקה טרייה, לא לפי mtime)</summary>
    internal static void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        SigCache.TryRemove(path, out _); ShaCache.TryRemove(path, out _); TrustLoc.TryRemove(path, out _);
    }

    internal static SigInfo GetSignature(string path)
    {
        try
        {
            var (len, ticks) = Stamp(path);
            if (SigCache.TryGetValue(path, out var c) && c.Len == len && c.Ticks == ticks) return c.Val;
            var res = ComputeSignature(path);
            SigCache[path] = new Cached<SigInfo>(len, ticks, res);
            return res;
        }
        catch { return SigInfo.None; }
    }

    private static SigInfo ComputeSignature(string path)
    {
        IntPtr pPath = Marshal.StringToHGlobalUni(path);
        IntPtr pFile = IntPtr.Zero;
        try
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pPath };
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fi, pFile, false);
            var wd = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (בלי רשת)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int hr = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref wd);
            if (hr != 0) return SigInfo.None;
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return new SigInfo(true, cert.Subject ?? "");
        }
        catch { return SigInfo.None; }
        finally
        {
            if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pPath);
        }
    }

    internal static string GetSha256(string path)
    {
        try
        {
            var (len, ticks) = Stamp(path);
            if (ShaCache.TryGetValue(path, out var c) && c.Len == len && c.Ticks == ticks) return c.Val;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string h = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            ShaCache[path] = new Cached<string>(len, ticks, h);
            return h;
        }
        catch { return ""; }
    }

    /// <summary>OriginalFilename מתוך VersionInfo (בלי סיומת, ללא תלות באותיות); ריק אם אין</summary>
    internal static string GetOriginalName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var (len, ticks) = Stamp(path);
            if (OrigCache.TryGetValue(path, out var c) && c.Len == len && c.Ticks == ticks) return c.Val;
            string o = FileVersionInfo.GetVersionInfo(path).OriginalFilename ?? "";
            OrigCache[path] = new Cached<string>(len, ticks, o);
            return o;
        }
        catch { return ""; }
    }

    private sealed class RealEnv : ITrustEnv
    {
        public bool IsTrustedLocation(string path) => AppTrust.IsTrustedLocation(path);
        public SigInfo Sig(string path) => GetSignature(path);
        public string Sha256(string path) => GetSha256(path);
    }

    internal static readonly ITrustEnv Env = new RealEnv();

    /// <summary>נעיצה של קובץ בעת Add: מפרסם (אם חתום-תקף) + SHA-256 (גיבוי, גם לקבצים לא חתומים)</summary>
    internal static (string sha256, string signer) CreatePin(string path)
    {
        Invalidate(path);
        var s = GetSignature(path);
        return (GetSha256(path), s.Valid ? s.Subject : "");
    }

    // ---------- הריגה (לא בולעים כשלון) ----------

    private static readonly object LogGate = new();
    private static readonly Dictionary<string, DateTime> LogTimes = new();

    internal static void LogThrottled(string key, string msg, int seconds)
    {
        lock (LogGate)
        {
            var now = DateTime.UtcNow;
            if (LogTimes.TryGetValue(key, out var t) && (now - t).TotalSeconds < seconds) return;
            LogTimes[key] = now;
        }
        Config.Log(msg);
    }

    /// <summary>
    /// Kill שלא בולע שגיאה: AccessDenied (תהליך מוגבה/מוגן) נרשם ליומן ומוחזר false כדי שהקורא יפעיל חלופה
    /// (החזרת אוצריא לחזית). הריגה כשירות (SYSTEM) לתהליך מוגבה דורשת פעולה חדשה בשירות — ר' הדוח.
    /// </summary>
    internal static bool KillLogged(Process p, string tag)
    {
        string name = "?"; int pid = -1;
        try { name = p.ProcessName; pid = p.Id; } catch { }
        try
        {
            if (p.HasExited) return true;
            p.Kill();
            return true;
        }
        catch (Exception ex)
        {
            try { if (p.HasExited) return true; } catch { }
            LogThrottled("killfail:" + tag + ":" + name, "kill נכשל (" + tag + ") " + name + " pid=" + pid + ": " + ex.GetType().Name + " " + ex.Message, 30);
            return false;
        }
    }

    // ---------- תהליכים שנפתחו דרך הגשר (PID reuse + תוקף) ----------

    internal readonly record struct ProcInfo(bool Alive, DateTime? Start);

    internal sealed class LaunchedSet
    {
        internal static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
        private readonly Dictionary<int, (DateTime at, DateTime start)> _d = new();
        private readonly object _g = new();

        public void Remember(int pid, DateTime start, DateTime now)
        {
            lock (_g)
            {
                foreach (int k in _d.Where(x => now - x.Value.at > Validity).Select(x => x.Key).ToList()) _d.Remove(k);
                _d[pid] = (now, start);
            }
        }

        /// <summary>
        /// האם pid הוא תהליך שנרשם. חי: זמן היצירה חייב להיות זהה (אחרת PID הוחזר לתהליך אחר). מת: רק אם childStart
        /// (זמן היצירה של הבן שהצביע עליו) מאוחר מזמן היצירה הרשום — מפעיל שכבר נסגר. תהליך חי שאין גישה לזמנו — לא מתקבל.
        /// </summary>
        public bool Contains(int pid, ProcInfo info, DateTime now, DateTime? childStart = null)
        {
            (DateTime at, DateTime start) rec;
            lock (_g)
            {
                if (!_d.TryGetValue(pid, out rec)) return false;
                if (now - rec.at > Validity) { _d.Remove(pid); return false; }   // תוקף 10 דקות באמת נאכף
            }
            if (info.Alive)
                return info.Start.HasValue && Math.Abs((info.Start.Value - rec.start).TotalSeconds) < 1;
            return childStart.HasValue && childStart.Value >= rec.start;
        }

        /// <summary>האם pid או אחד מאבותיו (עד 6 דורות) נפתח דרך הגשר; האב חייב להיות מבוגר מהבן (PID reuse)</summary>
        public bool IsTree(int pid, Func<int, ProcInfo> probe, Func<int, int> parentOf, DateTime now)
        {
            for (int i = 0; i < 6 && pid > 4; i++)
            {
                var info = probe(pid);
                if (Contains(pid, info, now)) return true;
                if (!info.Alive || !info.Start.HasValue) return false;
                int parent = parentOf(pid);
                if (parent <= 4) return false;
                var pi = probe(parent);
                if (!pi.Alive) return Contains(parent, pi, now, info.Start);
                if (!pi.Start.HasValue || pi.Start.Value > info.Start.Value) return false;   // "אב" צעיר מהבן = PID הוחזר
                pid = parent;
            }
            return false;
        }
    }
}
