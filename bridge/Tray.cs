using System.Diagnostics;
namespace OtzariaBridge;

/// <summary>אייקון במגש. רץ על החוט הראשי (STA) עם לולאת הודעות, שנדרשת גם ל-hook המקלדת</summary>
public static class Tray
{
    private static NotifyIcon _icon;
    private static ToolStripMenuItem _status, _exit, _plugins, _install, _lang, _about, _unpair, _changelog, _report;
    private static ContextMenuStrip _menu;
    private static Control _invoker;

    public static void Init()
    {
        _invoker = new Control();
        _invoker.CreateControl();
        _ = _invoker.Handle;

        var menu = _menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("") { Enabled = false };
        _plugins = new ToolStripMenuItem("", null, (_, _) => Installer.OpenPluginsFolder());
        _install = new ToolStripMenuItem("", null, (_, _) => { Installer.DeployPlugins(); Installer.InstallPluginsInOtzaria(); });
        _lang = new ToolStripMenuItem("", null, (_, _) => { Lang.Set(Lang.En ? "he" : "en"); ApplyTexts(); });
        _exit = new ToolStripMenuItem("", null, (_, _) => TryExit());
        _about = new ToolStripMenuItem("", null, (_, _) => AboutForm.Open());
        _unpair = new ToolStripMenuItem("", null, (_, _) => PairingForm.Open());
        _changelog = new ToolStripMenuItem("", null, (_, _) => ChangelogForm.Open());
        _report = new ToolStripMenuItem("", null, (_, _) => ReportsForm.Open());
        menu.Items.AddRange(new ToolStripItem[] { _status, _about, _changelog, _report, _install, _plugins, _unpair, _lang, new ToolStripSeparator(), _exit });

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = Lang.T(Program.AppName, Program.AppNameEn),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => AboutForm.Open();
        // בדיקת עדכון שקטה דקה אחרי העלייה: אם יש גרסה חדשה — הודעה בבועה, ולחיצה עליה פותחת את "אודות"
        _icon.BalloonTipClicked += (_, _) => AboutForm.Open();
        Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(async _ =>
        {
            try
            {
                if (OfflineMode.IsOn()) return; // #9: אוצריא במצב מנותק — בלי בדיקת עדכונים
                var i = await UpdateApi.Check(false);
                if (i.Newer) Invoke(() => _icon.ShowBalloonTip(8000, Lang.T(Program.AppName, Program.AppNameEn),
                    Lang.T("גרסה " + i.Latest + " זמינה. לחצו לעדכון.", "Version " + i.Latest + " is available. Click to update."), ToolTipIcon.Info));
            }
            catch { }
        });
        // בדיקה (מוזזת מבדיקת העדכון, כדי לא להתנגש בבועה) שעותק רכיב ההקשחה המותקן תואם לגרסת הגשר.
        // אחרי עדכון עצמי הגשר מתעדכן, אבל עותק השירות ב-Program Files לא — צריך עדכון ידני (עם סיסמה)
        // דרך "הגדרות מערכת → נעילה" (לא ניגשים לשירות אוטומטית בלי הסיסמה — זו גבול האבטחה שלו).
        Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ =>
        {
            try
            {
                if (!HardLock.ServiceInstalled()) return;
                var st = (dynamic)HardLock.Status(fresh: true);
                if (st.needsUpdate == true)
                    Invoke(() => _icon.ShowBalloonTip(8000, Lang.T(Program.AppName, Program.AppNameEn),
                        Lang.T("רכיב ההקשחה בגרסה ישנה מהגשר. עדכנו ב\"הגדרות מערכת → נעילה\" (נדרשת סיסמת הנעילה).",
                               "The hardening component is on an older version than the bridge. Update it in “System Settings → Lock” (lock password required)."),
                        ToolTipIcon.Warning));
            }
            catch { }
        });
        Application.ApplicationExit += (_, _) =>
        {
            _icon.Visible = false;
            _icon.Dispose();
        };
    }

    /// <summary>יציאה מהמגש: בנעילה פעילה (או כשהשירות נועל) נדרשת סיסמת הנעילה/קוד שחזור. כשל באימות או ב-pipe = נשארים</summary>
    private static void TryExit()
    {
        bool locked = false;
        try { locked = Kiosk.IsActive || HardLock.ServiceActive; } catch { locked = true; }
        if (!locked) { Application.ExitThread(); return; }
        using var f = new ExitPasswordForm();
        if (f.ShowDialog() != DialogResult.OK) return;
        try { Kiosk.RequirePasswordOrRecovery(f.Password); }
        catch (Exception ex)
        {
            Notify(Program.AppName, ex is BridgeException ? ex.Message : Lang.T("אימות נכשל", "Verification failed"));
            return;
        }
        Application.ExitThread();
    }

    private static void ApplyTexts()
    {
        if (_menu == null) return;
        _menu.RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        _plugins.Text = Lang.T("פתח את תיקיית התוספים", "Open the plugins folder");
        _about.Text = Lang.T("אודות ועדכונים…", "About and updates…");
        _changelog.Text = Lang.T("יומן שינויים (מקומי)…", "Changelog (offline)…");
        _report.Text = Lang.T("דיווחים: מה נשלח / הוספת תיאור…", "Reports: what was sent / add a description…");
        _install.Text = Lang.T("התקן/עדכן את התוספים באוצריא", "Install/update the plugins in Otzaria");
        _lang.Text = Lang.T("English", "עברית");
        _unpair.Text = Lang.T("צימוד תוספים…", "Plugin pairing…");
        _exit.Text = Lang.T("יציאה", "Exit");
        UpdateStatus();
    }

    private static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public static void Run()
    {
        ApplyTexts();
        Application.Run();
    }

    public static void Invoke(Action a)
    {
        if (_invoker == null || !_invoker.IsHandleCreated) { a(); return; }
        // לא חוסמים את החוט הקורא: קוראים שמחזיקים נעילה (Kiosk.Gate) היו נתקעים בדדלוק מול חוט הממשק
        if (_invoker.InvokeRequired)
            _invoker.BeginInvoke(new Action(() => { try { a(); } catch (Exception ex) { Config.Log("ui: " + ex.Message); } }));
        else a();
    }

    public static void SetKioskState(bool active) => Invoke(() =>
    {
        // WHY: "יציאה" מושבתת הייתה מונעת גם יציאה לגיטימית; עכשיו היא פעילה אך דורשת סיסמה כשנעילה/שירות פעילים (TryExit)
        UpdateStatus();
    });

    /// <summary>בועת התראה קצרה במגש (למשל DialogGuard שחוסם דיאלוג קבצים חורג ממדיניות)</summary>
    public static void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Warning) =>
        Invoke(() => _icon?.ShowBalloonTip(6000, title, message, icon));

    private static void UpdateStatus()
    {
        if (_status == null) return;
        string text = Lang.T(Program.AppName + " ", Program.AppNameEn + " ") + Program.Version + Lang.T(" — פורט ", " — port ") + Program.Port;
        if (Kiosk.IsActive) text += Lang.T(" (נעילה פעילה)", " (lock mode on)");
        _status.Text = text;
        if (_icon != null) _icon.Text = text.Length > 63 ? text[..63] : text;
    }
}

/// <summary>חלון "אודות ועדכונים" של הגשר: גרסה, הקדשה, קישור לעמוד ההוצאות, בדיקת עדכון והתקנה בלחיצה</summary>
internal sealed class AboutForm : Form
{
    private static AboutForm _open;
    private readonly Label _state = new() { AutoSize = false, Height = 44, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _check = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Button _install = new() { AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = false };

    public static void Open()
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new AboutForm();
        _open.Show();
    }

    private AboutForm()
    {
        Text = Lang.T("אודות " + Program.AppName, "About " + Program.AppNameEn);
        Icon = Overlay.AppIcon();
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ClientSize = new Size(480, 445);
        Padding = new Padding(20);

        var logo = new PictureBox { Image = new Icon(Overlay.AppIcon(), 64, 64).ToBitmap(), SizeMode = PictureBoxSizeMode.CenterImage, Height = 76, Dock = DockStyle.Top };
        var title = new Label { Text = Lang.T(Program.AppName, Program.AppNameEn), Font = new Font("Segoe UI Semibold", 16f), Height = 38, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter };
        var ver = new Label { Text = Lang.T("גרסה ", "Version ") + Program.Version + " · " + Lang.T("חד בדרא, יאיר דניאל", "Chad Bedera, Yair Daniel"), Height = 26, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
        // קרדיט נפרד מההקדשה: ספריית חישוב הזמנים של הגיבוי (לא קשור להקדשה למעלה)
        var zmanimCredit = new Label { Text = Lang.T(
                "חישוב זמני היום (גיבוי): פרויקט Zmanim/KosherJava מאת Eliyahu Hershfeld (kosherjava.com), ברישיון LGPL",
                "Fallback daily zmanim: the Zmanim/KosherJava project by Eliyahu Hershfeld (kosherjava.com), LGPL license"),
            Height = 36, Font = new Font("Segoe UI", 8.5f), Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray };
        var zmanimLink = new LinkLabel { Text = "kosherjava.com/zmanim-project", Height = 22, Font = new Font("Segoe UI", 8.5f), Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter };
        zmanimLink.LinkClicked += (_, _) => OpenUrl("http://www.kosherjava.com/zmanim-project/");
        var link = new LinkLabel { Text = Lang.T("עמוד ההוצאות ויומן השינויים", "Releases page and changelog"), Height = 28, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter };
        link.LinkClicked += (_, _) => OpenUrl(UpdateApi.AllReleasesUrl);

        _check.Text = Lang.T("בדיקת עדכונים", "Check for updates");
        _install.Text = Lang.T("עדכון עכשיו", "Update now");
        _check.Click += async (_, _) => await DoCheck(true);
        _install.Click += async (_, _) => await DoInstall();
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, Padding = new Padding(80, 6, 0, 0) };
        row.Controls.AddRange(new Control[] { _check, _install });

        if (OfflineMode.IsOn()) // #9: אוצריא במצב מנותק — מסתירים את אזור העדכונים
            Controls.AddRange(new Control[] { zmanimLink, zmanimCredit, ver, title, logo });
        else
        {
            Controls.AddRange(new Control[] { row, _state, link, zmanimLink, zmanimCredit, ver, title, logo });
            Shown += async (_, _) => await DoCheck(false);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private const int StateBaseHeight = 44, StateMaxHeight = 140;

    /// <summary>
    /// הטקסט כאן הוא לפעמים הודעת שגיאה ארוכה (כולל ex.Message פנימי), וב-Height קבוע הוא נחתך
    /// בלי שלוש נקודות ובלי אזהרה — נראה כמו תקלה. מודדים את הגובה הדרוש בפועל ומגדילים את התווית
    /// ואת החלון בהתאם (ה-Dock=Top של שאר הפקדים משאיר אותם במקומם).
    /// </summary>
    private void SetState(string text, Color color)
    {
        if (text.Length > 300) text = text[..300] + "…";
        _state.ForeColor = color;
        _state.Text = text;
        int width = Math.Max(1, _state.Width);
        var size = TextRenderer.MeasureText(text, _state.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int needed = Math.Clamp(size.Height + 12, StateBaseHeight, StateMaxHeight);
        if (needed != _state.Height)
        {
            ClientSize = new Size(ClientSize.Width, ClientSize.Height - _state.Height + needed);
            _state.Height = needed;
        }
    }

    private async Task DoCheck(bool force)
    {
        SetState(Lang.T("בודק עדכונים…", "Checking for updates…"), Color.DimGray);
        _check.Enabled = false;
        try
        {
            var i = await UpdateApi.Check(force);
            if (i.Newer)
            {
                string msg = Lang.T("גרסה " + i.Latest + " זמינה (מותקנת " + i.Current + ")", "Version " + i.Latest + " is available (installed " + i.Current + ")");
                _install.Enabled = i.ExeUrl != null && !Kiosk.IsActive;
                if (Kiosk.IsActive) msg += Lang.T(" — לעדכון יש לבטל את הנעילה", " — turn off lock mode to update");
                SetState(msg, Color.FromArgb(0, 100, 180));
            }
            else
            {
                SetState(Lang.T("הגשר מעודכן ✓ (גרסה " + i.Current + ", האחרונה: " + (i.Latest ?? "?") + ")", "Up to date ✓ (" + i.Current + ", latest: " + (i.Latest ?? "?") + ")"), Color.FromArgb(20, 120, 60));
                _install.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            SetState(ex.Message, Color.Firebrick);
        }
        finally { _check.Enabled = true; }
    }

    private async Task DoInstall()
    {
        _install.Enabled = _check.Enabled = false;
        SetState(Lang.T("מוריד ומאמת…", "Downloading and verifying…"), Color.DimGray);
        try
        {
            await UpdateApi.Install();
            SetState(Lang.T("מתקין… הגשר יופעל מחדש בעוד רגע.", "Installing… the bridge will restart in a moment."), Color.DimGray);
        }
        catch (Exception ex)
        {
            SetState(ex.Message, Color.Firebrick);
            _check.Enabled = true;
        }
    }
}

/// <summary>בקשת סיסמה ליציאה מהמגש בזמן נעילה</summary>
internal sealed class ExitPasswordForm : Form
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    public string Password => _pw.Text;

    public ExitPasswordForm()
    {
        Text = Lang.T("יציאה מהגשר", "Exit the bridge");
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(340, 120);
        Padding = new Padding(14);
        var ok = new Button { Text = Lang.T("אישור", "OK"), DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        var lbl = new Label { Text = Lang.T("הזינו את סיסמת הנעילה:", "Enter the lock password:"), Dock = DockStyle.Top, Height = 28 };
        Controls.AddRange(new Control[] { ok, _pw, lbl });
        AcceptButton = ok;
    }
}
