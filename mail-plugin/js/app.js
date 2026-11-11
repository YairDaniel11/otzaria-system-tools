/* דואר — תוסף לאוצריא (חד בדרא, 11.2026). קבלה ושליחה דרך התוכנה "אחת שאלתי" (/mail/*).
   עובד גם בלי אחת שאלתי ובלי רשת: דואר שמור (IndexedDB), טיוטות, אנשי קשר ותור יוצא —
   מה שנכתב נמסר לאחת שאלתי כשהיא פועלת, והיא שולחת בזמן שנקבע או כשהרשת חוזרת (גם כשהתוסף סגור).
   התוסף לא פונה לאינטרנט בעצמו; הסיסמה שמורה רק באחת שאלתי (מוצפנת). */
(function () {
  'use strict';
  var MX = window.MX, B = MX.Bridge, DBS = MX.store;
  var $ = function (id) { return document.getElementById(id); };
  var LS_SET = 'mx.settings', LS_ACCT = 'mx.acct', LS_FOLDERS = 'mx.folders';
  var DEF = { keep: 300, cacheBodies: true, prefetch: 30, attMaxMB: 5, attTotalMB: 300, syncMin: 10, autoContacts: false, signature: '', name: '' };
  var S = {
    set: load(LS_SET, DEF), acct: load(LS_ACCT, null), folders: load(LS_FOLDERS, []),
    view: 'folder', folder: 'INBOX', offset: 0, limit: 50, q: '', items: [], total: 0, msg: null,
    bridge: false, live: false, syncing: false, contacts: []
  };
  S.set = Object.assign({}, DEF, S.set);
  function load(k, d) { try { var v = JSON.parse(localStorage.getItem(k)); return v == null ? d : v; } catch (e) { return d; } }
  function save(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { /* מלא */ } }

  /* ---------- אייקונים ---------- */
  var P = {
    menu: 'M4 7h16M4 12h16M4 17h16', search: 'M10.5 4.5a6 6 0 1 1 0 12 6 6 0 0 1 0-12zM15 15l5 5',
    refresh: 'M19 12a7 7 0 1 1-2.05-4.95M19 4.5V8h-3.5', gear: 'M12 9a3 3 0 1 1 0 6 3 3 0 0 1 0-6zM12 3.5v2M12 18.5v2M3.5 12h2M18.5 12h2M6 6l1.4 1.4M16.6 16.6L18 18M6 18l1.4-1.4M16.6 7.4L18 6',
    inbox: 'M4 13l2.5-7h11L20 13v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1zM4 13h4.5l1 2h5l1-2H20', sent: 'M4 12l16-7-6 15-2.5-6.5zM11.5 13.5L20 5',
    drafts: 'M5 19h14M7 15l9-9 2 2-9 9H7z', junk: 'M12 4l8.5 15h-17zM12 10v4M12 16.5v.5', trash: 'M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12',
    starred: 'M12 4l2.4 5 5.4.6-4 3.7 1.1 5.4L12 16l-4.9 2.7 1.1-5.4-4-3.7 5.4-.6z', all: 'M5 6h14v4H5zM6 10v8h12v-8M10 13.5h4',
    important: 'M5 6h10l4 6-4 6H5l4-6z', folder: 'M4 7a1 1 0 0 1 1-1h4l2 2h8a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z',
    back: 'M14 6l6 6-6 6M20 12H5', prev: 'M9 6l6 6-6 6', next: 'M15 6l-6 6 6 6',
    clip: 'M16 8l-6.5 6.5a2 2 0 0 0 2.8 2.8L19 10.6a4 4 0 0 0-5.7-5.7L6.6 11.6a6 6 0 0 0 8.5 8.5L20 15',
    file: 'M7 3.5h7l4 4v13H7zM14 3.5v4h4', mail: 'M4 6h16v12H4zM4 7l8 6 8-6', pen: 'M5 19l1-4L15.5 5.5l3 3L9 18zM13.5 7.5l3 3',
    outbox: 'M12 15V4M8 8l4-4 4 4M5 13v6h14v-6', people: 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM3.5 19c.6-3 2.8-5 5.5-5s4.9 2 5.5 5M16 11a2.5 2.5 0 1 0 0-5M17 14c2 .4 3.3 2.2 3.6 5',
    reply: 'M10 7L5 12l5 5M5 12h9a5 5 0 0 1 5 5', fwd: 'M14 7l5 5-5 5M19 12h-9a5 5 0 0 0-5 5', close: 'M6 6l12 12M18 6L6 18',
    clock: 'M12 4a8 8 0 1 1 0 16 8 8 0 0 1 0-16zM12 8v4l3 2', plus: 'M12 5v14M5 12h14', cloudoff: 'M4 4l16 16M7 17h10a3.5 3.5 0 0 0 1.4-6.7M9.5 6.4A5 5 0 0 1 16 9'
  };
  function ic(n) { return '<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="' + (P[n] || P.folder) + '"/></svg>'; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  var tt;
  function toast(m) { var t = $('mx-toast'); t.textContent = m; t.classList.add('on'); clearTimeout(tt); tt = setTimeout(function () { t.classList.remove('on'); }, 4200); }
  function errMsg(e) { return (e && e.message) || 'שגיאה'; }
  function mb(n) { return n < 1048576 ? Math.max(1, Math.round(n / 1024)) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }
  MX.ui = { toast: toast }; MX.t = function (s) { return s; };

  /* ---------- ערכת הנושא ---------- */
  function kebab(k) { return k.replace(/[A-Z]/g, function (c) { return '-' + c.toLowerCase(); }); }
  function applyTheme(t) {
    if (!t || !t.colorScheme) return;
    var r = document.documentElement.style;
    Object.keys(t.colorScheme).forEach(function (k) { var v = t.colorScheme[k]; if (typeof v === 'string' && /^#/.test(v)) r.setProperty('--color-' + kebab(k), v); });
    var f = t.typography && t.typography.uiFontFamily;
    if (f) r.setProperty('--font-ui', "'" + f + "', 'Segoe UI', system-ui, sans-serif");
  }
  function fmtDate(iso, full) {
    var d = new Date(iso); if (isNaN(d)) return '';
    var now = new Date();
    if (full) return d.toLocaleString('he-IL', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
    if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString('he-IL', { hour: '2-digit', minute: '2-digit' });
    if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString('he-IL', { day: 'numeric', month: 'short' });
    return d.toLocaleDateString('he-IL');
  }
  function main(html) { $('mx-main').innerHTML = html; }
  function state(html) { main('<div class="mx-state">' + html + '</div>'); }

  /* ---------- מצב חיבור ---------- */
  function setStatus() {
    var el = $('mx-status');
    if (S.live) { el.hidden = true; return; }
    el.hidden = false;
    el.innerHTML = ic('cloudoff') + '<span>' + (S.bridge ? 'אין חיבור לשרת הדואר — מוצג דואר שמור. מה שתכתבו יישלח כשהרשת תחזור.' :
      'התוכנה "אחת שאלתי" אינה פועלת — מוצג דואר שמור. אפשר לקרוא, לכתוב טיוטות ולתזמן שליחה.') + '</span>';
  }
  async function tryConnect(quiet) {
    var ok = false;
    try { ok = !!(await B.probe()); } catch (e) { ok = false; }
    S.bridge = ok;
    if (!ok) { S.live = false; setStatus(); return false; }
    try {
      var a = await B.call('/mail/account/get', {});
      if (a && a.locked) { S.live = false; setStatus(); if (!quiet) toast('הדואר חסום במצב נעילה'); return false; }
      if (a && a.configured) { S.acct = a; save(LS_ACCT, a); } else { S.acct = null; save(LS_ACCT, null); }
    } catch (e) {
      S.bridge = !(e && (e.code === 'not_found' || /404/.test(e.message))) && S.bridge;
      S.live = false; setStatus(); return false;
    }
    if (!S.acct) { S.live = false; setStatus(); return false; }
    $('mx-acct').textContent = S.acct.email;
    await flushLocalOutbox();
    var f = await loadFolders(true);
    S.live = f; setStatus();
    return f;
  }

  /* ---------- תיקיות ---------- */
  var ROLE_ORDER = { inbox: 0, starred: 1, important: 2, sent: 3, drafts: 4, all: 5, junk: 6, trash: 7 };
  var ROLE_NAME = { inbox: 'דואר נכנס', starred: 'מסומנים בכוכב', important: 'חשוב', sent: 'דואר יוצא', drafts: 'טיוטות (בשרת)', all: 'כל הדואר', junk: 'ספאם', trash: 'אשפה' };
  async function loadFolders(fromServer) {
    if (fromServer) {
      try {
        var r = await B.call('/mail/folders', {}, { timeoutMs: 60000 });
        S.folders = (r && r.folders || []).sort(function (a, b) {
          var x = a.role in ROLE_ORDER ? ROLE_ORDER[a.role] : 50, y = b.role in ROLE_ORDER ? ROLE_ORDER[b.role] : 50;
          return x - y || String(a.name).localeCompare(String(b.name), 'he');
        });
        save(LS_FOLDERS, S.folders);
        renderSide();
        return true;
      } catch (e) { renderSide(); return false; }
    }
    renderSide();
    return false;
  }
  async function renderSide() {
    if (!S.folders.length) S.folders = [{ id: 'INBOX', name: 'INBOX', role: 'inbox', unread: 0 }];
    var drafts = 0, out = 0;
    try { drafts = (await DBS.all('drafts')).length; out = (await DBS.all('outbox')).length; } catch (e) { /* בלי מונה */ }
    function li(view, id, icon, name, count) {
      var sel = S.view === view && (view !== 'folder' || S.folder === id);
      return '<li data-view="' + view + '" data-id="' + esc(id || '') + '" class="' + (sel ? 'sel' : '') + '" title="' + esc(name) + '">' + ic(icon) +
        '<span class="mx-fname">' + esc(name) + '</span>' + (count ? '<span class="mx-count">' + count + '</span>' : '') + '</li>';
    }
    $('mx-folders').innerHTML = S.folders.map(function (f) { return li('folder', f.id, f.role || 'folder', ROLE_NAME[f.role] || f.name, f.unread); }).join('') +
      '<li class="mx-sep"></li>' + li('drafts', '', 'pen', 'טיוטות במחשב', drafts) + li('outbox', '', 'outbox', 'ממתין לשליחה', out) +
      li('contacts', '', 'people', 'אנשי קשר', 0) + li('settings', '', 'gear', 'הגדרות', 0);
  }
  $('mx-folders').onclick = function (e) {
    var li = e.target.closest('li[data-view]'); if (!li) return;
    var v = li.dataset.view;
    if (v === 'folder') { S.q = ''; $('mx-q').value = ''; openFolder(li.dataset.id); }
    else if (v === 'drafts') showDrafts(); else if (v === 'outbox') showOutbox(); else if (v === 'contacts') showContacts(); else showSettings();
  };

  /* ---------- רשימת הודעות (מהמטמון, ואז מהשרת) ---------- */
  async function openFolder(id, offset) {
    S.view = 'folder'; S.folder = id || 'INBOX'; S.offset = offset || 0;
    renderSide();
    await renderCached();
    if (S.live) syncFolder(false);
  }
  async function renderCached() {
    var all = [];
    try { all = await DBS.list(S.folder); } catch (e) { all = []; }
    if (S.q) {
      var q = S.q.toLowerCase();
      all = all.filter(function (m) { return [m.from, m.subject, m.preview, m.text].some(function (x) { return x && String(x).toLowerCase().indexOf(q) >= 0; }); });
    }
    S.total = all.length; S.items = all.slice(S.offset, S.offset + S.limit);
    S.fromCache = true;
    renderList();
  }
  async function syncFolder(quiet) {
    if (S.syncing) return; S.syncing = true;
    try {
      var r = await B.call('/mail/list', { folder: S.folder, offset: S.q ? S.offset : 0, limit: S.q ? S.limit : Math.max(S.limit, Math.min(S.set.keep, 200)), q: S.q }, { timeoutMs: 120000 });
      if (!S.q) {
        await DBS.saveList(S.folder, r.items || [], S.set.keep);
        if (S.view === 'folder' && !S.msg) await renderCached();
        prefetch(r.items || []);
      } else if (S.view === 'folder') {
        S.items = r.items || []; S.total = r.total || 0; S.fromCache = false; renderList();
      }
    } catch (e) {
      if (!quiet) toast(errMsg(e));
      if (e && (e.code === 'offline' || e.code === 'unreachable' || e.code === 'timeout')) { S.live = false; setStatus(); }
    } finally { S.syncing = false; }
  }
  function renderList() {
    if (S.view !== 'folder') return;
    S.msg = null;
    var from = S.total ? S.offset + 1 : 0, to = Math.min(S.offset + S.limit, S.total);
    var rows = S.items.length ? S.items.map(function (m, i) {
      return '<div class="mx-row' + (m.seen ? '' : ' unread') + '" data-i="' + i + '">' +
        '<button class="mx-star' + (m.flagged ? ' on' : '') + '" data-star="' + i + '" title="כוכב">' + ic('starred') + '</button>' +
        '<span class="mx-from">' + esc(m.from || '(ללא שולח)') + '</span>' +
        '<span class="mx-line">' + esc(m.subject || '(ללא נושא)') + (m.preview ? '<span class="mx-prev"> — ' + esc(m.preview) + '</span>' : '') + '</span>' +
        '<span class="mx-clip">' + (m.attach ? ic('clip') : '') + (m.body ? '<span class="mx-saved" title="שמור במחשב לקריאה בלי חיבור"></span>' : '') + '</span>' +
        '<span class="mx-date">' + esc(fmtDate(m.date)) + '</span></div>';
    }).join('') : '<div class="mx-state">' + (S.q ? 'לא נמצאו הודעות' : (S.live || S.acct ? 'אין הודעות שמורות בתיקייה זו' : 'עדיין לא חובר חשבון דואר')) +
      (!S.acct && S.bridge ? '<p><button class="mx-btn" id="mx-setup1">חיבור חשבון</button></p>' : '') + '</div>';
    main('<div class="mx-bar"><button class="mx-ib" id="mx-r2" title="רענון">' + ic('refresh') + '</button>' +
      (S.q ? '<span>תוצאות עבור "' + esc(S.q) + '"' + (S.fromCache ? ' (בדואר השמור)' : '') + '</span>' : '') + '<span class="mx-sp"></span>' +
      '<span>' + from + '–' + to + ' מתוך ' + S.total + '</span>' +
      '<button class="mx-ib" id="mx-pv" title="חדשות יותר"' + (S.offset ? '' : ' disabled') + '>' + ic('prev') + '</button>' +
      '<button class="mx-ib" id="mx-nx" title="ישנות יותר"' + (to < S.total ? '' : ' disabled') + '>' + ic('next') + '</button></div>' +
      '<div class="mx-list" id="mx-list">' + rows + '</div>');
    $('mx-r2').onclick = refresh;
    if ($('mx-setup1')) $('mx-setup1').onclick = setup;
    $('mx-pv').onclick = function () { S.offset = Math.max(0, S.offset - S.limit); S.q && !S.fromCache ? syncFolder() : renderCached(); };
    $('mx-nx').onclick = function () { S.offset += S.limit; S.q && !S.fromCache ? syncFolder() : renderCached(); };
    $('mx-list').onclick = function (e) {
      var st = e.target.closest('[data-star]');
      if (st) { e.stopPropagation(); toggleStar(S.items[+st.dataset.star], st); return; }
      var row = e.target.closest('.mx-row'); if (row) readMsg(S.items[+row.dataset.i]);
    };
  }
  async function toggleStar(m, el) {
    if (!S.live) { toast('אפשר לסמן בכוכב כשיש חיבור'); return; }
    m.flagged = !m.flagged; el.classList.toggle('on', m.flagged);
    try { await B.call('/mail/flag', { folder: S.folder, uid: m.uid, flagged: m.flagged }); DBS.put('messages', Object.assign({}, m, { key: DBS.msgKey(S.folder, m.uid), folder: S.folder })); }
    catch (e) { m.flagged = !m.flagged; el.classList.toggle('on', m.flagged); toast(errMsg(e)); }
  }
  async function refresh() {
    if (!S.live) { toast('מתחבר…'); await tryConnect(); }
    if (S.live) { await loadFolders(true); if (S.view === 'folder') { if (S.msg) readMsg(S.msg); else syncFolder(); } }
    else toast(S.bridge ? 'אין חיבור לשרת הדואר' : 'התוכנה "אחת שאלתי" אינה פועלת');
  }

  /* ---------- שמירה לקריאה בלי חיבור ---------- */
  async function fetchBody(folder, m) {
    var d = await B.call('/mail/read', { folder: folder, uid: m.uid }, { timeoutMs: 90000 });
    var row = Object.assign({}, m, { key: DBS.msgKey(folder, m.uid), folder: folder, seen: true, body: S.set.cacheBodies ? d : null, text: (d.text || '').slice(0, 4000) });
    if (S.set.cacheBodies) { await DBS.put('messages', row); cacheAtts(folder, m.uid, d.attachments || []); }
    return d;
  }
  async function cacheAtts(folder, uid, atts) {
    if (!S.set.attMaxMB || !atts.length) return;
    var total = 0; try { total = await DBS.attsSize(); } catch (e) { /* 0 */ }
    for (var i = 0; i < atts.length; i++) {
      var key = DBS.msgKey(folder, uid) + '\u0001' + atts[i].index;
      try {
        if (await DBS.get('atts', key)) continue;
        if (total > S.set.attTotalMB * 1048576) return;
        var r = await B.call('/mail/attachment-data', { folder: folder, uid: uid, index: atts[i].index, max: S.set.attMaxMB * 1048576 }, { timeoutMs: 120000 });
        if (r && r.data) { await DBS.put('atts', { key: key, msg: DBS.msgKey(folder, uid), name: r.name, type: r.type, size: r.size, data: r.data }); total += r.size; }
      } catch (e) { return; }
    }
  }
  async function prefetch(items) {
    if (!S.set.cacheBodies || !S.set.prefetch) return;
    var folder = S.folder, n = 0;
    for (var i = 0; i < items.length && n < S.set.prefetch; i++) {
      var k = DBS.msgKey(folder, items[i].uid), c = await DBS.get('messages', k);
      if (c && c.body) continue;
      if (!S.live) return;
      try { await fetchBody(folder, Object.assign({}, items[i], { seen: items[i].seen })); n++; } catch (e) { return; }
    }
    if (n && S.view === 'folder' && !S.msg) renderCached();
  }

  /* ---------- קריאת הודעה ---------- */
  function cleanHtml(html, allowImages) {
    var doc = new DOMParser().parseFromString(html, 'text/html');
    doc.querySelectorAll('script,iframe,object,embed,form,link[rel=import],meta[http-equiv]').forEach(function (n) { n.remove(); });
    var blocked = 0;
    doc.querySelectorAll('*').forEach(function (n) {
      Array.prototype.slice.call(n.attributes).forEach(function (a) { if (/^on/i.test(a.name)) n.removeAttribute(a.name); });
      if (!allowImages && n.tagName === 'IMG' && /^https?:/i.test(n.getAttribute('src') || '')) { n.removeAttribute('src'); n.removeAttribute('srcset'); blocked++; }
      if (!allowImages && /url\(/i.test(n.getAttribute('style') || '')) n.style.backgroundImage = 'none';
    });
    var css = getComputedStyle(document.documentElement);
    var base = '<style>body{font-family:' + css.getPropertyValue('--font-ui') + ';color:' + css.getPropertyValue('--color-on-surface') +
      ';background:' + css.getPropertyValue('--color-surface') + ';margin:0.75em;overflow-wrap:anywhere}img{max-width:100%;height:auto}</style>';
    return { html: '<!DOCTYPE html><html><head><meta charset="utf-8"><base target="_blank">' + base + '</head><body dir="auto">' + doc.body.innerHTML + '</body></html>', blocked: blocked };
  }
  async function readMsg(m, allowImages) {
    if (!m) return;
    S.msg = m;
    main('<div class="mx-state"><div class="mx-spin"></div>פותח הודעה…</div>');
    var folder = S.folder, d = null, cached = null;
    try { cached = await DBS.get('messages', DBS.msgKey(folder, m.uid)); } catch (e) { cached = null; }
    if (cached && cached.body) d = cached.body;
    else if (S.live) {
      try { d = await fetchBody(folder, m); }
      catch (e) { state('<p>' + esc(errMsg(e)) + '</p><button class="mx-btn" id="mx-back2">חזרה</button>'); $('mx-back2').onclick = function () { openFolder(S.folder, S.offset); }; return; }
    } else {
      state(ic('cloudoff') + '<p>ההודעה הזו לא נשמרה במחשב, ולכן אי אפשר לפתוח אותה בלי חיבור.</p><p class="mx-hint">בהגדרות אפשר לבחור לשמור תוכן של הודעות לקריאה בלי חיבור.</p><button class="mx-btn" id="mx-back2">חזרה</button>');
      $('mx-back2').onclick = function () { openFolder(S.folder, S.offset); }; return;
    }
    if (!m.seen) {
      m.seen = true;
      if (S.live && cached && cached.body) B.call('/mail/flag', { folder: folder, uid: m.uid, seen: true }).catch(function () {});
      var f = S.folders.find(function (x) { return x.id === folder; }); if (f && f.unread) { f.unread--; save(LS_FOLDERS, S.folders); renderSide(); }
      DBS.put('messages', Object.assign({}, cached || m, { key: DBS.msgKey(folder, m.uid), folder: folder, seen: true }));
    }
    var who = d.from || d.fromAddress || '?', body, blocked = 0;
    if (d.html) { var c = cleanHtml(d.html, allowImages); blocked = c.blocked; body = '<iframe class="mx-frame" id="mx-frame" sandbox="allow-popups allow-popups-to-escape-sandbox"></iframe>'; S._src = c.html; }
    else body = '<div class="mx-text">' + esc(d.text || '') + '</div>';
    main('<div class="mx-bar"><button class="mx-ib" id="mx-back" title="חזרה לרשימה">' + ic('back') + '</button><span class="mx-sp"></span>' +
      '<button class="mx-ib" id="mx-reply" title="תשובה">' + ic('reply') + '</button><button class="mx-ib" id="mx-fwd" title="העברה">' + ic('fwd') + '</button>' +
      '<button class="mx-ib" id="mx-unread" title="סימון כלא נקרא">' + ic('mail') + '</button></div>' +
      '<div class="mx-read"><h1>' + esc(d.subject || '(ללא נושא)') + '</h1>' +
      '<div class="mx-hdr"><div class="mx-ava">' + esc(who.trim().charAt(0) || '?') + '</div><div class="mx-who"><b>' + esc(who) + '</b> ' +
      (d.fromAddress && d.fromAddress !== who ? '<small style="display:inline">&lt;' + esc(d.fromAddress) + '&gt;</small>' : '') +
      ' <button class="mx-btn tx sm" id="mx-addc" title="הוספה לאנשי הקשר">' + ic('plus') + '</button>' +
      '<small>אל: ' + esc(d.to) + (d.cc ? ' · עותק: ' + esc(d.cc) : '') + '</small></div><small>' + esc(fmtDate(d.date, true)) + '</small></div>' +
      (blocked ? '<div class="mx-imgs">' + blocked + ' תמונות מרוחקות הוסתרו (מטעמי פרטיות).' + (S.live || navigator.onLine ? ' <button class="mx-btn tx" id="mx-showimg">הצגת התמונות</button>' : '') + '</div>' : '') +
      body + (d.attachments && d.attachments.length ? '<div class="mx-atts">' + d.attachments.map(function (a) {
        return '<button class="mx-att" data-a="' + a.index + '" title="פתיחה / שמירה">' + ic('file') + '<span>' + esc(a.name) + '</span></button>';
      }).join('') + '</div>' : '') + '</div>');
    var fr = $('mx-frame');
    if (fr) { fr.srcdoc = S._src; fr.onload = function () { try { fr.style.height = (fr.contentDocument.documentElement.scrollHeight + 24) + 'px'; } catch (e) { fr.style.height = '70vh'; } }; }
    $('mx-back').onclick = function () { S.msg = null; renderCached(); };
    $('mx-reply').onclick = function () { compose(replyDraft(d, false)); };
    $('mx-fwd').onclick = function () { compose(replyDraft(d, true)); };
    $('mx-addc').onclick = function () { editContact({ name: d.from, emails: [d.fromAddress].filter(Boolean), phones: [] }); };
    $('mx-unread').onclick = async function () {
      if (!S.live) { toast('אפשר לסמן כלא נקרא כשיש חיבור'); return; }
      try { await B.call('/mail/flag', { folder: folder, uid: m.uid, seen: false }); m.seen = false; DBS.put('messages', Object.assign({}, cached || m, { key: DBS.msgKey(folder, m.uid), folder: folder, seen: false })); toast('סומן כלא נקרא'); S.msg = null; renderCached(); }
      catch (e) { toast(errMsg(e)); }
    };
    if ($('mx-showimg')) $('mx-showimg').onclick = function () { readMsg(m, true); };
    var atts = document.querySelector('.mx-atts');
    if (atts) atts.onclick = async function (e) {
      var b = e.target.closest('[data-a]'); if (!b) return;
      var idx = +b.dataset.a, key = DBS.msgKey(folder, m.uid) + '\u0001' + idx, local = null;
      try { local = await DBS.get('atts', key); } catch (x) { local = null; }
      if (S.live) {
        b.disabled = true;
        try { var r = await B.call('/mail/attachment', { folder: folder, uid: m.uid, index: idx }, { timeoutMs: 120000 }); toast('נשמר: ' + r.path); openInExplorer(r.path); }
        catch (err) { if (local) downloadLocal(local); else toast(errMsg(err)); }
        b.disabled = false;
      } else if (local) downloadLocal(local);
      else toast('הקובץ לא נשמר במחשב (גדול מהמגבלה בהגדרות, או שההודעה נפתחה לפני שנשמר)');
    };
  }
  function downloadLocal(a) {
    try {
      var bin = atob(a.data), u8 = new Uint8Array(bin.length); for (var i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
      var url = URL.createObjectURL(new Blob([u8], { type: a.type || 'application/octet-stream' }));
      var l = document.createElement('a'); l.href = url; l.download = a.name || 'file'; document.body.appendChild(l); l.click(); l.remove();
      setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
      toast('הקובץ נפתח מהעותק השמור במחשב');
    } catch (e) { toast('לא ניתן לפתוח את הקובץ'); }
  }

  /* ---------- כתיבה, טיוטות ותור יוצא ---------- */
  function quote(d) {
    return '\n\n' + (S.set.signature ? '-- \n' + S.set.signature + '\n\n' : '') + 'בתאריך ' + fmtDate(d.date, true) + ', ' + (d.from || d.fromAddress) + ' כתב/ה:\n' +
      (d.text || '').split('\n').map(function (l) { return '> ' + l; }).join('\n');
  }
  function replyDraft(d, fwd) {
    var subj = d.subject || '';
    if (fwd) return { subject: /^(fwd?|הועבר):/i.test(subj) ? subj : 'הועבר: ' + subj, text: '\n\n---------- הודעה שהועברה ----------\nמאת: ' + (d.from || '') + ' <' + (d.fromAddress || '') + '>\nתאריך: ' + fmtDate(d.date, true) + '\nנושא: ' + subj + '\nאל: ' + (d.to || '') + '\n\n' + (d.text || '') };
    return { to: d.replyTo || d.fromAddress || '', subject: /^(re|תשובה):/i.test(subj) ? subj : 'תשובה: ' + subj, text: quote(d),
      inReplyTo: d.messageId ? '<' + d.messageId.replace(/^<|>$/g, '') + '>' : '', references: ((d.references || '') + (d.messageId ? ' <' + d.messageId.replace(/^<|>$/g, '') + '>' : '')).trim() };
  }
  var CMP = null, cmpTimer = null;
  function compose(draft) {
    draft = Object.assign({ id: DBS.uid(), to: '', cc: '', bcc: '', subject: '', text: S.set.signature ? '\n\n-- \n' + S.set.signature : '', attachments: [], sendAt: '' }, draft || {});
    if (draft.text == null) draft.text = '';
    CMP = draft;
    var box = $('mx-compose');
    box.hidden = false;
    box.innerHTML = '<div class="mx-c-head"><span>' + esc(draft.subject || 'הודעה חדשה') + '</span><button class="mx-ib sm" id="mx-c-x" title="סגירה (נשמר כטיוטה)">' + ic('close') + '</button></div>' +
      '<datalist id="mx-dl"></datalist>' +
      '<input class="mx-c-f" id="mx-c-to" list="mx-dl" placeholder="נמענים" dir="auto" value="' + esc(draft.to) + '">' +
      '<input class="mx-c-f" id="mx-c-cc" list="mx-dl" placeholder="עותק" dir="auto" value="' + esc(draft.cc) + '">' +
      '<input class="mx-c-f" id="mx-c-bcc" list="mx-dl" placeholder="עותק מוסתר" dir="auto" value="' + esc(draft.bcc) + '">' +
      '<input class="mx-c-f" id="mx-c-subj" placeholder="נושא" dir="auto" value="' + esc(draft.subject) + '">' +
      '<textarea class="mx-c-body" id="mx-c-body" dir="auto">' + esc(draft.text) + '</textarea>' +
      '<div class="mx-c-atts" id="mx-c-atts"></div>' +
      '<div class="mx-c-foot"><button class="mx-btn" id="mx-c-send">שליחה</button>' +
      '<label class="mx-btn tx" title="תזמון שליחה">' + ic('clock') + '<input type="datetime-local" id="mx-c-at" value="' + esc(draft.sendAt) + '"></label>' +
      '<label class="mx-ib" title="צירוף קובץ">' + ic('clip') + '<input type="file" id="mx-c-file" multiple hidden></label>' +
      '<span class="mx-sp"></span><span class="mx-c-saved" id="mx-c-saved"></span>' +
      '<button class="mx-ib" id="mx-c-del" title="מחיקת הטיוטה">' + ic('trash') + '</button></div>';
    fillContactsList();
    renderCmpAtts();
    ['mx-c-to', 'mx-c-cc', 'mx-c-bcc', 'mx-c-subj', 'mx-c-body', 'mx-c-at'].forEach(function (id) {
      $(id).addEventListener('input', function () { clearTimeout(cmpTimer); cmpTimer = setTimeout(saveDraft, 1200); $('mx-c-saved').textContent = ''; });
    });
    $('mx-c-at').addEventListener('change', function () { $('mx-c-send').textContent = $('mx-c-at').value ? 'תזמון' : 'שליחה'; });
    if (draft.sendAt) $('mx-c-send').textContent = 'תזמון';
    $('mx-c-x').onclick = async function () { await saveDraft(); closeCompose(); };
    $('mx-c-del').onclick = async function () { await DBS.del('drafts', CMP.id); closeCompose(); toast('הטיוטה נמחקה'); renderSide(); if (S.view === 'drafts') showDrafts(); };
    $('mx-c-send').onclick = sendCompose;
    $('mx-c-file').onchange = function (e) {
      Array.prototype.forEach.call(e.target.files, function (f) {
        if (f.size > 20 * 1048576) { toast('"' + f.name + '" גדול מ-20MB'); return; }
        var r = new FileReader();
        r.onload = function () { CMP.attachments.push({ name: f.name, type: f.type, size: f.size, data: String(r.result).split(',')[1] || '' }); renderCmpAtts(); saveDraft(); };
        r.readAsDataURL(f);
      });
      e.target.value = '';
    };
    $('mx-c-to').focus();
  }
  function renderCmpAtts() {
    $('mx-c-atts').innerHTML = CMP.attachments.map(function (a, i) { return '<span class="mx-chip">' + ic('file') + esc(a.name) + ' <small>' + mb(a.size || 0) + '</small><button data-rm="' + i + '" title="הסרה">' + ic('close') + '</button></span>'; }).join('');
    $('mx-c-atts').onclick = function (e) { var b = e.target.closest('[data-rm]'); if (b) { CMP.attachments.splice(+b.dataset.rm, 1); renderCmpAtts(); saveDraft(); } };
  }
  function readCompose() {
    CMP.to = $('mx-c-to').value.trim(); CMP.cc = $('mx-c-cc').value.trim(); CMP.bcc = $('mx-c-bcc').value.trim();
    CMP.subject = $('mx-c-subj').value; CMP.text = $('mx-c-body').value; CMP.sendAt = $('mx-c-at').value; CMP.updated = new Date().toISOString();
  }
  async function saveDraft() {
    if (!CMP || $('mx-compose').hidden) return;
    readCompose();
    if (!CMP.to && !CMP.subject && !CMP.text.replace(/--\s*\n[\s\S]*$/, '').trim() && !CMP.attachments.length) return;
    try { await DBS.put('drafts', CMP); if ($('mx-c-saved')) $('mx-c-saved').textContent = 'נשמר במחשב'; renderSide(); } catch (e) { toast('שמירת הטיוטה נכשלה'); }
  }
  function closeCompose() { clearTimeout(cmpTimer); $('mx-compose').hidden = true; $('mx-compose').innerHTML = ''; CMP = null; }
  function validAddrs(s) { return !s || s.split(/[,;]/).every(function (x) { x = x.trim(); return !x || /[^@\s<>]+@[^@\s<>]+\.[^@\s<>]+/.test(x); }); }
  async function sendCompose() {
    readCompose();
    if (!CMP.to) { toast('חסר נמען'); return; }
    if (!validAddrs(CMP.to) || !validAddrs(CMP.cc) || !validAddrs(CMP.bcc)) { toast('אחת הכתובות אינה תקינה'); return; }
    var at = CMP.sendAt ? new Date(CMP.sendAt) : null;
    if (at && isNaN(at)) at = null;
    var payload = { id: CMP.id, to: CMP.to, cc: CMP.cc, bcc: CMP.bcc, subject: CMP.subject, text: CMP.text, attachments: CMP.attachments, inReplyTo: CMP.inReplyTo || '', references: CMP.references || '' };
    if (at && at > new Date()) payload.sendAt = at.toISOString();
    rememberRecipients(CMP);
    var item = Object.assign({}, payload, { queuedAt: new Date().toISOString() });
    await DBS.put('outbox', item);                      // תמיד קודם במחשב — לא הולך לאיבוד גם אם הגשר נופל באמצע
    await DBS.del('drafts', CMP.id);
    closeCompose();
    var handed = S.bridge ? await handOver(item) : false;
    toast(handed ? (payload.sendAt ? 'תוזמן ל-' + fmtDate(payload.sendAt, true) + '. יישלח גם אם התוסף סגור.' : 'נשלח לתור השליחה. אם אין רשת — יישלח כשתחזור.')
      : 'נשמר בתור במחשב. יישלח כשהתוכנה "אחת שאלתי" תפעל' + (payload.sendAt ? ' ובזמן שנקבע.' : ' ותהיה רשת.'));
    renderSide();
    if (S.view === 'outbox') showOutbox();
  }
  async function handOver(item) {
    try {
      var p = Object.assign({}, item); delete p.queuedAt;
      await B.call(p.sendAt ? '/mail/outbox/add' : '/mail/send', p, { timeoutMs: 120000 });
      await DBS.del('outbox', item.id);
      return true;
    } catch (e) { return false; }
  }
  async function flushLocalOutbox() {
    var items = []; try { items = await DBS.all('outbox'); } catch (e) { return; }
    for (var i = 0; i < items.length; i++) await handOver(items[i]);
    if (items.length) renderSide();
  }
  async function showDrafts() {
    S.view = 'drafts'; S.msg = null; renderSide();
    var d = []; try { d = await DBS.all('drafts'); } catch (e) { d = []; }
    d.sort(function (a, b) { return String(b.updated || '').localeCompare(String(a.updated || '')); });
    main('<div class="mx-bar"><span>טיוטות שנשמרו במחשב (זמינות גם בלי חיבור)</span><span class="mx-sp"></span><button class="mx-btn sm" id="mx-new2">' + ic('pen') + 'כתיבה</button></div>' +
      '<div class="mx-list">' + (d.length ? d.map(function (x, i) {
        return '<div class="mx-row" data-i="' + i + '"><span></span><span class="mx-from">' + esc(x.to || '(ללא נמען)') + '</span><span class="mx-line">' + esc(x.subject || '(ללא נושא)') +
          '<span class="mx-prev"> — ' + esc((x.text || '').slice(0, 120)) + '</span></span><span class="mx-clip">' + (x.attachments && x.attachments.length ? ic('clip') : '') + '</span><span class="mx-date">' + esc(fmtDate(x.updated)) + '</span></div>';
      }).join('') : '<div class="mx-state">אין טיוטות</div>') + '</div>');
    $('mx-new2').onclick = function () { compose(); };
    var l = document.querySelector('.mx-list');
    l.onclick = function (e) { var r = e.target.closest('.mx-row'); if (r) compose(d[+r.dataset.i]); };
  }
  async function showOutbox() {
    S.view = 'outbox'; S.msg = null; renderSide();
    var local = []; try { local = await DBS.all('outbox'); } catch (e) { local = []; }
    var remote = [];
    if (S.bridge) { try { remote = (await B.call('/mail/outbox/list', {})).items || []; } catch (e) { remote = []; } }
    function row(x, where) {
      var when = x.sendAt ? 'מתוזמן ל-' + fmtDate(x.sendAt, true) : (where === 'local' ? 'ממתין לאחת שאלתי' : 'ממתין לרשת');
      return '<div class="mx-row mx-ob"><span>' + ic(where === 'local' ? 'cloudoff' : 'clock') + '</span><span class="mx-from">' + esc(x.to) + '</span><span class="mx-line">' + esc(x.subject || '(ללא נושא)') +
        '<span class="mx-prev"> — ' + esc(when) + (x.lastError ? ' · ניסיון אחרון נכשל: ' + esc(x.lastError) : '') + '</span></span><span></span>' +
        '<button class="mx-btn tx sm" data-cancel="' + esc(x.id) + '" data-where="' + where + '">ביטול</button></div>';
    }
    main('<div class="mx-bar"><span>הודעות שממתינות לשליחה. אחת שאלתי שולחת אותן בזמן שנקבע או כשהרשת חוזרת — גם כשהתוסף סגור.</span></div>' +
      '<div class="mx-list">' + (local.length + remote.length ? local.map(function (x) { return row(x, 'local'); }).join('') + remote.map(function (x) { return row(x, 'bridge'); }).join('') : '<div class="mx-state">אין הודעות ממתינות</div>') + '</div>');
    document.querySelector('.mx-list').onclick = async function (e) {
      var b = e.target.closest('[data-cancel]'); if (!b) return;
      var id = b.dataset.cancel, it = local.find(function (x) { return x.id === id; });
      try {
        if (b.dataset.where === 'local') await DBS.del('outbox', id); else await B.call('/mail/outbox/cancel', { id: id });
        if (it) { await DBS.put('drafts', Object.assign({}, it, { updated: new Date().toISOString() })); toast('השליחה בוטלה; ההודעה הוחזרה לטיוטות'); } else toast('השליחה בוטלה');
      } catch (err) { toast(errMsg(err)); }
      showOutbox();
    };
  }

  /* ---------- אנשי קשר ---------- */
  async function loadContacts() { try { S.contacts = await DBS.all('contacts'); } catch (e) { S.contacts = []; } S.contacts.sort(function (a, b) { return String(a.name || a.emails[0] || '').localeCompare(String(b.name || b.emails[0] || ''), 'he'); }); return S.contacts; }
  async function fillContactsList() {
    await loadContacts();
    var dl = $('mx-dl'); if (!dl) return;
    dl.innerHTML = S.contacts.map(function (c) { return (c.emails || []).map(function (e) { return '<option value="' + esc(c.name ? c.name + ' <' + e + '>' : e) + '">'; }).join(''); }).join('');
  }
  function rememberRecipients(c) {
    if (!S.set.autoContacts) return;
    var list = [c.to, c.cc, c.bcc].join(',').split(/[,;]/).map(function (x) { return x.trim(); }).filter(Boolean).map(function (x) {
      var m = x.match(/^(.*)<([^>]+)>$/); return { name: m ? m[1].replace(/"/g, '').trim() : '', emails: [m ? m[2].trim() : x], phones: [] };
    });
    mergeContacts(list);
  }
  async function mergeContacts(list) {
    await loadContacts();
    var r = MX.contacts.merge(S.contacts, list);
    if (r.rows.length) await DBS.putMany('contacts', r.rows);
    return r;
  }
  async function showContacts(q) {
    S.view = 'contacts'; S.msg = null; renderSide();
    await loadContacts();
    var list = S.contacts;
    if (q) { var s = q.toLowerCase(); list = list.filter(function (c) { return [c.name, c.org].concat(c.emails || [], c.phones || []).some(function (x) { return x && String(x).toLowerCase().indexOf(s) >= 0; }); }); }
    main('<div class="mx-bar"><input class="mx-c-f mx-csearch" id="mx-cq" placeholder="חיפוש באנשי הקשר" value="' + esc(q || '') + '"><span class="mx-sp"></span>' +
      '<button class="mx-btn sm" id="mx-cadd">' + ic('plus') + 'איש קשר</button>' +
      '<label class="mx-btn tx sm">ייבוא (vCard / CSV)<input type="file" id="mx-cimp" accept=".vcf,.csv,text/vcard,text/csv" hidden></label>' +
      '<button class="mx-btn tx sm" id="mx-cexp">ייצוא</button></div>' +
      '<div class="mx-list">' + (list.length ? list.map(function (c, i) {
        return '<div class="mx-row mx-crow" data-i="' + i + '"><span class="mx-ava sm">' + esc((c.name || (c.emails || [''])[0] || '?').trim().charAt(0)) + '</span><span class="mx-from">' + esc(c.name || '') + '</span>' +
          '<span class="mx-line">' + esc((c.emails || []).join(', ')) + '<span class="mx-prev">' + ((c.phones || []).length ? ' · ' + esc(c.phones.join(', ')) : '') + (c.org ? ' · ' + esc(c.org) : '') + '</span></span>' +
          '<button class="mx-ib sm" data-mail="' + i + '" title="כתיבת מייל">' + ic('pen') + '</button><span></span></div>';
      }).join('') : '<div class="mx-state">' + (q ? 'לא נמצאו אנשי קשר' : 'אין עדיין אנשי קשר.') +
        '<p class="mx-hint">ייבוא מגוגל: היכנסו ל-contacts.google.com ← "ייצוא" ← Google CSV או vCard, ואז "ייבוא" כאן. גם קובץ מ-Outlook או מהטלפון (vcf) מתאים. הייבוא נעשה במחשב בלבד, בלי חיבור לחשבון.</p></div>') + '</div>');
    var qi = $('mx-cq'); qi.oninput = function () { clearTimeout(qi._t); qi._t = setTimeout(function () { showContacts(qi.value.trim()); qi = $('mx-cq'); }, 300); };
    if (q) { qi.focus(); qi.setSelectionRange(q.length, q.length); }
    $('mx-cadd').onclick = function () { editContact({ name: '', emails: [], phones: [] }); };
    $('mx-cexp').onclick = function () {
      if (!S.contacts.length) { toast('אין אנשי קשר לייצוא'); return; }
      var url = URL.createObjectURL(new Blob([MX.contacts.toVcf(S.contacts)], { type: 'text/vcard' }));
      var a = document.createElement('a'); a.href = url; a.download = 'אנשי-קשר.vcf'; document.body.appendChild(a); a.click(); a.remove();
      setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
    };
    $('mx-cimp').onchange = function (e) {
      var f = e.target.files[0]; if (!f) return;
      var r = new FileReader();
      r.onload = async function () {
        var parsed = MX.contacts.parse(f.name, String(r.result || ''));
        if (!parsed.length) { toast('לא נמצאו אנשי קשר בקובץ'); return; }
        var res = await mergeContacts(parsed);
        toast('יובאו ' + res.added + ' חדשים, עודכנו ' + res.updated);
        showContacts();
      };
      r.readAsText(f);
    };
    document.querySelector('.mx-list').onclick = function (e) {
      var m = e.target.closest('[data-mail]');
      if (m) { var c = list[+m.dataset.mail]; compose({ to: c.name ? c.name + ' <' + c.emails[0] + '>' : (c.emails || [])[0] || '' }); return; }
      var r = e.target.closest('.mx-crow'); if (r) editContact(list[+r.dataset.i]);
    };
  }
  function editContact(c) {
    c = Object.assign({ name: '', emails: [], phones: [], org: '', note: '' }, c);
    var isNew = !c.id;
    main('<div class="mx-setup"><h2>' + (isNew ? 'איש קשר חדש' : 'עריכת איש קשר') + '</h2>' +
      '<label class="mx-f"><span>שם</span><input id="mx-cn" dir="auto" value="' + esc(c.name) + '"></label>' +
      '<label class="mx-f"><span>מיילים (מופרדים בפסיק)</span><input id="mx-ce" dir="ltr" value="' + esc((c.emails || []).join(', ')) + '"></label>' +
      '<label class="mx-f"><span>טלפונים</span><input id="mx-cp" dir="ltr" value="' + esc((c.phones || []).join(', ')) + '"></label>' +
      '<label class="mx-f"><span>ארגון</span><input id="mx-co" dir="auto" value="' + esc(c.org) + '"></label>' +
      '<label class="mx-f"><span>הערה</span><input id="mx-cno" dir="auto" value="' + esc(c.note) + '"></label>' +
      '<div class="mx-acts">' + (isNew ? '' : '<button class="mx-btn tx" id="mx-cdel">מחיקה</button>') + '<button class="mx-btn tx" id="mx-ccan">ביטול</button><button class="mx-btn" id="mx-csave">שמירה</button></div></div>');
    function split(v) { return v.split(/[,;]/).map(function (x) { return x.trim(); }).filter(Boolean); }
    $('mx-ccan').onclick = function () { showContacts(); };
    if ($('mx-cdel')) $('mx-cdel').onclick = async function () { await DBS.del('contacts', c.id); toast('נמחק'); showContacts(); };
    $('mx-csave').onclick = async function () {
      c.name = $('mx-cn').value.trim(); c.emails = split($('mx-ce').value); c.phones = split($('mx-cp').value); c.org = $('mx-co').value.trim(); c.note = $('mx-cno').value.trim();
      if (!c.name && !c.emails.length) { toast('צריך שם או מייל'); return; }
      if (!c.id) c.id = DBS.uid();
      await DBS.put('contacts', c); toast('נשמר'); showContacts();
    };
  }

  /* ---------- הגדרות ---------- */
  async function showSettings() {
    S.view = 'settings'; S.msg = null; renderSide();
    var used = 0; try { used = await DBS.usage(); } catch (e) { used = 0; }
    var st = S.set;
    function num(id, label, val, hint) { return '<label class="mx-f"><span>' + label + '</span><input id="' + id + '" type="number" min="0" dir="ltr" value="' + esc(val) + '">' + (hint ? '<small class="mx-hint">' + hint + '</small>' : '') + '</label>'; }
    function chk(id, label, val) { return '<label class="mx-chk"><input type="checkbox" id="' + id + '"' + (val ? ' checked' : '') + '><span>' + label + '</span></label>'; }
    main('<div class="mx-setup"><h2>הגדרות הדואר</h2>' +
      '<p>חשבון: <b>' + esc(S.acct ? S.acct.email : 'לא מחובר') + '</b> ' + (S.bridge ? '<button class="mx-btn tx sm" id="mx-acctbtn">' + (S.acct ? 'עריכת החשבון' : 'חיבור חשבון') + '</button>' : '<small class="mx-hint">(חיבור חשבון — כשהתוכנה "אחת שאלתי" פועלת)</small>') + '</p>' +
      '<h3>שמירה במחשב (לעבודה בלי חיבור)</h3>' +
      num('mx-s-keep', 'כמה הודעות לשמור בכל תיקייה', st.keep, '0 = בלי הגבלה') +
      chk('mx-s-bodies', 'לשמור את תוכן ההודעות לקריאה בלי חיבור', st.cacheBodies) +
      num('mx-s-pre', 'כמה הודעות חדשות להוריד מראש בכל סנכרון', st.prefetch, '') +
      num('mx-s-attmax', 'שמירת קבצים מצורפים — עד גודל של (MB) לקובץ', st.attMaxMB, '0 = לא לשמור קבצים') +
      num('mx-s-atttot', 'מקום כולל לקבצים מצורפים (MB)', st.attTotalMB, '') +
      num('mx-s-sync', 'סנכרון אוטומטי כל (דקות)', st.syncMin, '0 = רק ידני') +
      '<h3>כתיבה</h3>' +
      '<label class="mx-f"><span>חתימה</span><textarea id="mx-s-sig" class="mx-c-body sm" dir="auto">' + esc(st.signature) + '</textarea></label>' +
      chk('mx-s-autoc', 'לשמור אוטומטית באנשי הקשר את מי שאני כותב/ת אליו', st.autoContacts) +
      '<p class="mx-hint">נתונים שמורים במחשב: ' + mb(used) + '</p>' +
      '<div class="mx-acts"><button class="mx-btn tx" id="mx-s-clear">מחיקת הדואר השמור</button><button class="mx-btn" id="mx-s-save">שמירה</button></div>' +
      '<p class="mx-hint">גרסה ' + esc(VERSION) + '. שליחה מתוזמנת ושליחה כשהרשת חוזרת מתבצעות באחת שאלתי, גם כשהתוסף סגור. מה שנכתב בלי אחת שאלתי נשמר כאן ונמסר לה כשהיא פועלת.</p></div>');
    if ($('mx-acctbtn')) $('mx-acctbtn').onclick = setup;
    $('mx-s-save').onclick = function () {
      function n(id, d) { var v = parseInt($(id).value, 10); return isNaN(v) || v < 0 ? d : v; }
      S.set = Object.assign({}, S.set, { keep: n('mx-s-keep', 300), cacheBodies: $('mx-s-bodies').checked, prefetch: n('mx-s-pre', 30), attMaxMB: n('mx-s-attmax', 5),
        attTotalMB: n('mx-s-atttot', 300), syncMin: n('mx-s-sync', 10), signature: $('mx-s-sig').value, autoContacts: $('mx-s-autoc').checked });
      save(LS_SET, S.set); armSync();
      Promise.all(S.folders.map(function (f) { return DBS.prune(f.id, S.set.keep); })).catch(function () {});
      toast('ההגדרות נשמרו');
    };
    $('mx-s-clear').onclick = async function () {
      await DBS.clear('messages'); await DBS.clear('atts'); toast('הדואר השמור נמחק (טיוטות, תור יוצא ואנשי קשר נשארו)'); showSettings();
    };
  }

  /* ---------- הגדרת חשבון ---------- */
  var PRESETS = [
    ['Gmail', 'imap.gmail.com', 993, 'smtp.gmail.com', 465, 'ב-Gmail: חשבון Google ← אבטחה ← אימות דו-שלבי ← "סיסמאות לאפליקציות". יוצרים סיסמה ומדביקים כאן.', 'https://myaccount.google.com/apppasswords'],
    ['Outlook / Hotmail', 'outlook.office365.com', 993, 'smtp.office365.com', 587, 'ב-Outlook: account.microsoft.com ← אבטחה ← אפשרויות אבטחה מתקדמות ← "סיסמאות אפליקציה".', 'https://account.microsoft.com/security'],
    ['Yahoo', 'imap.mail.yahoo.com', 993, 'smtp.mail.yahoo.com', 465, 'ב-Yahoo: אבטחת החשבון ← "יצירת סיסמת אפליקציה".', 'https://login.yahoo.com/account/security'],
    ['iCloud', 'imap.mail.me.com', 993, 'smtp.mail.me.com', 587, 'ב-iCloud: appleid.apple.com ← כניסה ואבטחה ← "סיסמאות ייעודיות ליישומים".', 'https://appleid.apple.com'],
    ['אחר', '', 993, '', 465, 'הזינו את שרתי IMAP (קבלה) ו-SMTP (שליחה) מספק הדואר.', '']
  ];
  function setup() {
    if (!S.bridge) { toast('חיבור חשבון אפשרי כשהתוכנה "אחת שאלתי" פועלת'); return; }
    S.view = 'setup';
    var cur = S.acct && S.acct.configured ? S.acct : null;
    main('<div class="mx-setup"><h2>' + (cur ? 'חשבון הדואר' : 'הוספת חשבון דואר') + '</h2>' +
      '<p>הדואר מתקבל ונשלח דרך התוכנה "אחת שאלתי", שמתחברת לשרת הדואר שלכם. הסיסמה נשמרת במחשב בלבד, מוצפנת, ולא נשלחת לשום מקום אחר.</p>' +
      '<div class="mx-presets" id="mx-pre">' + PRESETS.map(function (p, i) { return '<button type="button" data-i="' + i + '">' + esc(p[0]) + '</button>'; }).join('') + '</div>' +
      '<p id="mx-howto"></p>' +
      '<label class="mx-f"><span>השם שיופיע אצל הנמענים</span><input id="mx-name" dir="auto" value="' + esc(cur ? cur.name : '') + '"></label>' +
      '<label class="mx-f"><span>כתובת מייל</span><input id="mx-email" type="email" dir="ltr" value="' + esc(cur ? cur.email : '') + '"></label>' +
      '<label class="mx-f"><span>סיסמת אפליקציה' + (cur ? ' (ריק = ללא שינוי)' : '') + '</span><input id="mx-pw" type="password" dir="ltr" autocomplete="off"></label>' +
      '<div class="mx-row2"><label class="mx-f"><span>שרת IMAP (קבלה)</span><input id="mx-host" dir="ltr" value="' + esc(cur ? cur.host : '') + '"></label>' +
      '<label class="mx-f"><span>פורט</span><input id="mx-port" dir="ltr" inputmode="numeric" value="' + esc(cur ? cur.port : 993) + '"></label></div>' +
      '<div class="mx-row2"><label class="mx-f"><span>שרת SMTP (שליחה)</span><input id="mx-shost" dir="ltr" value="' + esc(cur ? cur.smtpHost : '') + '"></label>' +
      '<label class="mx-f"><span>פורט</span><input id="mx-sport" dir="ltr" inputmode="numeric" value="' + esc(cur ? cur.smtpPort : 465) + '"></label></div>' +
      '<label class="mx-f"><span>שם משתמש (אם שונה מהמייל)</span><input id="mx-user" dir="ltr" value="' + esc(cur && cur.user !== cur.email ? cur.user : '') + '"></label>' +
      '<div class="mx-err" id="mx-err"></div>' +
      '<div class="mx-acts">' + (cur ? '<button class="mx-btn tx" id="mx-remove">הסרת החשבון</button>' : '') + '<button class="mx-btn tx" id="mx-cancel">ביטול</button>' +
      '<button class="mx-btn" id="mx-save">חיבור</button></div></div>');
    var link = '';
    function pick(i) {
      var p = PRESETS[i];
      Array.prototype.forEach.call($('mx-pre').children, function (b) { b.classList.toggle('sel', +b.dataset.i === i); });
      if (p[1]) { $('mx-host').value = p[1]; $('mx-shost').value = p[3]; }
      $('mx-port').value = p[2]; $('mx-sport').value = p[4];
      link = p[6];
      $('mx-howto').innerHTML = esc(p[5]) + (link ? ' <a href="#" id="mx-link">לדף ההגדרה</a>' : '');
      var a = $('mx-link'); if (a) a.onclick = function (e) { e.preventDefault(); openUrl(link); };
    }
    $('mx-pre').onclick = function (e) { var b = e.target.closest('button'); if (b) pick(+b.dataset.i); };
    $('mx-email').oninput = function () {
      var d = ($('mx-email').value.split('@')[1] || '').toLowerCase();
      var i = /gmail|googlemail/.test(d) ? 0 : /outlook|hotmail|live|msn/.test(d) ? 1 : /yahoo/.test(d) ? 2 : /icloud|me\.com|mac\.com/.test(d) ? 3 : -1;
      if (i >= 0) pick(i);
    };
    if (!cur) pick(0);
    $('mx-cancel').onclick = function () { openFolder(S.folder); };
    if ($('mx-remove')) $('mx-remove').onclick = async function () {
      try { await B.call('/mail/account/clear', {}); S.acct = null; save(LS_ACCT, null); S.live = false; $('mx-acct').textContent = ''; setStatus(); setup(); toast('החשבון הוסר (הדואר השמור נשאר; אפשר למחוק אותו בהגדרות)'); }
      catch (e) { $('mx-err').textContent = errMsg(e); }
    };
    $('mx-save').onclick = async function () {
      var btn = $('mx-save'); btn.disabled = true; $('mx-err').textContent = ''; btn.textContent = 'מתחבר…';
      try {
        S.acct = await B.call('/mail/account/set', {
          name: $('mx-name').value.trim(), email: $('mx-email').value.trim(), password: $('mx-pw').value, host: $('mx-host').value.trim(),
          port: parseInt($('mx-port').value, 10) || 993, smtpHost: $('mx-shost').value.trim(), smtpPort: parseInt($('mx-sport').value, 10) || 0,
          user: $('mx-user').value.trim() || $('mx-email').value.trim()
        }, { timeoutMs: 60000 });
        save(LS_ACCT, S.acct);
        toast('החשבון חובר');
        S.folder = 'INBOX';
        await tryConnect(); openFolder('INBOX');
      } catch (e) { $('mx-err').textContent = errMsg(e); btn.disabled = false; btn.textContent = 'חיבור'; }
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
  function openInExplorer(path) {
    var dir = String(path).replace(/[\\/][^\\/]*$/, '');
    try { window.Otzaria.call('plugin.openOther', { pluginId: 'com.chadbedera.fileexplorer', param: { path: dir } }).catch(function () {}); } catch (e) { /* אין סייר */ }
  }

  /* ---------- סנכרון תקופתי וחזרת רשת ---------- */
  var syncTimer = null, probeTimer = null;
  function armSync() {
    clearInterval(syncTimer);
    if (S.set.syncMin > 0) syncTimer = setInterval(function () { if (S.live && S.view === 'folder' && !S.msg) syncFolder(true); else if (S.live) flushLocalOutbox(); }, S.set.syncMin * 60000);
  }
  function armProbe() {
    clearInterval(probeTimer);
    probeTimer = setInterval(async function () {
      if (S.live) return;
      var was = S.bridge;
      if (await tryConnect(true)) { toast('החיבור חזר'); if (S.view === 'folder' && !S.msg) syncFolder(true); }
      else if (S.bridge && !was) { flushLocalOutbox(); renderSide(); }
    }, 30000);
  }
  window.addEventListener('online', function () { if (!S.live) tryConnect(true).then(function (ok) { if (ok && S.view === 'folder' && !S.msg) syncFolder(true); }); });

  /* ---------- אתחול ---------- */
  var VERSION = '1.0.0';
  $('mx-menu').innerHTML = ic('menu'); $('mx-refresh').innerHTML = ic('refresh'); $('mx-settings').innerHTML = ic('gear');
  document.querySelector('.mx-sic').innerHTML = ic('search');
  $('mx-compose-btn').innerHTML = ic('pen') + '<span>כתיבה</span>';
  $('mx-compose-btn').onclick = function () { compose(); };
  $('mx-menu').onclick = function () { $('mx-side').classList.toggle('collapsed'); };
  $('mx-refresh').onclick = refresh;
  $('mx-settings').onclick = showSettings;
  $('mx-q').addEventListener('keydown', function (e) { if (e.key === 'Enter') { S.q = $('mx-q').value.trim(); S.offset = 0; if (S.view !== 'folder') S.view = 'folder'; if (S.live && S.q) syncFolder(); else renderCached(); } });
  $('mx-q').addEventListener('search', function () { if (!$('mx-q').value && S.q) { S.q = ''; S.offset = 0; renderCached(); } });
  window.addEventListener('beforeunload', function () { if (CMP) saveDraft(); });
  var booted = false;
  async function boot(p) {
    if (booted) return; booted = true;
    if (p && p.theme) applyTheme(p.theme);
    if (p && p.plugin && p.plugin.version) VERSION = p.plugin.version;
    if (S.acct) $('mx-acct').textContent = S.acct.email;
    if (!DBS.ok) { state('האחסון המקומי אינו זמין בגרסה זו של אוצריא.'); return; }
    await openFolder('INBOX');             // קודם מה שבמחשב — מיידי, גם בלי חיבור
    setStatus();
    await tryConnect(true);
    if (S.live) { if (S.view === 'folder' && !S.msg) syncFolder(true); }
    else if (!S.acct && S.bridge) setup();
    armSync(); armProbe();
  }
  if (window.Otzaria) {
    window.Otzaria.on('plugin.boot', boot);
    window.Otzaria.on('theme.changed', applyTheme);
  } else state('התוסף פועל רק בתוך אוצריא.');
})();
