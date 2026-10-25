using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// דיווח תקלות אוטומטי + מסך "מה נשלח" — בסיס מינימלי (13.10.2026), להרחבה בהמשך.
/// הדיווחים עצמם נשלחים מהתוסף "הגדרות מערכת" דרך feedback.report (לגשר אין ערוץ שליחה משלו);
/// הקובץ הזה מוסיף: רשימת ממתינים/נשלחו עם תוכן מלא, מחיקה לפני שליחה, ארכיון "נשלחו",
/// יומן מקוצר מסונן (בלי סיסמאות/קודים) לצירוף לדיווח, והוספת תיאור ידני מהמגש.
/// נתיבים: /diag/list, /diag/delete {ids}, /diag/markSent {ids}, /diag/logTail, /diag/note {text}.
/// </summary>
internal static class Reports
{
    private static string Dir => Path.Combine(Config.Dir, "outbox");
    private static string SentDir => Path.Combine(Dir, "sent");
    private const int KeepSent = 30;
    private static readonly Regex Secret = new("(hash|salt|key|secret|password|passwd|token|recovery|code|סיסמ|קוד)", RegexOptions.IgnoreCase);

    public static void Init()
    {
        Server.Route("/diag/list", _ => List());
        Server.Route("/diag/delete", b => Move(b, null));
        Server.Route("/diag/markSent", b => Move(b, SentDir));
        Server.Route("/diag/logTail", _ => new { text = LogTail(60) });
        Server.Route("/diag/note", b => new { ok = AddNote(b.TryGetProperty("text", out var t) ? t.GetString() : null) });
    }

    public record Item(string Id, bool Sent, DateTime Date, string Kind, string Text);

    public static List<Item> Items()
    {
        var res = new List<Item>();
        void Scan(string dir, bool sent)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.txt"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                string kind = id.Split('-').LastOrDefault() ?? "";
                try { res.Add(new Item(id, sent, File.GetLastWriteTime(f), kind, File.ReadAllText(f))); } catch { }
            }
        }
        Scan(Dir, false);
        Scan(SentDir, true);
        return res.OrderByDescending(x => x.Date).ToList();
    }

    private static object List()
    {
        var arr = new JsonArray();
        foreach (var i in Items())
            arr.Add(new JsonObject { ["id"] = i.Id, ["sent"] = i.Sent, ["date"] = i.Date.ToString("s"), ["kind"] = i.Kind, ["text"] = i.Text });
        return new JsonObject { ["enabled"] = Config.Get("diag.autoReport", true), ["items"] = arr };
    }

    /// <summary>target=null: מחיקת ממתינים. אחרת: העברה לארכיון "נשלחו"</summary>
    private static object Move(JsonElement b, string target)
    {
        int n = 0;
        var ids = default(JsonElement);
        if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("ids", out ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var x in ids.EnumerateArray())
                if (MoveOne(x.GetString(), target)) n++;
        // אם התוסף שלח את הדיווח המאוחד (כולל היומן המקוצר) — הארכיון שומר אותו כמו שנשלח
        if (target != null && n > 0 && b.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.String)
            try
            {
                Directory.CreateDirectory(SentDir);
                File.WriteAllText(Path.Combine(SentDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-report.txt"), d.GetString(), new UTF8Encoding(false));
                foreach (var x in ids.EnumerateArray()) File.Delete(Path.Combine(SentDir, Path.GetFileName(x.GetString() ?? "") + ".txt"));
            }
            catch { }
        if (target != null) TrimSent();
        return new { done = n };
    }

    public static bool MoveOne(string id, string target)
    {
        id = Path.GetFileName(id ?? "");
        string f = Path.Combine(Dir, id + ".txt");
        if (id.Length == 0 || !File.Exists(f)) return false;
        try
        {
            if (target == null) File.Delete(f);
            else { Directory.CreateDirectory(target); File.Move(f, Path.Combine(target, id + ".txt"), true); }
            return true;
        }
        catch { return false; }
    }

    private static void TrimSent()
    {
        try
        {
            foreach (var f in Directory.GetFiles(SentDir, "*.txt").OrderByDescending(x => x).Skip(KeepSent)) File.Delete(f);
        }
        catch { }
    }

    /// <summary>סוף היומן, בלי שורות שנראות כמו סוד ובלי שם המשתמש בנתיבים</summary>
    public static string LogTail(int lines)
    {
        try
        {
            string log = Path.Combine(Config.Dir, "bridge.log");
            if (!File.Exists(log)) return "";
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > 64_000) fs.Seek(-64_000, SeekOrigin.End);
            using var r = new StreamReader(fs, Encoding.UTF8);
            var all = r.ReadToEnd().Split('\n').Where(l => !Secret.IsMatch(l)).TakeLast(lines);
            string user = Environment.UserName;
            string s = string.Join("\n", all);
            return string.IsNullOrEmpty(user) ? s : s.Replace("\\" + user + "\\", "\\<user>\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return ""; }
    }

    /// <summary>תיאור ידני מהמגש — נכנס לתור ויישלח עם הדיווח האוטומטי הבא</summary>
    public static bool AddNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            string body = "סוג: תיאור מהמשתמש\nגרסת גשר: " + Program.Version + "\nזמן: " + DateTime.Now.ToString("s") + "\n\n" + text.Trim();
            File.WriteAllText(Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-note.txt"),
                body.Length > 4000 ? body[..4000] : body, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }
}

/// <summary>חלון "דיווחים — מה נשלח" מהמגש: רשימה, תוכן מלא, מחיקה לפני שליחה, הוספת תיאור</summary>
internal sealed class ReportsForm : Form
{
    private static ReportsForm _inst;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Color.White, ForeColor = UiStyle.GoldText };
    private readonly TextBox _view = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly TextBox _note = new() { Dock = DockStyle.Fill, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly Button _del = new() { AutoSize = true, Enabled = false };

    public static void Open()
    {
        Hub.Show("reports", () => _inst = new ReportsForm());
        _inst?.Reload();
    }

    private ReportsForm()
    {
        Text = Lang.T("דיווחים — מה נשלח", "Reports — what was sent");
        Width = 640; Height = 620; MinimumSize = new System.Drawing.Size(520, 480); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen;
        if (!Lang.En) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

        BackColor = UiStyle.Page;
        Font = new Font("Segoe UI", 10f);
        var info = new Label
        {
            Dock = DockStyle.Fill, ForeColor = UiStyle.GoldText, BackColor = Color.White,
            Text = Lang.T("תקלות ויומן מקוצר (בלי סיסמאות וקודים) נשלחים אוטומטית דרך התוסף \"הגדרות מערכת\" כשהמתג \"שליחת תקלות אוטומטית\" פעיל. ממתינים אפשר למחוק לפני שליחה.",
                          "Errors and a short log (no passwords or codes) are sent automatically via the System Settings plugin when “automatic error reporting” is on. Pending items can be deleted before sending.")
        };
        _list.Columns.Add(Lang.T("מצב", "Status"), 90);
        _list.Columns.Add(Lang.T("תאריך", "Date"), 160);
        _list.Columns.Add(Lang.T("סוג", "Kind"), 120);
        _list.SelectedIndexChanged += (_, _) =>
        {
            var it = _list.SelectedItems.Count > 0 ? (Reports.Item)_list.SelectedItems[0].Tag : null;
            _view.Text = it?.Text.Replace("\n", "\r\n") ?? "";
            _del.Enabled = it != null && !it.Sent;
        };
        _del.Text = Lang.T("מחיקה (לא יישלח)", "Delete (won't be sent)");
        _del.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is Reports.Item it && !it.Sent) Reports.MoveOne(it.Id, null);
            Reload();
        };
        var add = new Button { Text = Lang.T("הוספת תיאור לדיווח", "Add a description") };
        add.Click += (_, _) => { if (Reports.AddNote(_note.Text)) { _note.Clear(); Reload(); } };
        var shot = new Button { Text = Lang.T("צרף צילום מסך", "Attach a screenshot") };
        shot.Click += (_, _) => AttachShot();
        var zip = new Button { Text = Lang.T("איסוף יומנים לקובץ zip…", "Collect logs to zip…") };
        zip.Click += (_, _) => Diagnostics_.Report();
        UiStyle.Button(add, primary: true); UiStyle.Button(shot); UiStyle.Button(_del); UiStyle.Button(zip);

        // פריסה: כרטיס הסבר למעלה, רשימה + תוכן באמצע (מתכווצים), תיאור וכפתורים למטה; כל גוש בכרטיס לבן מעוגל
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14), BackColor = UiStyle.Page };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        UiStyle.Card Card(Control inner) { var c = new UiStyle.Card { Dock = DockStyle.Fill }; c.Controls.Add(inner); return c; }
        layout.Controls.Add(Card(info), 0, 0);
        layout.Controls.Add(Card(_list), 0, 1);
        layout.Controls.Add(Card(_view), 0, 2);
        var noteBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
        noteBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        noteBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        noteBox.Controls.Add(new Label { Text = Lang.T("תיאור (מה קרה?) — יישלח עם הדיווח הבא:", "Description (what happened?) — sent with the next report:"), AutoSize = true, ForeColor = UiStyle.GoldText }, 0, 0);
        noteBox.Controls.Add(_note, 0, 1);
        layout.Controls.Add(Card(noteBox), 0, 3);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = UiStyle.Page };
        btns.Controls.AddRange(new Control[] { add, shot, _del, zip });
        layout.Controls.Add(btns, 0, 4);
        Controls.Add(layout);
    }

    /// #60: צילום אזור (החלון מוסתר בזמן הצילום), נשמר בתיקיית report-shots ונרשם כתיאור לדיווח.
    /// הדיווח עצמו טקסט בלבד (feedback.report), ולכן התמונה מצורפת לקובץ ה-zip של "איסוף יומנים".
    private void AttachShot()
    {
        var top = ParentForm ?? this;   // C1: מוטמע בחלון הטאבים
        top.Hide();
        string path = null;
        try { System.Threading.Thread.Sleep(250); path = Screenshot.Capture(region: true); }
        finally { top.Show(); top.Activate(); }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            string dir = Path.Combine(Config.Dir, "report-shots");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, Path.GetFileName(path));
            File.Copy(path, dest, overwrite: true);
            // לא לצבור ללא גבול: שומרים 20 אחרונים (ב-zip נכנסים 5)
            foreach (var old in Directory.GetFiles(dir, "*.png").OrderByDescending(x => x).Skip(20)) { try { File.Delete(old); } catch { } }
            string txt = (_note.Text.Trim() + "\n[צורף צילום מסך: " + Path.GetFileName(dest) + " — מצורף לקובץ ה-zip של איסוף היומנים]").Trim();
            if (Reports.AddNote(txt)) { _note.Clear(); Reload(); }
        }
        catch { }
    }

    private void Reload()
    {
        _list.Items.Clear();
        _view.Text = "";
        _del.Enabled = false;
        foreach (var i in Reports.Items())
            _list.Items.Add(new ListViewItem(new[] { i.Sent ? Lang.T("נשלח", "Sent") : Lang.T("ממתין", "Pending"), i.Date.ToString("g"), i.Kind }) { Tag = i });
    }
}
