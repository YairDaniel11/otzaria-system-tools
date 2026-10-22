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
                if (e.Length == h.Length && CryptographicOperations.FixedTimeEquals(e, h))
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
    public static async Task<object> Request(JsonElement b)
    {
        string client = (b.Str("client") ?? "").Trim();
        string name = (b.Str("name") ?? client).Trim();
        if (client.Length == 0 || client.Length > 60) throw BridgeException.BadRequest("שדה client חסר או ארוך מדי");
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()); // בלי שורות חדשות/תווי בקרה בחלון האישור
        if (name.Length > 60) name = name[..60];

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
                var a = Clients();
                a.Add(new JsonObject
                {
                    ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                    ["client"] = client,
                    ["name"] = name,
                    ["hash"] = Sha(token),
                    ["created"] = DateTime.Now.ToString("s"),
                    ["lastUsed"] = DateTime.Now.ToString("s"),
                });
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
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0),
            Text = Lang.T(
                $"התוסף \"{name}\" באוצריא מבקש להתחבר לגשר.\nהחיבור נותן גישה לקבצים במחשב ולהגדרות המחשב.\n\nאם לא פתחת עכשיו תוסף של אוצריא — לחצו \"דחה\".",
                $"The Otzaria plugin \"{name}\" asks to connect to the bridge.\nThis gives it access to files and computer settings.\n\nIf you did not just open an Otzaria plugin, click \"Deny\"."),
        });
        TextBox pw = null;
        if (needPassword)
        {
            panel.Controls.Add(new Label { AutoSize = true, Text = Lang.T("מצב נעילה פעיל — נדרשת סיסמת הנעילה:", "Lock mode is on — lock password required:") });
            pw = new TextBox { UseSystemPasswordChar = true, Width = 260 };
            panel.Controls.Add(pw);
        }
        var err = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        panel.Controls.Add(err);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft };
        var ok = new Button { Text = Lang.T("אשר", "Allow"), AutoSize = true };
        var no = new Button { Text = Lang.T("דחה", "Deny"), AutoSize = true, DialogResult = DialogResult.Cancel };
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
        f.Shown += (_, _) => { f.Activate(); (pw as Control ?? no).Focus(); };
        return f.ShowDialog() == DialogResult.OK;
    }
}
