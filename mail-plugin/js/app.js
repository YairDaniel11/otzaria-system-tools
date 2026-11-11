/* דואר — תוסף לאוצריא (חד בדרא, 11.2026, ענף mail-plugin). קבלת דואר דרך הגשר "אחת שאלתי" (/mail/*).
   התוסף לא פונה לאינטרנט בעצמו: הגשר מתחבר לשרת ה-IMAP ושומר את הסיסמה מוצפנת (DPAPI). */
(function () {
  'use strict';
  var MX = window.MX, B = MX.Bridge;
  var VERSION = '0.9.0';
  var $ = function (id) { return document.getElementById(id); };
  var S = { acct: null, folders: [], folder: 'INBOX', offset: 0, limit: 50, total: 0, q: '', items: [], view: 'list', msg: null };

  /* ---------- אייקונים (קו דק, בסגנון אוצריא) ---------- */
  var P = {
    menu: 'M4 7h16M4 12h16M4 17h16',
    search: 'M10.5 4.5a6 6 0 1 1 0 12 6 6 0 0 1 0-12zM15 15l5 5',
    refresh: 'M19 12a7 7 0 1 1-2.05-4.95M19 4.5V8h-3.5',
    gear: 'M12 9a3 3 0 1 1 0 6 3 3 0 0 1 0-6zM12 3.5v2M12 18.5v2M3.5 12h2M18.5 12h2M6 6l1.4 1.4M16.6 16.6L18 18M6 18l1.4-1.4M16.6 7.4L18 6',
    inbox: 'M4 13l2.5-7h11L20 13v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1zM4 13h4.5l1 2h5l1-2H20',
    sent: 'M4 12l16-7-6 15-2.5-6.5zM11.5 13.5L20 5',
    drafts: 'M5 19h14M7 15l9-9 2 2-9 9H7z',
    junk: 'M12 4l8.5 15h-17zM12 10v4M12 16.5v.5',
    trash: 'M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12',
    starred: 'M12 4l2.4 5 5.4.6-4 3.7 1.1 5.4L12 16l-4.9 2.7 1.1-5.4-4-3.7 5.4-.6z',
    all: 'M5 6h14v4H5zM6 10v8h12v-8M10 13.5h4',
    important: 'M5 6h10l4 6-4 6H5l4-6z',
    folder: 'M4 7a1 1 0 0 1 1-1h4l2 2h8a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z',
    back: 'M14 6l6 6-6 6M20 12H5',
    prev: 'M9 6l6 6-6 6', next: 'M15 6l-6 6 6 6',
    clip: 'M16 8l-6.5 6.5a2 2 0 0 0 2.8 2.8L19 10.6a4 4 0 0 0-5.7-5.7L6.6 11.6a6 6 0 0 0 8.5 8.5L20 15',
    file: 'M7 3.5h7l4 4v13H7zM14 3.5v4h4',
    mail: 'M4 6h16v12H4zM4 7l8 6 8-6'
  };
  function ic(n) { return '<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="' + (P[n] || P.folder) + '"/></svg>'; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  var tt;
  function toast(m) { var t = $('mx-toast'); t.textContent = m; t.classList.add('on'); clearTimeout(tt); tt = setTimeout(function () { t.classList.remove('on'); }, 3800); }
  function errMsg(e) { return (e && e.message) || 'שגיאה'; }
  MX.ui = { toast: toast };
  MX.t = function (s) { return s; };

  /* ---------- ערכת הנושא של אוצריא ---------- */
  function kebab(k) { return k.replace(/[A-Z]/g, function (c) { return '-' + c.toLowerCase(); }); }
  function applyTheme(t) {
    if (!t || !t.colorScheme) return;
    var r = document.documentElement.style;
    Object.keys(t.colorScheme).forEach(function (k) { var v = t.colorScheme[k]; if (typeof v === 'string' && /^#/.test(v)) r.setProperty('--color-' + kebab(k), v); });
    var f = t.typography && t.typography.uiFontFamily;
    if (f) r.setProperty('--font-ui', "'" + f + "', 'Segoe UI', system-ui, sans-serif");
    document.body.classList.toggle('dark', t.mode === 'dark');
  }

  /* ---------- תאריך בסגנון Gmail ---------- */
  function fmtDate(iso, full) {
    var d = new Date(iso); if (isNaN(d)) return '';
    var now = new Date();
    if (full) return d.toLocaleString('he-IL', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
    if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString('he-IL', { hour: '2-digit', minute: '2-digit' });
    if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString('he-IL', { day: 'numeric', month: 'short' });
    return d.toLocaleDateString('he-IL');
  }

  /* ---------- מצבים ---------- */
  function state(html) { $('mx-main').innerHTML = '<div class="mx-state">' + html + '</div>'; }
  function loading(t) { state('<div class="mx-spin"></div>' + esc(t || 'טוען…')); }

  async function connect() {
    loading('מתחבר לאחת שאלתי…');
    var info = null;
    try { info = await B.probe(); } catch (e) { info = null; }
    if (!info) {
      state(ic('mail') + '<p><b>אחת שאלתי אינה פועלת</b></p><p>הדואר מתקבל דרך אחת שאלתי (בגרסה עם תמיכה בדואר). הפעילו אותה ונסו שוב.</p>' +
        '<button class="mx-btn" id="mx-retry">ניסיון חוזר</button>');
      $('mx-retry').onclick = connect;
      return;
    }
    try { S.acct = await B.call('/mail/account/get', {}); }
    catch (e) {
      if (e && (e.code === 'not_found' || /404/.test(e.message))) { state('<p><b>הגרסה המותקנת של אחת שאלתי אינה תומכת בדואר.</b></p><p>יש להתקין את הגרסה מענף mail-plugin.</p>'); return; }
      state('<p>' + esc(errMsg(e)) + '</p><button class="mx-btn" id="mx-retry">ניסיון חוזר</button>'); $('mx-retry').onclick = connect; return;
    }
    if (S.acct && S.acct.locked) { state(ic('mail') + '<p><b>הדואר חסום במצב נעילה.</b></p><p>המנהל יכול לאפשר אותו בהגדרות הנעילה.</p>'); return; }
    if (!S.acct || !S.acct.configured) { setup(); return; }
    $('mx-acct').textContent = S.acct.email;
    await loadFolders();
    await openFolder(S.folder);
  }

  /* ---------- הגדרת חשבון ---------- */
  var PRESETS = [
    ['Gmail', 'imap.gmail.com', 993, 'ב-Gmail: חשבון Google ← אבטחה ← אימות דו-שלבי ← "סיסמאות לאפליקציות". יוצרים סיסמה ומדביקים כאן.', 'https://myaccount.google.com/apppasswords'],
    ['Outlook / Hotmail', 'outlook.office365.com', 993, 'ב-Outlook: account.microsoft.com ← אבטחה ← אפשרויות אבטחה מתקדמות ← "סיסמאות אפליקציה".', 'https://account.microsoft.com/security'],
    ['Yahoo', 'imap.mail.yahoo.com', 993, 'ב-Yahoo: אבטחת החשבון ← "יצירת סיסמת אפליקציה".', 'https://login.yahoo.com/account/security'],
    ['iCloud', 'imap.mail.me.com', 993, 'ב-iCloud: appleid.apple.com ← כניסה ואבטחה ← "סיסמאות ייעודיות ליישומים".', 'https://appleid.apple.com'],
    ['אחר', '', 993, 'הזינו את כתובת שרת ה-IMAP ואת הפורט מספק הדואר (בדרך כלל 993).', '']
  ];
  function setup() {
    var cur = S.acct && S.acct.configured ? S.acct : null;
    $('mx-main').innerHTML =
      '<div class="mx-setup"><h2>' + (cur ? 'חשבון הדואר' : 'הוספת חשבון דואר') + '</h2>' +
      '<p>הדואר מתקבל דרך אחת שאלתי, שמתחברת לשרת הדואר שלכם (IMAP). הסיסמה נשמרת במחשב בלבד, מוצפנת, ולא נשלחת לשום מקום אחר. אפשר רק לקרוא דואר — לא לשלוח.</p>' +
      '<div class="mx-presets" id="mx-pre">' + PRESETS.map(function (p, i) { return '<button type="button" data-i="' + i + '">' + esc(p[0]) + '</button>'; }).join('') + '</div>' +
      '<p id="mx-howto"></p>' +
      '<label class="mx-f"><span>כתובת מייל</span><input id="mx-email" type="email" dir="ltr" value="' + esc(cur ? cur.email : '') + '"></label>' +
      '<label class="mx-f"><span>סיסמת אפליקציה</span><input id="mx-pw" type="password" dir="ltr" autocomplete="off"></label>' +
      '<div class="mx-row2"><label class="mx-f"><span>שרת IMAP</span><input id="mx-host" dir="ltr" value="' + esc(cur ? cur.host : '') + '"></label>' +
      '<label class="mx-f"><span>פורט</span><input id="mx-port" dir="ltr" inputmode="numeric" value="' + esc(cur ? cur.port : 993) + '"></label></div>' +
      '<label class="mx-f"><span>שם משתמש (אם שונה מהמייל)</span><input id="mx-user" dir="ltr" value="' + esc(cur && cur.user !== cur.email ? cur.user : '') + '"></label>' +
      '<div class="mx-err" id="mx-err"></div>' +
      '<div class="mx-acts">' + (cur ? '<button class="mx-btn tx" id="mx-remove">הסרת החשבון</button><button class="mx-btn tx" id="mx-cancel">ביטול</button>' : '') +
      '<button class="mx-btn" id="mx-save">חיבור</button></div></div>';
    var link = '';
    function pick(i) {
      var p = PRESETS[i];
      Array.prototype.forEach.call($('mx-pre').children, function (b) { b.classList.toggle('sel', +b.dataset.i === i); });
      if (p[1]) $('mx-host').value = p[1];
      $('mx-port').value = p[2];
      link = p[4];
      $('mx-howto').innerHTML = esc(p[3]) + (link ? ' <a href="#" id="mx-link">לדף ההגדרה</a>' : '');
      var a = $('mx-link'); if (a) a.onclick = function (e) { e.preventDefault(); openUrl(link); };
    }
    $('mx-pre').onclick = function (e) { var b = e.target.closest('button'); if (b) pick(+b.dataset.i); };
    $('mx-email').oninput = function () {
      var d = ($('mx-email').value.split('@')[1] || '').toLowerCase();
      var i = /gmail|googlemail/.test(d) ? 0 : /outlook|hotmail|live|msn/.test(d) ? 1 : /yahoo/.test(d) ? 2 : /icloud|me\.com|mac\.com/.test(d) ? 3 : -1;
      if (i >= 0) pick(i);
    };
    if (!cur) pick(0);
    if ($('mx-cancel')) $('mx-cancel').onclick = function () { openFolder(S.folder); };
    if ($('mx-remove')) $('mx-remove').onclick = async function () {
      try { await B.call('/mail/account/clear', {}); S.acct = null; $('mx-folders').innerHTML = ''; $('mx-acct').textContent = ''; setup(); toast('החשבון הוסר מהמחשב'); }
      catch (e) { $('mx-err').textContent = errMsg(e); }
    };
    $('mx-save').onclick = async function () {
      var btn = $('mx-save'); btn.disabled = true; $('mx-err').textContent = '';
      btn.textContent = 'מתחבר…';
      try {
        S.acct = await B.call('/mail/account/set', {
          email: $('mx-email').value.trim(), password: $('mx-pw').value, host: $('mx-host').value.trim(),
          port: parseInt($('mx-port').value, 10) || 993, user: $('mx-user').value.trim() || $('mx-email').value.trim()
        }, { timeoutMs: 60000 });
        toast('החשבון חובר');
        S.folder = 'INBOX'; connect();
      } catch (e) { $('mx-err').textContent = errMsg(e); btn.disabled = false; btn.textContent = 'חיבור'; }
    };
  }

  /* ---------- תיקיות ---------- */
  var ROLE_ORDER = { inbox: 0, starred: 1, important: 2, sent: 3, drafts: 4, all: 5, junk: 6, trash: 7 };
  var ROLE_NAME = { inbox: 'דואר נכנס', starred: 'מסומנים בכוכב', important: 'חשוב', sent: 'דואר יוצא', drafts: 'טיוטות', all: 'כל הדואר', junk: 'ספאם', trash: 'אשפה' };
  async function loadFolders() {
    try {
      var r = await B.call('/mail/folders', {}, { timeoutMs: 60000 });
      S.folders = (r && r.folders || []).sort(function (a, b) {
        var x = a.role in ROLE_ORDER ? ROLE_ORDER[a.role] : 50, y = b.role in ROLE_ORDER ? ROLE_ORDER[b.role] : 50;
        return x - y || String(a.name).localeCompare(String(b.name), 'he');
      });
    } catch (e) { S.folders = [{ id: 'INBOX', name: 'INBOX', role: 'inbox', unread: 0 }]; toast(errMsg(e)); }
    renderFolders();
  }
  function renderFolders() {
    $('mx-folders').innerHTML = S.folders.map(function (f) {
      var nm = ROLE_NAME[f.role] || f.name;
      return '<li data-id="' + esc(f.id) + '" class="' + (f.id === S.folder ? 'sel' : '') + '" title="' + esc(nm) + '">' + ic(f.role || 'folder') +
        '<span class="mx-fname">' + esc(nm) + '</span>' + (f.unread ? '<span class="mx-count">' + f.unread + '</span>' : '') + '</li>';
    }).join('');
  }
  $('mx-folders').onclick = function (e) { var li = e.target.closest('li'); if (li) { S.q = ''; $('mx-q').value = ''; openFolder(li.dataset.id); } };

  /* ---------- רשימת הודעות ---------- */
  async function openFolder(id, offset) {
    S.folder = id || 'INBOX'; S.offset = offset || 0; S.view = 'list';
    renderFolders();
    loading();
    try {
      var r = await B.call('/mail/list', { folder: S.folder, offset: S.offset, limit: S.limit, q: S.q }, { timeoutMs: 90000 });
      S.items = r.items || []; S.total = r.total || 0;
      renderList();
    } catch (e) {
      state('<p>' + esc(errMsg(e)) + '</p><button class="mx-btn" id="mx-retry">ניסיון חוזר</button>' +
        (e && (e.code === 'auth_failed' || e.code === 'no_account') ? ' <button class="mx-btn tx" id="mx-fix">הגדרת החשבון</button>' : ''));
      $('mx-retry').onclick = function () { openFolder(S.folder, S.offset); };
      if ($('mx-fix')) $('mx-fix').onclick = setup;
    }
  }
  function renderList() {
    var from = S.total ? S.offset + 1 : 0, to = Math.min(S.offset + S.limit, S.total);
    var bar = '<div class="mx-bar"><button class="mx-ib" id="mx-r2" title="רענון">' + ic('refresh') + '</button>' +
      (S.q ? '<span>תוצאות עבור "' + esc(S.q) + '"</span>' : '') + '<span class="mx-sp"></span>' +
      '<span>' + from + '–' + to + ' מתוך ' + S.total + '</span>' +
      '<button class="mx-ib" id="mx-pv" title="חדשות יותר"' + (S.offset ? '' : ' disabled') + '>' + ic('prev') + '</button>' +
      '<button class="mx-ib" id="mx-nx" title="ישנות יותר"' + (to < S.total ? '' : ' disabled') + '>' + ic('next') + '</button></div>';
    var rows = S.items.length ? S.items.map(function (m, i) {
      return '<div class="mx-row' + (m.seen ? '' : ' unread') + '" data-i="' + i + '">' +
        '<button class="mx-star' + (m.flagged ? ' on' : '') + '" data-star="' + i + '" title="כוכב">' + ic('starred') + '</button>' +
        '<span class="mx-from">' + esc(m.from || '(ללא שולח)') + '</span>' +
        '<span class="mx-line">' + esc(m.subject || '(ללא נושא)') + (m.preview ? '<span class="mx-prev"> — ' + esc(m.preview) + '</span>' : '') + '</span>' +
        '<span class="mx-clip">' + (m.attach ? ic('clip') : '') + '</span>' +
        '<span class="mx-date">' + esc(fmtDate(m.date)) + '</span></div>';
    }).join('') : '<div class="mx-state">' + (S.q ? 'לא נמצאו הודעות' : 'אין הודעות בתיקייה זו') + '</div>';
    $('mx-main').innerHTML = bar + '<div class="mx-list" id="mx-list">' + rows + '</div>';
    $('mx-r2').onclick = refresh;
    $('mx-pv').onclick = function () { openFolder(S.folder, Math.max(0, S.offset - S.limit)); };
    $('mx-nx').onclick = function () { openFolder(S.folder, S.offset + S.limit); };
    $('mx-list').onclick = function (e) {
      var st = e.target.closest('[data-star]');
      if (st) { e.stopPropagation(); toggleStar(+st.dataset.star, st); return; }
      var row = e.target.closest('.mx-row'); if (row) readMsg(S.items[+row.dataset.i]);
    };
  }
  async function toggleStar(i, el) {
    var m = S.items[i]; m.flagged = !m.flagged; el.classList.toggle('on', m.flagged);
    try { await B.call('/mail/flag', { folder: S.folder, uid: m.uid, flagged: m.flagged }); }
    catch (e) { m.flagged = !m.flagged; el.classList.toggle('on', m.flagged); toast(errMsg(e)); }
  }
  function refresh() { loadFolders(); if (S.view === 'list') openFolder(S.folder, S.offset); else if (S.msg) readMsg(S.msg); }

  /* ---------- קריאת הודעה ---------- */
  /* HTML של מייל: בלי סקריפטים (iframe sandbox ריק), ובלי תמונות מרוחקות עד שהמשתמש מבקש (פרטיות: "פיקסל מעקב") */
  function cleanHtml(html, allowImages) {
    var doc = new DOMParser().parseFromString(html, 'text/html');
    doc.querySelectorAll('script,iframe,object,embed,form,link[rel=import],meta[http-equiv]').forEach(function (n) { n.remove(); });
    var blocked = 0;
    doc.querySelectorAll('*').forEach(function (n) {
      Array.prototype.slice.call(n.attributes).forEach(function (a) { if (/^on/i.test(a.name)) n.removeAttribute(a.name); });
      if (!allowImages && n.tagName === 'IMG' && /^https?:/i.test(n.getAttribute('src') || '')) { n.removeAttribute('src'); n.removeAttribute('srcset'); blocked++; }
      if (!allowImages && n.style && /url\(/i.test(n.getAttribute('style') || '')) { n.style.backgroundImage = 'none'; }
      if (n.tagName === 'A') n.setAttribute('target', '_blank');
    });
    var css = getComputedStyle(document.documentElement);
    var base = '<style>body{font-family:' + css.getPropertyValue('--font-ui') + ';color:' + css.getPropertyValue('--color-on-surface') +
      ';background:' + css.getPropertyValue('--color-surface') + ';margin:0.75em;overflow-wrap:anywhere}img{max-width:100%;height:auto}</style>';
    return { html: '<!DOCTYPE html><html><head><meta charset="utf-8"><base target="_blank">' + base + '</head><body dir="auto">' + doc.body.innerHTML + '</body></html>', blocked: blocked };
  }
  async function readMsg(m, allowImages) {
    if (!m) return;
    S.view = 'read'; S.msg = m;
    loading('פותח הודעה…');
    var d;
    try { d = await B.call('/mail/read', { folder: S.folder, uid: m.uid }, { timeoutMs: 90000 }); }
    catch (e) { state('<p>' + esc(errMsg(e)) + '</p><button class="mx-btn" id="mx-back2">חזרה</button>'); $('mx-back2').onclick = function () { openFolder(S.folder, S.offset); }; return; }
    if (!m.seen) { m.seen = true; var f = S.folders.find(function (x) { return x.id === S.folder; }); if (f && f.unread) { f.unread--; renderFolders(); } }
    var who = d.from || d.fromAddress || '?';
    var body, blocked = 0;
    if (d.html) { var c = cleanHtml(d.html, allowImages); blocked = c.blocked; body = '<iframe class="mx-frame" id="mx-frame" sandbox="allow-popups allow-popups-to-escape-sandbox"></iframe>'; S._src = c.html; }
    else body = '<div class="mx-text">' + esc(d.text || '') + '</div>';
    $('mx-main').innerHTML =
      '<div class="mx-bar"><button class="mx-ib" id="mx-back" title="חזרה לרשימה">' + ic('back') + '</button><span class="mx-sp"></span>' +
      '<button class="mx-ib" id="mx-unread" title="סימון כלא נקרא">' + ic('mail') + '</button></div>' +
      '<div class="mx-read"><h1>' + esc(d.subject || '(ללא נושא)') + '</h1>' +
      '<div class="mx-hdr"><div class="mx-ava">' + esc(who.trim().charAt(0) || '?') + '</div><div class="mx-who"><b>' + esc(who) + '</b> ' +
      (d.fromAddress && d.fromAddress !== who ? '<small style="display:inline">&lt;' + esc(d.fromAddress) + '&gt;</small>' : '') +
      '<small>אל: ' + esc(d.to) + (d.cc ? ' · עותק: ' + esc(d.cc) : '') + '</small></div><small>' + esc(fmtDate(d.date, true)) + '</small></div>' +
      (blocked ? '<div class="mx-imgs">' + blocked + ' תמונות מרוחקות הוסתרו (מטעמי פרטיות). <button class="mx-btn tx" id="mx-showimg">הצגת התמונות</button></div>' : '') +
      body +
      (d.attachments && d.attachments.length ? '<div class="mx-atts">' + d.attachments.map(function (a) {
        return '<button class="mx-att" data-a="' + a.index + '" title="שמירה בתיקיית ההורדות">' + ic('file') + '<span>' + esc(a.name) + '</span></button>';
      }).join('') + '</div>' : '') + '</div>';
    var fr = $('mx-frame');
    if (fr) {
      fr.srcdoc = S._src;
      fr.onload = function () { try { fr.style.height = (fr.contentDocument.documentElement.scrollHeight + 24) + 'px'; } catch (e) { fr.style.height = '70vh'; } };
    }
    $('mx-back').onclick = function () { openFolder(S.folder, S.offset); };
    $('mx-unread').onclick = async function () {
      try { await B.call('/mail/flag', { folder: S.folder, uid: m.uid, seen: false }); m.seen = false; toast('סומן כלא נקרא'); openFolder(S.folder, S.offset); }
      catch (e) { toast(errMsg(e)); }
    };
    if ($('mx-showimg')) $('mx-showimg').onclick = function () { readMsg(m, true); };
    var atts = document.querySelector('.mx-atts');
    if (atts) atts.onclick = async function (e) {
      var b = e.target.closest('[data-a]'); if (!b) return;
      b.disabled = true;
      try {
        var r = await B.call('/mail/attachment', { folder: S.folder, uid: m.uid, index: +b.dataset.a }, { timeoutMs: 120000 });
        toast('נשמר: ' + r.path);
        openInExplorer(r.path);
      } catch (err) { toast(errMsg(err)); }
      b.disabled = false;
    };
  }

  /* ---------- עזרים ---------- */
  async function openUrl(url) {
    var names = ['app.openUrl', 'app.open_url'];
    for (var i = 0; i < names.length; i++) {
      try { var r = await window.Otzaria.call(names[i], { url: url }); if (!r || r.success !== false) return; } catch (e) { /* הבא */ }
    }
    toast(url);
  }
  /* פתיחת התיקייה בסייר הקבצים (אם מותקן), כדי לפתוח את הקובץ שנשמר */
  function openInExplorer(path) {
    var dir = String(path).replace(/[\\/][^\\/]*$/, '');
    try { window.Otzaria.call('plugin.openOther', { pluginId: 'com.chadbedera.fileexplorer', param: { path: dir } }).catch(function () {}); } catch (e) { /* אין סייר */ }
  }

  /* ---------- אתחול ---------- */
  $('mx-menu').innerHTML = ic('menu'); $('mx-refresh').innerHTML = ic('refresh'); $('mx-settings').innerHTML = ic('gear');
  document.querySelector('.mx-sic').innerHTML = ic('search');
  $('mx-menu').onclick = function () { $('mx-side').classList.toggle('collapsed'); };
  $('mx-refresh').onclick = refresh;
  $('mx-settings').onclick = function () { if (S.acct) setup(); };
  $('mx-q').addEventListener('keydown', function (e) { if (e.key === 'Enter') { S.q = $('mx-q').value.trim(); openFolder(S.folder, 0); } });
  $('mx-q').addEventListener('search', function () { if (!$('mx-q').value && S.q) { S.q = ''; openFolder(S.folder, 0); } });
  var booted = false;
  function boot(p) {
    if (booted) return; booted = true;
    if (p && p.theme) applyTheme(p.theme);
    if (p && p.plugin && p.plugin.version) VERSION = p.plugin.version;
    connect();
  }
  if (window.Otzaria) {
    window.Otzaria.on('plugin.boot', boot);
    window.Otzaria.on('theme.changed', applyTheme);
  } else {
    state('התוסף פועל רק בתוך אוצריא.');
  }
})();
