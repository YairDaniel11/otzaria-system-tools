using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>נקודות הקצה /fs/* שהצופה משתמש בהן: roots, stat, read, write-binary, pick-save-file, search.</summary>
public static class FsApi
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".md"] = "text/markdown",
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".webp"] = "image/webp",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    public static void Register()
    {
        Server.Route("/fs/roots", _ => Roots());
        Server.Route("/fs/stat", b => Stat(FullPath(b.Req("path"))));
        Server.Route("/fs/read", Read);
        Server.Route("/fs/write-binary", WriteBinary);
        Server.Route("/fs/pick-save-file", PickSaveFile);
    }

    // ---------- עזרים ----------

    private static string FullPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) throw BridgeException.BadRequest("נתיב ריק");
        p = p.Trim();
        if (!Path.IsPathFullyQualified(p)) throw BridgeException.BadRequest("נדרש נתיב מלא: " + p);
        return Path.GetFullPath(p);
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>כתיבה אסורה באזורי מערכת, ב-Program Files ובתיקיית הנתונים של הגשר עצמו (שם נשמרים מפתחות הצימוד)</summary>
    private static void EnsureWritable(string p)
    {
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\"))
            throw BridgeException.Forbidden("נתיב מסוג התקן אינו נתמך");
        string full = Path.GetFullPath(p).TrimEnd('\\');
        string root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
        if (full.Length == 0 || full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw BridgeException.Forbidden("לא ניתן לכתוב בשורש כונן");

        var guarded = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Cfg.Dir,
        };
        foreach (string g in guarded.Where(x => !string.IsNullOrEmpty(x)))
        {
            string gg = g.TrimEnd('\\');
            if (full.Equals(gg, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(gg + "\\", StringComparison.OrdinalIgnoreCase))
                throw BridgeException.Forbidden("אזור מוגן: " + gg);
        }
        string rest = full.Substring(root.Length).TrimStart('\\');
        if (rest.Split('\\')[0].StartsWith("Program Files", StringComparison.OrdinalIgnoreCase))
            throw BridgeException.Forbidden("אזור מוגן: Program Files");
    }

    // ---------- /fs/roots ----------

    private static object Roots()
    {
        var drives = new List<object>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "", type = d.DriveType.ToString().ToLowerInvariant();
            long total = 0, free = 0;
            try
            {
                if (d.IsReady) { label = d.VolumeLabel; total = d.TotalSize; free = d.AvailableFreeSpace; }
            }
            catch { }
            drives.Add(new { path = d.RootDirectory.FullName, label, type, totalBytes = total, freeBytes = free });
        }

        var known = new List<object>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Add(string id, string name, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) known.Add(new { id, name, path });
        }
        Add("desktop", "שולחן העבודה", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("documents", "מסמכים", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("downloads", "הורדות", KnownDownloads() ?? Path.Combine(home, "Downloads"));
        Add("pictures", "תמונות", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("music", "מוזיקה", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("videos", "סרטונים", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("home", "תיקיית הבית", home);
        return new { drives, known };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string KnownDownloads()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                string s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return null;
    }

    // ---------- /fs/stat ----------

    private static object Stat(string path)
    {
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return new { name = f.Name, path = f.FullName, type = "file", size = f.Length, created = Iso(f.CreationTime), modified = Iso(f.LastWriteTime), readOnly = f.IsReadOnly };
        }
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            return new { name = d.Name, path = d.FullName, type = "dir", size = 0L, created = Iso(d.CreationTime), modified = Iso(d.LastWriteTime), readOnly = false };
        }
        throw BridgeException.NotFound("לא נמצא: " + path);
    }

    // ---------- /fs/read ----------

    private static object Read(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + path);
        long max = Math.Clamp(b.Long("maxBytes", 5L * 1024 * 1024), 1, 50L * 1024 * 1024);
        // offset: קריאה בחלקים של קובץ גדול מ-50MB. binary: תמיד base64.
        long offset = Math.Max(0, b.Long("offset", 0));
        bool binary = b.Bool("binary");
        var fi = new FileInfo(path);
        string ext = fi.Extension;
        long remaining = Math.Max(0, fi.Length - offset);
        bool truncated = remaining > max;
        int len = (int)Math.Min(remaining, max);

        byte[] buf = new byte[len];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (offset > 0) fs.Seek(Math.Min(offset, fs.Length), SeekOrigin.Begin);
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n == 0) break;
                read += n;
            }
            if (read < len) Array.Resize(ref buf, read);
        }

        string mime = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        if (!binary && offset == 0 && TextExt.Contains(ext))
            return new { name = fi.Name, size = fi.Length, mime, encoding = "utf8", content = DecodeText(buf), truncated, offset };
        return new { name = fi.Name, size = fi.Length, mime, encoding = "base64", content = Convert.ToBase64String(buf), truncated, offset };
    }

    private const int Slice = 786432;   // 768KB — מכפלה של 3, כדי שקידוד base64 של כל פרוסה (חוץ מהאחרונה) לא ירפד

    /// <summary>/fs/read בזרימה לקבצים בינאריים: קריאה → base64 → כתיבה ללקוח בפרוסות של 768KB, בלי ליצור במקביל עותקים של כל החלק בזיכרון
    /// (במסלול הישן: מערך + מחרוזת base64 + מחרוזת JSON + בייטים, כמה מאות MB לחלק של 24MB). פורמט התשובה זהה לחלוטין.
    /// מחזיר false אם הבקשה אינה מתאימה (קובץ טקסט — מפוענח במסלול הרגיל); אז לא נכתב כלום.
    /// onStart נקרא רגע לפני תחילת הכתיבה — מאותו רגע שגיאות לא נשלחות כ-JSON רגיל.</summary>
    public static async Task<bool> ReadStreaming(HttpListenerResponse res, JsonElement b, Action onStart)
    {
        string path = FullPath(b.Req("path"));
        if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + path);
        long max = Math.Clamp(b.Long("maxBytes", 5L * 1024 * 1024), 1, 50L * 1024 * 1024);
        long offset = Math.Max(0, b.Long("offset", 0));
        bool binary = b.Bool("binary");
        string ext = Path.GetExtension(path);
        if (!binary && offset == 0 && TextExt.Contains(ext)) return false;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = fs.Length;
        long remaining = Math.Max(0, length - offset);
        bool truncated = remaining > max;
        int len = (int)Math.Min(remaining, max);
        if (offset > 0) fs.Seek(Math.Min(offset, length), SeekOrigin.Begin);

        string mime = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        byte[] head = Encoding.UTF8.GetBytes("{\"ok\":true,\"data\":{\"name\":" + JsonSerializer.Serialize(Path.GetFileName(path), Server.Json)
            + ",\"size\":" + length + ",\"mime\":" + JsonSerializer.Serialize(mime, Server.Json) + ",\"encoding\":\"base64\",\"content\":\"");
        byte[] tail = Encoding.UTF8.GetBytes("\",\"truncated\":" + (truncated ? "true" : "false") + ",\"offset\":" + offset + "}}");

        onStart();
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = head.Length + 4L * ((len + 2) / 3) + tail.Length;
        var os = res.OutputStream;
        byte[] raw = ArrayPool<byte>.Shared.Rent(Slice), enc = ArrayPool<byte>.Shared.Rent(Slice / 3 * 4);
        try
        {
            await os.WriteAsync(head);
            int left = len;
            while (left > 0)
            {
                int want = Math.Min(Slice, left), got = 0;
                while (got < want)
                {
                    int n = await fs.ReadAsync(raw.AsMemory(got, want - got));
                    if (n == 0) break;
                    got += n;
                }
                if (got < want) throw new IOException("הקובץ השתנה באמצע הקריאה");
                Base64.EncodeToUtf8(raw.AsSpan(0, want), enc, out _, out int written, want == left);
                await os.WriteAsync(enc.AsMemory(0, written));
                left -= want;
            }
            await os.WriteAsync(tail);
            res.Close();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(enc);
        }
        return true;
    }

    private static string DecodeText(byte[] buf)
    {
        if (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return Encoding.UTF8.GetString(buf, 3, buf.Length - 3);
        if (buf.Length >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf, 2, buf.Length - 2);
        if (buf.Length >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(buf, 2, buf.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(buf); }
        catch (DecoderFallbackException)
        {
            // קבצי טקסט ישנים בעברית (Windows-1255)
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1255).GetString(buf);
            }
            catch { return Encoding.UTF8.GetString(buf); }
        }
    }

    // ---------- /fs/pick-save-file ----------

    /// <summary>חלון "שמירה בשם" של Windows. גוף: { suggestedName, initialDir?, title?, filter? } → { cancelled, path }</summary>
    private static object PickSaveFile(JsonElement b)
    {
        string name = b.Str("suggestedName", "document.pdf");
        string initial = b.Str("initialDir", "");
        string title = b.Str("title", "שמירה");
        string filter = b.Str("filter", "PDF (*.pdf)|*.pdf");
        string result = null;
        Exception error = null;
        var t = new Thread(() =>
        {
            try
            {
                // חלון בעלים בלתי נראה וקבוע-מלמעלה, כדי שחלון השמירה יופיע מעל אוצריא
                using var owner = new Form
                {
                    TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1),
                };
                owner.Show();
                using var dlg = new SaveFileDialog
                {
                    Title = title, FileName = name, Filter = filter, OverwritePrompt = true,
                    AddExtension = true, DefaultExt = "pdf", CheckPathExists = true, RestoreDirectory = true,
                };
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
                if (dlg.ShowDialog(owner) == DialogResult.OK) result = dlg.FileName;
                owner.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(115000)) throw new BridgeException("timeout", "חלון השמירה לא נסגר בזמן", 408);
        if (error != null) throw error;
        if (result == null) return new { cancelled = true, path = (string)null };
        EnsureWritable(result);
        return new { cancelled = false, path = result };
    }

    // ---------- /fs/write-binary ----------

    /// <summary>כתיבת קובץ בינארי בחתיכות. גוף: { path, content (base64), append?, overwrite?, unique?, makeDirs? }.
    /// unique = אם הקובץ קיים — "שם (2).סיומת". החתיכה הראשונה מחזירה את הנתיב הסופי; הבאות מצרפות אליו עם append.</summary>
    private static object WriteBinary(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        EnsureWritable(path);
        bool append = b.Bool("append"), overwrite = b.Bool("overwrite"), unique = b.Bool("unique"), makeDirs = b.Bool("makeDirs");
        if (Directory.Exists(path)) throw BridgeException.Exists("קיימת תיקייה בשם זה");
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir))
        {
            if (!makeDirs) throw BridgeException.NotFound("התיקייה לא נמצאה: " + dir);
            Directory.CreateDirectory(dir);
        }
        byte[] data;
        try { data = Convert.FromBase64String(b.Str("content", "")); }
        catch (FormatException) { throw BridgeException.BadRequest("התוכן אינו base64 תקין"); }

        if (!append)
        {
            if (File.Exists(path))
            {
                if (unique)
                {
                    string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
                    int n = 2;
                    do { path = Path.Combine(dir, stem + " (" + n + ")" + ext); n++; } while (File.Exists(path));
                }
                else if (!overwrite) throw BridgeException.Exists("הקובץ כבר קיים: " + path);
            }
            File.WriteAllBytes(path, data);
        }
        else
        {
            if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא להמשך כתיבה: " + path);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(data, 0, data.Length);
        }
        return new { path, size = new FileInfo(path).Length };
    }

    // ---------- /fs/search (NDJSON זורם) ----------

    private static readonly string[] SkipDirNames =
        { "$Recycle.Bin", "System Volume Information", "Windows", "WinSxS", "$WinREAgent", "Config.Msi", "node_modules", ".git" };

    /// <summary>גוף: { root, query (תת-מחרוזת או עם * ?), extensions?, includeDirs?, maxResults? }. מחזיר שורות JSON: {type:"hit",...} ... {type:"done"}.
    /// onStart נקרא רגע לפני תחילת הזרימה — מאותו רגע שגיאות כבר לא נשלחות כ-JSON רגיל.</summary>
    public static async Task Search(HttpListenerResponse res, JsonElement b, Action onStart)
    {
        string root = FullPath(b.Req("root"));
        if (!Directory.Exists(root)) throw BridgeException.NotFound("תיקיית החיפוש לא נמצאה: " + root);
        string query = b.Str("query", "") ?? "";
        if (query.Length == 0) throw BridgeException.BadRequest("חסר query");
        var exts = new HashSet<string>(b.StrArray("extensions").Select(e => "." + e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        int maxResults = (int)Math.Clamp(b.Long("maxResults", 500), 1, 10000);
        bool includeDirs = b.Bool("includeDirs", true);

        Func<string, bool> match;
        if (query.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            var rx = new Regex("^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            match = n => rx.IsMatch(n);
        }
        else match = n => n.Contains(query, StringComparison.OrdinalIgnoreCase);

        onStart();
        res.StatusCode = 200;
        res.ContentType = "application/x-ndjson; charset=utf-8";
        res.SendChunked = true;
        res.Headers["Cache-Control"] = "no-cache";
        var os = res.OutputStream;

        async Task Emit(object o)
        {
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o, Server.Json) + "\n");
            await os.WriteAsync(line);
            await os.FlushAsync();
        }

        long scanned = 0;
        int hits = 0;
        bool truncated = false;
        var stack = new Stack<string>();
        stack.Push(root);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        try
        {
            while (stack.Count > 0 && !truncated)
            {
                string dir = stack.Pop();
                List<FileSystemInfo> items;
                try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
                catch { continue; }

                foreach (var i in items)
                {
                    scanned++;
                    bool isDir = i is DirectoryInfo;
                    if (isDir)
                    {
                        if (!SkipDirNames.Contains(i.Name, StringComparer.OrdinalIgnoreCase) &&
                            (i.Attributes & FileAttributes.System) == 0)
                            stack.Push(i.FullName);
                    }

                    bool ok = isDir
                        ? includeDirs && exts.Count == 0 && match(i.Name)
                        : (exts.Count == 0 || exts.Contains(i.Extension)) && match(i.Name);
                    if (!ok) continue;

                    hits++;
                    await Emit(new
                    {
                        type = "hit",
                        name = i.Name,
                        path = i.FullName,
                        entryType = isDir ? "dir" : "file",
                        size = isDir ? 0L : ((FileInfo)i).Length,
                        modified = Iso(i.LastWriteTime),
                    });
                    if (hits >= maxResults) { truncated = true; break; }
                }
            }
            await Emit(new { type = "done", scanned, hits, truncated, cancelled = false });
            res.Close();
        }
        catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException)
        {
            // התוסף עצר את החיפוש וניתק — זה תקין
            try { res.Abort(); } catch { }
        }
    }
}
