using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>C1: חלון אחד עם טאבים לכל חלונות המגש (אודות, שינויים, דיווחים, צימוד).
/// כל טופס קיים מוטמע כטאב (TopLevel=false) במקום לפתוח חלון נפרד.</summary>
internal sealed class Hub : Form
{
    private static Hub _inst;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
    // משוב 15.10: רשימת הטאבים בצד (ימין בעברית — Dock.Left מתהפך ב-RightToLeftLayout) במקום כותרות למעלה
    private readonly FlowLayoutPanel _side = new() { Dock = DockStyle.Left, Width = 170, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 10, 6, 6), BackColor = Color.FromArgb(243, 236, 220) };
    private readonly Dictionary<string, TabPage> _pages = new();

    private static bool _filling;
    /// <summary>משוב 15.10: בכל פתיחה החלון מכיל את כל הטאבים, והטאב המבוקש נבחר</summary>
    public static void Show(string key, Func<Form> make)
    {
        if (_inst == null || _inst.IsDisposed) _inst = new Hub();
        if (_filling) { _inst.Open(key, make, select: false); return; }
        _filling = true;
        try { AboutForm.Open(); ChangelogForm.Open(); ReportsForm.Open(); PairingForm.Open(); ToolsForm.Open(); }
        catch (Exception ex) { Config.Log("hub fill: " + ex.Message); }
        finally { _filling = false; }
        _inst.Open(key, make, select: true);
    }

    /// <summary>פתיחת החלון הראשי (לחיצה על "אחת שאלתי" במגש)</summary>
    public static void ShowMain() => AboutForm.Open();

    private Hub()
    {
        Text = Program.AppName;
        try { Icon = Overlay.AppIcon(); } catch { }
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        Size = new Size(760, 620);
        MinimumSize = new Size(520, 420);
        BackColor = UiStyle.Page;
        Controls.Add(_tabs);
        Controls.Add(_side);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab != null) Text = Program.AppName + " — " + _tabs.SelectedTab.Text; PaintSide(); };
    }

    /// <summary>שם קצר לטאב: בלי הסיומת אחרי " — " ובלי שם המוצר ("אודות אחת שאלתי" ← "אודות"), כדי שלא ייחתך בסרגל הצד</summary>
    private static string ShortName(string t)
    {
        int i = t.IndexOf(" — ", StringComparison.Ordinal);
        if (i > 0) t = t[..i];
        foreach (var p in new[] { "אודות ", "About " }) if (t.StartsWith(p, StringComparison.Ordinal)) return p.Trim();
        return t.Trim();
    }

    private void PaintSide()
    {
        foreach (Control c in _side.Controls)
        {
            bool sel = c.Tag == _tabs.SelectedTab;
            c.BackColor = sel ? Color.White : Color.FromArgb(243, 236, 220);
            c.ForeColor = UiStyle.GoldText;
            c.Font = new Font(Font, sel ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private void Open(string key, Func<Form> make, bool select)
    {
        if (!_pages.TryGetValue(key, out var page) || page.IsDisposed || page.Controls.Count == 0)
        {
            var f = make();
            UiStyle.Theme(f);
            string name = ShortName(f.Text);
            page = new TabPage(name) { UseVisualStyleBackColor = true };
            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.Dock = DockStyle.Fill;
            page.Controls.Add(f);
            var btn = new Button { Text = name, Width = 156, Height = 36, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 0, 0, 4), Tag = page, Cursor = Cursors.Hand };
            btn.FlatAppearance.BorderSize = 0;
            if (Lang.En) btn.TextAlign = ContentAlignment.MiddleLeft;
            btn.Click += (_, _) => _tabs.SelectedTab = page;
            _side.Controls.Add(btn);
            f.FormClosed += (_, _) => { _tabs.TabPages.Remove(page); _pages.Remove(key); _side.Controls.Remove(btn); if (_tabs.TabCount == 0) Close(); };
            _pages[key] = page;
            _tabs.TabPages.Add(page);
            f.Show();
        }
        if (!select) return;
        _tabs.SelectedTab = page;
        PaintSide();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
}
