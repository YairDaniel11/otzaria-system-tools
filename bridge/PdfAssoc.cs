using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>שלב ד של docs/PDF-INTEGRATION-SPEC.md — "פתח באמצעות" של Windows ישירות לתוסף "צופה PDF+OCR".
/// • רישום ב-HKCU (בלי הרשאות מנהל): Applications\OtzariaBridge.exe + ProgId OtzariaBridge.pdf ב-.pdf\OpenWithProgids,
///   כך שהצופה מופיע ב"פתח באמצעות" וב"אפליקציות ברירת מחדל". נרשם אוטומטית בעליית הגשר (אפשר לבטל: pdf.assoc=false).
/// • OtzariaBridge.exe --open-pdf "path" → הנתיב נכתב לתור (קובץ בתיקיית inbox, כדי לעבור בין מופעים בלי IPC),
///   ואוצריא נפתחת ישירות בתוסף (otzaria://open/plugin/&lt;id&gt;). התוסף שולף ב-/pdf/inbox/list ומאשר ב-/pdf/inbox/ack.
/// • חלון "פתח באמצעות" של Windows (בחירת תוכנה) נפתח מחוט STA עם חלון בעלים בחזית, כדי שלא ייעלם מאחור וייסגר מעצמו.</summary>
internal static class PdfAssoc
{
    public const string PluginId = "com.chadbedera.pdfviewer";
    private const string ProgId = "OtzariaBridge.pdf";
    private const string ExeName = "OtzariaBridge.exe";
    private static string InboxDir => Path.Combine(Config.Dir, "pdf-inbox");
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static void Register()
    {
        Server.Route("/pdf/assoc/status", _ => Status());
        Server.Route("/pdf/assoc/register", _ => { if (Kiosk.IsActive) throw BridgeException.Locked(); Write(); return Status(); });
        Server.Route("/pdf/assoc/unregister", _ => { if (Kiosk.IsActive) throw BridgeException.Locked(); Remove(); Config.Set("pdf.assoc", false); return Status(); });
        Server.Route("/pdf/assoc/open-settings", _ => { if (Kiosk.IsActive) throw BridgeException.Locked(); Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); return new { opened = true }; });
        Server.Route("/pdf/inbox/list", _ => new { items = List() });
        Server.Route("/pdf/inbox/ack", Ack);
        // רישום אוטומטי בעלייה (מה שסוכם): הצופה מופיע ב"פתח באמצעות" בלי פעולה נוספת
        try { if (Config.Get("pdf.assoc", true)) Write(); } catch (Exception ex) { Config.Log("pdf assoc: " + ex.Message); }
    }

    private static string Exe => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? Config.Dir, ExeName);
    private static string Command => "\"" + Exe + "\" --open-pdf \"%1\"";

    private static object Status()
    {
        string cmd = null;
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"); cmd = k?.GetValue("") as string; } catch { }
        bool isDefault = false;
        try
        {
            using var u = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            isDefault = string.Equals(u?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase)
                        || (u?.GetValue("ProgId") as string ?? "").Contains("OtzariaBridge", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return new { registered = cmd != null, isDefault, command = cmd };
    }

    private static void Write()
    {
        string name = Lang.T("צופה PDF+OCR (אוצריא)", "PDF+OCR Viewer (Otzaria)");
        string icon = "\"" + Exe + "\",0";
        var C = Registry.CurrentUser;
        using (var k = C.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            k.SetValue("", name); k.SetValue("FriendlyTypeName", name);
            using (var di = k.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
            using (var sh = k.CreateSubKey(@"shell\open")) sh.SetValue("FriendlyAppName", name);
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", Command);
        }
        using (var k = C.CreateSubKey(@"Software\Classes\Applications\" + ExeName))
        {
            k.SetValue("FriendlyAppName", name);
            using (var st = k.CreateSubKey("SupportedTypes")) st.SetValue(".pdf", "");
            using (var di = k.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", Command);
        }
        using (var k = C.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids")) k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        // "אפליקציות ברירת מחדל" של Windows
        using (var k = C.CreateSubKey(@"Software\OtzariaBridge\Capabilities"))
        {
            k.SetValue("ApplicationName", name); k.SetValue("ApplicationDescription", name);
            using var fa = k.CreateSubKey("FileAssociations"); fa.SetValue(".pdf", ProgId);
        }
        using (var k = C.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue("OtzariaBridge", @"Software\OtzariaBridge\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    public static void Remove()
    {
        var C = Registry.CurrentUser;
        try { C.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch { }
        try { C.DeleteSubKeyTree(@"Software\Classes\Applications\" + ExeName, false); } catch { }
        try { using var k = C.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", true); k?.DeleteValue(ProgId, false); } catch { }
        try { C.DeleteSubKeyTree(@"Software\OtzariaBridge\Capabilities", false); } catch { }
        try { using var k = C.OpenSubKey(@"Software\RegisteredApplications", true); k?.DeleteValue("OtzariaBridge", false); } catch { }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, int flags, IntPtr a, IntPtr b);

    // ── תור הפתיחה ─────────────────────────────────────────────────────────

    /// <summary>OtzariaBridge.exe --open-pdf "path": נכתב לתור ופותח את אוצריא בתוסף. נקרא מ-Main לפני בדיקת המופע היחיד.</summary>
    public static void Enqueue(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(InboxDir);
            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(Path.Combine(InboxDir, id + ".txt"), path);
            OpenPluginInOtzaria();
        }
        catch (Exception ex) { Config.Log("open-pdf: " + ex.Message); }
    }

    private static void OpenPluginInOtzaria()
    {
        try { Process.Start(new ProcessStartInfo("otzaria://open/plugin/" + PluginId) { UseShellExecute = true }); return; }
        catch (Exception ex) { Config.Log("open-pdf otzaria uri: " + ex.Message); }
        try
        {
            string p = Config.Get<string>("kiosk.otzariaPath", "");
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch { }
    }

    private static List<object> List()
    {
        var res = new List<object>();
        if (!Directory.Exists(InboxDir)) return res;
        foreach (var f in Directory.GetFiles(InboxDir, "*.txt").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var at = File.GetLastWriteTimeUtc(f);
                if (DateTime.UtcNow - at > Ttl) { File.Delete(f); continue; }
                string path = File.ReadAllText(f).Trim();
                try { FxPolicy.EnsurePathAllowed(path); } catch { File.Delete(f); continue; } // מחוץ למדיניות — נזרק בשקט
                if (Kiosk.IsActive && !Kiosk.AllowedExtensions.Any(x => x.Equals("pdf", StringComparison.OrdinalIgnoreCase))) continue;
                long size = 0; try { size = new FileInfo(path).Length; } catch { }
                res.Add(new { id = Path.GetFileNameWithoutExtension(f), path, name = Path.GetFileName(path), size, at = at.ToString("o"), source = "open-with" });
            }
            catch { }
        }
        return res;
    }

    private static object Ack(JsonElement b)
    {
        int n = 0;
        if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var e in ids.EnumerateArray())
            {
                string id = e.GetString() ?? "";
                if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) continue;
                string f = Path.Combine(InboxDir, id + ".txt");
                try { if (File.Exists(f)) { File.Delete(f); n++; } } catch { }
            }
        return new { removed = n };
    }

    // ── חלון "פתח באמצעות" שנשאר פתוח ───────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO { public string pcszFile; public string pcszClass; public int oaifInFlags; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>הקודם (rundll32 OpenAs_RunDLL מתהליך רקע) נפתח בלי זכות חזית, מאחורי אוצריא, ו"נעלם" אחרי כמה שניות
    /// כי חלונית הבחירה של Windows 10/11 נסגרת כשאין לה פוקוס. כאן: חוט STA, חלון בעלים זעיר שנלקח לחזית,
    /// ו-SHOpenWithDialog מודאלי עם OAIF_EXEC — נשאר עד שהמשתמש בוחר או סוגר.</summary>
    public static void ShowOpenWith(string path)
    {
        var t = new Thread(() =>
        {
            try
            {
                using var owner = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                    StartPosition = FormStartPosition.Manual, Size = new System.Drawing.Size(1, 1), Opacity = 0.01,
                    Location = Cursor.Position,
                };
                owner.Show();
                ForceForeground(owner.Handle);
                var info = new OPENASINFO { pcszFile = path, pcszClass = null, oaifInFlags = 0x1 | 0x4 | 0x20 }; // ALLOW_REGISTRATION | EXEC | HIDE_REGISTRATION
                int hr = SHOpenWithDialog(owner.Handle, ref info);
                if (hr < 0 && hr != unchecked((int)0x800704C7)) // לא "בוטל ע"י המשתמש"
                    Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false });
                owner.Close();
            }
            catch (Exception ex) { Config.Log("open-with dialog: " + ex.Message); }
        }) { IsBackground = true, Name = "OpenWith" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static void ForceForeground(IntPtr h)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            IntPtr fg = GetForegroundWindow();
            uint fgT = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
            if (fgT != 0 && fgT != me) AttachThreadInput(me, fgT, true);
            SetForegroundWindow(h);
            if (fgT != 0 && fgT != me) AttachThreadInput(me, fgT, false);
        }
        catch { }
    }
}
