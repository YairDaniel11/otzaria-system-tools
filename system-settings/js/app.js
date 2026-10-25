/* app.js - shell, navigation, bridge probing and offline screen */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];
  SS.VERSION = '1.0.6';
  SS.BRIDGE_URL = 'https://github.com/Hadabadra/achat-shaalti/releases/latest';

  var state = { current: null, connected: false, probeTimer: null, suspended: false, booted: false };

  SS.openUrl = async function (url) {
    if (window.Otzaria) {
      /* אם הפתיחה נכשלת — מעתיקים את הקישור ללוח, כדי שאפשר להדביק אותו בדפדפן */
      var failed = async function () {
        var ok = await SS.copyText(url);
        ui.toast(ok ? 'לא ניתן לפתוח את הקישור, הוא הועתק ללוח' : 'לא ניתן לפתוח את הקישור: ' + ui.isolateLtr(url), 'error');
      };
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
      await failed();
    } else {
      ui.toast('בתוך אוצריא הקישור ייפתח בדפדפן', 'info');
    }
  };

  SS.copyText = async function (text) {
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
  };

  /* ---------- ממסר זמני היום לשומר המסך (#33): רק כאן (בתוך אוצריא) אפשר לקרוא ל-calendar.*;
     הגשר (תהליך Windows נפרד) לא יכול. שולחים לו מדי דקה POST ‏/kiosk/zmanim-cache עם מה שהתקבל,
     והוא רק שומר ומציג. בלי window.Otzaria (תצוגה מקדימה בדפדפן) זה פשוט לא רץ. */
  var zmanimTimer = null;
  function unwrapOtzaria(r) { return r && r.data !== undefined ? r.data : r; }
  async function pushZmanimCache() {
    if (!window.Otzaria || !state.connected) return;
    await SS.sync.zmanim(); /* #69: js/sync.js */
  }
  function scheduleZmanim() {
    clearTimeout(zmanimTimer);
    if (state.suspended || !state.connected) return;
    zmanimTimer = setTimeout(function () { pushZmanimCache().finally(scheduleZmanim); }, 60000);
  }
  /* נקרא גם מיד אחרי חיבור/שחזור, וגם אחרי שמירת הגדרות הנעילה, כדי שהתצוגה תתעדכן בלי לחכות דקה */
  SS.pushZmanimNow = function () { clearTimeout(zmanimTimer); pushZmanimCache().finally(scheduleZmanim); };

  /* #63: הצגת זמני הנעילה בלוח השנה של אוצריא. רק כאן (בתוך אוצריא) אפשר publishedData.*.
     מפרסמים את חלונות הנעילה ל-7 הימים הקרובים כאירועים, ומסירים אירועים ישנים/כבויים. */
  async function pushLockCalendar() {
    if (!window.Otzaria || !state.connected) return;
    await SS.sync.lockCalendar(); /* #69: הלוגיקה ב-js/sync.js — משותפת למופע הרקע (background.html) */
  }
  SS.pushLockCalendar = function () { pushLockCalendar(); };

  function sectionById(id) {
    return SS.sections.find(function (s) { return s.id === id; });
  }

  /* סדר וקבוצות כמו בהגדרות של Windows */
  var ORDER = ['display', 'audio', 'energy', 'time', 'keyboard', 'mouse', 'printer', 'usb', 'bluetooth', 'wifi', 'airplane', 'programs', 'kiosk', 'fxpolicy', 'screensaver', 'system', 'about'];
  var GROUPS = [
    ['system', 'מערכת', ['display', 'audio', 'energy', 'time', 'system']],
    ['devices', 'התקנים', ['keyboard', 'mouse', 'printer', 'usb', 'bluetooth']],
    ['network', 'רשת', ['wifi', 'airplane']],
    ['study', 'לימוד ונעילה', ['programs', 'kiosk', 'fxpolicy', 'screensaver']],
    ['about', '', ['about']]
  ];
  /* הגדרות שאפשר לסגור בנעילה (חייב להתאים ל-Kiosk.AllSections בגשר) */
  var LOCKABLE = ['audio', 'display', 'energy', 'keyboard', 'mouse', 'printer', 'time', 'wifi', 'bluetooth', 'airplane', 'usb'];
  var NETWORK = ['wifi', 'bluetooth', 'airplane'];

  SS.prefs = {
    get: function (k, def) { try { var v = localStorage.getItem('ss.' + k); return v == null ? def : JSON.parse(v); } catch (e) { return def; } },
    set: function (k, v) { try { localStorage.setItem('ss.' + k, JSON.stringify(v)); } catch (e) { /* ignore */ } }
  };
  /* מצב מנותק: מחשב בלי רשת — מסתירים Wi-Fi, בלוטות׳ ומצב טיסה */
  /* #8 (13.10.2026): נלקח ממצב המנותק של אוצריא עצמה דרך הגשר — אין הגדרה נפרדת בתוסף */
  /* #13: מצב קומפקטי (אייקונים בלבד), נשמר בהעדפות */
  function applyCompact() {
    var on = !!SS.prefs.get('compact', false);
    document.body.classList.toggle('ss-compact', on);
    var b = document.getElementById('top-compact');
    if (b) { b.setAttribute('aria-pressed', on ? 'true' : 'false'); b.innerHTML = ui.icon('display'); }
  }
  document.addEventListener('DOMContentLoaded', function () {
    var b = document.getElementById('top-compact');
    if (b) b.addEventListener('click', function () { SS.prefs.set('compact', !SS.prefs.get('compact', false)); applyCompact(); });
    applyCompact();
  });
  setTimeout(applyCompact, 0);
  SS._offline = false;
  SS.isOfflineMode = function () { return !!SS._offline; };
  async function syncOffline() {
    /* מקור אמת: אוצריא עצמה (app.getConnectivity → isOfflineMode). מדווחים לגשר כדי שגם המגש וחלון "אודות"
       יסתירו רשת ועדכונים; כשאין תשובה מאוצריא — נשארים עם מה שהגשר יודע. */
    var rep = null;
    try {
      if (window.Otzaria) {
        var c = unwrapOtzaria(await window.Otzaria.call('app.getConnectivity', {}));
        if (c && typeof c.isOfflineMode === 'boolean') rep = c.isOfflineMode;
      }
    } catch (e) { }
    var on = rep;
    try {
      var r = await SS.bridge.call('/sys/offline', rep === null ? {} : { report: rep });
      if (on === null) on = !!(r && r.offline);
    } catch (e) { }
    on = !!on;
    if (on !== SS._offline) { SS._offline = on; document.documentElement.classList.toggle('otz-offline', on); refreshNav(); }
  }
  SS.syncOffline = syncOffline;
  SS.lockOpen = null; /* רשימת ההגדרות הפתוחות בנעילה, כשהנעילה פעילה */

  function visible(s) {
    if (SS.isOfflineMode() && NETWORK.indexOf(s.id) >= 0) return false;
    if (SS.lockOpen && LOCKABLE.indexOf(s.id) >= 0 && SS.lockOpen.indexOf(s.id) < 0) return false;
    return true;
  }
  SS.sectionVisible = function (id) { var s = sectionById(id); return !!s && visible(s); };

  /* 4ד: כל מסך הגדרות הוא "ספר" בספריית אוצריא — חיפוש "שמע"/"נעילה" בספרייה פותח את המסך */
  function publishSettingsBooks() {
    if (!window.SdkX || !SdkX.setLibraryBooks) return;
    SdkX.setLibraryBooks(SS.sections.map(function (x) {
      return { id: x.id, title: x.title + ' — הגדרות מערכת', categoryPath: '/הגדרות מערכת' };
    }));
  }

  function buildNav() {
    SS.sections.sort(function (a, b) {
      var x = ORDER.indexOf(a.id), y = ORDER.indexOf(b.id);
      return (x < 0 ? 99 : x) - (y < 0 ? 99 : y);
    });
    var nav = document.getElementById('nav');
    nav.innerHTML = '<label class="nav-search">' + ui.icon('search') +
      '<input type="search" id="nav-q" placeholder="חיפוש הגדרה" aria-label="חיפוש הגדרה" autocomplete="off"></label><div id="nav-list"></div>';
    nav.addEventListener('click', function (e) {
      var b = e.target.closest('.nav-item');
      if (b) show(b.dataset.id);
    });
    var q = nav.querySelector('#nav-q');
    q.addEventListener('input', function () { refreshNav(); });
    q.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { var first = nav.querySelector('.nav-item'); if (first) show(first.dataset.id); }
      if (e.key === 'Escape') { q.value = ''; refreshNav(); }
    });
    refreshNav();
  }

  function refreshNav() {
    var list = document.getElementById('nav-list');
    if (!list) return;
    var qEl = document.getElementById('nav-q');
    var q = (qEl && qEl.value || '').trim().toLowerCase();
    var html = '';
    GROUPS.forEach(function (g) {
      var items = g[2].map(sectionById).filter(function (s) {
        if (!s || !visible(s)) return false;
        if (!q) return true;
        return (s.title + ' ' + (s.keywords || '') + ' ' + (window.I18N ? window.I18N.t(s.title) : '')).toLowerCase().indexOf(q) >= 0;
      });
      if (!items.length) return;
      if (g[1]) html += '<div class="nav-group">' + ui.esc(g[1]) + '</div>';
      html += items.map(function (s) {
        return '<button class="nav-item' + (s.id === state.current ? ' active' : '') + '" data-id="' + s.id + '">' + ui.icon(s.icon) + '<span>' + ui.esc(s.title) + '</span></button>';
      }).join('');
    });
    list.innerHTML = html || '<div class="empty small">לא נמצאו הגדרות</div>';
    if (state.current && !SS.sectionVisible(state.current) && !q) show('about');
  }
  SS.refreshNav = refreshNav;

  /* בזמן נעילה: מביאים מהגשר אילו הגדרות פתוחות */
  async function syncLock(h) {
    var prev = JSON.stringify(SS.lockOpen);
    if (h && h.kioskActive) {
      try { SS.lockOpen = (await SS.bridge.call('/kiosk/status')).openSections || []; } catch (e) { SS.lockOpen = SS.lockOpen || []; }
    } else SS.lockOpen = null;
    if (JSON.stringify(SS.lockOpen) !== prev) refreshNav();
  }
  SS.syncLock = syncLock;

  function show(id) {
    var s = sectionById(id);
    if (!s || !visible(s)) s = SS.sections.find(visible) || SS.sections[0];
    state.current = s.id;
    document.querySelectorAll('.nav-item').forEach(function (b) {
      b.classList.toggle('active', b.dataset.id === s.id);
      b.setAttribute('aria-current', b.dataset.id === s.id ? 'page' : 'false');
    });
    /* משוב 15.10: כל טאב נבנה ונטען פעם אחת ונשמר — מעבר בין טאבים רק מציג אותו, בלי רענון והמתנה.
       רענון רק בכפתור "רענון", אחרי שמירה (הטאבים עצמם קוראים ל-load), או כשהטאב ישן מ-5 דקות */
    var main = document.getElementById('content');
    var v = buildView(s);
    Array.prototype.forEach.call(main.children, function (c) { c.hidden = c !== v.wrap; });
    if (v.wrap.parentNode !== main) main.appendChild(v.wrap);
    v.wrap.hidden = false;
    main.scrollTop = 0;
    if (Date.now() - v.at > 5 * 60 * 1000) { v.at = Date.now(); s.load(v.body); }
    try { localStorage.setItem('ss.section', s.id); } catch (e) { /* ignore */ }
  }
  SS.show = show;

  var views = {};
  function buildView(s) {
    if (views[s.id]) return views[s.id];
    var wrap = ui.el('<section class="section fade-in" data-section="' + s.id + '">' +
      '<div class="section-head"><div class="section-title">' + ui.icon(s.icon) + '<h2>' + ui.esc(s.title) + '</h2></div>' +
      '<button class="btn btn-tonal btn-icon-text" data-act="refresh" title="רענון">' + ui.icon('refresh') + '<span>רענון</span></button></div>' +
      '<div class="section-body"></div></section>');
    var body = wrap.querySelector('.section-body');
    var v = views[s.id] = { wrap: wrap, body: body, at: Date.now(), loading: null };
    wrap.querySelector('[data-act=refresh]').addEventListener('click', function () {
      var btn = this;
      btn.classList.add('spinning'); v.at = Date.now();
      Promise.resolve(s.load(body)).finally(function () { btn.classList.remove('spinning'); });
    });
    wrap.hidden = true;
    document.getElementById('content').appendChild(wrap);
    s.mount(body);
    v.loading = Promise.resolve(s.load(body)).catch(function () {});
    return v;
  }
  /* טעינה מראש ברקע של כל הטאבים בזה אחר זה (לא במקביל — לא להעמיס על הגשר) */
  async function prewarm() {
    for (var i = 0; i < SS.sections.length; i++) {
      var s = SS.sections[i];
      if (views[s.id] || !visible(s)) continue;
      try { await buildView(s).loading; } catch (e) { /* ממשיכים */ }
    }
  }
  SS.prewarm = prewarm;

  function setStatus(connected) {
    if (connected && SS.prefetchDetect) SS.prefetchDetect(); /* זיהוי תוכנות מראש — ריבוע הנעילה לא ימתין לו */
    var chip = document.getElementById('bridge-chip');
    var h = SS.bridge.health;
    chip.className = 'topbar-chip ' + (connected ? (h && h.kioskActive ? 'warn' : 'ok') : 'bad');
    chip.innerHTML = '<i class="dot"></i>' + (connected
      ? (h && h.kioskActive ? 'מצב נעילה פעיל' : 'הגשר מחובר') + (SS.bridge.isMock ? ' · תצוגה מקדימה' : '')
      : 'הגשר אינו מחובר');
  }
  SS.setStatus = setStatus;

  function showOffline() {
    /* notifications.system: התראה כשהגשר נופל (רק במעבר ממחובר למנותק) */
    if (state.connected && window.SdkX) SdkX.notify('גשר אוצריא אינו מחובר', 'הגדרות המערכת ומצב הנעילה אינם זמינים עד שהגשר יחזור');
    state.connected = false;
    setStatus(false);
    document.getElementById('shell-body').hidden = true;
    var off = document.getElementById('offline');
    off.hidden = false;
    if (!off.dataset.built) {
      off.dataset.built = '1';
      off.innerHTML =
        '<div class="offline-card fade-in">' +
        '<div class="offline-art">' + ui.icon('bridge') + '</div>' +
        '<h2>התוסף דורש הפעלה של התוכנה "אחת שאלתי"</h2>' +
        '<p>כדי לשנות הגדרות של המחשב (שמע, מסך, בלוטות׳, רשת ונעילה), התוסף נעזר בתוכנה קטנה בשם <b>אחת שאלתי</b> שרצה ברקע במחשב שלכם. נראה שהיא עדיין לא מותקנת או לא פועלת.</p>' +
        '<ol class="steps">' +
        '<li>מורידים את <b>OtzariaBridge.exe</b> מהקישור למטה.</li>' +
        '<li>מפעילים אותו פעם אחת — הוא מתקין את עצמו ויעלה אוטומטית בכל הפעלה של Windows.</li>' +
        '<li>חוזרים לכאן — החיבור יזוהה מעצמו.</li></ol>' +
        '<div class="row-actions center">' +
        '<button class="btn btn-primary btn-lg" id="off-dl">' + ui.icon('download') + 'פתח בדפדפן</button>' +
        '<button class="btn btn-tonal btn-lg" id="off-copy">' + ui.icon('link') + 'העתק קישור</button>' +
        '<button class="btn btn-tonal btn-lg" id="off-retry">' + ui.icon('refresh') + 'נסה שוב</button></div>' +
        '<p class="muted small" id="off-note">בודק שוב אוטומטית כל 10 שניות…</p></div>';
      off.querySelector('#off-dl').addEventListener('click', function () { SS.openUrl(SS.BRIDGE_URL); });
      off.querySelector('#off-copy').addEventListener('click', function () {
        SS.copyText(SS.BRIDGE_URL).then(function (ok) {
          ui.toast(ok ? 'הקישור הועתק' : 'ההעתקה נכשלה: ' + ui.isolateLtr(SS.BRIDGE_URL), ok ? 'success' : 'error');
        });
      });
      off.querySelector('#off-retry').addEventListener('click', function () {
        var b = this; b.disabled = true;
        var note = off.querySelector('#off-note'); note.textContent = 'מחפש את הגשר…';
        connect().then(function (ok) {
          b.disabled = false;
          if (!ok) note.textContent = 'הגשר לא נמצא. בודק שוב אוטומטית כל 10 שניות…';
        });
      });
    }
    scheduleProbe();
  }

  function scheduleProbe() {
    clearTimeout(state.probeTimer);
    if (state.suspended) return;
    state.probeTimer = setTimeout(function () {
      connect().then(function (ok) { if (!ok) scheduleProbe(); });
    }, 10000);
  }

  async function connect() {
    var h = null;
    try { h = await SS.bridge.probe(); } catch (e) { h = null; }
    if (!h) { if (state.connected || !document.getElementById('offline').dataset.built) showOffline(); return false; }
    clearTimeout(state.probeTimer);
    var wasConnected = state.connected;
    state.connected = true;
    setStatus(true);
    document.getElementById('offline').hidden = true;
    document.getElementById('shell-body').hidden = false;
    await syncLock(h);
    syncOffline();
    if (!wasConnected) setTimeout(sendPendingReports, 4000);
    if (!wasConnected) {
      var start = null;
      try { start = localStorage.getItem('ss.section'); } catch (e) { start = null; }
      var hash = (location.hash || '').replace('#', '');
      show(hash || start || (h.kioskActive ? 'kiosk' : 'audio'));
      setTimeout(prewarm, 300);
    }
    SS.pushZmanimNow();
    SS.pushLockCalendar();
    pushTheme();
    return true;
  }
  SS.reprobe = connect;

  /* תקלות שהגשר אסף אוטומטית (קריסות ושגיאות פנימיות) נשלחות בדיווח אחד דרך feedback.report.
     אוצריא מבקשת אישור לפני שליחה, ואם אין חיבור היא שומרת ושולחת כשיחזור. */
  var reportsSent = false;
  async function sendPendingReports() {
    if (reportsSent || !window.Otzaria || SS.bridge.isMock) return;
    var r = null;
    try { r = await SS.bridge.call('/diag/pending', {}, { timeoutMs: 15000 }); } catch (e) { return; }
    if (!r || r.enabled === false || !r.items || !r.items.length) return;
    reportsSent = true;
    var tail = '';
    try { tail = ((await SS.bridge.call('/diag/logTail', {}, { timeoutMs: 15000 })) || {}).text || ''; } catch (e) { tail = ''; }
    var details = ('דיווח תקלות אוטומטי מהגשר (' + r.items.length + ')\nתוסף: הגדרות מערכת ' + SS.VERSION + '\n\n' +
      r.items.map(function (x) { return x.text; }).join('\n\n=====\n\n') + (tail ? '\n\n===== יומן מקוצר =====\n' + tail : '')).slice(0, 5000);
    var res = null;
    try { res = await window.Otzaria.call('feedback.report', { details: details, reportType: 'bug' }); } catch (e) { res = null; }
    var val = res && (res.data !== undefined ? res.data : res);
    if (val === 'sent' || val === 'queued') {
      var ids = r.items.map(function (x) { return x.id; });
      try { await SS.bridge.call('/diag/markSent', { ids: ids, details: details }, { timeoutMs: 15000 }); }
      catch (e) { try { await SS.bridge.call('/diag/ack', { ids: ids }, { timeoutMs: 15000 }); } catch (e2) { /* ננסה בפעם הבאה */ } }
    }
    reportsSent = false; /* מינימלי: מאפשר שליחה חוזרת בסבב הבא */
  }
  setInterval(function () { if (state.connected) sendPendingReports(); }, 30 * 60 * 1000);
  setInterval(function () { if (state.connected) syncOffline(); }, 60 * 1000);
  /* ריפוי אוטומטי: טאב שנטען בזמן שהגשר היה לא זמין נשאר עם "הגשר אינו מחובר". כל כמה שניות בודקים אם הגשר חזר,
     ואם כן — טוענים מחדש (בלי לחכות ללחיצה על "רענון") כל טאב שמציג שגיאה. עד 4 ניסיונות לטאב, כדי לא להציף כששגיאה אמיתית. */
  var healBusy = false, healTries = {};
  setInterval(async function () {
    if (healBusy || state.suspended || document.hidden) return;
    var bad = Object.keys(views).filter(function (id) {
      var v = views[id];
      var has = v && v.body && v.body.querySelector('.state-box.error');
      if (!has) healTries[id] = 0;
      return has && (healTries[id] || 0) < 3;
    });
    if (!bad.length) return;
    healBusy = true;
    try {
      var h = null;
      try { h = await SS.bridge.probe(); } catch (e) { h = null; }
      if (!h) return;
      if (!state.connected) { await connect(); return; }
      bad.forEach(function (id) {
        healTries[id] = (healTries[id] || 0) + 1;
        var sec = sectionById(id), v = views[id];
        if (sec && v) { v.at = Date.now(); try { sec.load(v.body); } catch (e) { /* כלום */ } }
      });
    } finally { healBusy = false; }
  }, 8000);

  SS.sendPendingReports = sendPendingReports;

  SS.bridge.onPairing = function (on) {
    if (on) SS.ui.toast(window.I18N && window.I18N.isEn() ? 'Approve the connection in the window that opened on the computer' : 'יש לאשר את החיבור בחלון שנפתח במחשב', 'info');
  };
  SS.bridge.onLost = function () {
    if (!state.connected) return;
    /* גשר עמוס (בקשות כבדות במקביל) עלול לפספס בדיקה בודדת — מכריזים "לא מחובר" רק אחרי שלוש בדיקות כושלות ברצף */
    (async function () {
      for (var i = 0; i < 3; i++) {
        var h = null;
        try { h = await SS.bridge.probe(); } catch (e) { h = null; }
        if (h) { setStatus(true); return; }
        await new Promise(function (r) { setTimeout(r, 2000); });
      }
      showOffline();
    })();
  };

  async function boot(payload) {
    if (state.booted) return;
    state.booted = true;
    if (payload && payload.theme) onTheme(payload.theme);
    try { await window.I18N.init(payload); } catch (e) { /* נשארים בעברית */ }
    var lb = document.getElementById('top-lang');
    lb.textContent = window.I18N.toggleLabel();
    lb.addEventListener('click', function () { window.I18N.toggle(); });
    var ab = document.getElementById('top-about');
    if (ab) { ab.innerHTML = ui.icon('info'); ab.addEventListener('click', function () { show('about'); }); }
    var fb = document.getElementById('top-feedback');
    fb.innerHTML = window.Feedback.ICON;
    fb.addEventListener('click', function () {
      window.Feedback.open({
        ui: ui, plugin: 'הגדרות מערכת', version: SS.VERSION, bridge: SS.bridge,
        extra: function () { return ['גשר: ' + (state.connected ? 'מחובר' : 'לא מחובר')]; }
      });
    });
    if (payload && payload.plugin && payload.plugin.version) SS.VERSION = payload.plugin.version;
    buildNav();
    publishSettingsBooks();
    document.getElementById('top-refresh').addEventListener('click', function () {
      if (!state.connected) { connect(); return; }
      var s = sectionById(state.current);
      var body = document.querySelector('.section-body');
      if (s && body) s.load(body);
      SS.bridge.probe().then(function (h) { setStatus(!!h); if (!h) showOffline(); });
    });
    connect();
  }

  /* #56: ערכת העיצוב של אוצריא גם לחלונות הגשר (שורת שעון בנעילה, מסך מנוחה) */
  var lastTheme = null;
  function pushTheme() {
    if (!lastTheme || !state.connected) return;
    var cs = lastTheme.colorScheme || {};
    SS.bridge.call('/theme', { mode: lastTheme.mode === 'light' ? 'light' : 'dark', primary: String(cs.primary || '') }).catch(function () { /* גשר ישן */ });
  }
  function onTheme(th) { ui.applyTheme(th); lastTheme = th; pushTheme(); }
  SS.pushTheme = pushTheme;

  function init() {
    if (window.Otzaria) {
      window.Otzaria.on('plugin.boot', boot);
      window.Otzaria.on('theme.changed', onTheme);
      window.Otzaria.on('plugin.page_opened', function (ev) { /* #56: נפתח מהסייר — {section} */
        var pr = ev && (ev.param || (ev.data && ev.data.param));
        if (pr && pr.section) { try { location.hash = pr.section; } catch (e) { /* כלום */ } if (state.connected) show(pr.section); }
      });
      window.Otzaria.on('reader.toolbar_item_clicked', function (ev) {
        var id = ev && (ev.id || ev.itemId || (ev.item && ev.item.id));
        if (id !== 'chadbedera-ss-lock') return;
        SS.quickLockPending = true;
        if (state.connected) show('kiosk'); else { try { location.hash = 'kiosk'; } catch (e) { /* כלום */ } }
      });
      window.Otzaria.on('calendar.city_changed', function () { pushZmanimCache(); });
      /* #69: קיצורים מהמניפסט — פתיחת הגדרות מערכת / ישר להגדרות הנעילה */
      if (window.SdkX) {
        SdkX.onCommand('openSystemSettings', function () { SdkX.openSelf(); });
        SdkX.onCommand('openLockSettings', function () { SdkX.openSelf({ section: 'kiosk' }); });
        SdkX.onLibraryBookOpen(function (ev) { var id = ev && (ev.id || (ev.data && ev.data.id)); if (id) { if (state.connected) show(id); else SdkX.openSelf({ section: id }); } });
      }
      window.Otzaria.on('plugin.suspended', function () { state.suspended = true; clearTimeout(state.probeTimer); clearTimeout(zmanimTimer); });
      window.Otzaria.on('plugin.resumed', function () {
        state.suspended = false;
        if (!state.connected) connect();
        else SS.bridge.probe().then(function (h) { if (!h) showOffline(); else { setStatus(true); SS.pushZmanimNow(); } });
      });
    } else {
      document.body.classList.add('preview');
      if (/[?&]dark=1/.test(location.search)) document.body.classList.add('dark-mode');
      boot(null);
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
