using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// נקודות הקצה /recycle/* — סל המיחזור של Windows.
/// כל פריט בסל הוא זוג קבצים ב-"\$Recycle.Bin\SID" של המשתמש: "$I..." (מטא-נתונים) ו-"$R..." (התוכן).
/// </summary>
public static class RecycleApi
{
    private static readonly Regex IName =
        new(@"^\$I[A-Za-z0-9]{1,16}(\.[^\\/:*?""<>|]*)?$", RegexOptions.Compiled);

    private sealed class Item
    {
        public string Id, Name, OriginalPath, Drive, Ext, IPath, RPath;
        public bool IsDir;
        public long Size;
        public DateTime Deleted;
    }

    public static void Register()
    {
        Server.Route("/recycle/list", _ => List());
        Server.Route("/recycle/restore", Restore);
        Server.Route("/recycle/delete", Delete);
        Server.Route("/recycle/empty", _ => Empty());
    }

    // ---------- עזרים ----------

    private static string Sid => WindowsIdentity.GetCurrent().User?.Value;

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static List<string> BinDirs()
    {
        var res = new List<string>();
        string sid = Sid;
        if (string.IsNullOrEmpty(sid)) return res;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) continue;
                string dir = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (Directory.Exists(dir)) res.Add(dir);
            }
            catch { /* כונן לא נגיש */ }
        }
        return res;
    }

    private static Item Parse(string iPath, string iName, string drive, string dir)
    {
        byte[] b = File.ReadAllBytes(iPath);
        if (b.Length < 28) return null;
        long ver = BitConverter.ToInt64(b, 0);
        long size = BitConverter.ToInt64(b, 8);
        long ft = BitConverter.ToInt64(b, 16);
        string orig;
        if (ver == 2)
        {
            int chars = BitConverter.ToInt32(b, 24);
            if (chars < 1 || 28L + (long)chars * 2 > b.Length) return null;
            orig = Encoding.Unicode.GetString(b, 28, (chars - 1) * 2);
        }
        else if (ver == 1)
        {
            int n = Math.Min(520, b.Length - 24);
            orig = Encoding.Unicode.GetString(b, 24, n);
            int z = orig.IndexOf('\0');
            if (z >= 0) orig = orig.Substring(0, z);
        }
        else return null;

        if (!Path.IsPathFullyQualified(orig)) return null;
        // $I ניתן לכתיבה ע"י המשתמש (תיקיית הסל שלו): נתיב המקור הוא קלט לא מהימן. מנרמלים (8.3, התקן, ADS)
        // ודוחים כל נתיב שאינו תקין; ההגנה על יעד מוגן נאכפת ב-Restore (ר' CheckRestoreTarget)
        try { orig = PathPolicy.Normalize(orig); } catch { return null; }
        string rPath = Path.Combine(dir, "$R" + iName.Substring(2));
        bool isDir = Directory.Exists(rPath);
        if (!isDir && !File.Exists(rPath)) return null; // $I יתום
        // $R שהוא קישור (reparse) ולא תוכן אמיתי: שחזור שלו ייצור קישור לקובץ סודי בתיקייה מותרת
        try { if ((File.GetAttributes(rPath) & FileAttributes.ReparsePoint) != 0) return null; } catch { return null; }

        DateTime deleted;
        try { deleted = ft > 0 ? DateTime.FromFileTimeUtc(ft) : File.GetLastWriteTimeUtc(iPath); }
        catch { deleted = File.GetLastWriteTimeUtc(iPath); }

        return new Item
        {
            Id = drive + ":" + iName,
            Name = Path.GetFileName(orig.TrimEnd('\\')),
            OriginalPath = orig,
            Drive = drive,
            Ext = isDir ? "" : Path.GetExtension(orig),
            IPath = iPath,
            RPath = rPath,
            IsDir = isDir,
            Size = Math.Max(0, size),
            Deleted = deleted,
        };
    }

    private static List<Item> LoadAll()
    {
        var list = new List<Item>();
        foreach (string dir in BinDirs())
        {
            string drive = dir.Substring(0, 1).ToUpperInvariant();
            string[] files;
            try { files = Directory.GetFiles(dir, "$I*"); }
            catch { continue; }
            foreach (string ip in files)
            {
                try
                {
                    string iName = Path.GetFileName(ip);
                    if (!IName.IsMatch(iName)) continue;
                    var it = Parse(ip, iName, drive, dir);
                    if (it != null) list.Add(it);
                }
                catch { /* פריט פגום מדולג */ }
            }
        }
        return list;
    }

    /// <summary>מאתר פריט לפי מזהה, ורק בתוך סל המיחזור של המשתמש הנוכחי</summary>
    private static Item Resolve(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 4 || id[1] != ':')
            throw BridgeException.BadRequest("מזהה פריט לא תקין");
        string drive = id.Substring(0, 1).ToUpperInvariant();
        string iName = id.Substring(2);
        if (!Regex.IsMatch(drive, "^[A-Z]$") || !IName.IsMatch(iName))
            throw BridgeException.BadRequest("מזהה פריט לא תקין");
        string sid = Sid;
        if (string.IsNullOrEmpty(sid)) throw BridgeException.Unsupported("לא ניתן לזהות את המשתמש");
        string dir = Path.Combine(drive + ":\\", "$Recycle.Bin", sid);
        string ip = Path.Combine(dir, iName);
        if (!File.Exists(ip)) throw BridgeException.NotFound("הפריט כבר אינו בסל המיחזור");
        return Parse(ip, iName, drive, dir) ?? throw BridgeException.NotFound("הפריט כבר אינו בסל המיחזור");
    }

    /// <summary>
    /// יעד שחזור (מגיע מקובץ $I שמשתמש יכול לזייף): חייב לעבור מדיניות, אזורים מוגנים (כולל Startup/Program Files/
    /// נתוני הגשר) וחסימת סיומות מריצות. נקרא גם מהבדיקות.
    /// </summary>
    internal static void CheckRestoreTarget(string dest)
    {
        FxPolicy.EnsurePathAllowed(dest);
        FsApi.EnsureNotProtected(dest);
        FsApi.EnsureNotReadable(dest);
        FsApi.EnsureWriteTargetAllowed(dest);
    }

    private static void DeleteTree(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(dir, true);
    }

    // ---------- נקודות קצה ----------

    private static object List()
    {
        FxPolicy.EnsureAvailable();
        var items = LoadAll()
            .Where(i => FxPolicy.IsPathAllowed(i.OriginalPath))   // במצב מוגבל: רק פריטים שמקורם בתיקיות המותרות
            .OrderByDescending(i => i.Deleted)
            .Select(i => new
            {
                id = i.Id,
                name = i.Name,
                originalPath = i.OriginalPath,
                size = i.Size,
                deleted = Iso(i.Deleted),
                type = i.IsDir ? "dir" : "file",
                ext = i.Ext,
                drive = i.Drive,
            })
            .ToList();
        return new { items };
    }

    private static object Restore(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        string conflict = b.Str("conflict", "rename");
        if (conflict is not ("rename" or "skip"))
            throw BridgeException.BadRequest("conflict חייב להיות skip או rename");

        int restored = 0, skipped = 0;
        var failed = new List<object>();
        foreach (string id in b.StrArray("ids"))
        {
            try
            {
                var it = Resolve(id);
                string dest = it.OriginalPath;
                string parent = Path.GetDirectoryName(dest);
                if (string.IsNullOrEmpty(parent)) throw BridgeException.Forbidden("אין לשחזר לשורש כונן");
                Kiosk.EnsureFileWriteAllowed();
                CheckRestoreTarget(dest);
                Directory.CreateDirectory(parent);
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    if (conflict == "skip") { skipped++; continue; }
                    dest = FsApi.UniqueName(parent, Path.GetFileName(dest));
                }
                if (it.IsDir) Directory.Move(it.RPath, dest);
                else File.Move(it.RPath, dest);
                try { File.Delete(it.IPath); } catch { }
                restored++;
            }
            catch (BridgeException bx) when (bx.Code.StartsWith("fx_")) { throw; }
            catch (Exception ex)
            {
                failed.Add(new { id, message = ex.Message });
            }
        }
        return new { restored, skipped, failed };
    }

    private static object Delete(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        int deleted = 0;
        var failed = new List<object>();
        foreach (string id in b.StrArray("ids"))
        {
            try
            {
                var it = Resolve(id);
                FxPolicy.EnsurePathAllowed(it.OriginalPath);
                if (it.IsDir) DeleteTree(it.RPath);
                else
                {
                    File.SetAttributes(it.RPath, FileAttributes.Normal);
                    File.Delete(it.RPath);
                }
                File.Delete(it.IPath);
                deleted++;
            }
            catch (BridgeException bx) when (bx.Code.StartsWith("fx_")) { throw; }
            catch (Exception ex)
            {
                failed.Add(new { id, message = ex.Message });
            }
        }
        return new { deleted, failed };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    private static object Empty()
    {
        Kiosk.EnsureFileWriteAllowed();
        FxPolicy.EnsureAvailable();
        if (FxPolicy.IsRestricted) throw FxPolicy.Outside("במצב מוגבל אי אפשר לרוקן את כל סל המיחזור");
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        // 0x8000FFFF — הסל כבר ריק (בחלק מגרסאות Windows)
        if (hr != 0 && unchecked((uint)hr) != 0x8000FFFF)
            throw new IOException("ריקון סל המיחזור נכשל (קוד 0x" + hr.ToString("X8") + ")");
        return new { emptied = true };
    }
}
