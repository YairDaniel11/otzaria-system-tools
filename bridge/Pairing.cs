using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// צימוד תוספים לגשר (בעיה #2, גרסה 3.0.0).
/// עד 2.x כל תהליך מקומי שידע את הכותרת הקבועה X-Otzaria-Bridge: 1 קיבל גישה מלאה.
/// מעכשיו כל בקשה (חוץ מ-/health ו-/pair/request) חייבת את הכותרת X-Otzaria-Token עם מפתח אקראי
/// שהגשר נותן לתוסף רק אחרי שהמשתמש אישר בחלון של הגשר (ובזמן נעילה: רק עם סיסמת הנעילה).
/// הגשר שומר רק גיבוב SHA-256 של המפתח. התוסף שומר את המפתח באחסון של אוצריא (storage.set).
/// </summary>
public static class Pairing
{
    public const string TokenHeader = "X-Otzaria-Token";
    private const string Key = "auth.clients";

    private static readonly SemaphoreSlim OneDialog = new(1, 1);
    private static int _denials;
    private static DateTime _cooldownUntil = DateTime.MinValue;

    public static void Register()
    {
        Server.Route("/pair/list", _ => new { clients = List() });
        Server.Route("/pair/revoke", Revoke);
        PruneDuplicates();
    }

    /// <summary>חיבורים ישנים כפולים של אותו תוסף (client) — נשאר רק זה שנעשה בו שימוש לאחרונה</summary>
    private static void PruneDuplicates()
    {
        try
        {
            lock (Gate)
            {
                var all = Clients().OfType<JsonObject>().ToList();
                string Stamp(JsonObject c) => (c["lastUsed"]?.ToString() ?? "") + "|" + (c["created"]?.ToString() ?? "");
                var keep = all.GroupBy(c => c["client"]?.ToString() ?? "").Select(g => g.OrderByDescending(Stamp).First()).ToList();
                if (keep.Count == all.Count) return;
                SaveClients(new JsonArray(keep.Select(c => (JsonNode)c.DeepClone()).ToArray()));
                Config.Log("pair: נוקו " + (all.Count - keep.Count) + " חיבורים כפולים");
            }
        }
        catch (Exception ex) { Config.Log("pair prune: " + ex.Message); }
    }

    /// <summary>
    /// אתגר-תגובה לתוסף (תואם לאחור, תוסף ישן מתעלם): התוסף שולח nonce אקראי ב-X-Otzaria-Challenge ל-/health, והגשר מחזיר לכל צימוד
    /// HMAC-SHA256(מפתח = SHA-256 hex של הטוקן, הודעה = nonce). התוסף מחשב את אותו דבר מהטוקן שלו ובודק לפני שהוא שולח טוקן/סיסמה.
    /// WHY: גשר מזויף על פורט נמוך (47917) לא מכיר את הגיבוב, ולכן לא יכול להוכיח — והתוסף לא מוסר לו טוקן וסיסמה. הגשר שומר רק גיבוב,
    /// והגיבוב עצמו אינו נחשף (רק HMAC). ללא שינוי בתוסף הקיים ההגנה אינה פעילה — נדרש עדכון קטן בצד התוסף
    /// </summary>
    public static Dictionary<string, string> Proofs(string nonce)
    {
        var res = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(nonce) || nonce.Length is < 16 or > 128) return res;
        lock (Gate)
            foreach (var c in Clients().OfType<JsonObject>())
            {
                string id = c["id"]?.ToString(), hash = c["hash"]?.ToString();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(hash)) continue;
                if (!Trusted(c)) continue;
                res[id] = ProofFor(hash, nonce);
            }
        return res;
    }

    /// <summary>ההוכחה לגיבוב נתון (hex באותיות גדולות כפי ששמור) — התוסף מחשב את אותו דבר עם SHA-256 hex של הטוקן שלו</summary>
    public static string ProofFor(string tokenSha256Hex, string nonce)
    {
        byte[] key = Encoding.ASCII.GetBytes(tokenSha256Hex.ToUpperInvariant());
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
    }

    /// <summary>הנתונים שנחתמים לכל צימוד: מזהה, שם תוסף וגיבוב הטוקן (לא lastUsed, שמשתנה כל שעה)</summary>
    internal static string Canon(JsonObject c) =>
        "pair|" + c["id"] + "|" + c["client"] + "|" + c["hash"];

    /// <summary>
    /// האם רשומת צימוד מהימנה. WHY (S3): auth.clients.json נשמר ב-config.json הכתיב למשתמש, ורשומה שנוספה לשם ישירות (גיבוב של טוקן שהתוקף בחר)
    /// הייתה מתקבלת בלי חלון אישור. כשהחתימה נאכפת (שירות שתומך ב-config-sign) רק רשומה עם sig תקף, שנוצרה ב-Request אחרי אישור, מתקבלת.
    /// בלי שירות/עם שירות ישן: התנהגות קודמת (אין מקור אמון לאכוף)
    /// </summary>
    internal static bool Trusted(JsonObject c)
    {
        if (!ConfigSeal.Enforced) return true;
        string sig = c["sig"]?.ToString();
        return !string.IsNullOrEmpty(sig) && ConfigSeal.Verify(Canon(c), sig);
    }

    private static string Sha(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    private static JsonArray Clients()
    {
        var a = new JsonArray();
        string raw = Config.Get<string>(Key + ".json", null);
        if (string.IsNullOrEmpty(raw)) return a;
        try { return JsonNode.Parse(raw) as JsonArray ?? a; } catch { return a; }
    }

    private static void SaveClients(JsonArray a)
    {
        Config.Set(Key + ".json", a.ToJsonString());
        Config.Save();
    }

    public static object[] List() => Clients().OfType<JsonObject>().Select(c => (object)new
    {
        id = c["id"]?.ToString(),
        client = c["client"]?.ToString(),
        name = c["name"]?.ToString(),
        created = c["created"]?.ToString(),
        lastUsed = c["lastUsed"]?.ToString(),
    }).ToArray();

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> _touched = new();

    /// <summary>האם המפתח שייך לתוסף מצומד</summary>
    public static bool IsValid(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32) return false;
        byte[] h = Encoding.ASCII.GetBytes(Sha(token));
        lock (Gate)
        {
            var a = Clients();
            foreach (var c in a.OfType<JsonObject>())
            {
                byte[] e = Encoding.ASCII.GetBytes(c["hash"]?.ToString() ?? "");
                if (e.Length == h.Length && CryptographicOperations.FixedTimeEquals(e, h) && Trusted(c))
                {
                    // עדכון "שימוש אחרון" לכל היותר פעם בשעה, כדי לא לכתוב לדיסק בכל בקשה
                    string id = c["id"]?.ToString() ?? "";
                    if (!_touched.TryGetValue(id, out var t) || DateTime.UtcNow - t > TimeSpan.FromHours(1))
                    {
                        _touched[id] = DateTime.UtcNow;
                        c["lastUsed"] = DateTime.Now.ToString("s");
                        SaveClients(a);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    private static object Revoke(JsonElement b)
    {
        string id = b.Req("id");
        if (Kiosk.IsActive) Kiosk.RequirePassword(b.Str("password"));
        lock (Gate)
        {
            var a = Clients();
            var keep = new JsonArray(a.OfType<JsonObject>().Where(c => c["id"]?.ToString() != id)
                .Select(c => (JsonNode)c.DeepClone()).ToArray());
            SaveClients(keep);
        }
        return new { clients = List() };
    }

    /// <summary>צימוד קל: עד הזמן הזה בקשות צימוד מאושרות בלי חלון (רק כשהנעילה כבויה). נפתח מחלון "צימוד תוספים" בגשר</summary>
    public static DateTime AutoApproveUntil = DateTime.MinValue;
    public static bool AutoApproveOpen => !Kiosk.IsActive && DateTime.UtcNow < AutoApproveUntil;

    /// <summary>ניתוק תוסף אחד (מחלון "צימוד תוספים" בגשר)</summary>
    public static void RevokeId(string id)
    {
        lock (Gate)
        {
            var keep = new JsonArray(Clients().OfType<JsonObject>().Where(c => c["id"]?.ToString() != id)
                .Select(c => (JsonNode)c.DeepClone()).ToArray());
            SaveClients(keep);
        }
    }

    /// <summary>ביטול כל הצימודים (מתפריט המגש)</summary>
    public static void RevokeAll()
    {
        lock (Gate) SaveClients(new JsonArray());
    }

    /// <summary>
    /// בקשת צימוד: מציגה למשתמש חלון אישור (עם סיסמת נעילה כשהנעילה פעילה).
    /// רק חלון אחד בכל פעם; אחרי 3 דחיות — המתנה של דקה.
    /// </summary>
    // #32 ממצא 6: זיהוי התהליך הפונה לפי הפורט המקומי של החיבור (טבלת TCP של Windows)
    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr tbl, ref int size, bool sort, int af, int cls, uint reserved);

    public static string CallerProcess(int remotePort)
    {
        foreach (int af in new[] { 2, 23 })
        {
            int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0);
            if (size <= 0) continue;
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, 5, 0) != 0) continue;
                int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                int rowSize = af == 2 ? 24 : 56, portOff = af == 2 ? 8 : 20, pidOff = af == 2 ? 20 : 52;
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = buf + 4 + i * rowSize;
                    int lp = System.Runtime.InteropServices.Marshal.ReadInt32(row + portOff);
                    int port = ((lp & 0xFF) << 8) | ((lp >> 8) & 0xFF);
                    if (port != remotePort) continue;
                    int pid = System.Runtime.InteropServices.Marshal.ReadInt32(row + pidOff);
                    try { using var pr = System.Diagnostics.Process.GetProcessById(pid); return pr.ProcessName; } catch { return "pid " + pid; }
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    public static async Task<object> Request(JsonElement b, int remotePort = 0)
    {
        string client = (b.Str("client") ?? "").Trim();
        string name = (b.Str("name") ?? client).Trim();
        string caller = null; try { if (remotePort > 0) caller = CallerProcess(remotePort); } catch { }
        Config.Log("pair/request מ-" + (caller ?? "לא ידוע") + ": " + client);
        if (client.Length == 0 || client.Length > 60) throw BridgeException.BadRequest("שדה client חסר או ארוך מדי");
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()); // בלי שורות חדשות/תווי בקרה בחלון האישור
        if (name.Length > 60) name = name[..60];
        if (caller != null) name += " (" + caller + ")";

        if (DateTime.UtcNow < _cooldownUntil)
            throw new BridgeException("too_many", "יותר מדי בקשות צימוד שנדחו. נסו שוב בעוד דקה", 429);
        if (!await OneDialog.WaitAsync(0))
            throw new BridgeException("busy", "חלון אישור אחר כבר פתוח במחשב", 409);
        try
        {
            bool approved = AutoApproveOpen || await PairDialog.AskAsync(name, Kiosk.IsActive);
            if (!approved)
            {
                if (++_denials >= 3) { _denials = 0; _cooldownUntil = DateTime.UtcNow.AddMinutes(1); }
                throw BridgeException.Forbidden("הבקשה לחיבור לגשר נדחתה במחשב");
            }
            _denials = 0;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            lock (Gate)
            {
                // צימוד חדש של אותו תוסף מחליף את הקודמים (התקנה מחדש / מזהה תוסף שהשתנה) — לא נשארים חיבורים כפולים
                var a = new JsonArray(Clients().OfType<JsonObject>().Where(c => c["client"]?.ToString() != client)
                    .Select(c => (JsonNode)c.DeepClone()).ToArray());
                var entry = new JsonObject
                {
                    ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                    ["client"] = client,
                    ["name"] = name,
                    ["hash"] = Sha(token),
                    ["created"] = DateTime.Now.ToString("s"),
                    ["lastUsed"] = DateTime.Now.ToString("s"),
                };
                // S3: החתימה נעשית רק כאן, אחרי אישור המשתמש. כשהיא נאכפת ואין שירות זמין לחתום: נכשלים (בלי טוקן שלא יתקבל)
                string sig = ConfigSeal.Sign(Canon(entry));
                if (sig != null) entry["sig"] = sig;
                else if (ConfigSeal.Enforced) throw new BridgeException("unsupported", "רכיב ההקשחה אינו זמין לחתימת הצימוד", 503);
                a.Add(entry);
                SaveClients(a);
            }
            Config.Log("pair: אושר צימוד ל-" + client + " (" + name + ")");
            return new { token };
        }
        finally { OneDialog.Release(); }
    }
}

/// <summary>חלון האישור של בקשת צימוד</summary>
public static class PairDialog
{
    public static Task<bool> AskAsync(string name, bool needPassword)
    {
        var tcs = new TaskCompletionSource<bool>();
        Tray.Invoke(() =>
        {
            try { tcs.TrySetResult(Ask(name, needPassword)); }
            catch (Exception ex) { Config.Log("pair dialog: " + ex.Message); tcs.TrySetResult(false); }
        });
        // אם אף אחד לא עונה תוך 2 דקות — נחשב דחייה
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => tcs.TrySetResult(false));
        return tcs.Task;
    }

    private static bool Ask(string name, bool needPassword)
    {
        using var f = new Form
        {
            Text = Lang.T(Program.AppName + " — בקשת גישה", Program.AppNameEn + " — access request"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, TopMost = true, ShowInTaskbar = true,
            RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes,
            RightToLeftLayout = !Lang.En,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16), Font = new Font("Segoe UI", 10f),
        };
        f.BackColor = UiStyle.Page;
        f.Padding = new Padding(20);
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        // כותרת עם פס זהב (כמו כותרות הכרטיסים בתוספים)
        var head = new Label
        {
            AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), ForeColor = UiStyle.GoldText,
            Text = Lang.T("בקשת חיבור לגשר", "Bridge connection request"),
            Padding = new Padding(0, 0, 0, 10),
        };
        panel.Controls.Add(head);
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.FromArgb(60, 44, 20),
            Margin = new Padding(0, 0, 0, 10),
            Text = Lang.T(
                $"התוסף \"{name}\" באוצריא מבקש להתחבר לגשר.\nהחיבור נותן גישה לקבצים במחשב ולהגדרות המחשב.\n\nאם לא פתחת עכשיו תוסף של אוצריא — לחצו \"דחה\".",
                $"The Otzaria plugin \"{name}\" asks to connect to the bridge.\nThis gives it access to files and computer settings.\n\nIf you did not just open an Otzaria plugin, click \"Deny\"."),
        });
        TextBox pw = null;
        if (needPassword)
        {
            panel.Controls.Add(new Label { AutoSize = true, ForeColor = UiStyle.GoldText, Text = Lang.T("מצב נעילה פעיל — נדרשת סיסמת הנעילה:", "Lock mode is on — lock password required:") });
            pw = new TextBox { UseSystemPasswordChar = true, Width = 280, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11f) };
            panel.Controls.Add(pw);
        }
        var err = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        panel.Controls.Add(err);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, Margin = new Padding(0, 6, 0, 0) };
        var ok = new Button { Text = Lang.T("אשר", "Allow"), AutoSize = true };
        var no = new Button { Text = Lang.T("דחה", "Deny"), AutoSize = true, DialogResult = DialogResult.Cancel };
        UiStyle.Button(ok, primary: true); UiStyle.Button(no);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(no);
        panel.Controls.Add(buttons);
        f.Controls.Add(panel);
        f.CancelButton = no;
        ok.Click += (_, _) =>
        {
            if (pw != null)
            {
                try { Kiosk.RequirePassword(pw.Text); }
                catch (BridgeException ex) { err.Text = ex.Message; pw.SelectAll(); pw.Focus(); return; }
            }
            f.DialogResult = DialogResult.OK;
        };
        // ביקורת #30 ממצא 6: חלון שנשכח פתוח לא חוסם צימוד לתמיד — נסגר כדחייה אחרי 2 דקות
        var expire = new System.Windows.Forms.Timer { Interval = 120_000 };
        expire.Tick += (_, _) => { expire.Stop(); f.DialogResult = DialogResult.Cancel; };
        f.FormClosed += (_, _) => expire.Dispose();
        f.Shown += (_, _) => { f.Activate(); (pw as Control ?? no).Focus(); expire.Start(); };
        return f.ShowDialog() == DialogResult.OK;
    }
}
