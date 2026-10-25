using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace OtzariaBridge;

/// <summary>שגיאה עם קוד מהחוזה שהתוסף מכיר: unpaired, bad_request, not_found, forbidden, exists, unsupported, ocr_failed...</summary>
public class BridgeException : Exception
{
    public string Code { get; }
    public int Status { get; }
    public BridgeException(string code, string message, int status = 400) : base(message) { Code = code; Status = status; }

    public static BridgeException BadRequest(string m) => new("bad_request", m, 400);
    public static BridgeException NotFound(string m) => new("not_found", m, 404);
    public static BridgeException Forbidden(string m) => new("forbidden", m, 403);
    public static BridgeException Exists(string m) => new("exists", m, 409);
    public static BridgeException Unsupported(string m = "לא נתמך במחשב זה") => new("unsupported", m, 501);
}

/// <summary>כל הקבצים של הגשר נוצרים בתיקייה "data" שליד ה-exe (נייד). אם אין הרשאת כתיבה שם — ב-%LOCALAPPDATA%.</summary>
public static class Cfg
{
    public static readonly string Dir = Resolve();

    private static string Resolve()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string d = Path.Combine(exeDir, "data");
        try
        {
            Directory.CreateDirectory(d);
            string probe = Path.Combine(d, ".w");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return d;
        }
        catch
        {
            d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridgeMin");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static readonly object LogLock = new();
    public static void Log(string s)
    {
        try
        {
            lock (LogLock)
            {
                string f = Path.Combine(Dir, "bridge.log");
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s") + " " + s + Environment.NewLine);
            }
        }
        catch { }
    }
}

public static class Program
{
    public const string Version = "3.9.1"; // רמת תאימות לחוזה שהתוסף בודק (נדרש 3.1.0 ומעלה)

    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, @"Local\OtzariaBridgeMinimal", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        int port;
        try { port = Server.Start(); }
        catch (Exception ex)
        {
            Cfg.Log("start failed: " + ex);
            MessageBox.Show("הגשר לא הצליח להתחיל: " + ex.Message, "גשר אוצריא", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AutoStart.Init();
        Mem.Schedule(2000);

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        var ni = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "גשר אוצריא (מינימלי) — פורט " + port,
            Visible = true,
            ContextMenuStrip = menu,
        };
        var auto = new ToolStripMenuItem("הפעלה אוטומטית עם Windows") { CheckOnClick = true, Checked = AutoStart.IsOn() };
        auto.Click += (_, _) => AutoStart.Set(auto.Checked);
        menu.Opening += (_, _) => auto.Checked = AutoStart.IsOn();
        menu.Items.Add(auto);
        menu.Items.Add("ביטול כל הצימודים", null, (_, _) =>
        {
            if (MessageBox.Show("לבטל את הצימוד של כל התוספים? בפעם הבאה יתבקש אישור מחדש.", "גשר אוצריא",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == DialogResult.Yes)
                Pairing.RevokeAll();
        });
        menu.Items.Add("פתיחת תיקיית הנתונים", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Cfg.Dir) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("יציאה", null, (_, _) =>
        {
            ni.Visible = false;
            Server.Stop();
            Application.Exit();
        });

        Application.Run();
        ni.Dispose();
    }
}

/// <summary>
/// הפעלה אוטומטית עם Windows: ערך אחד ב-HKCU\...\Run (בלי הרשאות מנהל), שמצביע על ה-exe הנוכחי.
/// בהפעלה הראשונה ההפעלה האוטומטית נדלקת; מאותו רגע הבחירה במגש נשמרת (הסמן data\autostart.init).
/// אם ה-exe הועבר — הנתיב ברישום מתעדכן בהפעלה הבאה שלו.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "OtzariaBridge";
    private static string Cmd => "\"" + Environment.ProcessPath + "\"";

    public static bool IsOn()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is string; }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(ValueName, Cmd); else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Cfg.Log("autostart: " + ex.Message); }
    }

    public static void Init()
    {
        try
        {
            string marker = Path.Combine(Cfg.Dir, "autostart.init");
            if (!File.Exists(marker)) { Set(true); File.WriteAllText(marker, "1"); return; }
            if (IsOn())
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                if (!string.Equals(k?.GetValue(ValueName) as string, Cmd, StringComparison.OrdinalIgnoreCase)) Set(true);
            }
        }
        catch (Exception ex) { Cfg.Log("autostart init: " + ex.Message); }
    }
}

/// <summary>
/// שחרור זיכרון: כשאין בקשות פעילות במשך כמה שניות — איסוף זבל מלא עם דחיסה (כולל LOH) והחזרת הזיכרון ל-Windows (Working Set).
/// הזיכרון המשוחרר חוזר לפי הצורך בבקשה הבאה (עלות: כמה עשרות אלפיות שנייה).
/// </summary>
public static class Mem
{
    private const int IdleMs = 3000;
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

    private static readonly object L = new();
    private static System.Threading.Timer _t;
    private static int _active;

    public static void Begin()
    {
        Interlocked.Increment(ref _active);
        lock (L) _t?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public static void End()
    {
        if (Interlocked.Decrement(ref _active) <= 0) { Interlocked.Exchange(ref _active, 0); Schedule(IdleMs); }
    }

    public static void Schedule(int ms = IdleMs)
    {
        lock (L)
        {
            if (_t == null) _t = new System.Threading.Timer(_ => Trim(), null, ms, Timeout.Infinite);
            else _t.Change(ms, Timeout.Infinite);
        }
    }

    public static void Trim()
    {
        if (Volatile.Read(ref _active) > 0) return;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
}

public static class Server
{
    public const int FirstPort = 47917, LastPort = 47921;
    public static int Port;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly Dictionary<string, Func<JsonElement, Task<object>>> Routes = new(StringComparer.OrdinalIgnoreCase);
    private static HttpListener _l;

    public static void Route(string path, Func<JsonElement, Task<object>> handler) => Routes[path] = handler;
    public static void Route(string path, Func<JsonElement, object> handler) => Routes[path] = b => Task.FromResult(handler(b));

    public static int Start()
    {
        FsApi.Register();
        OneOcrApi.Register();
        SnipStubs.Register();

        for (int p = FirstPort; p <= LastPort; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + p + "/");
            try { l.Start(); _l = l; Port = p; break; }
            catch (HttpListenerException) { try { l.Close(); } catch { } }
        }
        if (_l == null) throw new InvalidOperationException("כל הפורטים 47917-47921 תפוסים");
        Cfg.Log("listening on " + Port);
        _ = Task.Run(Loop);
        return Port;
    }

    public static void Stop() { try { _l?.Stop(); _l?.Close(); } catch { } }

    private static async Task Loop()
    {
        while (_l != null && _l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        Mem.Begin();
        try { await HandleCore(ctx); }
        finally { Mem.End(); }
    }

    private static async Task HandleCore(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        bool streaming = false;
        try
        {
            if (req.HttpMethod == "OPTIONS") throw BridgeException.Forbidden("בקשות OPTIONS אינן נתמכות");
            // הגנה מאתרי אינטרנט שפונים ל-localhost (לבקשות של אוצריא אין Origin)
            string origin = req.Headers["Origin"] ?? "";
            if (origin.Length > 0 && origin != "null") throw BridgeException.Forbidden("מקור הבקשה אינו מורשה");
            // הגנה מ-DNS rebinding
            string host = (req.UserHostName ?? "").Split(':')[0];
            if (host != "127.0.0.1" && host != "localhost") throw BridgeException.Forbidden("כתובת היעד אינה מורשית");
            if ((req.Headers["X-Otzaria-Bridge"] ?? "") != "1") throw BridgeException.Forbidden("חסרה הכותרת X-Otzaria-Bridge");

            string path = (req.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            string token = req.Headers[Pairing.TokenHeader] ?? "";

            if (req.HttpMethod == "GET" && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteOk(res, new
                {
                    name = "otzaria-bridge",
                    version = Program.Version,
                    port = Port,
                    kioskActive = false,
                    platform = "windows",
                    auth = "token",
                    paired = Pairing.IsValid(token),
                    edition = "minimal",
                });
                return;
            }
            if (req.HttpMethod != "POST") throw new BridgeException("bad_request", "יש להשתמש ב-POST", 405);

            bool pairReq = path.Equals("/pair/request", StringComparison.OrdinalIgnoreCase);
            if (!pairReq && !Pairing.IsValid(token))
                throw new BridgeException("unpaired", "התוסף אינו מצומד לגשר. יש לאשר את החיבור בחלון שנפתח במחשב", 401);

            using var doc = await ReadBody(req, pairReq ? 16 * 1024 : 64L * 1024 * 1024);
            var body = doc.RootElement;

            if (pairReq)
            {
                await WriteOk(res, await Pairing.Request(body, req.RemoteEndPoint?.Port ?? 0));
                return;
            }
            if (path.Equals("/fs/search", StringComparison.OrdinalIgnoreCase))
            {
                await FsApi.Search(res, body, () => streaming = true);
                return;
            }
            if (path.Equals("/fs/read", StringComparison.OrdinalIgnoreCase) &&
                await FsApi.ReadStreaming(res, body, () => streaming = true))
                return;
            if (!Routes.TryGetValue(path, out var handler))
                throw BridgeException.NotFound("נקודת קצה לא קיימת: " + path);

            await WriteOk(res, await handler(body));
        }
        catch (Exception ex)
        {
            if (streaming) { try { res.Abort(); } catch { } return; }
            try
            {
                var (code, status, message) = Map(ex);
                if (code == "internal") Cfg.Log(req.Url?.AbsolutePath + ": " + ex);
                res.StatusCode = status;
                res.ContentType = "application/json; charset=utf-8";
                byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, error = new { code, message } }, Json));
                res.ContentLength64 = b.Length;
                await res.OutputStream.WriteAsync(b);
                res.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }
    }

    private static (string code, int status, string message) Map(Exception ex) => ex switch
    {
        BridgeException b => (b.Code, b.Status, b.Message),
        JsonException => ("bad_request", 400, "גוף הבקשה אינו JSON תקין"),
        UnauthorizedAccessException => ("forbidden", 403, "אין הרשאת גישה: " + ex.Message),
        FileNotFoundException or DirectoryNotFoundException => ("not_found", 404, "לא נמצא: " + ex.Message),
        PlatformNotSupportedException => ("unsupported", 501, ex.Message),
        PathTooLongException or ArgumentException or NotSupportedException => ("bad_request", 400, ex.Message),
        System.Runtime.InteropServices.COMException => ("unsupported", 501, "לא נתמך במחשב זה: " + ex.Message),
        _ => ("internal", 500, "שגיאה פנימית. פרטים נרשמו ביומן הגשר"),
    };

    private static async Task<JsonDocument> ReadBody(HttpListenerRequest req, long limit)
    {
        if (req.ContentLength64 > limit) throw BridgeException.BadRequest("בקשה גדולה מדי");
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await req.InputStream.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit) throw BridgeException.BadRequest("בקשה גדולה מדי");
        }
        if (ms.Length == 0) return JsonDocument.Parse("{}");
        ms.Position = 0;
        var doc = await JsonDocument.ParseAsync(ms);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw BridgeException.BadRequest("גוף הבקשה חייב להיות אובייקט JSON");
        return doc;
    }

    private static async Task WriteOk(HttpListenerResponse res, object data)
    {
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
        byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, data }, Json));
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
        res.Close();
    }
}

/// <summary>עזרי קריאה מגוף JSON</summary>
public static class J
{
    public static string Str(this JsonElement e, string name, string fallback = null) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

    public static string Req(this JsonElement e, string name)
    {
        string s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) throw BridgeException.BadRequest("חסר השדה " + name);
        return s;
    }

    public static bool Bool(this JsonElement e, string name, bool fallback = false) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    public static long Long(this JsonElement e, string name, long fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : fallback;

    public static string[] StrArray(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
    }
}

/// <summary>
/// צימוד: כל בקשה (חוץ מ-/health ו-/pair/request) חייבת מפתח אקראי שניתן רק אחרי אישור המשתמש בחלון במחשב.
/// נשמר רק גיבוב SHA-256 של המפתח, בקובץ data\clients.json.
/// </summary>
public static class Pairing
{
    public const string TokenHeader = "X-Otzaria-Token";
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim OneDialog = new(1, 1);
    private static int _denials;
    private static DateTime _cooldownUntil = DateTime.MinValue;
    private static string FilePath => Path.Combine(Cfg.Dir, "clients.json");

    internal class Rec { public string Id { get; set; } public string Client { get; set; } public string Name { get; set; } public string Hash { get; set; } public string Created { get; set; } }

    private static string Sha(string t) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t ?? "")));

    private static List<Rec> Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<Rec>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    private static void Save(List<Rec> l) => File.WriteAllText(FilePath, JsonSerializer.Serialize(l));

    public static bool IsValid(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32) return false;
        byte[] h = Encoding.ASCII.GetBytes(Sha(token));
        lock (Gate)
        {
            foreach (var c in Load())
            {
                byte[] e = Encoding.ASCII.GetBytes(c.Hash ?? "");
                if (e.Length == h.Length && CryptographicOperations.FixedTimeEquals(e, h)) return true;
            }
        }
        return false;
    }

    public static void RevokeAll() { lock (Gate) Save(new()); }

    public static async Task<object> Request(JsonElement b, int remotePort)
    {
        string client = (b.Str("client") ?? "").Trim();
        string name = (b.Str("name") ?? client).Trim();
        if (client.Length == 0 || client.Length > 60) throw BridgeException.BadRequest("שדה client חסר או ארוך מדי");
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray());
        if (name.Length > 60) name = name[..60];

        if (DateTime.UtcNow < _cooldownUntil)
            throw new BridgeException("too_many", "יותר מדי בקשות צימוד שנדחו. נסו שוב בעוד דקה", 429);
        if (!await OneDialog.WaitAsync(0))
            throw new BridgeException("busy", "חלון אישור אחר כבר פתוח במחשב", 409);
        try
        {
            bool approved = await AskAsync(name);
            if (!approved)
            {
                if (++_denials >= 3) { _denials = 0; _cooldownUntil = DateTime.UtcNow.AddMinutes(1); }
                throw BridgeException.Forbidden("הבקשה לחיבור לגשר נדחתה במחשב");
            }
            _denials = 0;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            lock (Gate)
            {
                var l = Load();
                l.Add(new Rec
                {
                    Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                    Client = client, Name = name, Hash = Sha(token), Created = DateTime.Now.ToString("s"),
                });
                Save(l);
            }
            Cfg.Log("pair approved: " + client);
            return new { token };
        }
        finally { OneDialog.Release(); }
    }

    private static Task<bool> AskAsync(string name)
    {
        var tcs = new TaskCompletionSource<bool>();
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(Ask(name)); }
            catch (Exception ex) { Cfg.Log("pair dialog: " + ex.Message); tcs.TrySetResult(false); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    private static bool Ask(string name)
    {
        using var f = new Form
        {
            Text = "גשר אוצריא — בקשת גישה",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, TopMost = true, ShowInTaskbar = true,
            RightToLeft = RightToLeft.Yes, RightToLeftLayout = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16), Font = new Font("Segoe UI", 10f),
        };
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0),
            Text = $"התוסף \"{name}\" באוצריא מבקש להתחבר לגשר.\nהחיבור נותן גישה לקבצים במחשב.\n\nאם לא פתחת עכשיו תוסף של אוצריא — לחצו \"דחה\".",
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "אשר", AutoSize = true, DialogResult = DialogResult.OK };
        var no = new Button { Text = "דחה", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(no);
        panel.Controls.Add(buttons);
        f.Controls.Add(panel);
        f.CancelButton = no;
        // חלון שנשכח פתוח נסגר כדחייה אחרי שתי דקות
        var expire = new System.Windows.Forms.Timer { Interval = 120_000 };
        expire.Tick += (_, _) => { expire.Stop(); f.DialogResult = DialogResult.Cancel; };
        f.FormClosed += (_, _) => expire.Dispose();
        f.Shown += (_, _) => { f.Activate(); no.Focus(); expire.Start(); };
        return f.ShowDialog() == DialogResult.OK;
    }
}

/// <summary>
/// /snip/* — כלי החיתוך כגיבוי (אוטומציית חלון) לא נכלל בגשר המינימלי.
/// התוסף קורא ל-/snip/status לתצוגת מצב בלבד; את הזיהוי עצמו עושה המנוע הישיר (/oneocr/*).
/// </summary>
public static class SnipStubs
{
    public static void Register()
    {
        Server.Route("/snip/status", _ => new { installed = OneOcrApi.Installed(), running = false, scriptOverride = false });
        Server.Route("/snip/dump", _ => new { window = "הגשר המינימלי אינו כולל אוטומציה של חלון כלי החיתוך", items = Array.Empty<string>() });
        Server.Route("/snip/ocr", (Func<JsonElement, object>)(_ => throw BridgeException.Unsupported("הגשר המינימלי תומך רק במנוע הזיהוי הישיר (oneocr), ללא חלון כלי החיתוך")));
    }
}
