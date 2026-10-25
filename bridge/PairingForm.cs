namespace OtzariaBridge;

/// <summary>
/// חלון "צימוד תוספים" בגשר (3.1.0): הסבר, מצב כל תוסף, צימוד קל (2 דקות בלי חלון אישור), ניתוק תוסף או כולם.
/// </summary>
internal sealed class PairingForm : Form
{
    private static PairingForm _open;
    private static readonly (string id, string he, string en)[] Known =
    {
        ("file-explorer", "סייר קבצים", "File Explorer"),
        ("system-settings", "הגדרות מערכת", "System Settings"),
    };

    private readonly Label _known = new() { AutoSize = false, Height = 54, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Top, Height = 150 };
    private readonly Button _easy = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Button _revoke = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Button _revokeAll = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Label _state = new() { AutoSize = false, Height = 46, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string _sig = "";

    public static void Open()
    {
        Hub.Show("pairing", () => _open = new PairingForm());
    }

    private PairingForm()
    {
        Text = Lang.T("צימוד תוספים — " + Program.AppName, "Plugin pairing — " + Program.AppNameEn);
        try { Icon = Overlay.AppIcon(); } catch { }
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ClientSize = new Size(560, 520);
        Padding = new Padding(18);

        var title = new Label { Text = Lang.T("צימוד תוספים לגשר", "Pairing plugins with the bridge"), Font = new Font("Segoe UI Semibold", 14f), Height = 34, Dock = DockStyle.Top };
        var help = new Label
        {
            Dock = DockStyle.Top, Height = 118, ForeColor = Color.FromArgb(60, 60, 60),
            Text = Lang.T(
                "כדי ששום תוכנה אחרת במחשב לא תוכל להשתמש בגשר, כל תוסף של אוצריא צריך אישור חד-פעמי (\"צימוד\").\n\n" +
                "הדרך הקלה: לחצו \"צימוד קל\", ואז תוך 2 דקות פתחו באוצריא את \"סייר קבצים\" ואת \"הגדרות מערכת\" — הם יתחברו לבד, בלי חלון אישור.\n" +
                "אם תוסף מפסיק לעבוד — נתקו אותו כאן ופתחו אותו שוב.",
                "So that no other program on the computer can use the bridge, each Otzaria plugin needs a one-time approval (\"pairing\").\n\n" +
                "The easy way: click \"Easy pairing\", then within 2 minutes open \"File Explorer\" and \"System Settings\" in Otzaria — they connect on their own, with no approval window.\n" +
                "If a plugin stops working, disconnect it here and open it again."),
        };

        _list.RightToLeftLayout = !Lang.En;
        _list.Columns.Add(Lang.T("תוסף", "Plugin"), 190);
        _list.Columns.Add(Lang.T("צומד", "Paired"), 160);
        _list.Columns.Add(Lang.T("שימוש אחרון", "Last used"), 160);
        var listTitle = new Label { Text = Lang.T("תוספים מחוברים:", "Connected plugins:"), Height = 26, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 10f) };

        _easy.Click += (_, _) =>
        {
            if (Pairing.AutoApproveOpen) Pairing.AutoApproveUntil = DateTime.MinValue;
            else Pairing.AutoApproveUntil = DateTime.UtcNow.AddMinutes(2);
            Refresh2();
        };
        _revoke.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0 || !CheckLock()) return;
            Pairing.RevokeId((string)_list.SelectedItems[0].Tag);
            Refresh2();
        };
        _revokeAll.Click += (_, _) =>
        {
            if (!CheckLock()) return;
            if (MessageBox.Show(this, Lang.T("לנתק את כל התוספים מהגשר? בפתיחה הבאה כל תוסף יבקש אישור מחדש.", "Disconnect all plugins from the bridge? Each plugin will ask for approval again."),
                    Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Pairing.RevokeAll();
            Refresh2();
        };
        _list.SelectedIndexChanged += (_, _) => _revoke.Enabled = _list.SelectedItems.Count > 0;

        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        row.Controls.AddRange(new Control[] { _easy, _revoke, _revokeAll });

        Controls.AddRange(new Control[] { _state, row, _list, listTitle, _known, help, title });

        _timer.Tick += (_, _) => Refresh2();
        Shown += (_, _) => { Refresh2(); _timer.Start(); };
        FormClosed += (_, _) => { _timer.Stop(); Pairing.AutoApproveUntil = DateTime.MinValue; };
    }

    /// <summary>במצב נעילה ניתוק דורש את סיסמת הנעילה</summary>
    private bool CheckLock()
    {
        if (!Kiosk.IsActive) return true;
        MessageBox.Show(this, Lang.T("מצב נעילה פעיל. ניתוק תוספים אפשרי רק אחרי ביטול הנעילה, או מ\"הגדרות מערכת\" ← \"נעילה\" עם הסיסמה.",
            "Lock mode is on. Plugins can be disconnected only after turning it off, or from System Settings → Lock with the password."), Text);
        return false;
    }

    private static string Fmt(string s) =>
        DateTime.TryParse(s, out var d) ? d.ToString("dd/MM/yyyy HH:mm") : (s ?? "");

    private void Refresh2()
    {
        var clients = Pairing.List().Select(o =>
        {
            var t = o.GetType();
            string G(string n) => t.GetProperty(n)?.GetValue(o)?.ToString();
            return (id: G("id"), client: G("client"), name: G("name"), created: G("created"), lastUsed: G("lastUsed"));
        }).ToList();

        string sig = string.Join("|", clients.Select(c => c.id + c.lastUsed));
        if (sig != _sig)
        {
            _sig = sig;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in clients)
            {
                var it = new ListViewItem(new[] { c.name ?? c.client, Fmt(c.created), Fmt(c.lastUsed) }) { Tag = c.id };
                _list.Items.Add(it);
            }
            _list.EndUpdate();
        }

        _known.Text = string.Join("\n", Known.Select(k =>
            (clients.Any(c => c.client == k.id) ? "✓ " : "✗ ") + Lang.T(k.he, k.en) + " — " +
            (clients.Any(c => c.client == k.id) ? Lang.T("מחובר", "connected") : Lang.T("לא מחובר", "not connected"))));
        _known.ForeColor = Known.All(k => clients.Any(c => c.client == k.id)) ? Color.FromArgb(20, 120, 60) : Color.FromArgb(150, 90, 0);

        _revoke.Text = Lang.T("נתק את הנבחר", "Disconnect selected");
        _revokeAll.Text = Lang.T("נתק הכל", "Disconnect all");
        _revoke.Enabled = _list.SelectedItems.Count > 0;
        _revokeAll.Enabled = clients.Count > 0;

        if (Kiosk.IsActive)
        {
            _easy.Text = Lang.T("צימוד קל (2 דקות)", "Easy pairing (2 min)");
            _easy.Enabled = false;
            _state.ForeColor = Color.DimGray;
            _state.Text = Lang.T("מצב נעילה פעיל: צימוד מתבצע רק בחלון האישור, עם סיסמת הנעילה.", "Lock mode is on: pairing only through the approval window, with the lock password.");
        }
        else if (Pairing.AutoApproveOpen)
        {
            int sec = (int)Math.Ceiling((Pairing.AutoApproveUntil - DateTime.UtcNow).TotalSeconds);
            _easy.Text = Lang.T("עצור צימוד קל", "Stop easy pairing");
            _easy.Enabled = true;
            _state.ForeColor = Color.FromArgb(0, 100, 180);
            _state.Text = Lang.T($"צימוד קל פתוח עוד {sec} שניות — פתחו עכשיו את התוספים באוצריא.", $"Easy pairing is open for {sec} more seconds — open the plugins in Otzaria now.");
        }
        else
        {
            _easy.Text = Lang.T("צימוד קל (2 דקות)", "Easy pairing (2 min)");
            _easy.Enabled = true;
            _state.ForeColor = Color.DimGray;
            _state.Text = Lang.T("תוסף חדש שיבקש להתחבר יציג חלון אישור.", "A new plugin that asks to connect will show an approval window.");
        }
    }
}
