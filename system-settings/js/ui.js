/* ui.js - theme, icons, toasts, dialogs and small DOM helpers */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = (SS.ui = {});

  /* ---------- theme ---------- */
  function hexToRgba(hex, a) {
    if (!hex || hex[0] !== '#' || hex.length < 7) return null;
    var h = hex.length === 9 ? hex.slice(3) : hex.slice(1);
    var r = parseInt(h.slice(0, 2), 16), g = parseInt(h.slice(2, 4), 16), b = parseInt(h.slice(4, 6), 16);
    return 'rgba(' + r + ', ' + g + ', ' + b + ', ' + a + ')';
  }
  var MAP = {
    primary: '--color-primary', onPrimary: '--color-on-primary',
    primaryContainer: '--color-primary-container', onPrimaryContainer: '--color-on-primary-container',
    secondary: '--color-secondary', onSecondary: '--color-on-secondary',
    secondaryContainer: '--color-secondary-container', onSecondaryContainer: '--color-on-secondary-container',
    tertiary: '--color-tertiary', tertiaryContainer: '--color-tertiary-container', onTertiaryContainer: '--color-on-tertiary-container',
    surface: '--color-surface', onSurface: '--color-on-surface', onSurfaceVariant: '--color-on-surface-variant',
    surfaceContainerLowest: '--color-surface-container-lowest', surfaceContainerLow: '--color-surface-container-low',
    surfaceContainer: '--color-surface-container', surfaceContainerHigh: '--color-surface-container-high',
    surfaceContainerHighest: '--color-surface-container-highest',
    error: '--color-error', onError: '--color-on-error', errorContainer: '--color-error-container', onErrorContainer: '--color-on-error-container',
    outline: '--color-outline', outlineVariant: '--color-outline-variant',
    inverseSurface: '--color-inverse-surface', onInverseSurface: '--color-on-inverse-surface'
  };
  ui.applyTheme = function (theme) {
    if (!theme || !theme.colorScheme) return;
    var cs = theme.colorScheme, root = document.documentElement;
    Object.keys(MAP).forEach(function (k) { if (cs[k]) root.style.setProperty(MAP[k], cs[k]); });
    var ps = hexToRgba(cs.primary, 0.12); if (ps) root.style.setProperty('--color-primary-subtle', ps);
    var ss = hexToRgba(cs.secondary, 0.12); if (ss) root.style.setProperty('--color-secondary-subtle', ss);
    document.body.classList.toggle('dark-mode', theme.mode === 'dark');
    var t = theme.typography;
    if (t) {
      if (t.uiFontFamily) root.style.setProperty('--font-ui', "'" + t.uiFontFamily + "', system-ui, sans-serif");
      if (t.fontFamily) root.style.setProperty('--font-main', "'" + t.fontFamily + "', 'David', serif");
    }
  };

  /* ---------- icons (inline SVG, Fluent-like strokes, currentColor) ---------- */
  var P = {
    speaker: '<path d="M4 9.5h3.2L12 5.5v13l-4.8-4H4z"/><path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11"/>',
    mute: '<path d="M4 9.5h3.2L12 5.5v13l-4.8-4H4z"/><path d="M16 9.5l5 5M21 9.5l-5 5"/>',
    display: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"/>',
    bluetooth: '<path d="M7 7.5l10 9-5 4.5V3l5 4.5-10 9"/>',
    wifi: '<path d="M2.5 9a14 14 0 0 1 19 0M5.5 12.3a9.5 9.5 0 0 1 13 0M8.6 15.6a5 5 0 0 1 6.8 0"/><circle cx="12" cy="19" r="1.2" fill="currentColor"/>',
    lock: '<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/><circle cx="12" cy="15.5" r="1.3" fill="currentColor"/>',
    unlock: '<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 7.7-1.5"/>',
    computer: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M7 20h10"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6"/><circle cx="12" cy="7.8" r="1" fill="currentColor"/>',
    refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3"/><path d="M19.5 4.5v4h-4"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    power: '<path d="M12 3v8"/><path d="M7 6.3a7.5 7.5 0 1 0 10 0"/>',
    restart: '<path d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3"/><path d="M4.5 4.5v4h4"/>',
    sleep: '<path d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l8-8M16 7l2.5 2.5M14 9l2 2"/>',
    shield: '<path d="M12 3l7.5 3v5.5c0 4.5-3.2 8.2-7.5 9.5-4.3-1.3-7.5-5-7.5-9.5V6z"/>',
    warning: '<path d="M12 3.5L2.5 20h19z"/><path d="M12 10v4.5"/><circle cx="12" cy="17.2" r="1" fill="currentColor"/>',
    search: '<circle cx="10.5" cy="10.5" r="6"/><path d="M15 15l5.5 5.5"/>',
    link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
    download: '<path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/>',
    battery: '<rect x="3" y="7" width="16" height="10" rx="2"/><path d="M21 10.5v3"/>',
    plug: '<path d="M9 3v5M15 3v5M6.5 8h11v3a5.5 5.5 0 0 1-11 0zM12 16.5V21"/>',
    headphones: '<path d="M4 15v-3a8 8 0 0 1 16 0v3"/><rect x="3.5" y="14" width="4" height="6" rx="1.5"/><rect x="16.5" y="14" width="4" height="6" rx="1.5"/>',
    mouse: '<rect x="6.5" y="3" width="11" height="18" rx="5.5"/><path d="M12 7v3"/>',
    device: '<rect x="6" y="3" width="12" height="18" rx="2"/><path d="M11 18h2"/>',
    plug2: '<path d="M8 12h8M12 8v8"/>',
    edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
    usb: '<path d="M12 3v14"/><path d="M12 7l4 2.5v2M12 11l-4 2v2"/><circle cx="12" cy="19" r="2"/><rect x="15" y="11.5" width="2" height="2"/><circle cx="8" cy="16" r="1.2"/>',
    keyboard: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/>',
    printer: '<path d="M7 9V3.5h10V9"/><rect x="3.5" y="9" width="17" height="8" rx="2"/><path d="M7 14h10v6.5H7z"/>',
    airplane: '<path d="M21 15.5l-8-4.5V5a1.5 1.5 0 0 0-3 0v6l-8 4.5V17l8-2.5V19l-2.5 1.5V22l4-1 4 1v-1.5L13 19v-4.5l8 2.5z"/>',
    hibernate: '<path d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z"/><path d="M15 3.5h4l-4 4h4"/>',
    signout: '<path d="M14 4h4.5A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5H14"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
    bridge: '<path d="M2.5 17h19M4 17v-4M20 17v-4M2.5 13c3.5 0 6-4 9.5-4s6 4 9.5 4M8 17v-5.5M16 17v-5.5M12 17V9"/>',
    copy: '<rect x="8.5" y="8.5" width="12" height="12" rx="2"/><path d="M15.5 8.5V5.5A1.5 1.5 0 0 0 14 4H5.5A1.5 1.5 0 0 0 4 5.5V14a1.5 1.5 0 0 0 1.5 1.5h3"/>'
  };
  ui.icon = function (name, cls) {
    return '<svg class="ic ' + (cls || '') + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + (P[name] || '') + '</svg>';
  };

  /* ---------- helpers ---------- */
  ui.esc = function (s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  };
  ui.el = function (html) {
    var t = document.createElement('template');
    t.innerHTML = html.trim();
    return t.content.firstElementChild;
  };
  /* בידוד bidi: שם/נתיב/SSID באנגלית שמוטמע בתוך משפט עברי (טוסט, כותרת) לא "יסחוב" איתו את כיוון
     התצוגה של שאר המשפט. שקול ל-<bdi dir="ltr"> אבל עובד גם בטקסט רגיל (למשל הודעת toast), לא רק ב-HTML. */
  ui.isolateLtr = function (s) { return '⁦' + s + '⁩'; };
  ui.debounce = function (fn, ms) {
    var t = null;
    return function () {
      var a = arguments, self = this;
      clearTimeout(t);
      t = setTimeout(function () { fn.apply(self, a); }, ms);
    };
  };

  /* ---------- error messages ---------- */
  var ERR = {
    unsupported: 'לא נתמך במחשב זה',
    locked: 'הפעולה חסומה במצב נעילה',
    wrong_password: 'הסיסמה שגויה',
    forbidden: 'הפעולה אינה מותרת',
    not_found: 'הפריט לא נמצא',
    exists: 'הפריט כבר קיים',
    bad_request: 'הבקשה אינה תקינה',
    internal: 'שגיאה פנימית בגשר',
    timeout: 'הגשר לא הגיב בזמן',
    transport: 'אין חיבור לגשר',
    offline: 'הגשר אינו מחובר',
    bad_response: 'תשובה לא תקינה מהגשר'
  };
  ui.rawErrText = function (e) {
    if (!e) return 'שגיאה לא ידועה';
    var code = e.code || 'internal';
    var msg = e.message && /[֐-׿]/.test(e.message) && !(window.I18N && window.I18N.isEn() && ERR[e.code]) ? e.message : '';
    /* unsupported אין בו טעם אבטחתי להסתיר את הסיבה הספציפית (בניגוד ל-wrong_password/locked, ששם
       הסתרת פרטים מונעת דליפת מידע על ניסיונות כניסה) - ולכן לא ברשימה הזו. בלעדי זה אי אפשר היה
       לדעת למה פעולה נכשלה ("לא נתמך במחשב זה" בלי שום הקשר) */
    if (code === 'wrong_password' || code === 'locked') {
      if (msg && msg !== ERR[code] && /שני|נסו|המתן|המתינו/.test(msg)) return msg;
      return ERR[code];
    }
    return msg || ERR[code] || ('שגיאה: ' + code);
  };

  /* הודעות ידידותיות לכל הטאבים: משפט פשוט + "מה לעשות", בלי מונחים טכניים */
  var TECH_RE = /\b[45]\d\d\b|fail[- ]?closed|exception|stack|undefined|\bnull\b|HTTP|ECONN|fetch|network|pipe/i;
  var NET_CODES = { transport: 1, offline: 1, timeout: 1, bad_response: 1 };
  ui.errText = function (e) {
    var code = e && e.code, raw = (e && e.message) || '';
    if (!e || code === 'wrong_password' || code === 'locked' || code === 'too_many') return ui.rawErrText(e);
    if (NET_CODES[code] || /\b503\b|ECONN|fetch|network/i.test(raw))
      return 'הגשר לא זמין כרגע. מה לעשות: ודאו שהאייקון של „אחת שאלתי” פועל במגש המערכת, המתינו כמה שניות ונסו שוב.';
    if (/fail[- ]?closed|corrupt|פגום|לא מגיב|\b50[0-9]\b/i.test(raw) || code === 'corrupt' || code === 'internal')
      return 'רכיב ההקשחה לא הגיב, ולכן הפעולה נחסמה כדי לשמור על האבטחה. מה לעשות: המתינו דקה ונסו שוב. אם זה חוזר, הפעילו מחדש את המחשב.';
    var t = ui.rawErrText(e);
    return TECH_RE.test(t) ? 'הפעולה לא הושלמה. מה לעשות: נסו שוב, ואם זה חוזר — רעננו את העמוד או הפעילו מחדש את הגשר.' : t;
  };

  /* ---------- toasts ---------- */
  ui.toast = function (message, kind) {
    var host = document.getElementById('toasts');
    /* אותה הודעה כבר מוצגת — לא מכפילים */
    var dup = Array.prototype.some.call(host.querySelectorAll('.toast span'), function (s) { return s.textContent === message; });
    if (dup) return;
    var t = ui.el('<div class="toast ' + (kind || '') + '" role="status">' +
      ui.icon(kind === 'error' ? 'warning' : kind === 'success' ? 'check' : 'info') +
      '<span>' + ui.esc(message) + '</span></div>');
    host.appendChild(t);
    requestAnimationFrame(function () { t.classList.add('show'); });
    setTimeout(function () {
      t.classList.remove('show');
      setTimeout(function () { t.remove(); }, 250);
    }, kind === 'error' ? 4800 : 3000);
  };
  ui.error = function (e, prefix) { ui.toast((prefix ? prefix + ': ' : '') + ui.errText(e), 'error'); };

  /* ---------- לכידת פוקוס (Tab/Shift+Tab) לחלונות מודאליים: דיאלוגים וצופה הקבצים ---------- */
  ui.focusables = function (root) {
    return Array.prototype.filter.call(
      root.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'),
      function (x) { return !x.disabled && x.offsetParent !== null; }
    );
  };
  ui.trapTab = function (root, e) {
    if (e.key !== 'Tab') return;
    var list = ui.focusables(root);
    if (!list.length) return;
    var i = list.indexOf(document.activeElement);
    if (e.shiftKey) { if (i <= 0) { e.preventDefault(); list[list.length - 1].focus(); } }
    else { if (i === list.length - 1 || i === -1) { e.preventDefault(); list[0].focus(); } }
  };

  /* ---------- dialogs ---------- */
  /* options: title, body (html), icon, tone (danger|primary), actions [{label, value, kind}], fields [{name,label,type,placeholder,value,disabled}],
     onSubmit(values) -> may throw to keep dialog open (shows message), dismissible.
     disabled: השדה מושבת (data-locked) ונשאר מושבת גם אחרי מצב "שומר…"; onOpen יכול לשנות זאת עם ui.lockField */
  /* C7: שדות עם more:true מקובצים לאזור מתקפל "עוד" (מוסתרים, לא נמחקים).
     f.type === 'group' (יאיר, 049b564): כותרת קבוצה מתקפלת (<details>/<summary>) - כל השדות שאחריה
     נכנסים לתוכה עד הקבוצה הבאה (או סוף הרשימה). ר' kiosk.js editSettings */
  ui.fieldsHtml = function (fields) {
    var out = '', more = '', inGroup = false;
    function flushMore() { if (more) { out += '<details class="field-more"><summary class="muted small">עוד…</summary>' + more + '</details>'; more = ''; } }
    (fields || []).forEach(function (f) {
      if (f.type === 'group') {
        flushMore();
        if (inGroup) out += '</div></details>';
        out += '<details class="field-group"' + (f.open ? ' open' : '') + '><summary>' +
          (f.icon ? ui.icon(f.icon) : '') + '<span>' + ui.esc(f.label) + '</span>' +
          (f.hint ? '<small>' + f.hint + '</small>' : '') + '</summary><div class="field-group-body">';
        inGroup = true;
        return;
      }
      var h = ui.fieldHtml(f);
      if (f.more) more += h; else { flushMore(); out += h; }
    });
    flushMore();
    if (inGroup) out += '</div></details>';
    return out;
  };
  ui.fieldHtml = function (f) {
    return [f].map(function (f) {
        if (f.type === 'note') {
          return '<div class="field-note muted small">' + f.label + '</div>';
        }
        if (f.type === 'pills') {
          /* משוב 15.10: כמה מתגים קטנים בשורה אחת (כל פריט בחירה נפרדת, בלי תפריט) */
          return '<div class="field"><span class="field-label">' + ui.esc(f.label) + '</span><div class="pill-row">' +
            f.items.map(function (it) {
              return '<label class="mini-switch pill"><input type="checkbox" name="' + it.name + '"' + (it.value ? ' checked' : '') + '>' +
                '<span class="mini-track"></span><span class="mini-text">' + ui.esc(it.label) + '</span></label>';
            }).join('') + '</div>' + (f.hint ? '<small>' + f.hint + '</small>' : '') + '</div>';
        }
        if (f.type === 'textarea') {
          return '<label class="field"><span class="field-label">' + ui.esc(f.label) + '</span><textarea class="input" rows="' + (f.rows || 3) + '" name="' + f.name + '"' +
            (f.placeholder ? ' placeholder="' + ui.esc(f.placeholder) + '"' : '') + '>' + ui.esc(f.value || '') + '</textarea>' + (f.hint ? '<small>' + f.hint + '</small>' : '') + '</label>';
        }
      if (f.type === 'checkpair') {
          /* שורה אחת: תיבת סימון + מתג קטן בצד (משוב 15.10: "להציג" + "התראה" לכל זמן) */
          return '<div class="check-pair"><label class="check-row switch-row"><input type="checkbox" name="' + f.name + '"' + (f.value ? ' checked' : '') + '>' +
            '<span class="mini-track big"></span><span class="check-text">' + f.label + '</span></label>' +
            '<label class="mini-switch" title="' + ui.esc(f.label2) + '"><input type="checkbox" name="' + f.name2 + '"' + (f.value2 ? ' checked' : '') + '>' +
            '<span class="mini-track"></span><span class="mini-text">' + ui.esc(f.label2) + '</span></label></div>';
        }
        if (f.type === 'checkbox') {
          /* משוב 15.10: מתג בסגנון אוצריא (ולא תיבת סימון) לכל הגדרת הפעלה/כיבוי */
          return '<label class="check-row switch-row' + (f.disabled ? ' is-disabled' : '') + '"><input type="checkbox" name="' + f.name + '"' + (f.value ? ' checked' : '') + (f.disabled ? ' disabled data-locked' : '') + '>' +
            '<span class="mini-track big"></span><span class="check-text">' + f.label +
            (f.hint ? '<small>' + f.hint + '</small>' : '') + '</span></label>';
        }
        if (f.type === 'select') {
          /* בחירה: תפריט נפתח */
          var lb = !!f.listbox; /* תפריט נפתח כברירת מחדל (משוב 15.10) — רשימה פתוחה רק אם מבקשים במפורש */
          var opts = (f.options || []).map(function (o) {
            return '<option value="' + ui.esc(o.value) + '"' + (o.value === f.value ? ' selected' : '') + '>' + ui.esc(o.label) + '</option>';
          }).join('');
          return '<label class="field"><span class="field-label">' + ui.esc(f.label) + '</span>' +
            '<select class="input' + (lb ? ' listbox' : '') + '" name="' + f.name + '"' + (lb ? ' size="' + Math.min(f.options.length, 6) + '"' : '') + (f.dir ? ' dir="' + f.dir + '"' : '') + '>' + opts + '</select>' + (f.hint ? '<small>' + f.hint + '</small>' : '') + '</label>';
        }
        var half = f.half || (f.half !== false && f.dir === 'ltr' && !f.type && String(f.value || '').length <= 6 && String(f.label).length <= 50);
        return '<label class="field' + (half ? ' half' : '') + '"><span class="field-label">' + ui.esc(f.label) + '</span>' +
          '<input class="input" name="' + f.name + '" type="' + (f.type || 'text') + '"' +
          (f.placeholder ? ' placeholder="' + ui.esc(f.placeholder) + '"' : '') +
          (f.value ? ' value="' + ui.esc(f.value) + '"' : '') +
          (f.dir ? ' dir="' + f.dir + '"' : '') + (f.disabled ? ' disabled data-locked' : '') + ' autocomplete="off"></label>';
    }).join('');
  };

  /* ערכי שדות מתוך טופס (גם בתוך עמוד, לא רק בדיאלוג) */
  ui.fieldValues = function (form, fields) {
    var v = {};
    (fields || []).forEach(function (f) {
      if (f.type === 'note' || f.type === 'group') return;
      var inp = form.querySelector('[name="' + f.name + '"]');
      if (f.type === 'pills') { f.items.forEach(function (it) { var e = form.querySelector('[name="' + it.name + '"]'); if (e) v[it.name] = e.checked; }); return; }
      if (f.type === 'checkpair') { var i2 = form.querySelector('[name="' + f.name2 + '"]'); if (i2) v[f.name2] = i2.checked; }
      if (inp) v[f.name] = (f.type === 'checkbox' || f.type === 'checkpair') ? inp.checked : inp.value;
    });
    return v;
  };

  /* טופס בתוך עמוד (במקום דיאלוג מאחורי כפתור): אותן אפשרויות של ui.dialog — fields, body, validate, onOpen, onSubmit.
     מחזיר Promise שמתקיים (true) אחרי שמירה מוצלחת. בסיס מינימלי — להרחבה בהמשך. */
  ui.inlineForm = function (slot, opt) {
    return new Promise(function (resolve) {
      slot.innerHTML = (opt.body || '') + '<form class="ss-form" novalidate><div class="dlg-fields">' + ui.fieldsHtml(opt.fields) + '</div>' +
        '<div class="form-msg" hidden></div>' +
        '<div class="row-actions"><button class="btn btn-primary" type="submit">' + ui.icon('check') + 'שמירה</button></div></form>';
      var form = slot.querySelector('form'), msg = form.querySelector('.form-msg'), btn = form.querySelector('button[type=submit]');
      function say(m) { msg.textContent = m || ''; msg.hidden = !m; }
      if (opt.onOpen) opt.onOpen(form);
      form.addEventListener('submit', async function (e) {
        e.preventDefault();
        var v = ui.fieldValues(form, opt.fields);
        var m = opt.validate ? opt.validate(v) : '';
        if (m) { say(m); return; }
        say(''); btn.disabled = true;
        try { if (opt.onSubmit) await opt.onSubmit(v); resolve(true); }
        catch (err) { say(ui.errText ? ui.errText(err) : String(err && err.message || err)); btn.disabled = false; }
      });
    });
  };

  ui.dialog = function (opt) {
    return new Promise(function (resolve) {
      var host = document.getElementById('dialogs');
      Array.prototype.forEach.call(host.querySelectorAll('.dlg-scrim:not(.open)'), function (x) { x.remove(); });
      var prevFocus = document.activeElement; /* להחזרת הפוקוס לאחר סגירת הדיאלוג */
      var fieldsHtml = ui.fieldsHtml(opt.fields);
      var actions = opt.actions || [{ label: 'ביטול', value: false }, { label: 'אישור', value: true, kind: 'primary' }];
      var d = ui.el(
        '<div class="dlg-scrim"><form class="dlg ' + (opt.tone || '') + '" role="dialog" aria-modal="true" novalidate>' +
        (opt.icon ? '<div class="dlg-icon">' + ui.icon(opt.icon) + '</div>' : '') +
        '<h2 class="dlg-title">' + ui.esc(opt.title || '') + '</h2>' +
        (opt.body ? '<div class="dlg-body">' + opt.body + '</div>' : '') +
        (fieldsHtml ? '<div class="dlg-fields">' + fieldsHtml + '</div>' : '') +
        '<div class="dlg-msg" hidden></div>' +
        '<div class="dlg-actions">' + actions.map(function (a, i) {
          return '<button type="' + (a.kind === 'primary' || a.kind === 'danger' ? 'submit' : 'button') + '" class="btn ' +
            (a.kind === 'primary' ? 'btn-primary' : a.kind === 'danger' ? 'btn-danger' : 'btn-text') + '" data-i="' + i + '">' + ui.esc(a.label) + '</button>';
        }).join('') + '</div></form></div>');
      host.appendChild(d);
      var form = d.querySelector('form');
      var msg = d.querySelector('.dlg-msg');
      requestAnimationFrame(function () { d.classList.add('open'); });
      var first = form.querySelector('input:not([type=checkbox])');
      setTimeout(function () {
        if (first) first.focus(); else { var f = ui.focusables(form)[0]; if (f) f.focus(); }
        // דיאלוג ארוך בלי שדות (למשל הדרכה טקסטואלית): הפוקוס נופל על כפתור הפעולה - בדרך כלל
        // בתחתית - והדפדפן גולל אליו אוטומטית. form עצמו הוא אזור הגלילה (overflow-y: auto ב-.dlg),
        // אז מאפסים את הגלילה שלו בחזרה לראש אחרי שהפוקוס כבר נקבע.
        form.scrollTop = 0;
      }, 60);

      function values() {
        var v = {};
        (opt.fields || []).forEach(function (f) {
          if (f.type === 'note' || f.type === 'group') return;
          var inp = form.querySelector('[name="' + f.name + '"]');
          v[f.name] = f.type === 'checkbox' ? inp.checked : inp.value;
        });
        return v;
      }
      function close(result) {
        d.classList.remove('open');
        d.style.pointerEvents = 'none';
        document.removeEventListener('keydown', onKey);
        setTimeout(function () { d.remove(); }, 200);
        if (prevFocus && prevFocus.focus && document.contains(prevFocus)) prevFocus.focus();
        resolve(result);
      }
      function setBusy(b) {
        form.querySelectorAll('button,input').forEach(function (x) { x.disabled = b || x.hasAttribute('data-locked'); });
        form.classList.toggle('busy', b);
      }
      async function act(a) {
        if (a.value === false || !opt.onSubmit) {
          if (a.value !== false && opt.validate) {
            var vm = opt.validate(values());
            if (vm) { msg.textContent = vm; msg.hidden = false; return; }
          }
          close(a.value === false ? null : (opt.fields ? values() : a.value));
          return;
        }
        var v = values();
        if (opt.validate) {
          var m = opt.validate(v);
          if (m) { msg.textContent = m; msg.hidden = false; return; }
        }
        msg.hidden = true;
        setBusy(true);
        try {
          var r = await opt.onSubmit(v);
          setBusy(false);
          close(r === undefined ? v : r);
        } catch (e) {
          setBusy(false);
          msg.textContent = ui.errText(e);
          msg.hidden = false;
          /* סיסמת האימות של הדיאלוג (name=pw) קודמת לשדות סיסמה אחרים בטופס */
          var pw = form.querySelector('input[type=password][name=pw]') || form.querySelector('input[type=password]');
          if (pw) { pw.select(); pw.focus(); }
        }
      }
      form.addEventListener('submit', function (e) {
        e.preventDefault();
        var idx = actions.findIndex(function (a) { return a.kind === 'primary' || a.kind === 'danger'; });
        if (idx >= 0) act(actions[idx]);
      });
      form.querySelectorAll('button[type=button]').forEach(function (b) {
        b.addEventListener('click', function () { act(actions[+b.dataset.i]); });
      });
      function onKey(e) {
        if (e.key === 'Escape' && opt.dismissible !== false) { close(null); return; }
        ui.trapTab(form, e);
      }
      document.addEventListener('keydown', onKey);
      d.addEventListener('mousedown', function (e) { if (e.target === d && opt.dismissible !== false) close(null); });
      if (opt.onOpen) opt.onOpen(form);
    });
  };

  /* השבתה/הפעלה של שדה בדיאלוג פתוח (שדה תלוי בשדה אחר). מושבת נשאר מושבת גם אחרי מצב "שומר…" */
  ui.lockField = function (inp, locked) {
    if (!inp) return;
    inp.disabled = !!locked;
    if (locked) inp.setAttribute('data-locked', ''); else inp.removeAttribute('data-locked');
    var row = inp.closest('.check-row, .field');
    if (row) row.classList.toggle('is-disabled', !!locked);
  };

  ui.confirm = function (title, body, okLabel, tone) {
    return ui.dialog({
      title: title, body: body, icon: tone === 'danger' ? 'warning' : 'info', tone: tone,
      actions: [{ label: 'ביטול', value: false }, { label: okLabel || 'אישור', value: true, kind: tone === 'danger' ? 'danger' : 'primary' }]
    });
  };

  /* ---------- section skeleton helpers ---------- */
  ui.loading = function (text) {
    return '<div class="loading"><div class="spinner"></div><span>' + ui.esc(text || 'טוען…') + '</span></div>';
  };
  /* כפתור "נסה שוב": onRetry היא פונקציה שמריצה מחדש את הפעולה שנכשלה (בדרך כלל ה-load של האותו מסך).
     נרשמת במילון פנימי ומופעלת דרך האזנה אחת (event delegation) ב-document, כדי שלא יהיה צורך
     לצרף מאזין לכל כפתור בנפרד אחרי הזרקת ה-HTML ל-innerHTML. */
  var retryHandlers = {};
  var retrySeq = 0;
  document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-retry]');
    if (!btn) return;
    var fn = retryHandlers[btn.getAttribute('data-retry')];
    if (fn) fn();
  });
  ui.errorBox = function (e, onRetry) {
    var id = '';
    if (onRetry) { id = 'r' + (++retrySeq); retryHandlers[id] = onRetry; }
    return '<div class="state-box error">' + ui.icon('warning') + '<div><b>' + ui.esc(ui.errText(e)) + '</b>' +
      (onRetry ? '<button class="btn btn-text" data-retry="' + id + '">' + ui.icon('refresh') + 'נסה שוב</button>' : '') + '</div></div>';
  };
  ui.switchHtml = function (id, on, label) {
    return '<button class="switch' + (on ? ' on' : '') + '" role="switch" aria-checked="' + (on ? 'true' : 'false') + '" id="' + id + '" aria-label="' + ui.esc(label || '') + '"><span class="knob"></span></button>';
  };
  ui.setSwitch = function (btn, on) {
    btn.classList.toggle('on', !!on);
    btn.setAttribute('aria-checked', on ? 'true' : 'false');
  };
  ui.signalBars = function (signal) {
    var n = signal >= 75 ? 4 : signal >= 50 ? 3 : signal >= 25 ? 2 : 1;
    var s = '<span class="bars" title="' + signal + '%">';
    for (var i = 1; i <= 4; i++) s += '<i class="' + (i <= n ? 'on' : '') + '" style="height:' + (i * 25) + '%"></i>';
    return s + '</span>';
  };

  /* נר מרצד רלוונטי רק בסגנון "עמודה": בסגנון "קבועה" השדה מוסתר */
  function syncCandle() {
    Array.prototype.forEach.call(document.querySelectorAll('select[name="dedStyle"]'), function (sel) {
      var scope = sel.closest('form') || document;
      var cb = scope.querySelector('input[name="candleFlicker"]');
      var wrap = cb && cb.closest('label');
      if (wrap) wrap.hidden = sel.value !== 'column';
    });
  }
  document.addEventListener('change', function (e) { if (e.target && e.target.name === 'dedStyle') syncCandle(); });
  function watchCandle() { try { new MutationObserver(syncCandle).observe(document.body, { childList: true, subtree: true }); } catch (e) { /* כלום */ } syncCandle(); }
  if (document.body) watchCandle(); else document.addEventListener('DOMContentLoaded', watchCandle);
})();
