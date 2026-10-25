/* app.js - מסך הסייר: ניווט, רשימה, בחירה, פעולות קבצים, חיפוש זורם ומעבר אוטומטי למצב חלופי */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var ui = window.SS.ui;
  FX.ui = ui;
  FX.t = window.I18N ? window.I18N.t : function (x) { return x; };
  FX.VERSION = '1.0.5';
  FX.CHANGELOG_URL = 'https://github.com/Hadabadra/achat-shaalti/blob/main/CHANGELOG.md';
  FX.BRIDGE_URL = 'https://github.com/Hadabadra/achat-shaalti/releases/latest';

  var LS = 'fx.view';
  var S = {
    prov: null, path: '', isRoot: true, recycle: false, known: [], entries: [], view: [],
    sel: {}, anchor: -1, focus: -1,
    back: [], fwd: [],
    sort: { key: 'name', dir: 'asc' }, showHidden: false,
    cols: { date: true, type: true, size: true }, colw: {}, stripes: true,
    tabs: null, tab: 0,
    clip: null,            /* { paths, cut, kind } */
    search: null,          /* { id, query, hits, cancelled, done, scanned } */
    loadId: 0, probeTimer: null, suspended: false, booted: false, busy: false
  };
  var $ = function (id) { return document.getElementById(id); };
  var ic = function (n, c) { return FX.icon(n, c); };

  /* ---------- שמירת העדפות תצוגה (לכל צופה בנפרד) ---------- */
  function loadPrefs() {
    try {
      var v = JSON.parse(localStorage.getItem(LS) || '{}');
      if (v.sort) S.sort = v.sort;
      S.showHidden = !!v.showHidden;
      if (v.cols) S.cols = Object.assign({ date: true, type: true, size: true }, v.cols);
      if (v.colw) S.colw = v.colw;
      if (v.stripes != null) S.stripes = !!v.stripes;
      return v;
    } catch (e) { return {}; }
  }
  function savePrefs() {
    try {
      var v = JSON.parse(localStorage.getItem(LS) || '{}');
      v.sort = S.sort; v.showHidden = S.showHidden; v.cols = S.cols; v.colw = S.colw; v.stripes = S.stripes;
      if (S.prov) v['last.' + S.prov.kind] = S.recycle ? '' : S.path;
      localStorage.setItem(LS, JSON.stringify(v));
    } catch (e) { /* אחסון לא זמין */ }
  }

  /* ---------- פתיחת קישור והעתקה ללוח ---------- */
  async function openUrl(url) {
    if (!window.Otzaria) { ui.toast('בתוך אוצריא הקישור ייפתח בדפדפן', 'info'); return; }
    /* שם הקריאה המתועד הוא app.openUrl; app.open_url הוא גיבוי למקרה שהגרסה של אוצריא מכירה רק אותו */
    var names = ['app.openUrl', 'app.open_url'], lastErr = '';
    for (var i = 0; i < names.length; i++) {
      try {
        var r = await window.Otzaria.call(names[i], { url: url });
        if (!(r && r.success === false)) return;
        lastErr = names[i] + ': ' + ((r && (r.error || r.message)) || 'success=false');
      } catch (e) { lastErr = names[i] + ': ' + ((e && e.message) || e); }
    }
    try { console.warn('openUrl failed', lastErr); } catch (e) { /* אין קונסול */ }
    /* גיבוי: מעתיקים את הקישור ללוח, כדי שאפשר להדביק אותו בדפדפן */
    var ok = await copyText(url);
    ui.toast(ok ? 'לא ניתן לפתוח את הקישור, הוא הועתק ללוח' : 'לא ניתן לפתוח את הקישור: ' + ui.isolateLtr(url), 'error');
  }
  async function copyText(text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) { await navigator.clipboard.writeText(text); return true; }
    } catch (e) { /* נעבור לשיטה הישנה */ }
    var ta = document.createElement('textarea');
    ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    ta.remove();
    return ok;
  }

  /* ---------- פתיחה זמנית של המחשב (מצב נעילה): חיווי קבוע בכותרת ----------
     אותו שדה tempUnlock מ-/kiosk/status שמוצג בהגדרות מערכת ← נעילה. החיווי בכותרת, ולא באזור התוכן,
     כדי שיישאר גלוי גם כשמסך הנעילה של הסייר מוצג. בתחילת הפתיחה ובסופה המדיניות נטענת מחדש,
     כי בזמן הפתיחה גם הסייר פתוח לגמרי (הגשר אוכף; כאן רק משקפים) */
  var TU = { end: 0, poll: null, tick: null, on: false };

  function tempPoll() {
    clearTimeout(TU.poll);
    if (S.suspended || !S.prov || S.prov.kind !== 'bridge' || !window.Otzaria) { tempShow(0); return; }
    FX.Bridge.call('/kiosk/status', {}).then(function (st) {
      var tu = st && st.tempUnlock;
      tempShow(tu && tu.active ? tu.secondsLeft : 0);
    }, function () { /* גשר ישן או תקלה רגעית: לא משנים את החיווי */ }).then(function () {
      TU.poll = setTimeout(tempPoll, TU.on ? 15000 : 30000);
    });
  }

  function tempShow(secondsLeft) {
    var was = TU.on;
    TU.on = secondsLeft > 0;
    TU.end = TU.on ? Date.now() + secondsLeft * 1000 : 0;
    clearInterval(TU.tick); TU.tick = null;
    var c = $('fx-temp');
    if (!TU.on) {
      c.hidden = true; c.innerHTML = '';
    } else {
      c.title = 'המחשב פתוח זמנית. בתום הזמן הנעילה תחזור מעצמה';
      c.innerHTML = ic('clock') + '<span>פתוח זמנית</span> <bdi dir="ltr" id="fx-temp-left"></bdi>';
      c.hidden = false;
      tempTickOnce();
      TU.tick = setInterval(tempTickOnce, 1000);
    }
    if (was !== TU.on && S.prov) afterPolicy();
  }

  function tempTickOnce() {
    var left = Math.max(0, Math.ceil((TU.end - Date.now()) / 1000));
    var el = $('fx-temp-left');
    if (el) el.textContent = Math.floor(left / 60) + ':' + (left % 60 < 10 ? '0' : '') + (left % 60);
    /* בתום הזמן: בודקים מול הגשר (הוא מקור האמת) במקום להסתמך על השעון כאן */
    if (left <= 0) { clearInterval(TU.tick); TU.tick = null; setTimeout(tempPoll, 2500); }
  }

  /* ---------- חיבור לגשר ומעבר אוטומטי ---------- */
  function setChip() {
    var c = $('fx-chip');
    var bridge = S.prov && S.prov.kind === 'bridge';
    c.className = 'topbar-chip ' + (bridge ? 'ok' : 'warn');
    c.innerHTML = '<i class="dot"></i>' + (bridge ? 'מחובר לגשר' : 'מצב מוגבל');
    c.title = bridge ? 'אחת שאלתי פועלת' : 'אחת שאלתי לא נמצאה — עובדים על תיקיות שאושרו בלבד';
  }

  function showBanner(on) {
    var b = $('fx-banner');
    b.hidden = !on;
    if (!on || b.dataset.built) return;
    b.dataset.built = '1';
    b.innerHTML =
      '<span class="fx-banner-ic">' + ic('plug') + '</span>' +
      '<div class="fx-banner-text"><b>אחת שאלתי לא נמצאה.</b> ' +
      '<span>בלעדיו אפשר לעבוד רק על תיקיות שאישרתם, בלי העתקה ויצירת קבצים. מורידים את <b>OtzariaBridge.exe</b>, מפעילים פעם אחת, והחיבור יזוהה מעצמו.</span></div>' +
      '<div class="fx-banner-actions">' +
      '<button class="btn btn-primary" id="fx-dl">' + ic('open') + 'פתח בדפדפן</button>' +
      '<button class="btn btn-tonal" id="fx-copy-link">' + ic('link') + 'העתק קישור</button>' +
      '<button class="btn btn-text" id="fx-retry">' + ic('refresh') + 'נסה שוב</button></div>';
    $('fx-dl').addEventListener('click', function () { openUrl(FX.BRIDGE_URL); });
    $('fx-copy-link').addEventListener('click', async function () {
      var ok = await copyText(FX.BRIDGE_URL);
      ui.toast(ok ? 'הקישור הועתק' : 'ההעתקה נכשלה: ' + ui.isolateLtr(FX.BRIDGE_URL), ok ? 'success' : 'error');
    });
    $('fx-retry').addEventListener('click', function () {
      var btn = this; btn.disabled = true;
      connect(true).then(function (ok) {
        btn.disabled = false;
        if (!ok) ui.toast('הגשר עדיין לא נמצא', 'info');
      });
    });
  }

  function scheduleProbe() {
    clearTimeout(S.probeTimer);
    if (S.suspended) return;
    S.probeTimer = setTimeout(function () {
      connect(false).then(function (ok) { if (!ok) scheduleProbe(); });
    }, 15000);
  }

  async function setProvider(p) {
    cancelSearch();
    if (p.kind === 'fallback') await p.init();
    S.prov = p;
    S.back = []; S.fwd = []; S.clip = null; S.tabs = null; S.tab = 0;
    setChip();
    renderCmds();
    tempPoll();
    var prefs = loadPrefs();
    $('fx-hidden').hidden = !p.caps.hidden;
    $('fx-add-folder').hidden = !p.caps.addFolder;
    $('fx-content-wrap').hidden = !p.caps.contentSearch;
    await FX.Policy.refresh();
    if (FX.Policy.locked()) { FX.Policy.showLock(); return; }
    FX.Policy.hideLock();
    await renderSide();
    var start = prefs['last.' + p.kind] || '';
    if (!FX.Policy.pathAllowed(start)) start = '';   /* מצב מוגבל: לא פותחים נתיב שמחוץ לתיקיות המותרות */
    try { await load(start, false); } catch (e) { await load('', false); }
  }

  /* אחרי שינוי מדיניות או פתיחה בסיסמה: טוענים מחדש את הסרגל והתיקייה */
  async function afterPolicy() {
    if (!S.prov) return;
    await FX.Policy.refresh();
    if (FX.Policy.locked()) { FX.Policy.showLock(); return; }
    FX.Policy.hideLock();
    await renderSide();
    var start = FX.Policy.pathAllowed(S.path) ? S.path : '';
    try { await load(start, false); } catch (e) { try { await load('', false); } catch (e2) { /* כבר דווח */ } }
  }

  async function connect(manual) {
    var info = null;
    try { info = await FX.Bridge.probe(); } catch (e) { info = null; }
    if (info) {
      clearTimeout(S.probeTimer);
      showBanner(false);
      if (!S.prov || S.prov.kind !== 'bridge') {
        var switching = !!S.prov;
        await setProvider(FX.BridgeProvider);
        if (switching) ui.toast('אחת שאלתי חוברה — כל הפעולות זמינות', 'success');
      }
      return true;
    }
    if (!S.prov || S.prov.kind === 'bridge') {
      var lost = !!S.prov;
      try { await setProvider(FX.FallbackProvider); } catch (e) { ui.toast(FX.errorText(e), 'error'); }
      if (lost) ui.toast('החיבור לגשר אבד — עברנו למצב מוגבל', 'error');
    }
    showBanner(true);
    if (!manual) scheduleProbe(); else scheduleProbe();
    return false;
  }

  function isLinkError(e) {
    var c = String(e && e.code || '');
    return c === 'unreachable' || c === 'transport' || c === 'offline';
  }
  function opError(e, prefix) {
    if (e && e.code === 'fx_locked') {   /* הגשר נעל את הסייר (למשל בתום הזמן): מסך הנעילה */
      FX.Policy.refresh().then(function () { if (FX.Policy.locked()) FX.Policy.showLock(); });
      return;
    }
    ui.toast((prefix ? prefix + ': ' : '') + FX.errorText(e), 'error');
    if (S.prov && S.prov.kind === 'bridge' && isLinkError(e)) connect(false);
  }

  /* 4ג (לפי בחירה): תיקיות הגישה המהירה כספרים בספריית אוצריא; כבוי = רשימה ריקה */
  function publishLibFolders() {
    if (!window.SdkX || !SdkX.setLibraryBooks) return;
    var on = !!opts().libFolders;
    SdkX.setLibraryBooks(on ? (S.known || []).filter(function (k) { return k.path && k.path.indexOf('::') !== 0; })
      .map(function (k) { return { id: k.path, title: k.name, categoryPath: '/סייר קבצים' }; }) : []);
  }

  /* ---------- סרגל צד: מיקומים ---------- */
  async function renderSide() {
    var side = $('fx-side');
    var html = '';
    try {
      var r = await S.prov.roots();
      S.known = r.known || [];
      publishLibFolders();
      html += '<button class="nav-item" data-path="">' + ic(S.prov.kind === 'bridge' ? 'pc' : 'star') + '<span>' + FX.esc(S.prov.rootLabel) + '</span></button>';
      if (S.known.length) {
        html += '<div class="fx-side-label">' + (S.prov.kind === 'bridge' ? 'גישה מהירה' : 'תיקיות שאושרו') + '</div>';
        html += S.known.map(function (k) {
          return '<div class="fx-side-row"><button class="nav-item" data-path="' + FX.esc(k.path) + '" title="' + FX.esc(k.full || k.path) + '">' +
            ic(k.icon || 'folder') + '<span>' + FX.esc(k.name) + '</span></button>' +
            (k.removable ? '<button class="icon-btn fx-side-x" data-remove="' + FX.esc(k.token) + '" title="הסרה מהרשימה" aria-label="הסרה מהרשימה">' + ic('close') + '</button>' : '') +
            '</div>';
        }).join('');
      }
      if (r.drives && r.drives.length) {
        html += '<div class="fx-side-label">כוננים</div>';
        html += r.drives.map(function (d) {
          return '<button class="nav-item" data-path="' + FX.esc(d.path) + '">' + ic(d.driveType === 'removable' ? 'usb' : d.driveType === 'network' ? 'network' : 'drive') +
            '<span>' + FX.esc(d.name) + '</span></button>';
        }).join('');
      }
      if (S.prov.caps.addFolder) {
        html += '<button class="nav-item fx-side-add" data-add="1">' + ic('plus') + '<span>הוספת תיקייה…</span></button>';
      }
    } catch (e) {
      html = '<div class="fx-empty small">' + FX.esc(FX.errorText(e)) + '</div>';
    }
    side.innerHTML = html;
    markSide();
  }
  function markSide() {
    Array.prototype.forEach.call($('fx-side').querySelectorAll('.nav-item[data-path]'), function (b) {
      b.classList.toggle('active', S.prov.same(b.dataset.path, S.path) && !S.search);
    });
  }

  /* ---------- 4ב: ספריית אוצריא כתיקייה וירטואלית (library.getTree; ספר נפתח בעיון) ---------- */
  var OTZ = '::otzaria';
  function isOtz(p) { return typeof p === 'string' && p.indexOf(OTZ) === 0; }
  async function otzList(path) {
    var cat = path.slice(OTZ.length) || '';
    var t = await window.Otzaria.call('library.getTree', { path: cat || '/', includeBooks: !!cat });
    t = t && (t.data !== undefined ? t.data : t);
    if (!t) throw new Error('הקטגוריה לא נמצאה');
    var ents = (t.categories || []).map(function (c) { return { name: c.title, path: OTZ + c.path, type: 'dir' }; })
      .concat((t.books || []).map(function (b) { return { name: b.title, path: OTZ + '#' + (b.id || b.title), type: 'file', otzBook: b }; }));
    var up = cat.replace(/\/[^\/]*$/, '');
    return { path: path, entries: ents, isRoot: false, parent: cat ? (up ? OTZ + up : OTZ) : '' };
  }

  /* ---------- טעינת תיקייה ---------- */
  async function load(path, push) {
    cancelSearch();
    var id = ++S.loadId;
    var prev = S.path;
    $('fx-list').innerHTML = '<div class="fx-empty">' + ui.loading('טוען…') + '</div>';
    var r;
    try {
      r = isOtz(path) ? await otzList(path) : await S.prov.list(path, { showHidden: S.showHidden });
    } catch (e) {
      if (id !== S.loadId) return;
      opError(e, 'פתיחת התיקייה נכשלה');
      if (path !== prev) { renderList(); throw e; }
      S.entries = []; renderList();
      throw e;
    }
    if (id !== S.loadId) return;
    if (push && !S.prov.same(prev, r.path)) { S.back.push(prev); S.fwd = []; }
    S.path = r.path;
    S.isRoot = !!r.isRoot;
    S.recycle = !!r.recycle;
    S.otz = isOtz(r.path);
    S.parent = r.parent;
    S.truncated = !!r.truncated;
    var ents = r.entries || [];
    if (r.isRoot && r.known && r.known.length && S.prov.kind === 'bridge') {
      ents = r.known.map(function (k) { return { name: k.name, path: k.path, type: 'dir', icon: k.icon }; }).concat(ents);
    }
    if (r.isRoot && window.Otzaria) ents = [{ name: 'ספריית אוצריא', path: OTZ, type: 'dir' }].concat(ents); /* 4ב */
    S.entries = ents;
    S.sel = {}; S.anchor = -1; S.focus = -1;
    $('fx-search').value = '';
    $('fx-search-clear').hidden = true;
    savePrefs();
    renderCrumbs(); renderList(); markSide(); renderCmds(); renderTabs();
  }
  function go(path) { return load(path, true).catch(function () { /* כבר דווח */ }); }
  function refresh() {
    if (S.search) { runSearch(S.search.query); return; }
    var keep = Object.keys(S.sel);
    load(S.path, false).then(function () {
      keep.forEach(function (p) { if (S.view.some(function (e) { return e.path === p; })) S.sel[p] = true; });
      renderList();
    }).catch(function () { /* כבר דווח */ });
  }
  function goUp() {
    if (S.search) { exitSearch(); return; }
    if (S.isRoot) return;
    if (S.recycle) { go(''); return; }
    if (S.otz) { go(S.parent || ''); return; }
    var par = S.prov.parentOf(S.path);
    go(par == null ? '' : par);
  }
  function goBack() {
    if (!S.back.length) return;
    var p = S.back.pop(); S.fwd.push(S.path);
    load(p, false).catch(function () { /* כבר דווח */ });
  }
  function goFwd() {
    if (!S.fwd.length) return;
    var p = S.fwd.pop(); S.back.push(S.path);
    load(p, false).catch(function () { /* כבר דווח */ });
  }

  /* ---------- שורת הנתיב ---------- */
  function renderCrumbs() {
    var c = $('fx-crumbs');
    if (S.search) {
      c.innerHTML = '<span class="fx-crumb-static">' + ic('search') + 'תוצאות חיפוש: <b>' + FX.esc(S.search.query) + '</b> ב-' + FX.esc(S.prov.displayPath(S.search.root)) + '</span>';
      updateNavBtns();
      return;
    }
    if (S.otz) {
      var segs = S.path.slice(OTZ.length).split('/').filter(Boolean), acc = '';
      c.innerHTML = '<button class="fx-crumb" data-path="">' + FX.esc(S.prov.rootLabel) + '</button><span class="fx-crumb-sep">' + ic('chevron') + '</span>' +
        '<button class="fx-crumb" data-path="' + OTZ + '">ספריית אוצריא</button>' +
        segs.map(function (g) { acc += '/' + g; return '<span class="fx-crumb-sep">' + ic('chevron') + '</span><button class="fx-crumb" data-path="' + FX.esc(OTZ + acc) + '">' + FX.esc(g) + '</button>'; }).join('');
      updateNavBtns();
      return;
    }
    if (S.recycle) {
      c.innerHTML = '<button class="fx-crumb" data-path="">' + FX.esc(S.prov.rootLabel) + '</button><span class="fx-crumb-sep">' + ic('chevron') + '</span>' +
        '<span class="fx-crumb-static">' + ic('trash') + 'סל המיחזור</span>';
      updateNavBtns();
      return;
    }
    var segs = S.prov.segments(S.path);
    c.innerHTML = segs.map(function (s, i) {
      return (i ? '<span class="fx-crumb-sep">' + ic('chevron') + '</span>' : '') +
        '<button class="fx-crumb" data-path="' + FX.esc(s.path) + '">' + FX.esc(s.label) + '</button>';
    }).join('');
    updateNavBtns();
  }
  function updateNavBtns() {
    $('fx-back').disabled = !S.back.length;
    $('fx-fwd').disabled = !S.fwd.length;
    $('fx-up').disabled = S.isRoot && !S.search;
  }
  function editPath() {
    if (!S.prov.caps.editPath || S.search || S.recycle) return;
    var c = $('fx-crumbs');
    c.innerHTML = '<input class="input fx-path-input" dir="ltr" value="' + FX.esc(S.path) + '" aria-label="נתיב">';
    var inp = c.querySelector('input');
    inp.focus(); inp.select();
    var done = false;
    function finish(ok) {
      if (done) return; done = true;
      var v = S.prov.normalizeInput(inp.value);
      if (ok && v !== S.path) go(v).then(renderCrumbs); else renderCrumbs();
    }
    inp.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); finish(true); }
      else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
      e.stopPropagation();
    });
    inp.addEventListener('blur', function () { finish(false); });
  }

  /* ---------- כרטיסיות (כמו בסייר של Windows 11) ---------- */
  function tabTitle(path) {
    if (!path) return S.prov ? S.prov.rootLabel : 'סייר';
    var d = S.prov.displayPath(path).replace(/[\\/]+$/, '');
    var m = /([^\\/]+)$/.exec(d);
    return m ? m[1] : d;
  }
  function ensureTabs() {
    if (!S.tabs) { S.tabs = [{ path: S.path, back: S.back, fwd: S.fwd, title: tabTitle(S.path) }]; S.tab = 0; }
  }
  function saveTab() {
    ensureTabs();
    var t = S.tabs[S.tab];
    if (!t) return;
    t.path = S.path; t.back = S.back; t.fwd = S.fwd; t.title = S.recycle ? 'סל המיחזור' : tabTitle(S.path);
  }
  function renderTabs() {
    var bar = $('fx-tabs');
    if (!bar || !S.prov) return;
    ensureTabs();
    saveTab();
    bar.innerHTML = S.tabs.map(function (t, i) {
      return '<div class="fx-tab' + (i === S.tab ? ' on' : '') + '" role="tab" aria-selected="' + (i === S.tab ? 'true' : 'false') + '" data-tab="' + i + '" title="' + FX.esc(S.prov.displayPath(t.path)) + '">' +
        ic('folder') + '<span class="fx-tab-t">' + FX.esc(t.title) + '</span>' +
        (S.tabs.length > 1 ? '<button class="fx-tab-x" data-closetab="' + i + '" aria-label="סגירת כרטיסייה">' + ic('close') + '</button>' : '') + '</div>';
    }).join('') + '<button class="fx-tab-new" data-newtab="1" title="כרטיסייה חדשה (Ctrl+T)" aria-label="כרטיסייה חדשה">' + ic('plus') + '</button>';
  }
  async function newTab(path) {
    ensureTabs(); saveTab();
    cancelSearch(); S.search = null;
    S.tabs.push({ path: path || '', back: [], fwd: [], title: tabTitle(path || '') });
    S.tab = S.tabs.length - 1;
    S.back = S.tabs[S.tab].back; S.fwd = S.tabs[S.tab].fwd;
    try { await load(path || '', false); } catch (e) { /* כבר דווח */ }
    renderTabs();
  }
  async function switchTab(i) {
    ensureTabs();
    if (i === S.tab || i < 0 || i >= S.tabs.length) return;
    saveTab(); cancelSearch(); S.search = null;
    S.tab = i;
    S.back = S.tabs[i].back; S.fwd = S.tabs[i].fwd;
    try { await load(S.tabs[i].path, false); } catch (e) { /* כבר דווח */ }
    renderTabs();
  }
  async function closeTab(i) {
    ensureTabs();
    if (S.tabs.length <= 1) return;
    var wasActive = i === S.tab;
    S.tabs.splice(i, 1);
    if (!wasActive) { if (i < S.tab) S.tab--; renderTabs(); return; }
    S.tab = Math.min(i, S.tabs.length - 1);
    S.back = S.tabs[S.tab].back; S.fwd = S.tabs[S.tab].fwd;
    cancelSearch(); S.search = null;
    try { await load(S.tabs[S.tab].path, false); } catch (e) { /* כבר דווח */ }
    renderTabs();
  }

  /* ---------- בחירת עמודות והרחבתן ---------- */
  function columnsMenu(x, y) {
    var m = $('fx-menu');
    var names = { date: 'תאריך שינוי', type: 'סוג', size: 'גודל' };
    m.innerHTML = Object.keys(names).map(function (k) {
      return '<button class="fx-menu-item" role="menuitemcheckbox" aria-checked="' + (S.cols[k] !== false) + '" data-col="' + k + '"><span class="fx-chk">' + (S.cols[k] !== false ? '✓' : '') + '</span><span>' + names[k] + '</span></button>';
    }).join('') + '<button class="fx-menu-item" data-colreset="1"><span class="fx-chk"></span><span>איפוס רוחב העמודות</span></button>';
    m.hidden = false;
    var r = m.getBoundingClientRect(), vw = document.documentElement.clientWidth;
    m.style.left = Math.max(4, Math.min(x, vw - r.width - 4)) + 'px';
    m.style.top = y + 'px';
  }
  function bindColumns() {
    var head = $('fx-head');
    head.addEventListener('contextmenu', function (e) { e.preventDefault(); columnsMenu(e.clientX, e.clientY); });
    $('fx-menu').addEventListener('click', function (e) {
      var b = e.target.closest('[data-col],[data-colreset]');
      if (!b) return;
      e.stopPropagation(); hideMenu();
      if (b.dataset.colreset) S.colw = {};
      else S.cols[b.dataset.col] = S.cols[b.dataset.col] === false;
      savePrefs(); renderList();
    }, true);
    head.addEventListener('pointerdown', function (e) {
      var h = e.target.closest('[data-rs]');
      if (!h) return;
      e.preventDefault(); e.stopPropagation();
      var key = h.dataset.rs, col = h.parentNode, startX = e.clientX, start = col.getBoundingClientRect().width;
      var rtl = document.documentElement.dir !== 'ltr';
      h.setPointerCapture(e.pointerId);
      function move(ev) {
        var dx = ev.clientX - startX;
        S.colw[key] = Math.round(Math.max(48, Math.min(480, start + (rtl ? -dx : dx))));
        applyGrid();
      }
      function up() { h.removeEventListener('pointermove', move); h.removeEventListener('pointerup', up); savePrefs(); }
      h.addEventListener('pointermove', move); h.addEventListener('pointerup', up);
    });
    head.addEventListener('click', function (e) { if (e.target.closest('[data-rs]')) e.stopPropagation(); }, true);
    $('fx-tabs').addEventListener('click', function (e) {
      var x = e.target.closest('[data-closetab]');
      if (x) { e.stopPropagation(); closeTab(+x.dataset.closetab); return; }
      if (e.target.closest('[data-newtab]')) { newTab(''); return; }
      var t = e.target.closest('[data-tab]');
      if (t) switchTab(+t.dataset.tab);
    });
    $('fx-tabs').addEventListener('auxclick', function (e) {
      var t = e.target.closest('[data-tab]');
      if (e.button === 1 && t) { e.preventDefault(); closeTab(+t.dataset.tab); }
    });
    $('fx-side').addEventListener('auxclick', function (e) {
      var b = e.target.closest('.nav-item[data-path]');
      if (e.button === 1 && b) { e.preventDefault(); newTab(b.dataset.path); }
    });
  }

  /* ---------- הרשימה ---------- */
  var COLS = [
    { key: 'name', label: 'שם' },
    { key: 'date', label: 'תאריך שינוי' },
    { key: 'type', label: 'סוג' },
    { key: 'size', label: 'גודל' }
  ];
  function colOn(c) { return c.key === 'name' || S.cols[c.key] !== false; }
  /* תבנית העמודות לפי העמודות שנבחרו והרוחבים ששמורים */
  function applyGrid() {
    var root = document.documentElement;
    var parts = ['minmax(10em, 1fr)'];
    COLS.forEach(function (c) {
      if (c.key === 'name' || !colOn(c)) return;
      var w = S.colw[c.key];
      parts.push(w ? w + 'px' : 'var(--fx-col-' + c.key + ')');
    });
    root.style.setProperty('--fx-grid', parts.join(' '));
    root.style.setProperty('--fx-grid-where', parts.concat(['var(--fx-col-where)']).join(' '));
  }
  function renderHead() {
    applyGrid();
    $('fx-head').innerHTML = COLS.filter(colOn).map(function (c) {
      var on = S.sort.key === c.key;
      return '<button class="fx-col fx-col-' + c.key + (on ? ' on' : '') + '" data-sort="' + c.key + '">' + (S.recycle && c.key === 'date' ? 'תאריך מחיקה' : c.label) +
        (on ? '<span class="fx-sort-dir">' + (S.sort.dir === 'asc' ? '▲' : '▼') + '</span>' : '') +
        (c.key !== 'name' ? '<i class="fx-col-rs" data-rs="' + c.key + '" title="גררו לשינוי הרוחב"></i>' : '') + '</button>';
    }).join('') + (S.search ? '<span class="fx-col fx-col-where">מיקום</span>' : S.recycle ? '<span class="fx-col fx-col-where">מיקום מקורי</span>' : '');
    $('fx-head').classList.toggle('with-where', !!S.search || S.recycle);
  }

  /* שורת הקטע מהתוכן (חיפוש בתוכן), עם הדגשת המילה */
  function snipHtml(e) {
    if (!S.search || !e.snippet) return '';
    var q = S.search.query, sn = e.snippet, at = q ? sn.toLowerCase().indexOf(q.toLowerCase()) : -1;
    var h = at < 0 ? FX.esc(sn) : FX.esc(sn.slice(0, at)) + '<mark class="fx-hit">' + FX.esc(sn.slice(at, at + q.length)) + '</mark>' + FX.esc(sn.slice(at + q.length));
    return '<span class="fx-snip" dir="auto">' + h + '</span>';
  }
  function rowHtml(e, i) {
    var kind = FX.kindOf(e);
    var icon = e.icon || FX.kindInfo(kind).icon;
    var size = e.type === 'drive'
      ? (e.freeBytes != null ? FX.formatSize(e.freeBytes) + ' פנויים מתוך ' + FX.formatSize(e.totalBytes) : '')
      : (e.type === 'dir' ? '' : FX.formatSize(e.size));
    var cut = S.clip && S.clip.cut && S.clip.paths.indexOf(e.path) >= 0;
    var where = '';
    if (S.search) {
      var par = S.prov.parentOf(e.path);
      where = '<span class="fx-cell fx-cell-where" title="' + FX.esc(S.prov.displayPath(par)) + '">' + FX.esc(S.prov.displayPath(par)) + '</span>';
    } else if (S.recycle) {
      var orig = S.prov.parentOf(e.origPath || '') || '';
      where = '<span class="fx-cell fx-cell-where" title="' + FX.esc(e.origPath || '') + '">' + FX.esc(orig) + '</span>';
    }
    /* שורת מטא-דאטה משנית (סוג · גודל · תאריך), מוצגת רק בתצוגות "אריחים" ו"תוכן" (ר' fx.css) */
    var subMeta = [FX.typeLabel(e), size, e.modified ? FX.formatDate(e.modified) : ''].filter(Boolean).join(' · ');
    return '<div class="fx-row' + (e.snippet && S.search ? ' has-snip' : '') + (S.sel[e.path] ? ' sel' : '') + (i === S.focus ? ' focus' : '') + (cut ? ' cut' : '') + (e.hidden ? ' hid' : '') +
      '" role="option" aria-selected="' + (S.sel[e.path] ? 'true' : 'false') + '" data-i="' + i + '" draggable="false">' +
      '<span class="fx-cell fx-cell-name"><span class="fx-ic k-' + kind + '">' + ic(icon) + '</span><span class="fx-name-wrap"><span class="fx-name" title="' + FX.esc(e.name) + '">' + FX.esc(e.name) + '</span>' + snipHtml(e) +
      (subMeta ? '<span class="fx-sub-meta">' + FX.esc(subMeta) + '</span>' : '') + '</span></span>' +
      (S.cols.date !== false ? '<span class="fx-cell fx-cell-date">' + (e.modified ? FX.esc(FX.formatDate(e.modified)) : '') + '</span>' : '') +
      (S.cols.type !== false ? '<span class="fx-cell fx-cell-type">' + FX.esc(FX.typeLabel(e)) + '</span>' : '') +
      (S.cols.size !== false ? '<span class="fx-cell fx-cell-size">' + (e.type === 'drive' && e.totalBytes ? '<span class="fx-dbar' + (e.freeBytes < e.totalBytes / 10 ? ' low' : '') + '"><i style="width:' + Math.round(100 - e.freeBytes * 100 / e.totalBytes) + '%"></i></span>' : '') + FX.esc(size || '') + '</span>' : '') + where + '</div>';
  }

  function renderList() {
    renderHead();
    var list = $('fx-list');
    list.classList.toggle('with-where', !!S.search || S.recycle);
    list.classList.toggle('striped', !!S.stripes);
    var src = S.search ? S.search.hits : S.entries;
    if (S.isRoot && !S.search) S.view = src.slice();
    else S.view = FX.sortEntries(src, S.sort.key, S.sort.dir);
    if (!S.view.length) {
      var msg;
      if (S.search) msg = S.search.done ? 'לא נמצאו תוצאות' : 'מחפש…';
      else if (S.isRoot && S.prov.kind === 'fallback') msg = 'עדיין לא נוספו תיקיות. לחצו על "הוספת תיקייה" כדי לבחור תיקייה לעבודה.';
      else if (S.recycle) msg = 'סל המיחזור ריק';
      else msg = 'התיקייה ריקה';
      list.innerHTML = '<div class="fx-empty">' + ic(S.search ? 'search' : 'folderOpen') + '<span>' + FX.esc(msg) + '</span>' +
        (S.isRoot && !S.search && S.prov.caps.addFolder ? '<button class="btn btn-primary" data-add="1">' + ic('plus') + 'הוספת תיקייה</button>' : '') + '</div>';
    } else {
      list.innerHTML = S.view.map(rowHtml).join('');
    }
    renderStatus();
    if (FX.onSelection) FX.onSelection();
  }
  function refreshRows() {
    Array.prototype.forEach.call($('fx-list').querySelectorAll('.fx-row'), function (r) {
      var e = S.view[+r.dataset.i];
      var on = !!(e && S.sel[e.path]);
      r.classList.toggle('sel', on);
      r.setAttribute('aria-selected', on ? 'true' : 'false');
      r.classList.toggle('focus', +r.dataset.i === S.focus);
      r.classList.toggle('cut', !!(e && S.clip && S.clip.cut && S.clip.paths.indexOf(e.path) >= 0));
    });
    renderStatus(); renderCmds();
    if (FX.onSelection) FX.onSelection();
  }
  function renderStatus() {
    var sel = selected();
    var parts = [];
    if (S.search) {
      var s = S.search;
      parts.push(FX.formatNumber(s.hits.length) + ' תוצאות');
      if (!s.done) parts.push('נסרקו ' + FX.formatNumber(s.scanned || 0) + (s.current ? ' — ' + s.current : ''));
      else if (s.truncated) parts.push('הוצגו התוצאות הראשונות בלבד');
    } else {
      parts.push(FX.formatNumber(S.view.length) + ' פריטים');
      if (S.truncated) parts.push('הרשימה קוצרה');
    }
    if (sel.length) {
      var bytes = 0, files = 0;
      sel.forEach(function (e) { if (e.type === 'file' && e.size) { bytes += e.size; files++; } });
      parts.push(FX.formatNumber(sel.length) + ' נבחרו' + (files ? ' (' + FX.formatSize(bytes) + ')' : ''));
    }
    if (S.clip) parts.push((S.clip.cut ? 'לגזירה: ' : 'בלוח: ') + FX.formatNumber(S.clip.paths.length));
    var st = $('fx-status');
    st.innerHTML = '<span>' + parts.map(FX.esc).join('</span><span class="fx-dot">·</span><span>') + '</span>' +
      (S.search && !S.search.done ? '<button class="btn btn-text" id="fx-stop">' + ic('close') + 'עצירה</button>' : '');
    var stop = $('fx-stop');
    if (stop) stop.addEventListener('click', cancelSearch);
  }

  /* ---------- בחירה ---------- */
  function selected() {
    return S.view.filter(function (e) { return S.sel[e.path]; });
  }
  function selectOnly(i) {
    S.sel = {};
    if (S.view[i]) S.sel[S.view[i].path] = true;
    S.anchor = i; S.focus = i;
  }
  function clickRow(i, ev) {
    var e = S.view[i];
    if (!e) return;
    if (ev.shiftKey && S.anchor >= 0) {
      if (!(ev.ctrlKey || ev.metaKey)) S.sel = {};
      var a = Math.min(S.anchor, i), b = Math.max(S.anchor, i);
      for (var k = a; k <= b; k++) S.sel[S.view[k].path] = true;
      S.focus = i;
    } else if (ev.ctrlKey || ev.metaKey) {
      if (S.sel[e.path]) delete S.sel[e.path]; else S.sel[e.path] = true;
      S.anchor = i; S.focus = i;
    } else {
      selectOnly(i);
    }
    refreshRows();
  }
  function moveFocus(delta, ev) {
    if (!S.view.length) return;
    var i = S.focus < 0 ? (delta > 0 ? 0 : S.view.length - 1) : Math.max(0, Math.min(S.view.length - 1, S.focus + delta));
    if (ev.shiftKey) {
      if (S.anchor < 0) S.anchor = S.focus < 0 ? i : S.focus;
      S.sel = {};
      var a = Math.min(S.anchor, i), b = Math.max(S.anchor, i);
      for (var k = a; k <= b; k++) S.sel[S.view[k].path] = true;
      S.focus = i;
    } else if (ev.ctrlKey) {
      S.focus = i;
    } else selectOnly(i);
    refreshRows();
    var row = $('fx-list').querySelector('.fx-row[data-i="' + i + '"]');
    if (row) row.scrollIntoView({ block: 'nearest' });
  }
  function selectAll() {
    S.sel = {};
    S.view.forEach(function (e) { if (e.type !== 'drive') S.sel[e.path] = true; });
    refreshRows();
  }

  /* ---------- סרגל פעולות ---------- */
  var CMDS = [
    { id: 'newFolder', icon: 'newFolder', label: 'תיקייה חדשה', cap: 'mkdir' },
    { id: 'newFile', icon: 'newFile', label: 'קובץ חדש', cap: 'newFile' },
    { sep: true },
    { id: 'cut', icon: 'cut', label: 'גזירה', cap: 'move', key: 'Ctrl+X' },
    { id: 'copy', icon: 'copy', label: 'העתקה', cap: 'copy', key: 'Ctrl+C' },
    { id: 'paste', icon: 'paste', label: 'הדבקה', key: 'Ctrl+V' },
    { id: 'saveCopy', icon: 'downloads', label: 'שמירת עותק', cap: 'saveCopy' },
    { sep: true },
    { id: 'rename', icon: 'rename', label: 'שינוי שם', cap: 'rename', key: 'F2' },
    { id: 'delete', icon: 'trash', label: 'מחיקה', key: 'Delete', bin: true },
    { id: 'restore', icon: 'back', label: 'שחזור', binOnly: true },
    { id: 'emptyBin', icon: 'trash', label: 'ריקון סל המיחזור', binOnly: true },
    { sep: true },
    { id: 'open', icon: 'open', label: 'פתיחה', key: 'Enter' },
    { id: 'reveal', icon: 'reveal', label: 'הצגה בסייר של Windows', cap: 'reveal' },
    { id: 'openNewTab', icon: 'open', label: 'פתיחה בכרטיסייה חדשה', menuOnly: true },
    { id: 'openInReader', icon: 'open', label: 'פתיחה בעיון של אוצריא', menuOnly: true }, /* 4א */
    { id: 'openLocation', icon: 'folderOpen', label: 'פתיחת מיקום הקובץ', menuOnly: true },
    { id: 'copyPath', icon: 'copy', label: 'העתקה כנתיב', key: 'Ctrl+Shift+C', menuOnly: true },
    { id: 'eject', icon: 'usb', label: 'הוצאה בבטחה (USB)', menuOnly: true }, /* #20 */
    { id: 'props', icon: 'info', label: 'מאפיינים', cap: 'properties', key: 'Alt+Enter' },
    { id: 'zip', icon: 'copy', label: 'דחיסה ל-ZIP', menuOnly: true },
    { id: 'unzip', icon: 'folderOpen', label: 'חילוץ כאן', menuOnly: true },
    { id: 'sendTo', icon: 'usb', label: 'שליחה לכונן נייד', menuOnly: true },
    { id: 'eject', icon: 'usb', label: 'הוצאה בטוחה', menuOnly: true }
  ];
  /* 4א: קובץ שהוא ספר אישי בספריית אוצריא — פותחים אותו בעיון של אוצריא (התאמה לפי שם הספר) */
  async function openInReader(e) {
    var stem = String(e.name || '').replace(/\.[^.]+$/, '');
    var list = null;
    try { list = await window.Otzaria.call('library.findBooks', { query: stem, limit: 20 }); } catch (err) { list = null; }
    list = list && (list.data !== undefined ? list.data : list);
    var hit = Array.isArray(list) && (list.find(function (b) { return b && b.title === stem; }) || (list.length === 1 ? list[0] : null));
    if (!hit) { ui.toast('הקובץ לא נמצא כספר בספריית אוצריא', 'info'); return; }
    try { await window.Otzaria.call('reader.openBook', hit); } catch (err) { ui.toast('הפתיחה בעיון נכשלה', 'error'); }
  }
  function inFolder() { return !S.isRoot && !S.search && !S.recycle; }
  function recycleCmdState(id) {
    var n = selected().length;
    switch (id) {
      case 'restore': return { on: n > 0 };
      case 'delete': return { on: n > 0 };
      case 'emptyBin': return { on: S.entries.length > 0 };
    }
    return { on: false };
  }
  function cmdState(id) {
    if (S.recycle) return recycleCmdState(id);
    var p = S.prov, sel = selected(), n = sel.length;
    var hasDrive = sel.some(function (e) { return e.type === 'drive' || e.approvedRoot; });
    switch (id) {
      case 'newFolder': return { on: inFolder() && p.caps.mkdir, why: p.capReasons && p.capReasons.mkdir };
      case 'newFile': return { on: inFolder() && p.caps.newFile, why: p.capReasons && p.capReasons.newFile };
      case 'cut': return { on: n > 0 && !hasDrive && p.caps.move };
      case 'copy': return { on: n > 0 && !hasDrive && p.caps.copy, why: p.capReasons && p.capReasons.copy };
      case 'paste': return { on: inFolder() && (!!S.clip || p.caps.clipboardSync) };
      case 'saveCopy': return { on: n === 1 && sel[0].type === 'file' && !!p.caps.saveCopy };
      case 'rename': return { on: n === 1 && !hasDrive && p.caps.rename };
      case 'delete': return { on: n > 0 && !hasDrive };
      case 'open': return { on: n === 1 };
      case 'reveal': return { on: n >= 1 && p.caps.reveal && sel[0].type !== 'drive' };
      case 'props': return { on: (n === 1 || inFolder()) && p.caps.properties };
      case 'openNewTab': return { on: n === 1 && (sel[0].type === 'dir' || sel[0].type === 'drive') };
      case 'openInReader': return { on: n === 1 && sel[0].type !== 'dir' && sel[0].type !== 'drive' && /\.(txt|docx?|pdf|html?)$/i.test(sel[0].name || '') && !!window.Otzaria };
      case 'openLocation': return { on: n === 1 && !S.recycle && !!S.search };
      case 'copyPath': return { on: n >= 1 };
      case 'eject': return { on: n === 1 && p.kind === 'bridge' && (sel[0].type === 'drive' || FX.win.isDriveRoot(sel[0].path)) && sel[0].driveType === 'removable' };
      default: if (FX.packb && FX.packb.state) return FX.packb.state(id, sel, inFolder());
    }
    return { on: false };
  }
  function renderCmds() {
    if (!S.prov) return;
    var bar = $('fx-cmds');
    bar.innerHTML = CMDS.filter(function (c) {
      if (S.recycle) return !c.sep && !!(c.bin || c.binOnly);
      if (c.binOnly || c.menuOnly) return false;
      if (c.sep) return true;
      if (c.cap === 'saveCopy') return !!S.prov.caps.saveCopy;
      if (c.cap === 'reveal') return !!S.prov.caps.reveal;
      return true;
    }).map(function (c) {
      if (c.sep) return '<span class="fx-cmd-sep"></span>';
      var st = cmdState(c.id);
      var tip = c.label + (c.key ? ' (' + c.key + ')' : '') + (!st.on && st.why ? ' — ' + st.why : '');
      return '<button class="btn btn-text fx-cmd" data-cmd="' + c.id + '" title="' + FX.esc(tip) + '"' + (st.on ? '' : ' disabled') + '>' +
        ic(c.icon) + '<span>' + (S.recycle && c.id === 'delete' ? 'מחיקה לצמיתות' : c.label) + '</span></button>';
    }).join('');
  }
  function run(id, ev) {
    var st = cmdState(id);
    if (!st.on) { if (st.why) ui.toast(st.why, 'info'); return; }
    if (S.recycle) {
      if (id === 'restore') return restoreSel();
      if (id === 'delete') return purgeSel();
      if (id === 'emptyBin') return emptyBin();
      return;
    }
    switch (id) {
      case 'newFolder': return newItem(true);
      case 'newFile': return newItem(false);
      case 'cut': return setClip(true);
      case 'copy': return setClip(false);
      case 'paste': return paste();
      case 'saveCopy': return saveCopy();
      case 'rename': return rename();
      case 'delete': return del(ev && ev.shiftKey);
      case 'open': return openEntry(selected()[0]);
      case 'reveal': return S.prov.reveal(selected()[0].path).catch(function (e) { opError(e, 'ההצגה נכשלה'); });
      case 'props': return props();
      case 'openNewTab': return newTab(selected()[0].path);
      case 'openInReader': return openInReader(selected()[0]);
      case 'openLocation': return openLocation(selected()[0]);
      case 'zip': case 'unzip': case 'sendTo': case 'eject': return FX.packb[id](selected(), S.path);
      case 'copyPath': return copyText(selected().map(function (x) { return S.prov.displayPath(x.path); }).join('\n')).then(function () { ui.toast('הנתיב הועתק', 'success'); });
    }
  }
  /* "פתיחת מיקום": קופצים לתיקייה שמכילה את הקובץ ומסמנים אותו (כמו ב-Windows) */
  async function openLocation(e) {
    var par = S.prov.parentOf(e.path);
    if (S.search) { S.search.cancelled = true; S.search = null; }
    try { await load(par, true); } catch (er) { return; }
    var i = S.view.findIndex(function (x) { return S.prov.same(x.path, e.path); });
    if (i >= 0) {
      S.sel = {}; S.sel[S.view[i].path] = true; S.focus = i; S.anchor = i;
      refreshRows();
      var row = $('fx-list').querySelector('.fx-row.focus');
      if (row) row.scrollIntoView({ block: 'center' });
    }
  }

  /* ---------- פעולות ---------- */
  function validName(v) {
    var s = String(v || '').trim();
    if (!s) return 'יש להזין שם';
    if (/[\\/:*?"<>|]/.test(s)) return 'השם אינו יכול לכלול את התווים \\ / : * ? " < > |';
    if (/^\.+$/.test(s)) return 'שם לא תקין';
    return '';
  }
  /* A5: סוגי קבצים חדשים (קובץ ריק; Word/RTF/HTML פותחים קובץ ריק כמסמך חדש) */
  var NEW_TYPES = [
    { value: 'txt', label: 'מסמך טקסט (.txt)' }, { value: 'docx', label: 'מסמך Word (.docx)' },
    { value: 'rtf', label: 'מסמך RTF (.rtf)' }, { value: 'md', label: 'Markdown (.md)' },
    { value: 'csv', label: 'טבלה CSV (.csv)' }, { value: 'html', label: 'דף HTML (.html)' }, { value: 'json', label: 'JSON (.json)' }
  ];
  async function newItem(isDir) {
    var base = FX.t(isDir ? 'תיקייה חדשה' : 'מסמך חדש.txt');
    var names = {}; S.entries.forEach(function (e) { names[e.name.toLowerCase()] = 1; });
    var name = base, n = 2;
    while (names[name.toLowerCase()]) name = isDir ? base + ' (' + (n++) + ')' : base.replace(/\.txt$/, '') + ' (' + (n++) + ').txt';
    var made = null;
    var r = await ui.dialog({
      title: isDir ? 'תיקייה חדשה' : 'קובץ חדש', icon: 'info',
      fields: isDir ? [{ name: 'name', label: 'שם', value: name }] : [{ name: 'name', label: 'שם', value: name.replace(/\.txt$/, '') },
        { name: 'ext', type: 'select', label: 'סוג', value: 'txt', options: NEW_TYPES }],
      validate: function (v) { return validName(v.name); },
      onSubmit: async function (v) {
        var nm = v.name.trim();
        if (!isDir && v.ext && !/\.[^.]+$/.test(nm)) nm += '.' + v.ext; // A5: כל הסוגים
        if (!isDir && !/\.[^.]+$/.test(nm)) nm += '.txt';
        made = isDir ? await S.prov.mkdir(S.path, nm) : await S.prov.newTextFile(S.path, nm);
        return nm;
      },
      onOpen: function (form) {
        var inp = form.querySelector('input[name=name]');
        setTimeout(function () { var dot = isDir ? -1 : inp.value.lastIndexOf('.'); inp.setSelectionRange(0, dot > 0 ? dot : inp.value.length); }, 80);
      }
    });
    if (!r) return;
    var newPath = made && made.path ? made.path : S.prov.join(S.path, r);
    await reloadSelect([newPath]);
    // קובץ טקסט חדש הוא ריק - הצופה הפנימי (FX.viewText) תמיד לקריאה בלבד (ללא עריכה בכוונה,
    // כדי שלא "ייראה שבור": לפני התיקון, לחיצה על הקובץ החדש פתחה תצוגה ריקה וקבועה בלי אפשרות
    // להקליד לתוכה. כאן פותחים ישר ביישום חיצוני (Notepad וכו') כדי שאפשר יהיה לכתוב משהו בפועל
    if (!isDir) {
      if (S.prov.caps.open) S.prov.open(newPath).catch(function (err) { opError(err, 'הפתיחה נכשלה'); });
      else ui.toast('הקובץ נוצר ריק. לעריכה יש לפתוח אותו ביישום חיצוני (דורש אחת שאלתי)', 'info');
    }
  }
  async function reloadSelect(paths) {
    try { await load(S.path, false); } catch (e) { return; }
    S.sel = {};
    paths.forEach(function (p) {
      var i = S.view.findIndex(function (e) { return S.prov.same(e.path, p); });
      if (i >= 0) { S.sel[S.view[i].path] = true; S.focus = i; S.anchor = i; }
    });
    refreshRows();
    var row = $('fx-list').querySelector('.fx-row.focus');
    if (row) row.scrollIntoView({ block: 'nearest' });
  }

  function setClip(cut) {
    var paths = selected().map(function (e) { return e.path; });
    S.clip = { paths: paths, cut: cut, kind: S.prov.kind };
    if (S.prov.caps.clipboardSync) S.prov.clipboardSet(paths, cut).catch(function () { /* לוח Windows אופציונלי */ });
    ui.toast((cut ? 'נגזרו ' : 'הועתקו ') + FX.formatNumber(paths.length) + ' פריטים', 'info');
    refreshRows();
  }

  async function paste() {
    var clip = S.clip;
    if (S.prov.caps.clipboardSync) {
      try {
        var w = await S.prov.clipboardGet();
        if (w && w.paths && w.paths.length) {
          var same = clip && clip.paths.length === w.paths.length && clip.paths.every(function (p, i) { return S.prov.same(p, w.paths[i]); });
          if (!same) clip = { paths: w.paths, cut: !!w.cut, kind: 'bridge' };
        }
      } catch (e) { /* נשתמש בלוח הפנימי */ }
    }
    if (!clip || !clip.paths.length) { ui.toast('אין פריטים בלוח', 'info'); return; }
    if (clip.kind !== S.prov.kind) { S.clip = null; ui.toast('הפריטים בלוח שייכים למצב עבודה אחר', 'error'); refreshRows(); return; }
    var dest = S.path;
    var bad = clip.paths.filter(function (p) { return S.prov.isInside(dest, p); });
    if (bad.length) { ui.toast('אי אפשר להדביק תיקייה לתוך עצמה', 'error'); return; }
    if (clip.cut && clip.paths.every(function (p) { return S.prov.same(S.prov.parentOf(p), dest); })) {
      ui.toast('הפריטים כבר נמצאים בתיקייה הזו', 'info'); return;
    }
    var conflict = 'rename';
    var names = {}; S.entries.forEach(function (e) { names[e.name.toLowerCase()] = 1; });
    var clash = clip.paths.filter(function (p) { return names[String(S.prov.nameOf(p)).toLowerCase()] && !S.prov.same(S.prov.parentOf(p), dest); });
    if (clash.length) {
      var ans = await ui.dialog({
        title: 'פריטים בשם זהה', icon: 'warning',
        body: '<p>' + FX.formatNumber(clash.length) + ' מהפריטים כבר קיימים בתיקייה הזו (למשל <b>' + ui.isolateLtr(FX.esc(S.prov.nameOf(clash[0]))) + '</b>). מה לעשות?</p>',
        actions: [{ label: 'ביטול', value: false }, { label: 'דילוג', value: 'skip' }, { label: 'החלפה', value: 'overwrite' }, { label: 'שמירת שניהם', value: 'rename', kind: 'primary' }]
      });
      if (!ans) return;
      conflict = ans === true ? 'rename' : ans;
    }
    busy(true, clip.cut ? 'מעביר…' : 'מעתיק…');
    try {
      var r = clip.cut ? await S.prov.move(clip.paths, dest, conflict) : await S.prov.copy(clip.paths, dest, conflict);
      reportBatch(r, clip.cut ? 'הועברו' : 'הועתקו', (r && (clip.cut ? r.moved : r.copied)) || 0);
      if (clip.cut) S.clip = null;
    } catch (e) { opError(e, clip.cut ? 'ההעברה נכשלה' : 'ההעתקה נכשלה'); }
    busy(false);
    await reloadSelect(clip.paths.map(function (p) { return S.prov.join(dest, S.prov.nameOf(p)); }));
  }
  function reportBatch(r, verb, n) {
    var failed = (r && r.failed) || [];
    var skipped = (r && r.skipped) || 0;
    var msg = verb + ' ' + FX.formatNumber(n) + ' פריטים' + (skipped ? ', ' + FX.formatNumber(skipped) + ' דולגו' : '');
    if (failed.length) {
      ui.toast(msg + '. נכשלו ' + FX.formatNumber(failed.length) + ': ' + (failed[0].message || FX.errorText(failed[0])), 'error');
    } else ui.toast(msg, 'success');
  }

  async function saveCopy() {
    var e = selected()[0];
    busy(true, 'שומר עותק…');
    try {
      var ok = await S.prov.saveCopy(e);
      if (ok) ui.toast('העותק נשמר', 'success');
    } catch (err) { opError(err, 'שמירת העותק נכשלה'); }
    busy(false);
  }

  async function rename() {
    var e = selected()[0];
    var r = await ui.dialog({
      title: 'שינוי שם', icon: 'info',
      fields: [{ name: 'name', label: 'שם חדש', value: e.name }],
      validate: function (v) {
        var m = validName(v.name);
        if (m) return m;
        var t = v.name.trim().toLowerCase();
        if (t !== e.name.toLowerCase() && S.entries.some(function (x) { return x.name.toLowerCase() === t; })) return 'כבר קיים פריט בשם הזה';
        return '';
      },
      onSubmit: async function (v) {
        var nm = v.name.trim();
        if (nm === e.name) return nm;
        var res = await S.prov.rename(e.path, nm);
        return { name: nm, path: res && res.path };
      },
      onOpen: function (form) {
        var inp = form.querySelector('input[name=name]');
        setTimeout(function () { var dot = e.type === 'dir' ? -1 : inp.value.lastIndexOf('.'); inp.setSelectionRange(0, dot > 0 ? dot : inp.value.length); }, 80);
      }
    });
    if (!r || r === e.name) return;
    if (S.clip) S.clip = null;
    if (S.search) { refresh(); return; }
    await reloadSelect([r.path || S.prov.join(S.path, r.name)]);
  }

  /* ---------- סל המיחזור: שחזור, מחיקה לצמיתות וריקון ---------- */
  function binIds(sel) { return sel.map(function (e) { return e.recycleId; }); }
  async function restoreSel() {
    var sel = selected();
    busy(true, 'משחזר…');
    try {
      var r = await S.prov.recycleRestore(binIds(sel));
      var failed = (r && r.failed) || [];
      reportBatch(r, 'שוחזרו', (r && r.restored != null) ? r.restored : sel.length - failed.length);
    } catch (e) { opError(e, 'השחזור נכשל'); }
    busy(false);
    refresh();
  }
  async function purgeSel() {
    var sel = selected();
    var what = sel.length === 1 ? '<b>' + ui.isolateLtr(FX.esc(sel[0].name)) + '</b>' : FX.formatNumber(sel.length) + ' פריטים';
    var ok = await ui.confirm('מחיקה לצמיתות', '<p>' + what + ' יימחקו <b>לצמיתות</b> מסל המיחזור. לא ניתן לשחזר אותם.</p>', 'מחיקה לצמיתות', 'danger');
    if (!ok) return;
    busy(true, 'מוחק…');
    try {
      var r = await S.prov.recycleDelete(binIds(sel));
      var failed = (r && r.failed) || [];
      reportBatch(r, 'נמחקו', (r && r.deleted != null) ? r.deleted : sel.length - failed.length);
    } catch (e) { opError(e, 'המחיקה נכשלה'); }
    busy(false);
    refresh();
  }
  async function emptyBin() {
    var ok = await ui.confirm('ריקון סל המיחזור', '<p>כל הפריטים שבסל המיחזור יימחקו <b>לצמיתות</b>. לא ניתן לשחזר אותם.</p>', 'ריקון הסל', 'danger');
    if (!ok) return;
    busy(true, 'מרוקן…');
    try {
      await S.prov.recycleEmpty();
      ui.toast('סל המיחזור רוקן', 'success');
    } catch (e) { opError(e, 'ריקון הסל נכשל'); }
    busy(false);
    refresh();
  }

  async function del(permanent) {
    var sel = selected();
    var p = S.prov;
    var perm = !!permanent || !p.caps.recycle;
    var what = sel.length === 1 ? '<b>' + ui.isolateLtr(FX.esc(sel[0].name)) + '</b>' : FX.formatNumber(sel.length) + ' פריטים';
    var body = perm
      ? '<p>' + what + ' יימחקו <b>לצמיתות</b>. לא ניתן לשחזר אותם.</p>' + (!p.caps.recycle && p.capReasons ? '<p class="muted small">' + FX.esc(p.capReasons.recycle) + '</p>' : '')
      : '<p>' + what + ' יועברו לסל המיחזור.</p><p class="muted small">Shift+Delete מוחק לצמיתות.</p>';
    var ok = await ui.confirm(perm ? 'מחיקה לצמיתות' : 'מחיקה', body, perm ? 'מחיקה לצמיתות' : 'מחיקה', 'danger');
    if (!ok) return;
    busy(true, 'מוחק…');
    try {
      var r = p.kind === 'bridge'
        ? await p.remove(sel.map(function (e) { return e.path; }), perm)
        : await p.remove(sel.map(function (e) { return e.path; }), sel);
      var failed = (r && r.failed) || [];
      reportBatch(r, perm ? 'נמחקו' : 'הועברו לסל המיחזור', (r && r.deleted != null) ? r.deleted : sel.length - failed.length);
    } catch (e) { opError(e, 'המחיקה נכשלה'); }
    busy(false);
    if (S.clip) S.clip.paths = S.clip.paths.filter(function (x) { return !sel.some(function (e) { return e.path === x; }); });
    if (S.search) {
      var gone = {}; sel.forEach(function (e) { gone[e.path] = 1; });
      S.search.hits = S.search.hits.filter(function (h) { return !gone[h.path]; });
      S.sel = {}; renderList(); renderCmds();
    } else refresh();
  }

  /* ---------- הגדרות פתיחה (לכל צופה, נשמרות בדפדפן) ---------- */
  var OPT_KEY = 'fx.options';
  var OPT_DEF = { pdf: 'internal', word: 'two', text: 'internal', image: 'internal', singleClickOpen: false, askApp: true, libFolders: false };
  function opts() {
    var o = {};
    try { o = JSON.parse(localStorage.getItem(OPT_KEY) || '{}') || {}; } catch (e) { o = {}; }
    try { var wm = localStorage.getItem('fx.docCols'); if (!o.word && (wm === '1' || wm === '2')) o.word = wm === '1' ? 'one' : 'two'; } catch (e) { /* כלום */ }
    return Object.assign({}, OPT_DEF, o);
  }
  function saveOpts(o) {
    try { localStorage.setItem(OPT_KEY, JSON.stringify(o)); localStorage.setItem('fx.docCols', o.word === 'one' ? '1' : '2'); } catch (e) { /* כלום */ }
  }
  function sel(name, label, cur, choices) {
    return '<label class="field"><span class="field-label">' + label + '</span><select class="input fx-select" name="' + name + '">' +
      choices.map(function (c) { return '<option value="' + c[0] + '"' + (c[0] === cur ? ' selected' : '') + '>' + c[1] + '</option>'; }).join('') +
      '</select></label>';
  }
  /* ---------- מסך הגדרות בסגנון אוצריא: קטגוריות בצד, כרטיסים, ושמירה מיידית ---------- */
  var SET_CATS = [
    ['open', 'פתיחת קבצים', 'open'],
    ['view', 'תצוגה', 'grid'],
    ['security', 'נעילה והגבלה', 'lock'],
    ['general', 'כללי', 'settings'],
    ['about', 'אודות', 'info']
  ];
  function seg(name, cur, choices) {
    return '<div class="fx-seg" role="radiogroup" data-seg="' + name + '">' + choices.map(function (c) {
      return '<button type="button" class="fx-seg-btn' + (String(c[0]) === String(cur) ? ' on' : '') + '" role="radio" aria-checked="' +
        (String(c[0]) === String(cur) ? 'true' : 'false') + '" data-v="' + c[0] + '">' + c[1] + '</button>';
    }).join('') + '</div>';
  }
  function setRow(title, hint, control) {
    return '<div class="fx-set-row"><div class="fx-set-text"><b>' + title + '</b>' + (hint ? '<span class="muted small">' + hint + '</span>' : '') + '</div>' + control + '</div>';
  }
  function setSwitch(name, on) {
    return '<button type="button" class="fx-switch' + (on ? ' on' : '') + '" role="switch" aria-checked="' + (on ? 'true' : 'false') + '" data-sw="' + name + '"><span></span></button>';
  }
  function setCat(id) {
    var o = opts();
    var ext = [['external', 'יישום חיצוני'], ['ask', 'לשאול כל פעם']]; /* #16 */
    var pref = window.I18N ? window.I18N.pref() : 'auto';
    if (id === 'open') return '<div class="fx-set-card"><div class="fx-set-title">' + ic('open') + 'ניווט בתיקיות</div>' +
      setRow('פתיחת תיקיות בקליק יחיד', 'קליק אחד (ללא Ctrl/Shift) על תיקייה ייכנס אליה מיד, כמו קליק כפול. בחירה מרובה עם Ctrl/Shift ממשיכה לעבוד כרגיל', setSwitch('singleClickOpen', !!o.singleClickOpen)) +
      '</div><div class="fx-set-card"><div class="fx-set-title">' + ic('open') + 'באיזה צופה לפתוח</div>' +
      setRow('קובצי PDF', 'הצופה הפנימי, צופה PDF+OCR (תוסף), או התוכנה שמוגדרת ב-Windows', seg('pdf', o.pdf, [['internal', 'צופה פנימי'], ['ocr', 'PDF+OCR']].concat(ext))) +
      setRow('מסמכי Word', 'שני טורים נוחים לקריאה, טור אחד, או תוסף וורד לאוצריא', seg('word', o.word, [['two', 'שני טורים'], ['one', 'טור אחד'], ['wordplugin', 'וורד לאוצריא']].concat(ext))) +
      setRow('קובצי טקסט', '', seg('text', o.text, [['internal', 'צופה פנימי']].concat(ext))) +
      setRow('תמונות', '', seg('image', o.image, [['internal', 'תצוגה מקדימה']].concat(ext))) +
      setRow('לשאול באיזו תוכנה לפתוח', 'בכל פתיחה ביישום חיצוני מוצג חלון הבחירה של Windows, במקום לפתוח תמיד באותה תוכנה. בנעילה — תמיד תוכנת ברירת המחדל.', setSwitch('askApp', o.askApp !== false)) +
      setRow('תיקיות הגישה המהירה בספריית אוצריא', 'תיקיות הגישה המהירה מוצגות בספרייה תחת "סייר קבצים", ולחיצה פותחת אותן בסייר. רק התיקיות — לא הקבצים.', setSwitch('libFolders', !!o.libFolders)) +
      '<p class="muted small">פתיחה ביישום חיצוני דורשת את אחת שאלתי; בלעדיו הקובץ נפתח בצופה הפנימי.</p></div>';
    if (id === 'view') return '<div class="fx-set-card"><div class="fx-set-title">' + ic('grid') + 'תצוגת הקבצים</div>' +
      setRow('פריסה', 'כמו בתפריט "תצוגה" של סייר הקבצים של Windows', seg('mode', FX.layout.mode(), [
        ['xlicons', 'סמלים גדולים מאוד'], ['licons', 'סמלים גדולים'], ['micons', 'סמלים בינוניים'], ['sicons', 'סמלים קטנים'],
        ['list', 'רשימה'], ['details', 'פרטים'], ['tiles', 'אריחים'], ['content', 'תוכן']
      ])) +
      setRow('חלונית תצוגה מקדימה', 'פאנל בצד שמראה תצוגה מקדימה של הקובץ שנבחר (טקסט, תמונה, PDF ועוד)', setSwitch('preview', FX.layout.isPreview())) +
      setRow('חלונית פרטים', 'פאנל בצד שמראה מידע על הפריט שנבחר (סוג, גודל, תאריך) בלי לטעון תצוגה מקדימה. חולק את אותו אזור עם חלונית התצוגה המקדימה', setSwitch('details', FX.layout.isDetailsPane())) +
      setRow('גודל טקסט', 'גם ב-Ctrl+ / Ctrl- / Ctrl+0', seg('zoom', String(FX.layout.zoom()), [['0.8', 'קטן מאוד'], ['0.9', 'רגיל'], ['1', 'בינוני'], ['1.1', 'גדול'], ['1.2', 'גדול מאוד'], ['1.35', 'ענק']])) +
      setRow('שורות מפוספסות', 'צבע מתחלף בין השורות', setSwitch('stripes', S.stripes)) +
      setRow('עמודות להצגה', 'רלוונטי לתצוגת "פרטים". אפשר גם בלחיצה ימנית על כותרת העמודות. הרחבת עמודה: גרירת הקצה שלה', seg('cols', '', [['date', 'תאריך ' + (S.cols.date !== false ? '✓' : '✗')], ['type', 'סוג ' + (S.cols.type !== false ? '✓' : '✗')], ['size', 'גודל ' + (S.cols.size !== false ? '✓' : '✗')]])) +
      setRow('הצגת קבצים מוסתרים', 'קבצים ותיקיות עם התכונה "מוסתר" יוצגו בצבע חלש', setSwitch('hidden', S.showHidden)) +
      '</div>';
    if (id === 'general') return '<div class="fx-set-card"><div class="fx-set-title">' + ic('settings') + 'כללי</div>' +
      setRow('שפת הממשק', 'אוטומטי = לפי השפה של אוצריא', seg('lang', pref, [['auto', 'אוטומטי'], ['he', 'עברית'], ['en', 'English']])) +
      setRow('שליחת הערה או דיווח על תקלה', '', '<button type="button" class="btn btn-tonal" data-act="fb">שליחת הערה</button>') +
      setRow('איפוס הגדרות הסייר', 'מחזיר את כל ההגדרות שבמסך הזה לברירת המחדל', '<button type="button" class="btn btn-tonal" data-act="reset">איפוס</button>') +
      '</div>';
    if (id === 'security') return '<div class="fx-set-card fx-set-lockcard"><button type="button" class="btn btn-primary fx-lock-btn" data-act="open-ss-lock">' + ic('lock') + 'הגדרות הנעילה המלאות בהגדרות מערכת</button></div>' +
      '<div id="fx-pol-host">' + ui.loading('טוען…') + '</div>';
    var b = FX.Bridge && FX.Bridge.info;
    return '<div class="fx-set-card fx-set-hero"><div class="fx-set-logo">' + ic('folder') + '</div><div>' +
      '<img src="icon/brand.svg" alt="אחת שאלתי" width="144" height="144" style="display:block;margin:0 auto 12px">' +
      '<h3>סייר קבצים</h3><p class="muted">גרסה ' + FX.esc(FX.VERSION) + ' · חד בדרא, יאיר דניאל</p>' +
      '<p>עיון וניהול קבצים מתוך אוצריא: העתקה, העברה, מחיקה, שינוי שם, חיפוש, סל מיחזור, וצפייה ב-PDF, Word, טקסט ותמונות. עם אחת שאלתי — גישה לכל המחשב; בלעדיו — לתיקיות שאישרתם.</p></div></div>' +
      '<div class="fx-set-card fx-set-memorial"><p>לעילוי נשמת</p><b>אסתר גיטה דינר בת רבי מיכאל שלום הלוי שיבדלט"א</b><p>תנצב"ה</p></div>' +
      '<div class="fx-set-card"><div class="fx-set-title">' + ic('info') + 'אחת שאלתי</div>' +
      setRow(b ? 'מחובר · גרסה ' + FX.esc(b.version || '?') : 'הגשר אינו מחובר', b ? '' : 'בלי הגשר הסייר עובד על תיקיות שאישרתם בלבד',
        '') + /* #59: בדיקת עדכונים רק בגשר */
      '</div>'; /* #19: יומן השינויים ועמוד ההוצאות נמצאים בגשר (אודות) */
  }
  function settingsDialog(cat) {
    var old = $('fx-setpage');
    if (old) old.remove();
    var page = document.createElement('div');
    page.id = 'fx-setpage';
    page.className = 'fx-setpage fade-in';
    page.setAttribute('role', 'dialog');
    page.setAttribute('aria-label', 'הגדרות');
    page.innerHTML = '<header class="fx-set-head"><button type="button" class="icon-btn" data-act="close" title="חזרה (Esc)" aria-label="חזרה">' + ic('back') + '</button>' +
      '<h2>הגדרות</h2></header><div class="fx-set-body"><nav class="fx-set-nav">' +
      '<input type="search" class="fx-set-search" placeholder="חיפוש הגדרה…" aria-label="חיפוש הגדרה">' +
      SET_CATS.map(function (c) { return '<button type="button" class="fx-set-navbtn" data-cat="' + c[0] + '">' + ic(c[2]) + '<span>' + c[1] + '</span></button>'; }).join('') +
      '</nav><div class="fx-set-content"></div></div>';
    document.body.appendChild(page);
    var content = page.querySelector('.fx-set-content');
    function showCat(id) {
      page.querySelectorAll('.fx-set-navbtn').forEach(function (x) { x.classList.toggle('on', x.dataset.cat === id); });
      content.innerHTML = setCat(id);
      content.scrollTop = 0;
      if (id === 'security') {
        var ssb = content.querySelector('[data-act=open-ss-lock]');
        if (ssb) ssb.addEventListener('click', function () {
          openInPlugin(SS_PLUGIN, { section: 'fxpolicy' }).catch(function (er) { ui.toast((er && er.message) || 'תוסף הגדרות מערכת לא מותקן', 'error'); });
        });
        var host = content.querySelector('#fx-pol-host');
        FX.Policy.refresh().then(function () {
          if (!host.isConnected) return;
          host.innerHTML = FX.Policy.editorHtml();
          FX.Policy.bindEditor(host);
        });
      }
      try { localStorage.setItem('fx.setCat', id); } catch (e) { /* כלום */ }
    }
    /* חיפוש הגדרה: עובר על כל הקטגוריות (חוץ מנעילה ואודות, שנטענות דינמית) ומציג את השורות שמתאימות, מקובצות לפי קטגוריה */
    var search = page.querySelector('.fx-set-search');
    search.addEventListener('input', function () {
      var q = search.value.trim().toLowerCase();
      if (!q) { showCat(localStorageGet('fx.setCat') || SET_CATS[0][0]); return; }
      var html = '';
      SET_CATS.forEach(function (c) {
        if (c[0] === 'security' || c[0] === 'about') return;
        var tmp = document.createElement('div');
        tmp.innerHTML = setCat(c[0]);
        var rows = Array.prototype.filter.call(tmp.querySelectorAll('.fx-set-row'), function (r) { return r.textContent.toLowerCase().indexOf(q) >= 0; });
        if (rows.length) html += '<div class="fx-set-card"><div class="fx-set-title">' + ic(c[2]) + c[1] + '</div>' + rows.map(function (r) { return r.outerHTML; }).join('') + '</div>';
      });
      page.querySelectorAll('.fx-set-navbtn').forEach(function (x) { x.classList.remove('on'); });
      content.innerHTML = html || '<p class="muted">לא נמצאו הגדרות</p>';
      content.scrollTop = 0;
    });
    function localStorageGet(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
    function close() { page.remove(); document.removeEventListener('keydown', onKey, true); $('fx-main').focus(); }
    function onKey(e) { if (e.key === 'Escape') { e.stopPropagation(); close(); } }
    document.addEventListener('keydown', onKey, true);
    page.addEventListener('click', async function (e) {
      var t = e.target.closest('button');
      if (!t) return;
      if (t.dataset.cat) { showCat(t.dataset.cat); return; }
      var act = t.dataset.act;
      if (act === 'close') { close(); return; }
      if (act === 'fb') { sendFeedback(); return; }
      if (act === 'changelog') { openUrl(FX.CHANGELOG_URL); return; }
      if (act === 'releases') { openUrl('https://github.com/Hadabadra/achat-shaalti/releases/latest'); return; }
      if (act === 'reset') {
        if (!(await ui.confirm('איפוס הגדרות', '<p>להחזיר את הגדרות הסייר לברירת המחדל?</p>', 'איפוס'))) return;
        saveOpts(Object.assign({}, OPT_DEF)); FX.layout.setZoom(0.9); FX.layout.setMode('details'); FX.layout.setPanes(false, false);
        S.showHidden = false; savePrefs(); refresh(); if (window.I18N) window.I18N.set('auto');
        showCat('general'); ui.toast('ההגדרות אופסו', 'success'); return;
      }
      if (t.dataset.sw === 'libFolders') {
        var ol = Object.assign({}, opts()); ol.libFolders = !ol.libFolders;
        saveOpts(ol); t.classList.toggle('on', ol.libFolders); t.setAttribute('aria-checked', ol.libFolders ? 'true' : 'false'); publishLibFolders(); return;
      }
      if (t.dataset.sw === 'askApp') {
        var oa = Object.assign({}, opts()); oa.askApp = oa.askApp === false;
        saveOpts(oa); t.classList.toggle('on', oa.askApp); t.setAttribute('aria-checked', oa.askApp ? 'true' : 'false'); return;
      }
      if (t.dataset.sw === 'stripes') {
        S.stripes = !S.stripes; t.classList.toggle('on', S.stripes); t.setAttribute('aria-checked', S.stripes ? 'true' : 'false');
        savePrefs(); renderList(); return;
      }
      if (t.closest('[data-seg=cols]') && t.dataset.v) { S.cols[t.dataset.v] = S.cols[t.dataset.v] === false; savePrefs(); renderList(); showCat('view'); return; }
      if (t.dataset.sw === 'hidden') {
        S.showHidden = !S.showHidden;
        t.classList.toggle('on', S.showHidden); t.setAttribute('aria-checked', S.showHidden ? 'true' : 'false');
        var hb = $('fx-hidden'); hb.classList.toggle('on', S.showHidden); hb.setAttribute('aria-pressed', S.showHidden ? 'true' : 'false');
        savePrefs(); refresh(); return;
      }
      if (t.dataset.sw === 'singleClickOpen') {
        var o1 = opts(); o1.singleClickOpen = !o1.singleClickOpen; saveOpts(o1);
        t.classList.toggle('on', o1.singleClickOpen); t.setAttribute('aria-checked', o1.singleClickOpen ? 'true' : 'false');
        return;
      }
      /* חלונית תצוגה מקדימה וחלונית פרטים חולקות אותו אזור בצד (כמו בסייר של Windows): הפעלת אחת מכבה את השנייה */
      if (t.dataset.sw === 'preview') { FX.layout.togglePreview(); showCat('view'); return; }
      if (t.dataset.sw === 'details') { FX.layout.toggleDetailsPane(); showCat('view'); return; }
      var sg = t.closest('[data-seg]');
      if (sg && t.dataset.v != null) {
        sg.querySelectorAll('.fx-seg-btn').forEach(function (x) { var on = x === t; x.classList.toggle('on', on); x.setAttribute('aria-checked', on ? 'true' : 'false'); });
        var name = sg.dataset.seg, v = t.dataset.v;
        if (name === 'zoom') FX.layout.setZoom(parseFloat(v) || 0.9);
        else if (name === 'mode') FX.layout.setMode(v);
        else if (name === 'lang') { if (window.I18N) window.I18N.set(v); }
        else { var o = opts(); o[name] = v; saveOpts(o); }
      }
    });
    var start = typeof cat === 'string' ? cat : null;
    try { start = start || localStorage.getItem('fx.setCat'); } catch (e) { /* כלום */ }
    showCat(SET_CATS.some(function (c) { return c[0] === start; }) ? start : 'open');
    var first = page.querySelector('[data-act=close]'); if (first) first.focus();
  }

  function sendFeedback() {
    window.Feedback.open({
      ui: ui, plugin: 'סייר קבצים', version: FX.VERSION, bridge: FX.Bridge,
      extra: function () {
        var b = FX.Bridge && FX.Bridge.info;
        return ['מצב: ' + (S.prov ? S.prov.kind : '?'), 'גשר: ' + (b ? (b.version || '?') : 'לא מחובר')];
      }
    });
  }

  /* פתיחה בתוסף אחר (plugin.openOther, אוצריא 0.9.97) */
  var PDF_PLUGIN = 'com.chadbedera.pdfviewer';
  var SS_PLUGIN = 'com.chadbedera.systemsettings'; /* #56: מעבר ישיר להגדרות הנעילה ולהפך */
  var WORD_PLUGIN = 'com.otzaria_word_editor.superdoc';
  async function openInPlugin(pluginId, param) {
    var r = await window.Otzaria.call('plugin.openOther', { pluginId: pluginId, param: param || null });
    if (r && r.success === false) throw FX.OpError((r.error && r.error.code) || 'internal', (r.error && r.error.message) || '');
  }
  /* תוכן הקובץ כ-dataUrl לשליחה לתוסף אחר. כתובת resolveFileUrl לא עוברת בין תוספים
     (נאכפת לפי מזהה התוסף), ולכן בלי גשר קוראים אותה כאן וממירים בעצמנו. */
  var PLUGIN_MAX = 15 * 1024 * 1024;
  var MIME = { pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', doc: 'application/msword' };
  async function fileDataUrl(e) {
    if (e.size > PLUGIN_MAX) throw FX.OpError('too_large', 'קובץ גדול מ-15MB — פתחו אותו בצופה הפנימי או ביישום חיצוני');
    var ext = String(e.name).split('.').pop().toLowerCase();
    var mime = MIME[ext] || 'application/octet-stream';
    if (S.prov.kind === 'bridge') {
      var d = await FX.Bridge.call('/fs/read', { path: e.path, maxBytes: PLUGIN_MAX }, { timeoutMs: 120000 });
      return 'data:' + mime + ';base64,' + d.content;
    }
    var rd = await S.prov.read(e);
    var blob = await (await fetch(rd.url)).blob();
    if (blob.size > PLUGIN_MAX) throw FX.OpError('too_large', 'קובץ גדול מ-15MB — פתחו אותו בצופה הפנימי או ביישום חיצוני');
    var b = blob.type ? blob : new Blob([blob], { type: mime });
    return await new Promise(function (res, rej) {
      var fr = new FileReader();
      fr.onload = function () { res(fr.result); };
      fr.onerror = function () { rej(FX.OpError('internal', 'קריאת הקובץ נכשלה')); };
      fr.readAsDataURL(b);
    });
  }
  async function sendToPlugin(e, pluginId, label) {
    busy(true, 'מכין את הקובץ…');
    try {
      await openInPlugin(pluginId, { name: e.name, dataUrl: await fileDataUrl(e) });
    } catch (err) {
      busy(false);
      if (String(err && err.code).indexOf('not_found') >= 0) ui.toast('התוסף ' + label + ' אינו מותקן', 'error');
      else opError(err, 'הפתיחה ב' + label + ' נכשלה');
      return;
    }
    busy(false);
  }
  function openInPdfPlugin(e) { return sendToPlugin(e, PDF_PLUGIN, 'צופה PDF+OCR'); }
  /* וורד לאוצריא מקבל { name, dataUrl } ופותח בטאב חדש (Y-PLONI/otzaria-word-editor#91) */
  function openInWordPlugin(e) { return sendToPlugin(e, WORD_PLUGIN, 'וורד לאוצריא'); }

  function openExternal(e) {
    /* #59: בכל פתיחה ביישום חיצוני — חלון "פתח באמצעות" של Windows (אם לא כובה בהגדרות), במקום לזכור תוכנה */
    S.prov.open(e.path, { openWith: opts().askApp !== false }).catch(function (err) { opError(err, 'הפתיחה נכשלה'); });
  }
  /* מדיניות מסמכי Office שנקבעת על ידי המנהל (kiosk.allowExcelDocs בגשר): מסמכי Word מותרים תמיד,
     גיליונות Excel מותרים רק כשהמנהל אישר, ומצגות PowerPoint חסומות תמיד בלי אפשרות הפעלה */
  function officeBlockedMessage(bk) {
    var what = bk === 'sheet' ? 'קובצי Excel' : 'מצגות PowerPoint';
    var extra = bk === 'sheet' ? ' אפשר לבקש מהמנהל להתיר צפייה בקובצי Excel בהגדרות הנעילה.' : '';
    ui.toast('סוג קובץ זה חסום על ידי המנהל: ' + what + ' אינם זמינים בסייר הקבצים. מותר לצפות ולפתוח רק מסמכי Word.' + extra, 'error');
  }
  async function officeDocAllowed(bk) {
    if (bk !== 'sheet' && bk !== 'slides') return true;
    if (bk === 'slides') return false; /* PowerPoint חסום תמיד, בלי מתג הפעלה */
    if (!S.prov || S.prov.kind !== 'bridge') return false; /* בלי גשר אין מנהל שיכול לאשר — ברירת המחדל חסומה */
    try { return !!(await FX.Bridge.loadPolicy()).allowExcelDocs; }
    catch (e) { return false; }
  }
  async function openEntry(e, find) {
    if (!e) return;
    if (S.recycle) { ui.toast('כדי לפתוח פריט יש לשחזר אותו קודם', 'info'); return; }
    if (e.otzBook) { try { await window.Otzaria.call('reader.openBook', e.otzBook); } catch (err) { ui.toast('הפתיחה בעיון נכשלה', 'error'); } return; }
    if (e.type === 'dir' || e.type === 'drive') { go(e.path); return; }
    var ext = FX.extOf(e.name);
    var o = opts();
    var canExt = !!S.prov.caps.open;
    if (find === undefined && S.search && $('fx-content').checked) find = S.search.query;   /* חיפוש בתוכן: פותחים במקום ההתאמה */
    var bk = FX.kindOf(e);
    if (!(await officeDocAllowed(bk))) { officeBlockedMessage(bk); return; }
    var kind = ext === 'pdf' ? 'pdf' : (ext === 'docx' || ext === 'doc') ? 'word' : FX.isTextExt(ext) ? 'text' : FX.isImageExt(ext) ? 'image' : '';
    if (kind && o[kind] === 'ask') { /* #16 (13.10.2026): לשאול בכל פתיחה במה לפתוח */
      var CH = { pdf: [['internal', 'צופה פנימי'], ['ocr', 'PDF+OCR']], word: [['two', 'שני טורים'], ['one', 'טור אחד'], ['wordplugin', 'וורד לאוצריא']],
        text: [['internal', 'צופה פנימי']], image: [['internal', 'תצוגה מקדימה']] }[kind].concat(canExt ? [['external', 'יישום חיצוני']] : []);
      var pick = await ui.dialog({ title: 'במה לפתוח?', icon: 'open', body: '<p>' + FX.esc(e.name) + '</p>',
        actions: [{ label: 'ביטול', value: false }].concat(CH.map(function (c) { return { label: c[1], value: c[0] }; })) });
      if (!pick) return;
      o = Object.assign({}, o); o[kind] = pick;
    }
    if (kind && o[kind] === 'external' && canExt) { openExternal(e); return; }
    if (kind === 'pdf' && o.pdf === 'ocr') { openInPdfPlugin(e); return; }
    if (kind === 'word' && o.word === 'wordplugin' && ext === 'docx') { openInWordPlugin(e); return; }
    if (kind === 'pdf' && window.pdfjsLib) { FX.viewPdf(S.prov, e); return; }
    if (kind === 'word' && window.JSZip && ext === 'docx') { FX.viewDocx(S.prov, e, o.word === 'one' ? 1 : 2, find); return; }
    if (kind === 'text') { FX.viewText(S.prov, e, find); return; }
    if (kind === 'image') { preview(e); return; }
    /* שאר הסוגים: צופים פנימיים לכל הפורמטים הנפוצים, ופתיחה ביישום ב-Windows כשאין צופה */
    var big = e.size != null && e.size > 50 * 1024 * 1024;
    var mk = FX.mediaKind(ext);
    if (mk) { if (big && canExt) { openExternal(e); return; } FX.viewMedia(S.prov, e, mk); return; }
    if (window.JSZip && !big) {
      if (ext === 'xlsx') { FX.viewSheet(S.prov, e, find); return; }
      if (ext === 'pptx') { FX.viewSlides(S.prov, e, find); return; }
      if (ext === 'odt' || ext === 'ods' || ext === 'odp') { FX.viewOdf(S.prov, e, find); return; }
      if (ext === 'epub') { FX.viewEpub(S.prov, e, find); return; }
      if (ext === 'zip' || ext === 'otzplugin' || ext === 'jar') { FX.viewZip(S.prov, e); return; }
    }
    if (ext === 'rtf') { FX.viewRtf(S.prov, e, find); return; }
    if (bk === 'exe' || bk === 'archive' || bk === 'sheet' || bk === 'slides' || bk === 'word' || bk === 'book') {
      if (canExt) { openExternal(e); return; }
      FX.viewUnknown(S.prov, e); return;
    }
    /* סוג לא מוכר: אם התוכן טקסט — מציגים אותו; אחרת פותחים ביישום (או כרטיס פרטים) */
    if (bk === 'file' || bk === 'code') { FX.viewUnknown(S.prov, e); return; }
    if (canExt) { openExternal(e); return; }
    FX.viewUnknown(S.prov, e);
  }

  /* תצוגה מקדימה פשוטה לטקסט ולתמונות */
  async function preview(e) {
    busy(true, 'קורא…');
    var d;
    try { d = await S.prov.read(e); } catch (err) { busy(false); opError(err, 'הקריאה נכשלה'); return; }
    busy(false);
    var body;
    if (d.text != null) body = '<pre class="fx-pre" dir="auto">' + FX.esc(d.text) + '</pre>';
    else if (d.dataUrl || d.url) body = '<div class="fx-img"><img alt="' + FX.esc(e.name) + '" src="' + FX.esc(d.dataUrl || d.url) + '"></div>';
    else body = '<p>אין תצוגה מקדימה לקובץ זה.</p>';
    if (d.truncated) body += '<p class="muted small">הקובץ גדול — מוצג החלק הראשון בלבד.</p>';
    var acts = [{ label: 'סגירה', value: false }];
    if (S.prov.caps.open) acts.push({ label: 'פתיחה ביישום', value: 'open', kind: 'primary' });
    var r = await ui.dialog({ title: e.name, body: body, tone: 'fx-wide', actions: acts });
    if (r === 'open' || r === true) S.prov.open(e.path).catch(function (err) { opError(err, 'הפתיחה נכשלה'); });
  }

  async function props() {
    var sel = selected();
    var e = sel.length === 1 ? sel[0] : null;
    var path = e ? e.path : S.path;
    busy(true, 'קורא מאפיינים…');
    var d;
    try { d = S.prov.kind === 'bridge' ? await S.prov.stat(path) : await S.prov.stat(path, e); }
    catch (err) { busy(false); opError(err, 'קריאת המאפיינים נכשלה'); return; }
    if (d.type === 'dir' && S.prov.kind === 'bridge' && FX.packb) { var ds = await FX.packb.dirSize(d.path || path); if (ds) { d.size = ds.size; d.itemCount = ds.files + ds.dirs; } }
    busy(false);
    var name = d.name || (e ? e.name : S.prov.nameOf(path));
    var isDir = d.type === 'dir';
    var full = S.prov.displayPath(d.path || path);
    var ext = isDir ? '' : FX.extOf(name);
    var kind = FX.kindOf({ name: name, type: isDir ? 'dir' : 'file' });
    function grid(rows) {
      return '<dl class="info-grid">' + rows.filter(function (r) { return r[1]; }).map(function (r) {
        return '<dt>' + r[0] + '</dt><dd' + (r[2] ? ' dir="ltr" data-noi18n' : '') + '>' + FX.esc(r[1]) + '</dd>';
      }).join('') + '</dl>';
    }
    var canAttr = S.prov.kind === 'bridge' && !!S.prov.setAttributes && !(e ? (e.type === 'drive' || FX.win.isDriveRoot(e.path)) : (S.isRoot || FX.win.isDriveRoot(S.path)));
    var general = '<div class="fx-props-head"><span class="fx-ic k-' + kind + '">' + ic(FX.kindInfo(kind).icon) + '</span><b data-noi18n>' + FX.esc(name) + '</b></div>' +
      grid([
        ['סוג', isDir ? 'תיקייה' : FX.typeLabel({ name: name, type: 'file' })],
        ['מיקום', S.prov.displayPath(S.prov.parentOf(d.path || path) || ''), true],
        ['גודל', d.size != null ? FX.formatSize(d.size) + ' (' + FX.formatNumber(d.size) + ' בתים)' : ''],
        ['פריטים', d.itemCount != null ? FX.formatNumber(d.itemCount) : '']
      ]) +
      (canAttr ? '<div class="fx-attrs"><span class="field-label">תכונות</span>' +
        '<label class="check-row"><input type="checkbox" name="hidden"' + (d.hidden ? ' checked' : '') + '><span class="check-box">' + ui.icon('check') + '</span><span class="check-text">מוסתר</span></label>' +
        '<label class="check-row"><input type="checkbox" name="readOnly"' + (d.readOnly ? ' checked' : '') + '><span class="check-box">' + ui.icon('check') + '</span><span class="check-text">קריאה בלבד</span></label></div>'
        : grid([['תכונות', [d.readOnly ? 'קריאה בלבד' : '', d.hidden ? 'מוסתר' : ''].filter(Boolean).join(', ')]]));
    var details = grid([
      ['נתיב מלא', full, true],
      ['סיומת', ext ? '.' + ext : ''],
      ['נוצר', d.created ? FX.formatDate(d.created) : ''],
      ['שונה', d.modified ? FX.formatDate(d.modified) : '']
    ]) + '<a href="#" role="button" class="btn btn-text" data-act="copypath">' + ic('copy') + 'העתקת הנתיב</a>';
    var body = '<div class="fx-tabs" role="tablist">' +
      '<a href="#" role="tab" class="fx-tab on" data-tab="g">כללי</a><a href="#" role="tab" class="fx-tab" data-tab="d">פרטים</a></div>' +
      '<div class="fx-tabpanel" data-panel="g">' + general + '</div><div class="fx-tabpanel" data-panel="d" hidden>' + details + '</div>';
    var form = null;
    var r = await ui.dialog({
      title: 'מאפיינים', body: body, tone: 'fx-props',
      actions: canAttr ? [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }] : [{ label: 'סגירה', value: true, kind: 'primary' }],
      onOpen: function (f) {
        form = f;
        f.querySelectorAll('[data-tab]').forEach(function (t) {
          t.addEventListener('click', function (ev) {
            ev.preventDefault();
            f.querySelectorAll('[data-tab]').forEach(function (x) { x.classList.toggle('on', x === t); });
            f.querySelectorAll('[data-panel]').forEach(function (p) { p.hidden = p.dataset.panel !== t.dataset.tab; });
          });
        });
        var cp = f.querySelector('[data-act=copypath]');
        if (cp) cp.addEventListener('click', function (ev) { ev.preventDefault(); copyText(full).then(function (ok) { ui.toast(ok ? 'הנתיב הועתק' : 'ההעתקה נכשלה', ok ? 'success' : 'error'); }); });
      }
    });
    if (!r || !canAttr || !form) return;
    var hid = form.querySelector('input[name=hidden]').checked, ro = form.querySelector('input[name=readOnly]').checked;
    if (hid === !!d.hidden && ro === !!d.readOnly) return;
    try {
      await S.prov.setAttributes(d.path || path, { hidden: hid, readOnly: ro });
      ui.toast('התכונות עודכנו', 'success');
      refresh();
    } catch (err) { opError(err, 'עדכון התכונות נכשל'); }
  }

  function busy(on, text) {
    S.busy = on;
    document.body.classList.toggle('fx-busy', !!on);
    var st = $('fx-status');
    if (on) st.innerHTML = '<span class="spinner sm"></span><span>' + FX.esc(text || 'עובד…') + '</span>';
    else renderStatus();
  }

  /* ---------- חיפוש זורם ---------- */
  /* #56: חיפוש גם בספרייה של אוצריא עצמה (library.findBooks) — שורת ספרים מעל תוצאות הקבצים */
  var libSeq = 0;
  function libSearch(q) {
    var host = $('fx-libhits'); if (!host) return;
    var my = ++libSeq;
    if (!q || !window.Otzaria || !window.Otzaria.call) { host.hidden = true; host.innerHTML = ''; return; }
    window.Otzaria.call('library.findBooks', { query: q, limit: 12 }).then(function (r) {
      if (my !== libSeq) return;
      var list = (r && (r.data || r.result)) || (Array.isArray(r) ? r : []);
      if (!list.length) { host.hidden = true; host.innerHTML = ''; return; }
      host.innerHTML = '<span class="fx-libhits-title">' + ic('book') + 'בספריית אוצריא:</span>' + list.map(function (b, i) {
        return '<button type="button" class="fx-libhit" data-i="' + i + '" title="פתיחה בקורא של אוצריא · לחיצה ימנית: הקובץ המקורי (PDF/Word)">' + ui.esc(b.title || b.bookId || '') + '</button>';
      }).join('');
      host.hidden = false;
      /* #69 (library.content.read): לחיצה ימנית — הקובץ המקורי שמאחורי ספר PDF/Word בספרייה, בצופה PDF+OCR או בוורד לאוצריא */
      host.oncontextmenu = function (e) {
        var btn = e.target.closest('.fx-libhit'); if (!btn) return;
        e.preventDefault();
        openLibraryFile(list[+btn.dataset.i] || {});
      };
      host.onclick = function (e) {
        var btn = e.target.closest('.fx-libhit'); if (!btn) return;
        var b = list[+btn.dataset.i] || {};
        var a = b.bookUid ? { bookUid: b.bookUid } : { bookId: b.bookId || b.title };
        window.Otzaria.call('reader.openBook', a).then(function (x) {
          if (x && x.success === false) ui.toast('לא ניתן לפתוח את הספר', 'error');
        }, function () { ui.toast('לא ניתן לפתוח את הספר', 'error'); });
      };
    }, function () { if (my === libSeq) { host.hidden = true; host.innerHTML = ''; } });
  }

  async function openLibraryFile(b) {
    var id = b.bookUid ? { bookUid: b.bookUid } : { bookId: b.bookId || b.title };
    busy(true, 'מכין את הקובץ…');
    try {
      var r = await FX.callHost('library.openBookFile', Object.assign({ access: 'read' }, id));
      var ext = String((r && r.name) || '').split('.').pop().toLowerCase();
      var target = ext === 'pdf' ? [PDF_PLUGIN, 'צופה PDF+OCR'] : ext === 'docx' ? [WORD_PLUGIN, 'וורד לאוצריא'] : null;
      if (!target) { busy(false); ui.toast('לספר הזה אין קובץ PDF או Word מקורי — נפתח בקורא של אוצריא', 'info'); window.Otzaria.call('reader.openBook', id); return; }
      if (r.size > PLUGIN_MAX) throw FX.OpError('too_large', 'קובץ גדול מ-15MB — פתחו אותו בקורא של אוצריא');
      var blob = await (await fetch(r.url)).blob();
      var dataUrl = await new Promise(function (res, rej) { var fr = new FileReader(); fr.onload = function () { res(String(fr.result).replace(/^data:[^;]*/, 'data:' + MIME[ext])); }; fr.onerror = rej; fr.readAsDataURL(blob); });
      await openInPlugin(target[0], { name: r.name, dataUrl: dataUrl });
      busy(false);
    } catch (err) {
      busy(false);
      if (String(err && err.code).indexOf('not_found') >= 0) ui.toast('לא ניתן לפתוח את הספר', 'error');
      else opError(err, 'לא ניתן לפתוח את הספר');
    }
  }

  function cancelSearch() {
    if (S.search && !S.search.done) S.search.cancelled = true;
  }
  function exitSearch() {
    cancelSearch();
    S.search = null;
    libSearch('');
    $('fx-search').value = '';
    $('fx-search-clear').hidden = true;
    renderCrumbs(); renderList(); markSide(); renderCmds();
  }
  function runSearch(q) {
    q = String(q || '').trim();
    if (!q) { if (S.search) exitSearch(); return; }
    if (S.recycle) { ui.toast('אין חיפוש בסל המיחזור', 'info'); return; }
    cancelSearch();
    libSearch(q);
    var root = S.search ? S.search.root : S.path;
    var content = S.prov.caps.contentSearch && $('fx-content').checked;
    var query = content ? '*' : (/[*?]/.test(q) ? q : '*' + q + '*');
    var s = S.search = { query: q, root: root, hits: [], cancelled: false, done: false, scanned: 0, current: '' };
    S.sel = {}; S.focus = -1; S.anchor = -1;
    renderCrumbs(); renderList(); markSide(); renderCmds();
    var pending = false;
    function paint() {
      if (pending) return;
      pending = true;
      requestAnimationFrame(function () {
        pending = false;
        if (S.search !== s) return;
        renderList();
      });
    }
    var opts = { query: query, maxResults: 1000, includeDirs: !content };
    if (content) opts.contentQuery = q;
    S.prov.search(root, opts, {
      hit: function (h) { if (S.search === s) { s.hits.push(h); paint(); } },
      progress: function (p) {
        if (S.search !== s) return;
        s.scanned = p.scanned || s.scanned;
        s.current = p.current ? S.prov.displayPath(p.current) : '';
        if (!pending) renderStatus();
      }
    }, function () { return s.cancelled || S.search !== s; }).then(function (r) {
      s.done = true;
      s.truncated = !!(r && r.truncated);
      if (S.search === s) { renderList(); renderCmds(); }
    }).catch(function (e) {
      s.done = true;
      if (S.search === s) { renderList(); opError(e, 'החיפוש נכשל'); }
    });
  }

  /* ---------- תפריט הקשר ---------- */
  function showMenu(x, y) {
    var m = $('fx-menu');
    var items = [];
    var sel = selected();
    if (sel.length === 1) items.push('open');
    if (sel.length === 1 && (sel[0].type === 'dir' || sel[0].type === 'drive')) items.push('openNewTab');
    if (sel.length === 1 && S.search) items.push('openLocation');
    if (sel.length) items.push('cut', 'copy', 'copyPath');
    if (inFolder()) items.push('paste');
    if (S.prov.caps.saveCopy && sel.length === 1) items.push('saveCopy');
    if (sel.length === 1) items.push('rename');
    if (sel.length) items.push('delete');
    if (S.prov.caps.reveal && sel.length) items.push('reveal');
    if (!sel.length && inFolder()) items.push('newFolder', 'newFile');
    if (FX.packb) items = items.concat(FX.packb.items(sel, inFolder()));
    items.push('props');
    if (S.recycle) items = sel.length ? ['restore', 'delete'] : ['emptyBin'];
    var html = items.map(function (id) {
      var c = CMDS.find(function (k) { return k.id === id; });
      var st = cmdState(id);
      return '<button class="fx-menu-item" role="menuitem" data-cmd="' + id + '"' + (st.on ? '' : ' disabled') + '>' + ic(c.icon) +
        '<span>' + c.label + '</span>' + (c.key ? '<kbd>' + c.key + '</kbd>' : '') + '</button>';
    }).join('');
    if (!html) return;
    m.innerHTML = html;
    m.hidden = false;
    var r = m.getBoundingClientRect();
    var vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
    m.style.left = Math.max(4, Math.min(x - r.width, vw - r.width - 4)) + 'px';
    m.style.top = Math.max(4, Math.min(y, vh - r.height - 4)) + 'px';
    var first = m.querySelector('button:not(:disabled)');
    if (first) first.focus();
  }
  function hideMenu() { $('fx-menu').hidden = true; }

  /* ---------- אירועים ---------- */
  function bind() {
    $('fx-back').innerHTML = ic('back');
    $('fx-fwd').innerHTML = ic('forward');
    $('fx-up').innerHTML = ic('up');
    $('fx-refresh').innerHTML = ic('refresh');
    $('fx-hidden').innerHTML = ic('hidden');
    $('fx-add-folder').innerHTML = ic('plus');
    $('fx-settings').innerHTML = ic('settings');
    $('fx-search-ic').innerHTML = ic('search');
    $('fx-search-clear').innerHTML = ic('close');

    $('fx-back').addEventListener('click', goBack);
    $('fx-fwd').addEventListener('click', goFwd);
    $('fx-up').addEventListener('click', goUp);
    $('fx-refresh').addEventListener('click', refresh);
    $('fx-hidden').addEventListener('click', function () {
      S.showHidden = !S.showHidden;
      this.setAttribute('aria-pressed', S.showHidden ? 'true' : 'false');
      this.classList.toggle('on', S.showHidden);
      savePrefs(); refresh();
    });
    $('fx-add-folder').addEventListener('click', addFolder);
    $('fx-settings').addEventListener('click', function () { settingsDialog(); });
    $('fx-about').innerHTML = ic('info');
    $('fx-about').addEventListener('click', function () { settingsDialog('about'); });
    $('fx-feedback').innerHTML = window.Feedback.ICON;
    $('fx-feedback').addEventListener('click', sendFeedback);

    $('fx-crumbs').addEventListener('click', function (e) {
      var b = e.target.closest('.fx-crumb');
      if (b) go(b.dataset.path);
    });
    $('fx-crumbs').addEventListener('dblclick', function (e) {
      if (!e.target.closest('.fx-crumb')) editPath();
    });
    $('fx-crumbs').addEventListener('contextmenu', function (e) { e.preventDefault(); editPath(); });

    $('fx-search-form').addEventListener('submit', function (e) { e.preventDefault(); runSearch($('fx-search').value); });
    $('fx-search-clear').addEventListener('click', function () {
      $('fx-search').value = ''; $('fx-search-clear').hidden = true;
      if (S.search) exitSearch();
      $('fx-search').focus();
    });
    $('fx-search').addEventListener('input', function () { $('fx-search-clear').hidden = !$('fx-search').value.trim(); });
    $('fx-search').addEventListener('input', FX.debounce(function () {
      var v = $('fx-search').value.trim();
      if (!v && S.search) exitSearch();
      else if (v.length >= 2 && !(S.isRoot && !S.search && $('fx-content').checked)) runSearch(v);    /* חיפוש בתוכן על כל הכוננים רק ב-Enter, כדי לא להעמיס. חי: התוצאות מתעדכנות תוך כדי הקלדה, כמו ב-Everything */
    }, 260));
    $('fx-content').addEventListener('change', function () { var v = $('fx-search').value.trim(); if (v.length >= 2 && !(S.isRoot && !S.search && $('fx-content').checked)) runSearch(v); });
    $('fx-search').addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { e.preventDefault(); exitSearch(); $('fx-main').focus(); }
    });

    $('fx-side').addEventListener('click', function (e) {
      var rm = e.target.closest('[data-remove]');
      if (rm) { removeFolder(rm.dataset.remove); return; }
      if (e.target.closest('[data-add]')) { addFolder(); return; }
      var b = e.target.closest('.nav-item[data-path]');
      if (b) { if (S.search) { S.search.cancelled = true; S.search = null; } go(b.dataset.path); }
    });

    $('fx-head').addEventListener('click', function (e) {
      var b = e.target.closest('[data-sort]');
      if (!b) return;
      var k = b.dataset.sort;
      S.sort = { key: k, dir: S.sort.key === k && S.sort.dir === 'asc' ? 'desc' : 'asc' };
      savePrefs(); renderList();
    });

    $('fx-cmds').addEventListener('click', function (e) {
      var b = e.target.closest('[data-cmd]');
      if (b) run(b.dataset.cmd, e);
    });

    var list = $('fx-list');
    list.addEventListener('mousedown', function (e) {
      if (e.button === 2) return;
      var row = e.target.closest('.fx-row');
      if (!row) {
        if (e.target.closest('[data-add]')) return;
        S.sel = {}; S.focus = -1; refreshRows(); return;
      }
      clickRow(+row.dataset.i, e);
    });
    list.addEventListener('click', function (e) {
      if (e.target.closest('[data-add]')) { addFolder(); return; }
      if (e.detail > 1) return;   /* לחיצה כפולה מטופלת ב-dblclick; כאן רק הלחיצה הבודדת הראשונה */
      if (!opts().singleClickOpen) return;
      if (e.ctrlKey || e.metaKey || e.shiftKey) return;   /* לא לפגוע בבחירה מרובה */
      var row = e.target.closest('.fx-row');
      if (!row) return;
      var en = S.view[+row.dataset.i];
      if (en && (en.type === 'dir' || en.type === 'drive')) openEntry(en);
    });
    list.addEventListener('dblclick', function (e) {
      var row = e.target.closest('.fx-row');
      if (row) openEntry(S.view[+row.dataset.i]);
    });
    list.addEventListener('auxclick', function (e) {
      if (e.button !== 1) return;
      var row = e.target.closest('.fx-row');
      var en = row && S.view[+row.dataset.i];
      if (en && (en.type === 'dir' || en.type === 'drive')) { e.preventDefault(); newTab(en.path); }
    });
    list.addEventListener('contextmenu', function (e) {
      e.preventDefault();
      var row = e.target.closest('.fx-row');
      if (row) {
        var i = +row.dataset.i;
        if (!S.sel[S.view[i].path]) selectOnly(i);
        refreshRows();
      } else { S.sel = {}; refreshRows(); }
      showMenu(e.clientX, e.clientY);
    });

    $('fx-menu').addEventListener('click', function (e) {
      var b = e.target.closest('[data-cmd]');
      if (!b || b.disabled) return;
      hideMenu();
      run(b.dataset.cmd, e);
    });
    document.addEventListener('mousedown', function (e) { if (!e.target.closest('#fx-menu')) hideMenu(); });
    window.addEventListener('blur', hideMenu);
    window.addEventListener('resize', hideMenu);

    document.addEventListener('keydown', onKey);
    window.addEventListener('mouseup', function (e) {
      if (e.button === 3) { e.preventDefault(); goBack(); }
      else if (e.button === 4) { e.preventDefault(); goFwd(); }
    });
  }

  function typing(t) {
    return t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable);
  }
  function onKey(e) {
    if (!S.prov || S.busy) return;
    if (document.querySelector('#dialogs .dlg-scrim')) return;
    var menu = $('fx-menu');
    if (!menu.hidden) {
      if (e.key === 'Escape') { e.preventDefault(); hideMenu(); }
      else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        var btns = Array.prototype.slice.call(menu.querySelectorAll('button:not(:disabled)'));
        var at = btns.indexOf(document.activeElement);
        var nx = btns[(at + (e.key === 'ArrowDown' ? 1 : -1) + btns.length) % btns.length];
        if (nx) nx.focus();
      }
      return;
    }
    var ctrl = e.ctrlKey || e.metaKey;
    if (ctrl && e.key.toLowerCase() === 't' && !typing(e.target)) { e.preventDefault(); newTab(''); return; }
    if (ctrl && e.key.toLowerCase() === 'w' && !typing(e.target)) { e.preventDefault(); closeTab(S.tab); return; }
    if (ctrl && e.shiftKey && e.key.toLowerCase() === 'c' && !typing(e.target)) { e.preventDefault(); run('copyPath', e); return; }
    if (ctrl && e.key === 'Tab' && S.tabs && S.tabs.length > 1) { e.preventDefault(); switchTab((S.tab + (e.shiftKey ? -1 : 1) + S.tabs.length) % S.tabs.length); return; }
    if (ctrl && e.key.toLowerCase() === 'f') { e.preventDefault(); $('fx-search').focus(); $('fx-search').select(); return; }
    if (ctrl && e.key.toLowerCase() === 'l') { e.preventDefault(); editPath(); return; }
    if (e.key === 'F5') { e.preventDefault(); refresh(); return; }
    if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); goBack(); return; }
    if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); goFwd(); return; }
    if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); goUp(); return; }
    if (typing(e.target)) return;
    var k = e.key;
    if (ctrl && k.toLowerCase() === 'a') { e.preventDefault(); selectAll(); return; }
    if (ctrl && k.toLowerCase() === 'c') { e.preventDefault(); run('copy'); return; }
    if (ctrl && k.toLowerCase() === 'x') { e.preventDefault(); run('cut'); return; }
    if (ctrl && k.toLowerCase() === 'v') { e.preventDefault(); run('paste'); return; }
    if (ctrl && e.shiftKey && k.toLowerCase() === 'n') { e.preventDefault(); run('newFolder'); return; }
    if (k === 'Delete') { e.preventDefault(); run('delete', e); return; }
    if (k === 'F2') { e.preventDefault(); run('rename'); return; }
    if (k === 'Enter' && e.altKey) { e.preventDefault(); run('props'); return; }
    if (k === 'Enter') { e.preventDefault(); var f = S.view[S.focus]; if (f) openEntry(f); return; }
    if (k === 'Backspace') { e.preventDefault(); goUp(); return; }
    if (k === 'Escape') { if (S.search) { exitSearch(); return; } S.sel = {}; refreshRows(); return; }
    if (k === 'ArrowDown') { e.preventDefault(); moveFocus(1, e); return; }
    if (k === 'ArrowUp') { e.preventDefault(); moveFocus(-1, e); return; }
    if (k === 'Home') { e.preventDefault(); moveFocus(-S.view.length, e); return; }
    if (k === 'End') { e.preventDefault(); moveFocus(S.view.length, e); return; }
    if (k === ' ' && ctrl && S.focus >= 0) {
      e.preventDefault();
      var p = S.view[S.focus].path;
      if (S.sel[p]) delete S.sel[p]; else S.sel[p] = true;
      refreshRows(); return;
    }
    /* קפיצה לפי אות ראשונה */
    if (k.length === 1 && !ctrl && !e.altKey && /\S/.test(k)) {
      var start = S.focus + 1;
      for (var n = 0; n < S.view.length; n++) {
        var j = (start + n) % S.view.length;
        if (S.view[j].name.toLowerCase().indexOf(k.toLowerCase()) === 0) { moveFocus(j - S.focus, { shiftKey: false, ctrlKey: false }); break; }
      }
    }
  }

  /* ---------- מצב חלופי: ניהול תיקיות מאושרות ---------- */
  async function addFolder() {
    try {
      var f = await FX.FallbackProvider.addFolder();
      if (!f) return;
      await renderSide();
      go(typeof f === 'string' ? f : (f.path || ''));
    } catch (e) { opError(e, 'הוספת התיקייה נכשלה'); }
  }
  async function removeFolder(token) {
    var ok = await ui.confirm('הסרת תיקייה', '<p>התיקייה תוסר מרשימת הסייר בלבד. הקבצים עצמם לא יימחקו.</p>', 'הסרה');
    if (!ok) return;
    try {
      await FX.FallbackProvider.removeFolder(token);
      await renderSide();
      if (String(S.path).indexOf(token + '::') === 0) go('');
      else if (S.isRoot) refresh();
    } catch (e) { opError(e, 'ההסרה נכשלה'); }
  }

  /* ---------- מסמך חדש מכפתור + בסרגל הקורא ---------- */
  var NEW_DOC_ID = 'chadbedera-fx-new-doc';
  async function newDocFromReader() {
    if (!S.prov || S.prov.kind !== 'bridge') {
      ui.toast('יצירת מסמך חדש דורשת את אחת שאלתי', 'info');
      return;
    }
    var dir = '';
    try { dir = localStorage.getItem('fx.newDocDir') || ''; } catch (e) { dir = ''; }
    if (!dir) {
      var docs = (S.known || []).find(function (k) { return k.id === 'documents'; });
      dir = docs ? docs.path : (S.isRoot ? '' : S.path);
    }
    var made = null;
    var r = await ui.dialog({
      title: 'מסמך חדש', icon: 'info',
      fields: [
        { name: 'name', label: 'שם הקובץ', value: FX.t('מסמך חדש.txt') },
        { name: 'dir', label: 'תיקייה', value: dir, dir: 'ltr' }
      ],
      validate: function (v) { return validName(v.name) || (String(v.dir || '').trim() ? '' : 'יש לבחור תיקייה'); },
      onSubmit: async function (v) {
        var nm = v.name.trim();
        if (!/\.[^.]+$/.test(nm)) nm += '.txt';
        var d = S.prov.normalizeInput(v.dir);
        made = await S.prov.newTextFile(d, nm);
        try { localStorage.setItem('fx.newDocDir', d); } catch (e) { /* כלום */ }
        return { dir: d, name: nm };
      }
    });
    if (!r) return;
    ui.toast('המסמך נוצר', 'success');
    if (S.search) exitSearch();
    if (!S.prov.same(S.path, r.dir)) await go(r.dir);
    await reloadSelect([made && made.path ? made.path : S.prov.join(r.dir, r.name)]);
  }
  function onToolbarItem(ev) {
    var id = ev && (ev.id || ev.itemId || (ev.item && ev.item.id));
    if (id && id !== NEW_DOC_ID) return;
    var start = Date.now();
    (function wait() {
      if (S.prov) { newDocFromReader(); return; }
      if (Date.now() - start < 8000) setTimeout(wait, 150);
    })();
  }

  /* ---------- אתחול ---------- */
  async function boot(payload) {
    if (S.booted) return;
    S.booted = true;
    if (payload && payload.theme) ui.applyTheme(payload.theme);
    if (payload && payload.plugin && payload.plugin.version) FX.VERSION = payload.plugin.version;
    try { await window.I18N.init(payload); } catch (e) { /* נשארים בעברית */ }
    var lb = $('fx-lang');
    lb.textContent = window.I18N.toggleLabel();
    lb.addEventListener('click', function () { window.I18N.toggle(); });
    loadPrefs();
    $('fx-hidden').classList.toggle('on', S.showHidden);
    $('fx-hidden').setAttribute('aria-pressed', S.showHidden ? 'true' : 'false');
    bind();
    bindColumns();
    FX.layout.init();
    connect(false);
  }

  /* הרשאות SDK (sdk-extras.js): קיצור Ctrl+Shift+E, הדבקת נתיב מהלוח, ספרים בחיפוש הספרייה */
  function sdkExtras() {
    var X = window.SdkX; if (!X) return;
    /* #69: הקיצור מוצהר במניפסט (ctrl+shift+alt+e — ctrl+shift+e תפוס בלוח השנה של אוצריא; ניתן לשינוי בהגדרות אוצריא → קיצורי מקשים) */
    X.onCommand('openExplorer', function () { X.openSelf(); });
    /* Ctrl+Shift+V: נתיב שהועתק מחוץ לאוצריא -> מעבר אליו */
    document.addEventListener('keydown', function (e) {
      if (!(e.ctrlKey && e.shiftKey && (e.key === 'V' || e.key === 'v'))) return;
      e.preventDefault();
      X.readClipboard().then(function (t) { t = t.replace(/^"|"$/g, ''); if (/^([a-z]:\\|\\\\)/i.test(t)) go(t); });
    });
    /* ספרים: storage 'library.folders' = רשימת תיקיות; PDF/Word מהן מופיעים בחיפוש הספרייה */
    X.call('storage.get', { key: 'library.folders' }).then(async function (r) {
      var dirs = (r && (r.value || r.data)) || [];
      if (!Array.isArray(dirs) || !dirs.length || !FX.Bridge) return;
      var books = [];
      for (var i = 0; i < dirs.length && books.length < 2000; i++) {
        var d; try { d = await FX.Bridge.call('/fs/list', { path: dirs[i] }, { timeoutMs: 60000 }); } catch (e) { continue; }
        ((d && (d.entries || d.items)) || []).forEach(function (en) {
          if (/\.(pdf|docx)$/i.test(en.name || '')) books.push({ id: en.path || FX.win.join(dirs[i], en.name), title: (en.name || '').replace(/\.[^.]+$/, ''), category: 'קבצים מהמחשב' });
        });
      }
      if (books.length) X.setLibraryBooks(books);
    });
    X.onLibraryBookOpen(function (p) { var id = p && (p.id || p.bookId); if (id) go(id.replace(/[\\/][^\\/]*$/, '')); });
  }

  function init() {
    if (window.Otzaria) {
      window.Otzaria.on('plugin.boot', boot);
      window.Otzaria.on('theme.changed', ui.applyTheme);
      window.Otzaria.on('plugin.page_opened', function (ev) { /* #56: נפתח מתוסף אחר — {view:'settings', cat} */
        var pr = ev && (ev.param || (ev.data && ev.data.param));
        if (pr && pr.view === 'settings') settingsDialog(pr.cat || 'security');
        else if (pr && pr.path && S.prov) go(pr.path);
      });
      window.Otzaria.on('reader.toolbar_item_clicked', onToolbarItem);
      window.Otzaria.on('library.providerBook.openRequested', function (ev) { /* 4ג: תיקייה מהספרייה */
        var p = ev && (ev.id || (ev.data && ev.data.id));
        if (!p) return;
        if (window.SdkX && SdkX.openSelf) SdkX.openSelf({ path: p });
        var start = Date.now();
        (function wait() { if (S.prov) { go(p); return; } if (Date.now() - start < 8000) setTimeout(wait, 150); })();
      });
      sdkExtras();
      window.Otzaria.on('plugin.suspended', function () { S.suspended = true; clearTimeout(S.probeTimer); clearTimeout(TU.poll); cancelSearch(); });
      window.Otzaria.on('plugin.resumed', function () {
        S.suspended = false;
        tempPoll();
        if (!S.prov || S.prov.kind !== 'bridge') connect(false);
        else FX.Bridge.probe().then(function (h) { if (!h) connect(false); else afterPolicy(); });
      });
    } else {
      document.body.classList.add('preview');
      if (/[?&]dark=1/.test(location.search)) document.body.classList.add('dark-mode');
      boot(null);
    }
  }

  FX.app = { go: go, refresh: refresh, connect: connect, afterPolicy: afterPolicy, selected: function () { return S.prov ? selected() : []; }, prov: function () { return S.prov; } };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
