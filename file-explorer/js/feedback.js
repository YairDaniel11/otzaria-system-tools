/* feedback.js - שליחת הערות דרך ממשק הדיווח הרשמי של אוצריא (feedback.report).
   הקריאה אינה דורשת הצהרת הרשאה; אוצריא מציגה למשתמש דיאלוג אישור משלה */
(function () {
  'use strict';
  var FB = window.Feedback = window.Feedback || {};
  var TYPES = [['bug', 'תקלה'], ['other', 'הצעה לשיפור'], ['other', 'משוב כללי']];
  var t = function (s) { return window.I18N ? window.I18N.t(s) : s; };

  FB.ICON = '<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
    '<path d="M4.5 5.5h15a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H10l-4.5 3.5V16.5h-1a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1z"/><path d="M8 10h8M8 13h5"/></svg>';

  var REPO = 'achat-shaalti/achat-shaalti';
  /* 04.11: גרסת הגשר בכל דיווח (בנוסף לגרסת התוסף) */
  async function bridgeLine(b) {
    if (!b || !b.probe) return 'גשר: לא ידוע';
    try { var h = await b.probe(); return h && h.version ? 'גשר: ' + h.version + (b.isDemo ? ' (לא מחובר)' : '') : 'גשר: לא מחובר'; }
    catch (e) { return 'גשר: לא מחובר'; }
  }
  async function toGithub(b, text, opts) {
    if (!b || b.isDemo) return;
    try { await b.call('/sys/feedback', { text: text, plugin: opts.plugin, version: opts.version || '', repo: opts.repo || REPO }, { timeoutMs: 8000 }); } catch (e) { /* גשר לא זמין */ }
  }
  /* 11.2026: דיווחים מגרסה שהותקנה מקובץ (לא מהחנות) לא תמיד מגיעים — דרך גיבוי: issue בגיטהאב עם הטקסט (מועתק גם ללוח) */
  async function openUrl(url) {
    var names = ['app.openUrl', 'app.open_url'];
    for (var i = 0; i < names.length; i++) {
      try { var r = await window.Otzaria.call(names[i], { url: url }); if (!r || r.success !== false) return true; } catch (e) { /* הבא */ }
    }
    return false;
  }
  FB.githubFallback = async function (ui, details, title) {
    var r = await ui.dialog({
      title: 'לוודא שהדיווח יגיע', icon: 'info',
      body: '<p>דיווחים מגרסה שהותקנה מקובץ (ולא מחנות אוצריא) לא תמיד מגיעים. אפשר לפתוח אותו גם בגיטהאב: הטקסט מועתק ללוח וממולא בדף שנפתח, ונשאר רק ללחוץ "Submit". דרך נוספת: פורום אוצריא.</p>',
      actions: [{ label: 'סגירה', value: false }, { label: 'פתיחה בגיטהאב', value: true, kind: 'primary' }]
    });
    if (!r) return;
    try { await navigator.clipboard.writeText(details); } catch (e) { /* לא חובה */ }
    var url = 'https://github.com/' + REPO + '/issues/new?title=' + encodeURIComponent(title || 'דיווח') + '&body=' + encodeURIComponent(details.slice(0, 1800));
    if (!(await openUrl(url))) ui.toast('לא ניתן לפתוח קישור. הטקסט הועתק ללוח — אפשר להדביק ב-github.com/' + REPO + '/issues', 'info');
  };
  /* מתג בהגדרות: דיווח קריסות אוטומטי (ברירת מחדל: פעיל; אוצריא תמיד מבקשת אישור לפני שליחה) */
  FB.crashEnabled = function () { try { return localStorage.getItem('fb.crashOff') !== '1'; } catch (e) { return true; } };
  FB.setCrashEnabled = function (on) { try { if (on) localStorage.removeItem('fb.crashOff'); else { localStorage.setItem('fb.crashOff', '1'); localStorage.removeItem(QKEY); } } catch (e) { /* כלום */ } };
  FB.crashToggleHtml = function () {
    return '<label class="fb-crash-toggle" style="display:flex;align-items:center;gap:0.5em;cursor:pointer"><input type="checkbox" data-fb-crash' + (FB.crashEnabled() ? ' checked' : '') + '>' +
      '<span>דיווח קריסות אוטומטי (פעם אחת בהפעלה, באישורכם)</span></label>';
  };
  document.addEventListener('change', function (e) {
    var c = e.target && e.target.closest && e.target.closest('[data-fb-crash]');
    if (c) FB.setCrashEnabled(c.checked);
  });
  /* opts: { ui, plugin, version, bridge, extra: function () { return 'שורות סביבה' } } */
  FB.open = async function (opts) {
    var ui = opts.ui;
    if (!window.Otzaria) { ui.toast('שליחת הערות זמינה רק בתוך אוצריא', 'info'); return; }
    var form = null;
    var body = '<div class="fb-form">' +
      '<p class="muted small">ההערה נשלחת לצוות אוצריא ולמפתח התוסף דרך מערכת הדיווח של אוצריא, וגם כ-issue ציבורי בגיטהאב של התוסף (פרטים אישיים מוסתרים). לפני השליחה תוצג בקשת אישור.</p>' +
      '<label class="field"><span class="field-label">סוג</span><select class="input fb-type" name="type">' +
      TYPES.map(function (x, i) { return '<option value="' + i + '">' + x[1] + '</option>'; }).join('') + '</select></label>' +
      '<label class="field"><span class="field-label">מה תרצו לספר?</span><textarea class="input fb-text" name="text" rows="6" maxlength="4000" dir="auto"></textarea></label>' +
      '</div>';
    var r = await ui.dialog({
      title: 'שליחת הערה', icon: 'info', body: body,
      actions: [{ label: 'ביטול', value: false }, { label: 'שליחה', value: true, kind: 'primary' }],
      onOpen: function (f) { form = f; setTimeout(function () { var x = f.querySelector('textarea'); if (x) x.focus(); }, 80); }
    });
    if (!r || !form) return;
    var text = String(form.querySelector('textarea').value || '').trim();
    if (!text) { ui.toast('לא נכתב תוכן — ההערה לא נשלחה', 'info'); return; }
    var type = TYPES[+form.querySelector('select').value] || TYPES[0];
    var env = ['תוסף: ' + opts.plugin + ' ' + (opts.version || ''), 'סוג: ' + type[1], 'שפה: ' + (window.I18N ? window.I18N.lang() : 'he')];
    env.push(await bridgeLine(opts.bridge));
    try { if (opts.extra) env = env.concat(opts.extra() || []); } catch (e) { /* כלום */ }
    var details = (text + '\n\n---\n' + env.join('\n')).slice(0, 5000);
    var res = null;
    try { res = await window.Otzaria.call('feedback.report', { details: details, reportType: type[0] }); }
    catch (e) { res = { success: false }; }
    var val = res && (res.data !== undefined ? res.data : res);
    /* #70: גם ל-GitHub (issue ציבורי) דרך הגשר והמתווך, בנוסף לאוצריא */
    if (val !== 'cancelled') await toGithub(opts.bridge, details, opts);
    if (val === 'sent' || val === 'queued') ui.toast(val === 'sent' ? 'ההערה נשלחה. תודה!' : 'ההערה תישלח כשיהיה חיבור. תודה!', 'success');
    else if (val === 'cancelled') { ui.toast('השליחה בוטלה', 'info'); return; }
    else ui.toast('השליחה נכשלה. אפשר לנסות שוב מאוחר יותר.', 'error');
    await FB.githubFallback(ui, details, type[1] + ': ' + opts.plugin + ' ' + (opts.version || ''));
  };
  /* ---------- דיווח קריסות אוטומטי דרך מערכת הדיווח של אוצריא (feedback.report, reportType: 'crash') ----------
     שגיאות JS שלא נתפסו נאספות לתור מקומי (עד 5, בלי כפילויות). באתחול הבא, או כשאוצריא טוענת את הדף
     מחדש אחרי נפילה (restore.reason === 'crashed'), נשלח דיווח אחד. אוצריא מציגה דיאלוג אישור משלה
     (אי אפשר לעקוף אותו), ובמצב מנותק הדיווח נשמר בתור שלה ונשלח כשיחזור חיבור. */
  var QKEY = 'fb.crashQueue';
  function qGet() { try { return JSON.parse(localStorage.getItem(QKEY) || '[]') || []; } catch (e) { return []; } }
  function qSet(a) { try { localStorage.setItem(QKEY, JSON.stringify(a.slice(-5))); } catch (e) { /* כלום */ } }
  function scrub(s) {
    return String(s || '')
      .replace(/[A-Za-z]:\\Users\\[^\\\s]+/gi, 'C:\\Users\\<user>')
      .replace(/\/Users\/[^\/\s]+/g, '/Users/<user>')
      .slice(0, 1200);
  }
  /* 04.11: שגיאות חיבור/צימוד צפויות (גשר לא זמין, צימוד נדחה או מוגבל) אינן קריסה — לא נאספות */
  var EXPECTED = { too_many: 1, busy: 1, unsupported: 1, pair_failed: 1, forbidden: 1, unpaired: 1, unreachable: 1, offline: 1, timeout: 1, transport: 1 };
  function expected(r) { return !!(r && typeof r === 'object' && (r.expected || EXPECTED[r.code])); }
  function push(msg, stack) {
    if (!FB.crashEnabled()) return;
    var q = qGet(), key = String(msg).slice(0, 160);
    if (q.some(function (x) { return x.key === key; })) return;
    q.push({ key: key, at: new Date().toISOString(), text: scrub(msg + (stack ? '\n' + stack : '')) });
    qSet(q);
  }
  FB.watchErrors = function () {
    if (FB._watching) return;
    FB._watching = true;
    window.addEventListener('error', function (e) { push(e.message || 'error', e.error && e.error.stack); });
    window.addEventListener('unhandledrejection', function (e) { var r = e.reason; if (expected(r)) return; push((r && r.message) || String(r), r && r.stack); });
  };
  /* opts: { plugin, version, restore, bridge } — לקרוא מתוך plugin.boot */
  FB.reportCrashes = async function (opts) {
    if (!window.Otzaria || FB._crashAsked || !FB.crashEnabled()) return;
    var crashed = !!(opts && opts.restore && opts.restore.reason === 'crashed');
    var q = qGet();
    if (!crashed && !q.length) return;
    FB._crashAsked = true;
    var lines = ['דיווח קריסה אוטומטי: ' + opts.plugin + ' ' + (opts.version || '')];
    if (crashed) lines.push('אוצריא טענה את דף התוסף מחדש אחרי נפילה (restore: crashed).');
    q.forEach(function (x) { lines.push('', '[' + x.at + ']', x.text); });
    lines.push('', 'שפה: ' + (window.I18N ? window.I18N.lang() : 'he'), await bridgeLine(opts.bridge));
    /* 04.11: קריסה גם לגיטהאב (דרך הגשר), כי דיווחי אוצריא על גרסת בנייה לא מגיעים */
    await toGithub(opts.bridge, lines.join('\n').slice(0, 5000), opts);
    var res = null;
    try { res = await window.Otzaria.call('feedback.report', { details: lines.join('\n').slice(0, 5000), reportType: 'crash' }); } catch (e) { res = null; }
    var val = res && (res.data !== undefined ? res.data : res);
    if (val === 'sent' || val === 'queued' || val === 'cancelled') qSet([]);
  };
  FB.watchErrors();
  FB.t = t;
})();
