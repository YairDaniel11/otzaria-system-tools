/* sections/kiosk.js - lock mode (kiosk): password, recovery code, enable, disable, settings */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var MIN_PW = 4;

  SS.sections.push({
    id: 'kiosk', title: 'מצב נעילה', icon: 'lock',
    mount: function (body) { body.innerHTML = ui.loading('טוען מצב נעילה…'); },
    load: load
  });

  async function load(body) {
    /* משוב 3.9: הטאב נתקע ב"טוען" בלי לדעת על מה ולכמה זמן. עכשיו: מונה שניות + שם השלב,
       ותקרת זמן לכל קריאה איטית (BIOS/צימוד/קודים) - מה שלא חזר בזמן מוצג בלעדיו */
    var t0 = Date.now(), step = 'מצב נעילה';
    var tick = setInterval(function () {
      var el = body.querySelector('.ss-load-step');
      if (!el) { var d = document.createElement('div'); d.className = 'ss-load-step muted'; body.appendChild(d); el = d; }
      el.textContent = 'שלב: ' + step + ' · ' + Math.round((Date.now() - t0) / 1000) + ' שנ\' (בדרך כלל עד 5 שנ\')';
    }, 500);
    function lim(p, ms, name) {
      return Promise.race([p.catch(function () { return null; }), new Promise(function (r) { setTimeout(function () { r(null); }, ms); })])
        .then(function (v) { step = name + ' ✓'; return v; });
    }
    try { await loadInner(body, lim, function (n) { step = n; }); }
    finally { clearInterval(tick); }
  }

  async function loadInner(body, lim, setStep) {
    var st;
    /* מצב הנעילה חיוני - ממתינים לו בלי תקרה (המונה ממשיך), ובכשל אמיתי מנסים שוב אוטומטית פעמיים */
    /* משוב 3.9: הטאב נפתח מיד כמו שאר הטאבים - מהמצב האחרון שנשמר, ומתעדכן כשהגשר עונה */
    var live = body.__kioskLive || bcall('/kiosk/status', {}, { timeoutMs: 120000 }); body.__kioskLive = null;
    if (!body.__kioskShown) {
      var cached = null;
      try { cached = JSON.parse(localStorage.getItem('ss.kiosk.status') || 'null'); } catch (e) { cached = null; }
      if (cached) { body.__kioskShown = true; render(body, cached, [], [], null, null); body.__kioskLive = live; live.then(function () { load(body); }).catch(function () {}); return; }
    }
    body.__kioskShown = false;
    for (var i = 0; ; i++) {
      try { st = await (i === 0 ? live : bcall('/kiosk/status', {}, { timeoutMs: 120000 })); try { localStorage.setItem('ss.kiosk.status', JSON.stringify(st)); } catch (e) { } break; }
      catch (e) { if (i >= 2) { body.innerHTML = errPanel(e, function () { load(body); }); return; } setStep('מצב נעילה (ניסיון ' + (i + 2) + ')'); await new Promise(function (r) { setTimeout(r, 1000); }); }
    }
    if (SS.bridge.health) { SS.bridge.health.kioskActive = !!st.active; SS.setStatus(true); if (SS.syncLock) SS.syncLock(SS.bridge.health); }

    /* ארבע הקריאות האלה בלתי תלויות זו בזו (ולא ב-st) - היו רצות ברצף (await אחד אחרי השני), כך
       שזמן הטעינה הכולל היה סכום הזמנים של כל חמש הקריאות ביחד. "זיהוי תוכנות" (סריקת כוננים) ו-
       "מצב BIOS" (שאילתות WMI) הן הכי איטיות, ולכן הרגישו שהכל "תקוע". עכשיו רצות במקביל - הזמן
       הכולל הוא רק הזמן של האיטית מביניהן, לא הסכום */
    setStep('תוכנות, צימוד ו-BIOS');
    var results = await Promise.all([
      lim(bcall('/kiosk/programs'), 5000, 'תוכנות'),
      /* משוב 15.10: זיהוי תוכנות (סריקת כוננים) איטי והשהה את כל הטאב — כולל ריבוע הגדרות הנעילה.
         התוצאה נשמרת למשך הסשן; אם עוד לא מוכנה אחרי 1.5 שנ' מציגים בלעדיה ומשלימים כשהיא מגיעה */
      detectFast(body),
      lim(bcall('/pair/list', {}), 12000, 'צימוד'),
      lim(bcall('/bios/status', {}), 4000, 'BIOS'),
      lim(bcall('/kiosk/recovery/backup/status', {}), 4000, 'קודי שחזור'), /* #69 */
      lim(bcall('/fs/policy/get', {}), 4000, 'סייר קבצים') /* מצב נעילת הסייר, מוצג ישירות בלי לעבור טאב */
    ]);
    var pr = results[0], det = results[1], pairsRes = results[2], bios = results[3], backup = results[4], fx = results[5];

    var progs = (pr && pr.programs) || [];
    st.autoTorah = !!(pr && pr.autoTorah);
    var found = (det && det.found) || [];
    found = found.filter(function (f) {
      return !progs.some(function (p) { return p.path.toLowerCase() === f.path.toLowerCase(); });
    });
    var pairsKnown = !!pairsRes, pairs = (pairsRes && pairsRes.clients) || [];

    render(body, st, progs, found, pairsKnown ? pairs : null, bios, backup, fx);
  }

  var detPromise = null, detResult = null;
  SS.prefetchDetect = function () {
    if (!detPromise) detPromise = bcall('/kiosk/programs/detect')
      .then(function (r) { detResult = r; return r; })
      .catch(function () { detPromise = null; return null; });
    return detPromise;
  };
  function detectFast(body) {
    if (detResult) return Promise.resolve(detResult);
    var p = SS.prefetchDetect(), late = false;
    return Promise.race([p, new Promise(function (res) { setTimeout(function () { late = true; res(null); }, 1500); })])
      .then(function (r) {
        if (!r && late) p.then(function (d) {
          /* מרעננים רק אם יש מה להוסיף ואין עריכה פעילה בטופס */
          if (d && d.found && d.found.length && body.isConnected && !body.contains(document.activeElement)) load(body);
        });
        return r;
      });
  }

  /* ספירה לאחור חיה של הפתיחה הזמנית בכרטיס המצב (מתאפסת בכל רינדור) */
  var tempTimer = null;

  function render(body, st, progs, found, pairs, bios, backup, fx) {
    var active = !!st.active;
    var tu = st.tempUnlock || {};
    clearInterval(tempTimer); tempTimer = null;
    var html = '';
    if (!active && tu.active) {
      /* פתיחה זמנית: הנעילה עצמה דלוקה, ותחזור מעצמה בתום הזמן. הזמן מוצג כאן גם אם הבאנר במסך הוסתר */
      html +=
        '<div class="status-card active">' +
        '<div class="status-ic">' + ui.icon('clock') + '</div>' +
        '<div class="status-text"><span class="eyebrow">מצב נעילה</span><h3>פתוח זמנית</h3>' +
        '<p><span>הנעילה תחזור אוטומטית בעוד</span> <bdi class="temp-left" dir="ltr" data-temp-left>' + fmtLeft(tu.secondsLeft) + '</bdi></p></div>' +
        '<button class="btn btn-lg btn-on-status" data-act="temp-end">' + ui.icon('lock') + 'נעילה עכשיו</button>' +
        '</div>';
    } else html +=
      '<div class="status-card ' + (active ? 'active' : '') + '">' +
      '<div class="status-ic">' + ui.icon(active ? 'lock' : 'unlock') + '</div>' +
      '<div class="status-text"><span class="eyebrow">מצב נעילה</span><h3>' + (active ? 'פעיל' : 'לא פעיל') + '</h3>' +
      '<p>' + (active
        ? 'המחשב נעול לאוצריא בלבד' + (st.strict ? ' · נעילה קפדנית' : '') + (st.blockTaskManager ? ' · מנהל המשימות חסום' : '') + '.'
        : 'המחשב פועל כרגיל. אפשר להפעיל נעילה כדי שישמש ללימוד בלבד.') + '</p></div>' +
      (st.hasPassword
        ? (active
          ? (tu.usable ? '<button class="btn btn-lg btn-on-status" data-act="temp-unlock">' + ui.icon('clock') + 'פתיחה זמנית</button>' : '') +
            '<button class="btn btn-lg btn-on-status" data-act="disable">' + ui.icon('unlock') + 'ביטול נעילה</button>'
          : '<button class="btn btn-lg btn-primary" data-act="enable">' + ui.icon('lock') + 'הפעלת נעילה</button>')
        : '') +
      '</div>';

    html += healthPanel(st, bios, backup);
    html += emergencyCenter(st, backup);

    /* #136.3: כל ההסברים המתקפלים בריבוע אחד */
    html += '<div class="card explain-group"><div class="card-title">' + ui.icon('info') + 'הסברים על הנעילה</div>';
    html += '<details class="explain-sub"><summary>מה עושה מצב נעילה?</summary><ul class="feature-list">' +
      '<li>' + ui.icon('shield') + '<span><b>רק אוצריא פתוחה.</b> אם מישהו סוגר אותה — היא נפתחת מחדש מעצמה ונשארת בחזית.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>שורת המשימות ותפריט התחל מוסתרים,</b> כך שאי אפשר לפתוח תוכנות אחרות.</span></li>' +
      '<li>' + ui.icon('key') + '<span><b>מקשי מעבר חסומים:</b> Win, ‏Alt+Tab, ‏Alt+F4, ‏Ctrl+Esc ומנהל המשימות.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>יציאה רק עם סיסמה</b> — מכאן, מתוך התוסף. הנעילה חוזרת אוטומטית גם אחרי הפעלה מחדש.</span></li>' +
      '</ul></details>';

    html += '<details class="explain-sub"><summary>מה קורה בכיבוי, בהפעלה מחדש ובחשבון Windows אחר?</summary><ul class="feature-list">' +
      '<li>' + ui.icon('lock') + '<span><b>כיבוי או הפעלה מחדש:</b> כברירת מחדל הנעילה <b>נשמרת</b>. הגשר עולה עם Windows וננעל שוב, ואוצריא נפתחת. אם רוצים שבכיבוי הנעילה תתאפס, מסמנים "ביטול נעילה בכיבוי המחשב" בהגדרות הנעילה (לא פועל עם נעילה מוקשחת).</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>חשבון Windows אחר:</b> נעילה רגילה חלה רק בחשבון שבו הופעלה. מחשבון אחר אפשר לעצור את הגשר. <b>נעילה מוקשחת</b> (אופציונלית) מפעילה את הגשר גם בחשבונות אחרים וחוסמת בהם את מנהל המשימות.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>הכי בטוח לעמדת לימוד:</b> חשבון Windows <b>רגיל</b> (בלי הרשאות מנהל) ללא גישה להגדרות המחשב, שבו מפעילים את הנעילה; חשבון המנהל נשאר עם סיסמה נפרדת.</span></li>' +
      '</ul></details>';

    if ((st.warnings || []).indexOf('admin') >= 0) {
      html += '<div class="explain-warn"><b>' + ui.icon('warning') + 'אזהרה: חשבון עם הרשאות מנהל</b>' +
        '<p class="muted">המחשב מופעל בחשבון Windows עם הרשאות מנהל, שממנו אפשר לעקוף את הנעילה. ' +
        'מומלץ ליצור חשבון משתמש <b>רגיל (ללא הרשאות מנהל)</b> לעמדת הלימוד, ולהפעיל בו את הנעילה.</p></div>';
    }


    /* C6: מתקפל */
    html += '<details class="explain-sub"><summary>זו נעילה "רכה" — להקשחה מלאה</summary>' +
      '<p class="muted">הנעילה רצה בהרשאות המשתמש ומונעת שימוש מזדמן. מי שמבין במחשבים יכול לעקוף אותה. ' +
      'כדי שתעמוד גם מול ניסיון מכוון, השלימו שכבות שאינן תלויות בתוכנה:</p>' +
      '<ul class="feature-list">' +
      '<li>' + ui.icon('key') + '<span><b>סיסמת BIOS/UEFI</b> וביטול אתחול מ-USB — חוסם כניסה ל-Safe Mode ואתחול מהתקן חיצוני. ' +
      'ב-Lenovo וב-HP (דגמים עסקיים) אפשר לנסות להפעיל אוטומטית בכרטיס „הקשחת BIOS/UEFI” למטה; ' +
      'ב-Dell וב-Asus, וכשהניסיון האוטומטי לא מצליח, יש להגדיר ידנית בהגדרות ה-BIOS של המחשב.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>חשבון משתמש רגיל</b> ללא הרשאות מנהל, וחשבון המנהל עם סיסמה שהתלמיד אינו יודע.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>סינון תוכן על המחשב עצמו</b> (למשל נטפרי), ולא רק ברמת הרשת — כך מעבר לרשת אחרת אינו עוקף את הסינון.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>Safe Mode:</b> במצב בטוח הגשר אינו עולה והנעילה אינה חלה. סיסמת BIOS שחוסמת את תפריט האתחול סוגרת גם את זה.</span></li>' +
      '</ul></details>';
    html += '</div>';

    if (st.hasPassword && st.recoverySaved === false) {
      html += '<div class="card"><div class="card-title">' + ui.icon('key') + ' קוד השחזור עוד לא נשמר</div>' +
        '<p class="muted">לפני הפעלת הנעילה יש לשמור את קוד השחזור לקובץ או להדפיס אותו. אם הקוד לא נרשם — אפשר ליצור קוד חדש דרך „שינוי סיסמה” עם הסיסמה הנוכחית.</p></div>';
    }
    if (!st.hasPassword) {
      html += '<div class="card"><div class="card-title">אשף הגדרה ראשונה · שלב 1 מתוך 3: סיסמה</div>' +
        '<p class="muted">לפני הפעלת הנעילה צריך לבחור סיסמה. רק מי שיודע אותה יוכל לבטל את הנעילה או לשנות הגדרות.</p>' +
        '<form class="form-grid" data-form="setpw" novalidate>' +
        '<label class="field"><span class="field-label">סיסמה</span><input class="input" type="password" name="pw" autocomplete="new-password" dir="ltr"></label>' +
        '<label class="field"><span class="field-label">אימות סיסמה</span><input class="input" type="password" name="pw2" autocomplete="new-password" dir="ltr"></label>' +
        '<div class="form-msg" hidden></div>' +
        '<div class="row-actions"><button class="btn btn-primary btn-lg" type="submit">' + ui.icon('key') + 'שמירת סיסמה</button></div>' +
        '<p class="muted small">לפחות ' + MIN_PW + ' תווים. אחרי השמירה יוצג קוד שחזור חד-פעמי — חשוב לרשום אותו.</p>' +
        '</form></div>';
    } else {
      /* #136: עיצוב כמו „הגדרות נעילה” — הסבר וקבוצות מתקפלות */
      html += '<div class="card"><div class="card-title">סיסמה והגדרות</div>' +
        '<p class="muted">סיסמת המנהל, כלי ניהול, ותקציר ההגדרות הפעילות כעת.</p><div class="dlg-fields">' +
        '<details class="field-group" open><summary><span>סיסמה וקודי שחזור</span><small>שינוי סיסמה וקודי גיבוי</small></summary><div class="field-group-body">' +
        '<div class="setting-row"><div class="setting-text"><b>שינוי סיסמה</b><span class="muted">החלפת סיסמת הנעילה (נדרשת הסיסמה הנוכחית)</span></div>' +
        '<button class="btn btn-tonal" data-act="chpw">' + ui.icon('edit') + 'שינוי</button></div>' +
        (backup ? '<div class="setting-row"><div class="setting-text"><b>קודי גיבוי לשחזור</b><span class="muted">' +
          (backup.left ? (window.I18N && I18N.isEn && I18N.isEn() ? backup.left + ' one-time codes left' : 'נשארו ' + backup.left + ' קודים חד-פעמיים') : 'עוד לא נוצרו') +
          ' · למקרה ששכחתם גם את הסיסמה וגם את קוד השחזור. כל קוד עובד פעם אחת</span></div>' +
          '<button class="btn btn-tonal" data-act="backup">' + ui.icon('key') + (backup.left ? 'יצירה מחדש' : 'יצירה') + '</button></div>' : '') +
        '</div></details>' +
        '<details class="field-group"><summary><span>כלי ניהול</span><small>פרופיל, יומן, ייצוא ויבוא, נעילת הסייר</small></summary><div class="field-group-body">' +
        '<div class="setting-row"><div class="setting-text"><b>פרופיל נעילה מוכן</b><span class="muted">תלמיד / בית מדרש / מנהל — מחיל סט הגדרות בלחיצה (אפשר לשנות אחר כך)</span></div>' +
        '<button class="btn btn-tonal" data-act="profile">' + ui.icon('lock') + 'בחירה</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>יומן פעולות מנהל</b><span class="muted">הפעלה וביטול נעילה, שינויי סיסמה והגדרות, יבוא וייצוא</span></div>' +
        '<button class="btn btn-tonal" data-act="adminlog">' + ui.icon('info') + 'הצגה</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>ייצוא ויבוא הגדרות</b><span class="muted">העברת הגדרות הנעילה, הסייר ושומר המסך למחשב אחר (בלי סיסמאות וקודים)</span></div>' +
        '<div class="btn-group"><button class="btn btn-tonal" data-act="export">ייצוא</button><button class="btn btn-tonal" data-act="import">יבוא</button></div></div>' +
        '</div></details>' +
        '<details class="field-group"><summary><span>מצב נוכחי</span><small>מה פתוח, תזמון, קבצים, שומר מסך</small></summary><div class="field-group-body">' +
        '<div class="setting-row"><div class="setting-text"><b>מה פתוח בזמן נעילה</b><span class="muted">' +
        ui.esc((st.openSections || []).map(function (id) { return SECTION_NAMES[id] || id; }).join(', ') || 'רק תוכנות לימוד ואודות') +
        (st.readOnly ? ' · קבצים לקריאה בלבד' : '') + (st.allowExcelDocs ? ' · Excel מותר בסייר' : '') + (st.clock !== false ? ' · שעון צף' : '') + '</span></div>' +
        '</div>' +
        '<div class="setting-row"><div class="setting-text"><b>תזמון נעילה לפי שעות</b><span class="muted">' +
        (st.schedule && st.schedule.enabled
          ? 'נעילה אוטומטית בכל יום <bdi dir="ltr">' + ui.esc(st.schedule.start) + '–' + ui.esc(st.schedule.end) + '</bdi>'
          : 'כבוי — נעילה ידנית בלבד') + ' · סטטוס נוכחי, לשינוי ראו „הגדרות נעילה” למטה</span></div>' +
        '</div>' +
        '<div class="setting-row"><div class="setting-text"><b>פתיחה זמנית בסיסמה נפרדת</b><span class="muted">' + tempSummary(tu) + ' · סטטוס נוכחי, לשינוי ראו „הגדרות נעילה” למטה</span></div>' +
        '</div>' +
        '<div class="setting-row"><div class="setting-text"><b>קבצים שמותר לפתוח בזמן נעילה</b><span class="muted"><bdi dir="ltr">' +
        ui.esc((st.allowOpenExtensions || []).length ? st.allowOpenExtensions.map(function (x) { return '.' + x; }).join(', ') : '—') + '</bdi></span></div>' +
        '</div>' +
        '<div class="setting-row"><div class="setting-text"><b>שומר מסך וזמני היום</b><span class="muted">' +
        ui.esc(st.zmanimCity || (st.zmanimLocation ? 'קואורדינטות מותאמות אישית' : 'לא הוגדר מיקום')) +
        (st.yahrzeitDay ? ' · יום השנה מוגדר' : '') + '</span></div>' +
        '<button class="btn btn-tonal" data-act="settings3">' + ui.icon('clock') + 'לטאב שומר מסך</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>נעילת סייר הקבצים</b><span class="muted">' + fxSummary(fx) + '</span></div>' +
        '<button class="btn btn-tonal" data-act="fx-manage">' + ui.icon('lock') + 'פתיחת „נעילת סייר הקבצים”</button></div>' +
        '<div class="setting-row"><div class="setting-text"><b>נתיב אוצריא</b><span class="muted path"><bdi dir="ltr">' + ui.esc(st.otzariaPath || 'זיהוי אוטומטי') + '</bdi></span></div></div>' +
        '</div></details></div></div>';
      html += '<div class="card"><div class="card-title">הגדרות נעילה</div><div data-slot="kset">' + ui.loading('טוען הגדרות…') + '</div></div>';
      /* #60: הרשימה המלאה עברה לטאב "תוכנות תורניות" */
      html += '<div class="card"><div class="card-title">תוכנות מותרות לפתיחה</div><div class="setting-row"><div class="setting-text"><span class="muted">' + progs.length + ' תוכנות מותרות' + (st.autoTorah ? ' · זיהוי אוטומטי פעיל' : '') + '</span></div>' +
        '<button class="btn btn-tonal" data-act="prog-manage">' + ui.icon('computer') + 'ניהול בטאב „תוכנות לימוד”</button></div></div>';
      /* #136.4: כרטיסי המידע (נעילה מוקשחת, BIOS, שכחתם סיסמה) בריבוע אחד */
      html += '<div class="card info-group"><div class="card-title">' + ui.icon('shield') + 'חיבורים, הקשחה ושחזור</div>' +
        infoSub(pairCard(pairs || [], !!pairs)) + infoSub(hardCard(st.hard || {})) + infoSub(biosCard(bios)) + '</div>';
    }

    body.innerHTML = html;
    if (!active && tu.active) startTempCountdown(body, tu.secondsLeft);
    wire(body, st, progs, found, backup);
    wireHard(body, st, pairs || []);
    wireBios(body, bios);
  }

  function wire(body, st, progs, found, backup) {
    var f = body.querySelector('[data-form=setpw]');
    if (f) {
      setTimeout(function () { var i = f.querySelector('input'); if (i) i.focus(); }, 50);
      f.addEventListener('submit', async function (e) {
        e.preventDefault();
        var msg = f.querySelector('.form-msg');
        var pw = f.pw.value, pw2 = f.pw2.value;
        var bad = pw.length < MIN_PW ? 'הסיסמה חייבת להכיל לפחות ' + MIN_PW + ' תווים' : pw !== pw2 ? 'הסיסמאות אינן תואמות' : '';
        if (bad) { msg.textContent = bad; msg.hidden = false; return; }
        msg.hidden = true;
        var btn = f.querySelector('button[type=submit]');
        btn.disabled = true;
        try {
          var r = await bcall('/kiosk/set-password', { newPassword: pw });
          if (r && r.recoveryCode) await showRecovery(r.recoveryCode);
          else ui.toast('הסיסמה נשמרה', 'success');
          await wizardRest(pw);
          load(body);
        } catch (err) {
          msg.textContent = friendlyErr(err); msg.hidden = false; btn.disabled = false;
        }
      });
    }
    on('enable', function () { enable(body, st); });
    /* כפתור "נעילה מיידית" בסרגל הקורא: פותח מיד את חלונית ההפעלה */
    if (SS.quickLockPending) { SS.quickLockPending = false; if (!st.active) enable(body, st); }
    on('disable', function () { disable(body); });
    on('temp-unlock', function () { tempUnlock(body, st); });
    on('temp-end', function () { tempEnd(body); });
    on('chpw', function () { changePw(body); });
    on('profile', function () { applyProfile(body); });
    on('adminlog', function () { showAdminLog(); });
    on('backup', function () { makeBackupCodes(body); });
    on('export', function () { exportSettings(); });
    on('import', function () { importSettings(body); });
    editSettings(body, st); /* הגדרות הנעילה מוצגות ישירות בעמוד, לא מאחורי כפתור */
    on('settings3', function () { SS.show('screensaver'); });
    on('prog-manage', function () { SS.show('programs'); });
    on('em-copy', function () {
      SS.copyText(UNLOCK_CMD).then(function (ok) { ui.toast(ok ? 'הפקודה הועתקה. החליפו את <קוד-שחזור> בקוד האמיתי שלכם' : 'ההעתקה נכשלה', ok ? 'success' : 'error'); });
    });
    on('em-refresh', function () { ui.toast('מרענן את מצב ההקשחה…', 'success'); load(body); });
    on('em-backup', function () { makeBackupCodes(body); });
    on('em-chpw', function () { changePw(body); });
    on('fx-manage', function () { SS.show('fxpolicy'); });
    on('prog-add', function () { addProgram(body, {}); });
    found.forEach(function (f, i) {
      on('prog-found-' + i, function () { addProgram(body, f); });
    });
    progs.forEach(function (p) {
      on('prog-open-' + p.id, function () { openProgram(p); });
      on('prog-del-' + p.id, function () { removeProgram(body, p); });
    });

    function on(act, fn) {
      var b = body.querySelector('[data-act=' + act + ']');
      if (b) b.addEventListener('click', fn);
    }
  }

  function showRecovery(code) {
    var kept = false;
    var pretty = code.replace(/(.{4})(?=.)/g, '$1-');
    return ui.dialog({
      title: 'קוד השחזור שלכם', icon: 'key', tone: 'recovery', dismissible: false,
      body:
        '<p>זהו <b>קוד שחזור חד-פעמי</b>. הוא יוצג <b>רק עכשיו</b> ולא יהיה אפשר לראות אותו שוב.</p>' +
        '<div class="recovery-code" dir="ltr">' + ui.esc(pretty) +
        '<button type="button" class="btn btn-text" data-copy-code title="העתקת הקוד">' + ui.icon('copy') + '</button></div>' +
        '<p>רשמו אותו על דף ושמרו במקום בטוח, <b>לא ליד המחשב</b>. אם תשכחו את הסיסמה, אפשר לבטל את הנעילה עם הקוד הזה בחלון „ביטול נעילה”, או מחלון פקודה:</p>' +
        '<code class="cmd" dir="ltr">OtzariaBridge.exe --unlock ' + ui.esc(code) + '</code>' +
        '<p><b>חובה לשמור את הקוד לפני הפעלת הנעילה:</b></p><div class="row-actions full">' +
        '<select data-keep-drive aria-label="כונן חיצוני"><option value="">טוען כוננים חיצוניים…</option></select>' +
        '<button type="button" class="btn btn-tonal" data-keep="file" disabled>' + ui.icon('download') + 'שמירה לכונן חיצוני</button>' +
        '<button type="button" class="btn btn-tonal" data-keep="print">' + ui.icon('edit') + 'הדפסה</button></div>',
      fields: [{ name: 'ack', type: 'checkbox', label: 'שמרתי/הדפסתי את הקוד והוא במקום בטוח' }],
      actions: [{ label: 'סיום', value: true, kind: 'primary' }],
      validate: function (v) { return !kept ? 'יש לשמור את הקוד לקובץ או להדפיס אותו' : v.ack ? '' : 'יש לסמן שהקוד נשמר לפני הסגירה'; },
      onOpen: function (form) {
        var btn = form.querySelector('button[type=submit]');
        var cb = form.querySelector('input[name=ack]');
        btn.disabled = true; cb.disabled = true;
        cb.addEventListener('change', function () { btn.disabled = !(cb.checked && kept); });
        /* סבב G (9): שמירה לקובץ או הדפסה — חובה לפני שאפשר לסיים (וגם לפני הפעלת נעילה, נאכף בגשר) */
        var sel = form.querySelector('[data-keep-drive]');
        var fileBtn = form.querySelector('[data-keep=file]');
        /* שמירה לקובץ רק לכונן נשלף (אין עותק במחשב); הגשר מאמת שוב */
        function loadDrives() {
          bcall('/sys/usb/list').then(function (d) {
            var drives = (d && d.drives) || [];
            var cur = sel.value;
            sel.innerHTML = drives.length
              ? drives.map(function (x) { return '<option value="' + ui.esc(x.letter) + '\\">' + ui.esc(x.letter + ' ' + (x.label || x.model || 'כונן חיצוני')) + '</option>'; }).join('')
              : '<option value="">לא נמצא כונן חיצוני — חברו כונן והמתינו רגע</option>';
            if (cur) sel.value = cur;
            fileBtn.disabled = !drives.length;
          }, function () { sel.innerHTML = '<option value="">רשימת הכוננים לא זמינה</option>'; fileBtn.disabled = true; });
        }
        loadDrives();
        var drvTimer = setInterval(function () { if (!form.isConnected) clearInterval(drvTimer); else loadDrives(); }, 4000);
        form.querySelectorAll('[data-keep]').forEach(function (kb) {
          kb.addEventListener('click', function () {
            var mode = kb.getAttribute('data-keep');
            bcall('/kiosk/recovery/keep', { code: code, mode: mode, dir: mode === 'file' ? sel.value : undefined }).then(function (r) {
              kept = true; cb.disabled = false;
              ui.toast(mode === 'print' ? 'נשלח להדפסה' : 'נשמר: ' + ui.isolateLtr((r && r.path) || ''), 'success');
            }, function (err) { ui.toast(friendlyErr(err), 'error'); });
          });
        });
        var copyBtn = form.querySelector('[data-copy-code]');
        if (copyBtn) copyBtn.addEventListener('click', function () {
          SS.copyText(code).then(function (ok) { ui.toast(ok ? 'הקוד הועתק' : 'ההעתקה נכשלה', ok ? 'success' : 'error'); });
        });
      }
    });
  }

  async function enable(body, st) {
    var v = await ui.dialog({
      title: 'הפעלת מצב נעילה', icon: 'lock',
      body: '<p>הקלידו את סיסמת הנעילה ובחרו אפשרויות.</p>',
      fields: [
        { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' },
        { name: 'tm', type: 'checkbox', label: 'חסום מנהל משימות', value: st.blockTaskManager !== false, hint: 'מונע סגירה של אוצריא דרך Ctrl+Shift+Esc' },
        { name: 'strict', type: 'checkbox', label: 'נעילה קפדנית (החלפת מעטפת Windows — מהכניסה הבאה)', value: false,
          hint: '<span class="warn-text">' + ui.icon('warning') + 'אוצריא תחליף את שולחן העבודה של Windows למשתמש זה. השתמשו רק אם רשמתם את קוד השחזור.</span>' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'המשך', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; }
    });
    if (!v) return;
    var summary = '<p>לאחר האישור:</p><ul class="bullets">' +
      '<li>שורת המשימות ותפריט התחל ייעלמו.</li><li>רק אוצריא תישאר פתוחה, ומקשי המעבר ייחסמו.</li>' +
      (v.tm ? '<li>מנהל המשימות ייחסם.</li>' : '') +
      (v.strict ? '<li><b>נעילה קפדנית:</b> מהכניסה הבאה ל-Windows תיפתח אוצריא במקום שולחן העבודה.</li>' : '') +
      '</ul><p>כדי לצאת — חזרו למסך זה ובחרו „ביטול נעילה”.</p>';
    var ok = await ui.dialog({
      title: 'לנעול את המחשב?', icon: 'warning', tone: v.strict ? 'danger' : '', body: summary,
      actions: [{ label: 'חזרה', value: false }, { label: 'נעל עכשיו', value: true, kind: v.strict ? 'danger' : 'primary' }],
      onSubmit: async function () {
        await bcall('/kiosk/enable', { password: v.pw, strict: !!v.strict, blockTaskManager: !!v.tm }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('מצב נעילה הופעל', 'success'); load(body); }
  }

  async function disable(body) {
    var ok = await ui.dialog({
      title: 'ביטול מצב נעילה', icon: 'unlock',
      body: '<p>הקלידו את סיסמת הנעילה, או את קוד השחזור.</p>',
      fields: [{ name: 'pw', label: 'סיסמה או קוד שחזור', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'בטל נעילה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) {
        var pw = x.pw.trim();
        await bcall('/kiosk/disable', { password: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){2}$/.test(pw) ? pw.replace(/-/g, '') : x.pw }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('מצב נעילה בוטל', 'success'); load(body); }
  }

  /* ---------- פתיחה זמנית: כל הנעילה נפתחת לזמן קבוע, בסיסמה נפרדת מסיסמת הנעילה ---------- */
  function fmtLeft(sec) {
    sec = Math.max(0, sec | 0);
    var s = sec % 60;
    return Math.floor(sec / 60) + ':' + (s < 10 ? '0' : '') + s;
  }

  function tempSummary(tu) {
    if (tu.serviceOutdated) return 'רכיב ההקשחה צריך עדכון כדי לתמוך בפתיחה זמנית';
    if (!tu.enabled) return 'כבויה';
    if (!tu.twoFactorOn) return 'רדומה — פועלת רק כשהאימות הדו-שלבי מופעל';
    if (!tu.configured) return 'לא נבחרה סיסמת פתיחה זמנית';
    return tu.active ? 'פעילה עכשיו' : 'מופעלת · ' + (tu.minutes || 30) + ' דקות בכל פתיחה';
  }

  function startTempCountdown(body, secondsLeft) {
    var end = Date.now() + Math.max(0, secondsLeft | 0) * 1000;
    tempTimer = setInterval(function () {
      var el = body.querySelector('[data-temp-left]');
      if (!el) { clearInterval(tempTimer); tempTimer = null; return; }
      var left = Math.ceil((end - Date.now()) / 1000);
      el.textContent = fmtLeft(left);
      if (left <= 0) { clearInterval(tempTimer); tempTimer = null; setTimeout(function () { load(body); }, 2500); }
    }, 1000);
  }

  async function tempUnlock(body, st) {
    var tu = st.tempUnlock || {};
    var ok = await ui.dialog({
      title: 'פתיחה זמנית', icon: 'clock',
      body: '<p>הקלידו את <b>סיסמת הפתיחה הזמנית</b> (לא את סיסמת הנעילה). כל הנעילה, כולל סייר הקבצים, תיפתח ל-' +
        ui.esc(String(tu.minutes || 30)) + ' דקות, ואז תחזור מעצמה.</p>',
      fields: [{ name: 'pw', label: 'סיסמת פתיחה זמנית', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'פתיחה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הפתיחה הזמנית'; },
      onSubmit: async function (x) {
        await bcall('/kiosk/temp-unlock', { password: x.pw }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('המחשב נפתח זמנית', 'success'); load(body); }
  }

  async function tempEnd(body) {
    if (!(await ui.confirm('נעילה עכשיו', '<p>לסיים את הפתיחה הזמנית ולהחזיר את הנעילה מיד?</p>', 'נעילה עכשיו'))) return;
    try { await bcall('/kiosk/temp-unlock/end', {}, { timeoutMs: 30000 }); ui.toast('הנעילה הוחזרה', 'success'); }
    catch (e) { ui.toast(friendlyErr(e), 'error'); }
    load(body);
  }

  async function changePw(body) {
    var ok = await ui.dialog({
      title: 'שינוי סיסמת נעילה', icon: 'key',
      fields: [
        { name: 'old', label: 'סיסמה נוכחית', type: 'password', dir: 'ltr', hint: 'שכחתם? הקלידו כאן את קוד השחזור (12 תווים) או אחד מקודי הגיבוי החד-פעמיים. הנעילה נשארת פעילה, וקוד שחזור חדש יוצג' },
        { name: 'n1', label: 'סיסמה חדשה', type: 'password', dir: 'ltr' },
        { name: 'n2', label: 'אימות סיסמה חדשה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.old) return 'יש להקליד את הסיסמה הנוכחית או את קוד השחזור';
        if (x.n1.length < MIN_PW) return 'הסיסמה החדשה חייבת להכיל לפחות ' + MIN_PW + ' תווים';
        if (x.n1 !== x.n2) return 'הסיסמאות החדשות אינן תואמות';
        return '';
      },
      onSubmit: async function (x) {
        var r = await bcall('/kiosk/set-password', { oldPassword: x.old, newPassword: x.n1 });
        return r || true;
      }
    });
    if (ok) {
      if (ok.recoveryCode) await showRecovery(ok.recoveryCode);
      ui.toast('הסיסמה עודכנה', 'success');
      load(body);
    }
  }

  /* ---------- #57: פרופילי נעילה, יומן פעולות מנהל, ייצוא/יבוא ---------- */
  var PROFILES = {
    student: { label: 'תלמיד', desc: 'קבצים לקריאה בלבד, אוצריא במסך מלא, רק שמע ומסך פתוחים', p: { readOnly: true, fullscreenOtzaria: true, sideBar: true, clock: true, allowExcelDocs: false, openSections: ['audio', 'display'] } },
    beitmidrash: { label: 'בית מדרש', desc: 'גישה רחבה יותר: שמע, מסך, מדפסות, שעה; קבצים פתוחים לעריכה', p: { readOnly: false, fullscreenOtzaria: true, sideBar: true, clock: true, allowExcelDocs: true, openSections: ['audio', 'display', 'printer', 'time'] } },
    admin: { label: 'מנהל', desc: 'כמעט הכל פתוח, בלי מסך מלא', p: { readOnly: false, fullscreenOtzaria: false, sideBar: true, clock: true, allowExcelDocs: true, openSections: ['audio', 'display', 'energy', 'keyboard', 'mouse', 'printer', 'time', 'wifi', 'bluetooth'] } }
  };
  /* סבב G (8): אשף הגדרה ראשונה — אחרי סיסמה וקוד שחזור: פרופיל, ואז תוכנות */
  async function wizardRest(pw) {
    var prof = await ui.dialog({
      title: 'שלב 2 מתוך 3: פרופיל נעילה', icon: 'lock',
      body: '<p class="muted">סט הגדרות מוכן. אפשר לשנות כל הגדרה אחר כך.</p><p data-prof-desc>' + ui.esc(PROFILES.student.desc) + '</p>',
      onOpen: profDescWire,
      fields: [{ name: 'prof', type: 'select', label: 'פרופיל', value: 'student', options: Object.keys(PROFILES).map(function (k) { return { value: k, label: PROFILES[k].label }; }) }],
      actions: [{ label: 'דילוג', value: false }, { label: 'החלה', value: true, kind: 'primary' }],
      onSubmit: async function (x) {
        var pr = PROFILES[x.prof]; if (!pr) return false;
        await bcall('/kiosk/settings', Object.assign({ password: pw }, pr.p));
        return pr.label;
      }
    });
    if (prof) ui.toast('הוחל פרופיל: ' + prof, 'success');
    var go = await ui.dialog({
      title: 'שלב 3 מתוך 3: תוכנות לימוד', icon: 'computer',
      body: '<p>בטאב „תוכנות לימוד” אפשר לבחור אילו תוכנות (אוצר החכמה, בר אילן, כתבי הקודש, Word ועוד) יהיו פתוחות בזמן נעילה. תוכנות מוכרות מזוהות אוטומטית.</p><p>בסיום — חוזרים לכאן ולוחצים „הפעלת נעילה”.</p>',
      actions: [{ label: 'אחר כך', value: false }, { label: 'לתוכנות לימוד', value: true, kind: 'primary' }]
    });
    if (go) setTimeout(function () { SS.show('programs'); }, 50);
  }

  /* תיאור הפרופיל מתחת לבחירה (ולא בתוך הרשימה הנפתחת, שגולשת מהחלון) */
  function profDescWire(form) {
    var sel = form.querySelector('select[name="prof"]'), d = form.parentNode.querySelector('[data-prof-desc]') || form.querySelector('[data-prof-desc]');
    if (!sel || !d) return;
    sel.addEventListener('change', function () { d.textContent = (PROFILES[sel.value] || {}).desc || ''; });
  }

  async function applyProfile(body) {
    var ok = await ui.dialog({
      title: 'פרופיל נעילה מוכן', icon: 'lock',
      body: '<p class="muted" data-prof-desc>' + ui.esc(PROFILES.student.desc) + '</p>',
      onOpen: profDescWire,
      fields: [
        { name: 'prof', type: 'select', label: 'פרופיל', value: 'student', options: Object.keys(PROFILES).map(function (k) { return { value: k, label: PROFILES[k].label }; }) },
        { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'החלה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        var pr = PROFILES[x.prof]; if (!pr) return false;
        await bcall('/kiosk/settings', Object.assign({ password: x.pw }, pr.p));
        return pr.label;
      }
    });
    if (ok) { ui.toast('הוחל פרופיל: ' + ok, 'success'); load(body); }
  }
  /* #69: קודי גיבוי חד-פעמיים לשחזור (כמו בגוגל). מוצגים פעם אחת; הגשר שומר רק גיבובים */
  async function makeBackupCodes(body) {
    var r = await ui.dialog({
      title: 'קודי גיבוי לשחזור', icon: 'key',
      body: '<p>ייווצרו 10 קודים חד-פעמיים. כל קוד מבטל את הנעילה או מאפשר לבחור סיסמה חדשה — פעם אחת, ואז נמחק.</p>' +
        '<p class="muted">יצירה מחדש מבטלת את הקודים הקודמים. קוד השחזור הראשי ממשיך לעבוד כרגיל.</p>',
      fields: [
        { name: 'pw', label: 'סיסמת נעילה או קוד שחזור', type: 'password', dir: 'ltr' },
        { name: 'mode', type: 'select', label: 'מה לעשות עם הקודים', value: 'none',
          options: [{ value: 'none', label: 'הצגה במסך בלבד (מומלץ: לכתוב/להדפיס ולשמור הרחק מהמחשב)' }, { value: 'print', label: 'הדפסה (קובץ זמני שנמחק)' }] }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'יצירה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) { return await bcall('/kiosk/recovery/backup', { password: x.pw, mode: x.mode }); }
    });
    if (!r || !r.codes) return;
    var list = r.codes.map(function (c, i) { return '<li><code dir="ltr">' + ui.esc(c) + '</code></li>'; }).join('');
    await ui.dialog({
      title: 'קודי הגיבוי שלכם', icon: 'key', tone: 'recovery', dismissible: false,
      body: '<p>הקודים מוצגים <b>רק עכשיו</b>. ' + (r.path ? 'נשמרו גם בקובץ: <bdi dir="ltr">' + ui.esc(r.path) + '</bdi>' : '') + '</p>' +
        '<ol class="backup-codes" dir="ltr">' + list + '</ol>' +
        '<p>שמרו את הדף במקום בטוח, <b>לא ליד המחשב</b>. כל קוד עובד פעם אחת: בחלון „ביטול נעילה”, ב-Ctrl+Alt+Shift+F12, או בשדה „סיסמה נוכחית” בשינוי סיסמה.</p>',
      actions: [{ label: 'סיום', value: true, kind: 'primary' }]
    });
    load(body);
  }

  async function showAdminLog() {
    var r = await ui.dialog({
      title: 'יומן פעולות מנהל', icon: 'info',
      fields: [{ name: 'pw', label: 'סיסמת נעילה או קוד שחזור', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'הצגה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) { return await bcall('/admin-log', { password: x.pw }); }
    });
    if (!r) return;
    var rows = (r.entries || []).map(function (e) { return '<tr><td dir="ltr">' + ui.esc(e.time) + '</td><td>' + ui.esc(e.action) + '</td></tr>'; }).join('');
    await ui.dialog({ title: 'יומן פעולות מנהל', icon: 'info',
      body: rows ? '<div style="max-height:50vh;overflow:auto"><table class="tbl"><tbody>' + rows + '</tbody></table></div>' : '<p class="muted">היומן ריק.</p>',
      actions: [{ label: 'סגירה', value: true, kind: 'primary' }] });
  }
  async function exportSettings() {
    var r = await ui.dialog({
      title: 'ייצוא הגדרות', icon: 'info', body: '<p>נשמר קובץ בשולחן העבודה. סיסמאות, קודים ונתיבים לא נכללים.</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'ייצוא', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) { return await bcall('/settings/export', { password: x.pw }); }
    });
    if (r) ui.toast(r.file ? 'נשמר: ' + ui.isolateLtr(r.file) : 'הייצוא הצליח (' + r.count + ' הגדרות)', 'success');
  }
  async function importSettings(body) {
    var r = await ui.dialog({
      title: 'יבוא הגדרות', icon: 'info',
      body: '<p>בחרו קובץ הגדרות שיוצא ממחשב אחר. סיסמאות וקודים של המחשב הזה לא משתנים.</p><input type="file" accept=".json,application/json" data-slot="file">',
      fields: [{ name: 'pw', label: 'סיסמת נעילה של המחשב הזה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'יבוא', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
      onSubmit: async function (x) {
        var inp = document.querySelector('[data-slot=file]');
        var f = inp && inp.files && inp.files[0];
        if (!f) throw new Error('לא נבחר קובץ');
        var text = await f.text();
        return await bcall('/settings/import', { password: x.pw, text: text });
      }
    });
    if (r) { ui.toast('יובאו ' + r.applied + ' הגדרות', 'success'); load(body); }
  }

  /* ---------- הודעות שגיאה ידידותיות: משפט פשוט + "מה לעשות", בלי מונחים טכניים ---------- */
  /* הלוגיקה עברה ל-ui.errText (ui.js) כדי שכל הטאבים יציגו הודעות ידידותיות */
  function friendlyErr(e) { return ui.errText(e); }
  function softErr(e) {
    if (!e || e.code === 'wrong_password' || e.code === 'locked' || e.code === 'too_many') return e;
    var n = new Error(friendlyErr(e)); n.code = e.code || 'internal'; return n;
  }
  function bcall() { return SS.bridge.call.apply(SS.bridge, arguments).catch(function (e) { throw softErr(e); }); }
  function errPanel(e, onRetry) {
    return ui.errorBox({ code: 'unsupported', message: friendlyErr(e) }, onRetry);
  }

  /* ---------- לוח "בריאות ההקשחה" ----------
     רמות: ok (תקין) / warn (שימו לב) / bad (דורש טיפול) / na (לא זמין). הרמה מוצגת גם באייקון וגם במילה, לא רק בצבע.
     שדה שהגשר לא חושף (undefined) = "לא זמין" בעדינות, לא שגיאה. מקורות: ר' docs/ADMIN_UI.md */
  var LV = { ok: ['check', 'תקין'], warn: ['warning', 'שימו לב'], bad: ['warning', 'דורש טיפול'], na: ['info', 'לא זמין'] };
  function hrow(level, title, detail) { return { l: level, t: title, d: detail }; }
  function healthRows(st, bios, backup) {
    var h = st.hard || {}, tu = st.tempUnlock || {}, rows = [];
    // 1. שירות ההקשחה
    if (h.installed === undefined) rows.push(hrow('na', 'רכיב ההקשחה (שירות)', 'המידע אינו זמין בגרסת הגשר הזו.'));
    else if (!h.installed) rows.push(hrow('warn', 'רכיב ההקשחה (שירות)', 'לא מותקן. הנעילה פועלת ברמה „רכה” בלבד. אפשר להתקין למטה בחלק „חיבורים, הקשחה ושחזור”.'));
    else if (!h.running) rows.push(hrow('bad', 'רכיב ההקשחה (שירות)', 'מותקן אך לא רץ, ולכן הנעילה אינה מוקשחת. נסו „עדכון” או הפעלה מחדש של המחשב.'));
    else if (h.needsUpdate) rows.push(hrow('warn', 'רכיב ההקשחה (שירות)', 'רץ, אך בגרסה ישנה מהגשר. מומלץ לעדכן.'));
    else rows.push(hrow('ok', 'רכיב ההקשחה (שירות)', 'מותקן ורץ.'));
    // 2. מצב הנעילה
    var sch = st.schedule && st.schedule.enabled;
    if (st.active) rows.push(hrow('ok', 'נעילה', 'פעילה' + (sch ? ' (בתזמון ' + st.schedule.start + '–' + st.schedule.end + ')' : '') + '.'));
    else if (tu.active) rows.push(hrow('warn', 'נעילה', 'פתוחה זמנית וחוזרת אוטומטית.'));
    else if (sch) rows.push(hrow('ok', 'נעילה', 'כבויה כרגע, תיכנס אוטומטית ב-' + st.schedule.start + '.'));
    else rows.push(hrow('warn', 'נעילה', 'כבויה. המחשב פועל כרגיל עד שתפעילו נעילה.'));
    // 3. בעיות אכיפה
    var iss = h.enforcementIssues || st.enforcementIssues;
    if (!Array.isArray(iss)) rows.push(hrow('na', 'אכיפת הנעילה', 'הגשר אינו מדווח כרגע על בעיות אכיפה.'));
    else if (iss.length) rows.push(hrow('bad', 'אכיפת הנעילה', 'חסימות שלא הוחלו כראוי (' + iss.length + '). הפעלה מחדש של המחשב או „עדכון” של רכיב ההקשחה בדרך כלל פותרים.'));
    else rows.push(hrow('ok', 'אכיפת הנעילה', 'כל החסימות הוחלו ואומתו.'));
    // 4. קובץ הגדרות וחתימה
    var corrupt = h.stateCorrupt !== undefined ? h.stateCorrupt : st.stateCorrupt;
    var seal = h.configSealOk !== undefined ? h.configSealOk : st.configSealOk;
    if (corrupt === true) rows.push(hrow('bad', 'קובץ ההגדרות', 'נראה פגום, והנעילה עלולה לחזור להגדרות מחמירות. עיינו ב„מרכז החירום”.'));
    else if (seal === false) rows.push(hrow('bad', 'קובץ ההגדרות', 'חתימת ההגדרות אינה תקינה (ייתכן ששונה ידנית). שמירה מחדש של הגדרות הנעילה תתקן.'));
    else if (corrupt === false && seal === true) rows.push(hrow('ok', 'קובץ ההגדרות', 'תקין וחתום.'));
    else if (corrupt === false) rows.push(hrow('ok', 'קובץ ההגדרות', 'תקין. מצב החתימה אינו זמין.'));
    else rows.push(hrow('na', 'קובץ ההגדרות', 'מצב הקובץ והחתימה אינו נחשף כרגע.'));
    // 5. קוד שחזור וקודי גיבוי
    var hasRec = h.hasRecovery !== undefined ? h.hasRecovery : (st.recoverySaved === undefined ? undefined : !!st.recoverySaved);
    if (!st.hasPassword) rows.push(hrow('warn', 'קוד שחזור', 'עוד לא הוגדרה סיסמה, ולכן אין קוד שחזור.'));
    else if (hasRec === false) rows.push(hrow('bad', 'קוד שחזור', 'לא נשמר. בלעדיו אי אפשר לבטל נעילה בחירום. צרו קוד חדש דרך „שינוי סיסמה”.'));
    else if (hasRec === true) rows.push(hrow('ok', 'קוד שחזור', 'מוגדר ונשמר.'));
    else rows.push(hrow('na', 'קוד שחזור', 'אינו זמין.'));
    var left = backup ? backup.left : h.backupLeft;
    if (left === undefined || left === null) rows.push(hrow('na', 'קודי גיבוי', 'מספר הקודים שנותרו אינו זמין.'));
    else if (left <= 0) rows.push(hrow('warn', 'קודי גיבוי', 'לא נותרו קודים חד-פעמיים. מומלץ ליצור סט חדש.'));
    else if (left <= 2) rows.push(hrow('warn', 'קודי גיבוי', 'נותרו מעט קודים חד-פעמיים (' + left + '). כדאי ליצור סט חדש.'));
    else rows.push(hrow('ok', 'קודי גיבוי', 'נותרו קודים חד-פעמיים (' + left + ').'));
    // 6. הקשחת אתחול (Safe Mode / WinRE)
    var boot = bios && bios.boot, en = boot && boot.enforcement;
    if (!boot || boot.error) rows.push(hrow('na', 'הקשחת אתחול (Safe Mode / WinRE)', 'המידע אינו זמין כרגע.'));
    else if (en && en.incomplete) rows.push(hrow('bad', 'הקשחת אתחול (Safe Mode / WinRE)', 'לא הושלמה. ייתכן שעדיין אפשר להיכנס ל-Safe Mode או לתפריט ההתאוששות.'));
    else if (en && en.active) rows.push(hrow('ok', 'הקשחת אתחול (Safe Mode / WinRE)', 'פעילה: כניסה ל-Safe Mode ולתפריט ההתאוששות חסומה.'));
    else if (en) rows.push(hrow('warn', 'הקשחת אתחול (Safe Mode / WinRE)', 'כבויה. אפשר לעקוף נעילה דרך Safe Mode. היא נדלקת עם התקנת רכיב ההקשחה.'));
    else rows.push(hrow('na', 'הקשחת אתחול (Safe Mode / WinRE)', 'לא דווח מצב אכיפה.'));
    // 7. עדכון (health.ok / rollback)
    var up = h.updateHealth || st.updateHealth;
    if (up === undefined) rows.push(hrow('na', 'תקינות עדכון אחרון', 'הגשר אינו חושף כרגע את תוצאת בדיקת התקינות אחרי עדכון.'));
    else if (up && (up.rollback || up.state === 'rollback')) rows.push(hrow('bad', 'תקינות עדכון אחרון', 'העדכון האחרון נכשל והוחזרה הגרסה הקודמת.'));
    else if (up && (up.ok || up.state === 'healthy')) rows.push(hrow('ok', 'תקינות עדכון אחרון', 'הגרסה החדשה אומתה כתקינה.'));
    else rows.push(hrow('warn', 'תקינות עדכון אחרון', 'ממתין לאישור תקינות אחרי עדכון.'));
    // 8. גרסה
    var ver = h.version || (SS.bridge.health && SS.bridge.health.version);
    rows.push(ver ? hrow('ok', 'גרסת הגשר', 'גרסה ' + ver) : hrow('na', 'גרסת הגשר', 'אינה זמינה.'));
    return rows;
  }
  function healthPanel(st, bios, backup) {
    var rows = healthRows(st, bios, backup), worst = 'ok';
    rows.forEach(function (r) { if (r.l === 'bad') worst = 'bad'; else if (r.l === 'warn' && worst !== 'bad') worst = 'warn'; });
    var bad = rows.filter(function (r) { return r.l === 'bad'; }).length, warn = rows.filter(function (r) { return r.l === 'warn'; }).length;
    var head = worst === 'bad' ? 'יש דברים שדורשים טיפול (' + bad + ')' : worst === 'warn' ? 'הכול עובד, ויש דברים שכדאי לשפר (' + warn + ')' : 'הכול תקין';
    var h = '<section class="card health-panel" aria-labelledby="health-title"><div class="health-head ' + worst + '">' +
      '<span class="health-big">' + ui.icon(LV[worst][0]) + '</span><div><h3 id="health-title">בריאות ההקשחה</h3><p><b>' + head + '</b></p></div></div>' +
      '<ul class="health-list">';
    rows.forEach(function (r) {
      h += '<li class="health-row ' + r.l + '"><span class="health-ic">' + ui.icon(LV[r.l][0]) + '</span>' +
        '<span class="health-text"><b>' + ui.esc(r.t) + '</b><span class="muted">' + ui.esc(r.d) + '</span></span>' +
        '<span class="health-badge">' + LV[r.l][1] + '</span></li>';
    });
    return h + '</ul></section>';
  }

  /* ---------- מרכז חירום: הסבר וצעדים בלבד. אין כאן קודים או סיסמאות; הכפתורים בטוחים (העתקה/רענון/פעולה שדורשת סיסמה) ---------- */
  var UNLOCK_CMD = 'OtzariaBridge.exe --unlock <קוד-שחזור>';
  function emergencyCenter(st, backup) {
    return '<section class="card emergency" aria-labelledby="em-title"><div class="card-title" id="em-title">' + ui.icon('key') + 'מרכז חירום: שכחתם סיסמה או נתקעתם?</div>' +
      '<p class="muted">אל תיבהלו. יש כמה דרכים לשחרר את הנעילה, מהקלה לקשה. תצטרכו את <b>קוד השחזור</b> שנשמר (בקובץ או בהדפסה) בעת הפעלת הנעילה.</p>' +
      '<ol class="steps em-steps">' +
      '<li><b>קוד שחזור בחלון הביטול.</b> ב„ביטול נעילה” אפשר להקליד את קוד השחזור (או קוד גיבוי חד-פעמי) במקום הסיסמה.</li>' +
      '<li><b>קיצור חירום במקלדת:</b> <b dir="ltr">Ctrl + Alt + Shift + F12</b> פותח בכל רגע, גם בזמן נעילה, חלון להזנת הקוד. במחשבים ניידים לרוב צריך גם <b dir="ltr">Fn</b>, או לכבות „Fn Lock”.</li>' +
      '<li><b>בלי מקלדת תקינה:</b> 7 לחיצות מהירות על השעון בפינת המסך פותחות את אותו חלון.</li>' +
      '<li><b>שורת פקודה:</b> הריצו <code class="cmd" dir="ltr">' + ui.esc(UNLOCK_CMD) + '</code> והחליפו את הסוגריים בקוד שלכם.</li>' +
      '<li><b>מוצא אחרון:</b> אתחול ב-Safe Mode, שבו הגשר אינו עולה. אם הוגדרה סיסמת BIOS והקשחת אתחול, דרך זו חסומה בכוונה.</li></ol>' +
      '<p class="muted small">הקוד אינו מוצג כאן מטעמי אבטחה. אם איבדתם גם אותו וגם את קודי הגיבוי, אי אפשר לבטל. זה מכוון, כדי שלא יהיה מנגנון עקיפה.</p>' +
      '<div class="row-actions full">' +
      '<button class="btn btn-tonal" data-act="em-copy">' + ui.icon('copy') + 'העתקת פקודת השחזור</button>' +
      '<button class="btn btn-tonal" data-act="em-refresh">' + ui.icon('refresh') + 'רענון מצב ההקשחה</button>' +
      (st.hasPassword ? '<button class="btn btn-tonal" data-act="em-backup">' + ui.icon('key') + (backup && backup.left ? 'יצירת קודי גיבוי חדשים' : 'יצירת קודי גיבוי') + '</button>' : '') +
      '</div></section>';
  }
  function fxSummary(fx) {
    if (!fx || !fx.effective) return 'המצב אינו זמין כרגע. אפשר לפתוח את הטאב ולבדוק שם.';
    var m = { open: 'פתוח — הסייר ללא הגבלה', restricted: 'מוגבל לתיקיות שנבחרו', locked: 'נעול עד הזנת סיסמת מנהל' };
    return (m[fx.effective] || 'מצב לא ידוע') + '. לשינוי פתחו את הטאב.';
  }

  /* ---------- נעילה מוקשחת (3.0.0, בעיה #1) ---------- */
  function hardCard(h) {
    var state = !h.installed ? 'לא מותקן' : !h.running ? 'מותקן אבל לא רץ' :
      (h.active ? 'פעיל — הנעילה מוקשחת' : 'מותקן, ממתין להפעלת הנעילה') + (h.version ? ' · גרסה ' + ui.esc(h.version) : '');
    // כפתור "עדכון" בשורת המצב הרגילה רק כשהבעיה היא שהשירות לא רץ; פער גרסה מוצג בכרטיס אזהרה בולט למטה
    var actions = !h.installed
      ? '<button class="btn btn-primary" data-act="hard-install">' + ui.icon('shield') + 'התקנה</button>'
      : (!h.needsUpdate && !h.running ? '<button class="btn btn-tonal" data-act="hard-install">' + ui.icon('shield') + 'עדכון</button>' : '') +
        '<button class="btn btn-tonal" data-act="hard-settings">' + ui.icon('edit') + 'הגדרות</button>' +
        '<button class="btn btn-tonal" data-act="hard-remove">' + ui.icon('close') + 'הסרה</button>';
    var updateWarning = (h.installed && h.needsUpdate)
      ? '<div class="state-box error">' + ui.icon('warning') + '<div><b>רכיב ההקשחה בגרסה ישנה מהגשר</b>' +
        '<span class="muted">גרסה ' + ui.esc(h.version || '') + ' · מומלץ לעדכן כדי לשמור על תאימות מלאה</span>' +
        '<button class="btn btn-tonal" data-act="hard-install">' + ui.icon('shield') + 'עדכון עכשיו</button></div></div>'
      : '';
    return '<div class="card"><div class="card-title">נעילה מוקשחת (רכיב שירות)</div>' +
      '<p class="muted">רכיב אופציונלי שרץ ברקע בהרשאות מערכת. הוא שומר את הסיסמה ואת מצב הנעילה במקום שהתלמיד אינו יכול לשנות, ' +
      'מפעיל את הגשר מחדש אם נסגר (גם בחשבון Windows אחר), מחזיר את חסימת מנהל המשימות ועורך הרישום אם נמחקה, ויכול לחסום התקני אחסון USB. ' +
      'ההתקנה דורשת אישור מנהל פעם אחת.</p>' +
      updateWarning +
      '<div class="setting-row"><div class="setting-text"><b>מצב</b><span class="muted">' + state +
      (h.blockUsbStorage ? ' · אחסון USB חסום' : '') + '</span></div><div class="btn-group">' + actions + '</div></div>' +
      '<p class="muted small">הרכיב אינו משנה את הכניסה ל-Windows ואינו רץ במצב בטוח, כך שאינו יכול להשבית את המחשב. ' +
      'גם הוא אינו מחליף סיסמת BIOS. פרטים: docs/HARDLOCK.md במאגר.</p></div>';
  }

  function pairCard(pairs, known) {
    var rows = pairs.map(function (c) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(c.name || c.client) + '</b><span class="muted">חובר: ' +
        ui.esc((c.created || '').replace('T', ' ')) + ' · שימוש אחרון: ' + ui.esc((c.lastUsed || '').replace('T', ' ')) + '</span></div>' +
        '<button class="btn btn-tonal" data-act="unpair-' + ui.esc(c.id) + '">' + ui.icon('close') + 'ניתוק</button></div>';
    }).join('');
    /* סטטוס פשוט למעלה (כמה תוספים מחוברים) - הרשימה המלאה עם זמנים/ניתוק רק למי שבאמת צריך,
       מאחורי "פרטים נוספים". אם יש כפילויות (למשל אחרי שינוי מזהה תוסף - הישן נשאר מחובר כ"יתום"
       עד שמנתקים אותו ידנית), זה בדיוק המקום לנקות אותן */
    /* אם הקריאה לגשר לא הסתיימה בזמן — לא מכריזים "אין תוספים" (זו לא אמת), אלא מציינים שהמצב לא נטען */
    var status = known === false
      ? '<span class="chip warn">' + ui.icon('warning') + 'מצב החיבורים לא נטען — נסו לרענן</span>'
      : pairs.length
      ? '<span class="chip ok">' + ui.icon('check') + pairs.length + ' תוספים מחוברים</span>'
      : '<span class="chip warn">' + ui.icon('warning') + 'אין תוספים מחוברים</span>';
    return '<div class="card"><div class="card-title">תוספים מחוברים לגשר</div>' +
      '<p class="muted">רק תוסף שאושר בחלון של הגשר יכול להשתמש בו. תוכנה אחרת במחשב לא יכולה לגשת לקבצים ולהגדרות בלי אישור כזה.</p>' +
      status +
      '<details class="more-details"><summary>פרטים נוספים (' + pairs.length + ')</summary><div class="more-details-body">' +
      (rows || '<p class="muted">אין תוספים מחוברים.</p>') +
      '</div></details></div>';
  }

  /* ---------- הקשחת BIOS/UEFI מתוך תוכנה (ניסיון אוטומטי, ללא כניסה ידנית לתפריט ה-BIOS) ---------- */
  var BIOS_VENDOR_NAMES = { lenovo: 'Lenovo', hp: 'HP', dell: 'Dell', asus: 'Asus', other: 'לא זוהה' };
  /* ר' bridge/BiosHardening.cs DisableWords/EnableWords - אותה רשימת מילים, כדי שהמשמעות (נעול/פתוח)
     תתאים בדיוק לפעולה שהשרת באמת מבצע */
  var BIOS_DISABLE_WORDS = ['disable', 'off', 'no', 'block'];
  var BIOS_ENABLE_WORDS = ['enable', 'on', 'yes', 'require', 'prompt'];

  /* הערך הגולמי מ-WMI לפעמים מגיע כ"Enable,*Disable" (כל הערכים האפשריים, עם * לפני הנוכחי) -
     ולפעמים כערך יחיד נקי כמו "Disable". שתי הצורות נתמכות */
  function biosCurrentWord(raw) {
    raw = String(raw || '');
    var marked = raw.split(',').filter(function (p) { return p.charAt(0) === '*'; })[0];
    return (marked ? marked.slice(1) : raw).trim().toLowerCase();
  }
  function biosStatusChip(setting, wantWords, lockedLabel, openLabel) {
    if (!setting) return '<span class="chip">לא נמצאה הגדרה מתאימה</span>';
    var cur = biosCurrentWord(setting.currentValue);
    var locked = wantWords.some(function (w) { return cur.indexOf(w) >= 0; });
    return '<span class="chip ' + (locked ? 'ok' : 'warn') + '">' + ui.icon(locked ? 'lock' : 'unlock') + (locked ? lockedLabel : openLabel) + '</span>';
  }
  function biosDetailsHtml(setting) {
    if (!setting) return '';
    return '<details class="more-details"><summary>פרטים נוספים</summary><div class="more-details-body">' +
      '<span class="muted small">שם ההגדרה במחשב: „' + ui.esc(setting.name) + '”</span>' +
      '<span class="muted small">ערך נוכחי בפועל: <bdi dir="ltr">' + ui.esc(setting.currentValue || '—') + '</bdi></span>' +
      '</div></details>';
  }

  function biosCard(bios) {
    if (!bios) return '';
    var head = '<div class="card"><div class="card-title">הקשחת BIOS/UEFI (ניסיון אוטומטי)</div>' +
      '<p class="muted">מנסה להגדיר סיסמת BIOS ולחסום אתחול ממדיה חיצונית (USB) ישירות מכאן, דרך ממשק הניהול של יצרן המחשב — ' +
      'בלי להיכנס ידנית להגדרות ה-BIOS. <b>לא כל פעולה מובטחת להצליח</b> — תלוי ביצרן ובדגם המחשב.</p>' +
      '<div class="setting-row"><div class="setting-text"><b>יצרן המחשב שזוהה</b><span class="muted">' +
      ui.esc(BIOS_VENDOR_NAMES[bios.vendor] || bios.vendor) + (bios.model ? ' · ' + ui.esc(bios.model) : '') + '</span></div></div>';
    var manualBtn = '<div class="row-actions full"><button class="btn btn-tonal" data-act="bios-manual">' + ui.icon('info') + 'הדרכה ידנית (כל היצרנים)</button></div>';

    if (!bios.supported) {
      return head + '<div class="state-box"><div><b>לא נתמך להקשחה אוטומטית במחשב זה</b><span class="muted">' +
        ui.esc(bios.reason || '') + '</span></div></div>' + manualBtn + '</div>';
    }

    var usb = bios.usbBootSetting;
    head += '<div class="setting-row"><div class="setting-text"><b>חסימת אתחול ממדיה חיצונית (USB)</b>' +
      biosStatusChip(usb, BIOS_DISABLE_WORDS, 'נעול — אי אפשר לאתחל מ-USB', 'פתוח — עדיין אפשר לאתחל מ-USB') +
      biosDetailsHtml(usb) + '</div></div>';
    var bootMenu = bios.bootMenuAuthSetting;
    head += '<div class="setting-row"><div class="setting-text"><b>נעילת תפריט האתחול (F9/F12) בסיסמה</b>' +
      biosStatusChip(bootMenu, BIOS_ENABLE_WORDS, 'נעול — תפריט האתחול דורש סיסמה', 'פתוח — אפשר להיכנס לתפריט האתחול בלי סיסמה') +
      biosDetailsHtml(bootMenu) + '</div></div>';
    if (bios.wmiReachable === false) {
      head += '<div class="state-box error"><div><b>לא ניתן לקרוא את הגדרות ה-BIOS כרגע</b><span class="muted">' +
        ui.esc(bios.wmiError || '') + '</span></div></div>';
    }
    if (bios.note) head += '<p class="muted small">' + ui.esc(bios.note) + '</p>';
    head += '<div class="row-actions full">' +
      '<button class="btn btn-primary" data-act="bios-harden">' + ui.icon('shield') + 'הפעלת הקשחה</button>' +
      '<button class="btn btn-tonal" data-act="bios-manual">' + ui.icon('info') + 'הדרכה ידנית (כל היצרנים)</button></div></div>';
    return head;
  }

  /* ---------- הדרכה ידנית לפי יצרן: מפתח כניסה ל-BIOS, איפה הסיסמה, איפה חסימת USB/תפריט אתחול ---------- */
  var BIOS_MANUAL_GUIDE = [
    { vendor: 'hp', title: 'HP', key: 'Esc ואז F10 באתחול (או F10 ישירות)',
      steps: [
        'כבו את המחשב לגמרי (לא רק הפעלה מחדש), ואז הפעילו ולחצו מיד ברצף על Esc, ואז F10, עד שנפתח מסך ה-BIOS הכחול/האפור.',
        'היכנסו ללשונית Security ← Setup Password/Administrator Password ← הגדירו סיסמה (רשמו אותה!).',
        'חזרו ללשונית Security ← חפשו "Prompt for Admin authentication on F9/F12" ← Enable (זה חוסם כניסה לתפריט האתחול בלי הסיסמה).',
        'לחסימת USB: לשונית Advanced/System Configuration ← Boot Options ← חפשו "USB Storage Boot"/"Legacy Support" ← Disable.',
        'שמרו ויציאה: F10 ← Yes (או Save Changes and Exit בתפריט Main).',
      ] },
    { vendor: 'lenovo', title: 'Lenovo', key: 'Enter ואז F1 באתחול (ThinkPad), או F2 (IdeaPad/Yoga)',
      steps: [
        'כבו את המחשב לגמרי, הפעילו ולחצו מיד על Enter ואז F1 (ThinkPad) או החזיקו F2 (IdeaPad) עד שנפתח ה-BIOS.',
        'לשונית Security ← Password ← Supervisor Password ← הגדירו סיסמה (רשמו אותה!).',
        'לשונית Security ← Secure Boot / Virtualization — לרוב אין הגדרת "סיסמה לתפריט אתחול" נפרדת ב-Lenovo; הגנת ה-Supervisor Password עצמה חוסמת גם שינוי סדר האתחול.',
        'לחסימת USB: לשונית Startup/Boot ← Boot ← הסירו/הורידו "USB HDD"/"USB FDD" מרשימת סדר האתחול, או לשונית Security ← "USB Boot" ← Disable אם קיים.',
        'שמרו ויציאה: F10 ← Yes.',
      ] },
    { vendor: 'dell', title: 'Dell', key: 'F2 באתחול (או F12 לתפריט אתחול חד-פעמי)',
      steps: [
        'כבו את המחשב לגמרי, הפעילו ולחצו מיד ברצף על F2 עד שנפתח BIOS Setup הכחול.',
        'לשונית System Security ← Admin Password ← הגדירו סיסמה (רשמו אותה!).',
        'לשונית System Security ← "Secure Boot"/"Password Bypass"/"Admin Setup Lockout" — אפשרו נעילה שדורשת סיסמה לפני כניסה לתפריט אתחול (F12) אם האפשרות קיימת בדגם.',
        'לחסימת USB: לשונית System Configuration ← USB Configuration ← בטלו "Enable Boot Support"/"Enable External USB Port", או לשונית Boot Sequence ← הסירו USB מרשימת סדר האתחול.',
        'שמרו ויציאה: Apply ← Exit (או F10).',
      ] },
    { vendor: 'asus', title: 'Asus', key: 'F2 (נייד, בלחיצה מוחזקת) או Del (שולחני)',
      steps: [
        'כבו את המחשב לגמרי. בנייד: החזיקו F2 ואז לחצו הפעלה. בשולחני: הקישו Del בחוזרה מיד עם ההפעלה.',
        'אם נכנסתם למסך הפשוט (EZ Mode) — לחצו F7 או "Advanced Mode" כדי לראות את כל האפשרויות.',
        'לשונית Security ← Administrator Password ← הגדירו סיסמה (רשמו אותה!).',
        'לחסימת USB ותפריט אתחול: לשונית Boot ← Boot Configuration/CSM ← בטלו Launch CSM / הסירו התקני USB מ-Boot Option Priorities. חלק מהדגמים: Secure Boot ← Enable כדי לחסום אתחול ממדיה לא חתומה.',
        'שמרו ויציאה: F10 ← Yes (או Exit ← Save Changes & Reset).',
      ] },
    { vendor: 'other', title: 'יצרנים אחרים (Acer, MSI, Toshiba, Samsung ועוד)',
      steps: [
        'מקש הכניסה הנפוץ: F2 או Del בחוזרה מיד עם ההפעלה (לפעמים Esc או F1 — מופיע לרוב במסך הפתיחה הראשון של היצרן, שימו לב ל"Press ... to enter Setup").',
        'חפשו לשונית בשם Security/Password ← הגדירו Supervisor/Administrator/Setup Password (רשמו אותה!).',
        'חפשו לשונית Boot ← הסירו/בטלו USB/"External Device" מרשימת סדר האתחול, או הפעילו הגנת-סיסמה לתפריט האתחול אם קיימת.',
        'שמרו ויציאה: בדרך כלל F10, או Exit ← Save Changes.',
      ] },
  ];

  function biosManualGuideHtml(detectedVendor) {
    var ordered = BIOS_MANUAL_GUIDE.slice().sort(function (a, b) {
      return (a.vendor === detectedVendor ? -1 : 0) - (b.vendor === detectedVendor ? -1 : 0);
    });
    var html = '<p class="muted"><b>לפני שמתחילים:</b> קחו דף ועט, ורשמו עליו כל סיסמה שתגדירו ברגע שהגדרתם אותה — ' +
      'סיסמת BIOS שנשכחת <b>אינה ניתנת לשחזור מתוך Windows</b>, ולעיתים דורשת פנייה לתמיכת היצרן או איפוס פיזי בתוך המחשב. ' +
      'שמרו את הדף במקום בטוח, לא ליד המחשב.</p>';
    ordered.forEach(function (v) {
      var isDetected = v.vendor === detectedVendor;
      html += '<div class="state-box"><div>' +
        '<b>' + ui.esc(v.title) + (isDetected ? ' — זוהה כיצרן המחשב שלכם' : '') + '</b>' +
        (v.key ? '<span class="muted">מקש כניסה ל-BIOS: <b dir="ltr">' + ui.esc(v.key) + '</b></span>' : '') +
        '<ol class="bullets">' + v.steps.map(function (s) { return '<li>' + ui.esc(s) + '</li>'; }).join('') + '</ol>' +
        '</div></div>';
    });
    return html;
  }

  function biosManualGuide(bios) {
    return ui.dialog({
      title: 'הדרכה ידנית להקשחת BIOS/UEFI', icon: 'info',
      body: biosManualGuideHtml(bios && bios.vendor),
      actions: [{ label: 'סגירה', value: true, kind: 'primary' }]
    });
  }

  function wireBios(body, bios) {
    var b = body.querySelector('[data-act="bios-harden"]');
    if (b) b.addEventListener('click', function () { biosHarden(body, bios); });
    var m = body.querySelector('[data-act="bios-manual"]');
    if (m) m.addEventListener('click', function () { biosManualGuide(bios); });
  }

  async function biosHarden(body, bios) {
    var usb = bios && bios.usbBootSetting;
    var bootMenu = bios && bios.bootMenuAuthSetting;
    var pwNote = bios && bios.passwordRequiresPhysicalPresence
      ? ' <b>שימו לב:</b> ביצרן/דגם הזה הגדרת סיסמת BIOS חסומה במכוון מרחוק (דורשת נוכחות פיזית ליד המחשב) — הניסיון כאן צפוי להיכשל; יש להגדיר ידנית ב-F10.'
      : '';
    var v = await ui.dialog({
      title: 'הקשחת BIOS/UEFI', icon: 'shield',
      body: '<p class="muted"><b>אזהרה לפני שממשיכים:</b> סיסמת BIOS שנשכחה <b>אינה ניתנת לשחזור מתוך Windows</b> — ' +
        'יש לפנות לתמיכת היצרן, ולעיתים נדרש איפוס פיזי של ה-BIOS (סוללת CMOS) בתוך המחשב. רשמו ושמרו את הסיסמה במקום בטוח לפני שממשיכים.' + pwNote + '</p>',
      fields: [
        { name: 'setPw', type: 'checkbox', label: 'הגדרת סיסמת BIOS', value: true,
          hint: 'אם כבר מוגדרת סיסמה אחרת במחשב (שלא ידועה כאן), הפעולה פשוט תיכשל — אי אפשר לדרוס בטעות סיסמה קיימת.' },
        { name: 'biosPw', label: 'סיסמת BIOS חדשה', type: 'password', dir: 'ltr' },
        { name: 'biosPw2', label: 'אימות סיסמת BIOS', type: 'password', dir: 'ltr' },
        { name: 'blockUsb', type: 'checkbox', label: 'חסימת אתחול ממדיה חיצונית (USB)', value: !!usb, disabled: !usb,
          hint: usb ? 'ייעשה דרך ההגדרה „' + usb.name + '”.' : 'לא נמצאה הגדרה מתאימה במחשב זה — אי אפשר להפעיל אוטומטית.' },
        { name: 'lockBootMenu', type: 'checkbox', label: 'נעילת תפריט האתחול (F9/F12) בסיסמה', value: !!bootMenu, disabled: !bootMenu,
          hint: bootMenu ? 'ייעשה דרך ההגדרה „' + bootMenu.name + '”. חזק יותר מחסימת USB בלבד — חוסם כניסה לתפריט האתחול כולו.'
                         : 'לא נמצאה הגדרה מתאימה במחשב זה — אי אפשר להפעיל אוטומטית.' },
        { name: 'pw', label: 'סיסמת נעילה (של תוכנת החסימה)', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'הפעלה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.setPw && !x.blockUsb && !x.lockBootMenu) return 'יש לבחור לפחות פעולה אחת';
        if (x.setPw) {
          if (!x.biosPw || x.biosPw.length < 4) return 'סיסמת ה-BIOS חייבת להכיל לפחות 4 תווים';
          if (x.biosPw !== x.biosPw2) return 'סיסמאות ה-BIOS אינן תואמות';
        }
        return x.pw ? '' : 'יש להקליד את סיסמת הנעילה';
      },
      onSubmit: async function (x) {
        return await bcall('/bios/harden', {
          password: x.pw, setPassword: !!x.setPw, biosPassword: x.biosPw || '', blockUsbBoot: !!x.blockUsb, lockBootMenu: !!x.lockBootMenu
        }, { timeoutMs: 30000 });
      }
    });
    if (!v) return;
    await showBiosResult(v);
    load(body);
  }

  function showBiosResult(r) {
    var rows = (r.steps || []).map(function (s) {
      var label = s.id === 'password' ? 'סיסמת BIOS' : s.id === 'bootMenuAuth' ? 'נעילת תפריט האתחול' : 'חסימת אתחול USB';
      return '<li' + (s.success ? '' : ' class="bios-fail"') + '>' + ui.icon(s.success ? 'check' : 'warning') + '<span><b>' + label + (s.success ? ' — הצליח.' : ' — לא הצליח.') +
        '</b> ' + ui.esc(s.message || '') + '</span></li>';
    }).join('');
    return ui.dialog({
      title: 'תוצאת ההקשחה', icon: 'shield',
      body: '<ul class="bullets">' + (rows || '<li>לא בוצעה אף פעולה.</li>') + '</ul>',
      actions: [{ label: 'סגירה', value: true, kind: 'primary' }]
    });
  }

  function wireHard(body, st, pairs) {
    function on(act, fn) { var b = body.querySelector('[data-act="' + act + '"]'); if (b) b.addEventListener('click', fn); }
    var h = st.hard || {};
    on('hard-install', async function () {
      var ok = await ui.dialog({
        title: h.installed ? 'עדכון רכיב ההקשחה' : 'התקנת נעילה מוקשחת', icon: 'shield',
        body: '<p class="muted">אחרי ההקלדה Windows תבקש אישור מנהל. אחרי האישור הרכיב יותקן ויפעל ברקע. הסיסמה וקוד השחזור הנוכחיים יישארו בתוקף.</p>',
        fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
        actions: [{ label: 'ביטול', value: false }, { label: h.installed ? 'עדכון' : 'התקנה', value: true, kind: 'primary' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
        onSubmit: async function (x) { await bcall('/kiosk/hard/install', { password: x.pw }, { timeoutMs: 200000 }); return true; }
      });
      if (ok) { ui.toast('רכיב ההקשחה מותקן', 'success'); load(body); }
    });
    on('hard-settings', async function () {
      var ok = await ui.dialog({
        title: 'הגדרות נעילה מוקשחת', icon: 'shield',
        fields: [
          { name: 'usb', type: 'checkbox', label: 'חסימת התקני אחסון USB בזמן נעילה', value: !!h.blockUsbStorage, hint: 'דיסק-און-קי וכוננים חיצוניים לא ייטענו. עכבר, מקלדת ומדפסת ממשיכים לעבוד. התקן שכבר מחובר נשאר עד שמוציאים אותו' },
          { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }
        ],
        actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
        onSubmit: async function (x) { await bcall('/kiosk/hard/settings', { password: x.pw, blockUsbStorage: !!x.usb }); return true; }
      });
      if (ok) { ui.toast('ההגדרות נשמרו', 'success'); load(body); }
    });
    on('hard-remove', async function () {
      var ok = await ui.dialog({
        title: 'הסרת רכיב ההקשחה', icon: 'close',
        body: '<p class="muted">הנעילה הרגילה תמשיך לעבוד. החסימות שהרכיב הוסיף יבוטלו.</p>',
        fields: [{ name: 'pw', label: 'סיסמת נעילה או קוד שחזור', type: 'password', dir: 'ltr' }],
        actions: [{ label: 'ביטול', value: false }, { label: 'הסרה', value: true, kind: 'danger' }],
        validate: function (x) { return x.pw ? '' : 'יש להקליד סיסמה'; },
        onSubmit: async function (x) { await bcall('/kiosk/hard/uninstall', { password: x.pw }); return true; }
      });
      if (ok) { ui.toast('רכיב ההקשחה הוסר', 'success'); load(body); }
    });
    (pairs || []).forEach(function (c) {
      on('unpair-' + c.id, async function () {
        var x = { pw: '' };
        if (st.active) {
          var ok = await ui.dialog({
            title: 'ניתוק תוסף', icon: 'close', fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
            actions: [{ label: 'ביטול', value: false }, { label: 'ניתוק', value: true, kind: 'danger' }],
            onSubmit: async function (v) { x = v; return true; }
          });
          if (!ok) return;
        } else if (!(await ui.confirm('ניתוק תוסף', 'בפתיחה הבאה התוסף יבקש אישור מחדש.', 'ניתוק'))) return;
        try { await bcall('/pair/revoke', { id: c.id, password: x.pw }); load(body); }
        catch (e) { ui.toast(friendlyErr(e), 'error'); }
      });
    });
  }

  var SECTION_NAMES = {
    audio: 'שמע', display: 'מסך', energy: 'חשמל וסוללה', keyboard: 'מקלדת', mouse: 'עכבר ומשטח מגע',
    printer: 'מדפסות', time: 'שעה ותאריך', wifi: 'רשת אלחוטית', bluetooth: 'בלוטות׳', airplane: 'מצב טיסה', usb: 'התקני USB', power: 'הפעלה מחדש וכיבוי'
  };

  /* זמנים ידועים שאפשר להציג בשומר המסך (ת"ז מתאימות ל-bridge/Zmanim.cs KnownZmanim).
     ברירת המחדל (DEFAULT_ZMAN_SHOW) תואמת ל-DefaultShow שם */
  var ZMAN_FIELDS = [
    { id: 'alot', label: 'עלות השחר' }, { id: 'netz', label: 'הנץ החמה' },
    { id: 'shemaMga', label: 'סוף ק"ש (מג"א)' }, { id: 'shemaGra', label: 'סוף ק"ש (גר"א)' },
    { id: 'tefilaMga', label: 'סוף תפילה (מג"א)' }, { id: 'tefilaGra', label: 'סוף תפילה (גר"א)' },
    { id: 'chatzot', label: 'חצות' }, { id: 'minchaGedola', label: 'מנחה גדולה' },
    { id: 'minchaKetana', label: 'מנחה קטנה' }, { id: 'plag', label: 'פלג המנחה' },
    { id: 'shkia', label: 'שקיעה' }, { id: 'tzeit', label: 'צאת הכוכבים' }
  ];
  var DEFAULT_ZMAN_SHOW = ['alot', 'netz', 'shemaGra', 'chatzot', 'shkia', 'tzeit'];
  var YAHRZEIT_MONTHS = ['תשרי', 'חשון', 'כסלו', 'טבת', 'שבט', 'אדר', 'אדר א', 'אדר ב', 'ניסן', 'אייר', 'סיון', 'תמוז', 'אב', 'אלול'];
  /* ט"ו/ט"ז ולא י"ה/י"ו — נמנעים מצירוף שנראה כשם ה' */
  var YAHRZEIT_DAYS = ['א', 'ב', 'ג', 'ד', 'ה', 'ו', 'ז', 'ח', 'ט', 'י', 'יא', 'יב', 'יג', 'יד', 'טו', 'טז', 'יז', 'יח', 'יט', 'כ', 'כא', 'כב', 'כג', 'כד', 'כה', 'כו', 'כז', 'כח', 'כט', 'ל'];


  /* שדות שומר המסך — משותפים לטאב "שומר מסך" (sections/screensaver.js) */
  SS.screensaverFields = async function (st) {
    var zmanShow = (st.zmanimShow || DEFAULT_ZMAN_SHOW).map(function (x) { return String(x).toLowerCase(); }); // A4: הגשר שומר באותיות קטנות
    /* רשימת ערים מלוח השנה של אוצריא עצמה (calendar.getCities) — אם זמינה. קואורדינטות
       נשארות תמיד כאפשרות מתקדמת ("מותאם אישית") גם כשיש רשימת ערים */
    var cities = SS._citiesCache || [];
    if (!SS._citiesCache && window.Otzaria) {
      try {
        var r = await window.Otzaria.call('calendar.getCities', {});
        var list = (r && r.data !== undefined ? r.data : r) || [];
        if (Array.isArray(list)) { cities = list; SS._citiesCache = list; }
      } catch (e) { cities = []; }
    }
    var cityOptions = [{ value: '', label: 'מותאם אישית (קואורדינטות)' }].concat(
      cities.map(function (c) { return { value: c.name, label: c.name + (c.country ? ' (' + c.country + ')' : '') }; })
    );
    var cityByName = {};
    cities.forEach(function (c) { cityByName[c.name] = c; });
    var fields = [
      { name: 'rest', type: 'checkbox', label: 'מסך מנוחה כשאוצריא ממוזערת', value: st.restScreen !== false, hint: 'סמל אוצריא, שעה ותאריך במסך מלא. לחיצה בכל מקום מחזירה לאוצריא' },
      { name: 'saverOn', type: 'checkbox', label: 'שומר מסך', value: st.saverEnabled !== false, hint: 'מתג ראשי. כשהוא כבוי שומר המסך לא מופיע בכלל: לא בנעילה ולא בלעדיה. מסך המנוחה כשאוצריא ממוזערת נשלט בנפרד' },
      { name: 'idle', label: 'שומר מסך אחרי דקות ללא פעילות (ברירת מחדל: 1)', value: String(st.restIdleMinutes || 0), dir: 'ltr', placeholder: '0', hint: 'מסך המנוחה מופיע גם כשאוצריא פתוחה, אחרי הזמן הזה בלי מקלדת ועכבר. עד 240 דקות' },
      { name: 'saverUnl', type: 'checkbox', label: 'שומר מסך גם כשהנעילה כבויה', value: st.saverWhenUnlocked !== false, hint: 'אחרי זמן ההמתנה שלמעלה, גם בלי מצב נעילה. לחיצה או מקש חוזרים לחלון הקודם' },
      { name: 'bootCover', type: 'checkbox', label: 'מסך מנוחה מיד בהדלקת המחשב (בנעילה), עד שאוצריא עולה', value: st.bootCover !== false, hint: 'נסגר לבד כשאוצריא מוכנה, או אחרי דקה. לחיצה סוגרת אותו מיד' }
    ];
    if (cityOptions.length > 1) {
      fields.push({ name: 'zcity', type: 'select', label: 'עיר לזמני היום בשומר המסך', value: st.zmanimCity || '', options: cityOptions,
        hint: 'הזמנים עצמם (הנץ, שקיעה, זמני ק"ש ועוד) מחושבים על ידי לוח השנה של אוצריא עצמה' });
    }
    fields.push({ name: 'zloc', label: cityOptions.length > 1 ? 'קואורדינטות (כש"מותאם אישית" נבחר למעלה)' : 'מיקום לזמני היום בשומר המסך (קו רוחב, קו אורך)',
      value: st.zmanimLocation || '', dir: 'ltr', placeholder: '31.78, 35.22',
      hint: 'ריק וגם בלי עיר שנבחרה = בלי זמני היום בשומר המסך' });
    /* גיבוי מקומי (כשאוצריא סגורה/ישנה בלבד): שני מנועי חישוב לבחירה, ראו קרדיט בחלון "אודות" */
    fields.push({ name: 'zengine', type: 'select', label: 'מנוע חישוב זמני גיבוי (כשאוצריא סגורה או ישנה)',
      value: st.zmanimFallbackEngine || 'noaa',
      options: [
        { value: 'noaa', label: 'ישן (קירוב אסטרונומי פשוט)' },
        { value: 'kosherjava', label: 'חדש (ספריית KosherJava — זמנים הלכתיים מלאים, כולל תיקון גובה)' }
      ],
      hint: 'ישן: קירוב NOAA פשוט, הנץ/שקיעה בגובה פני הים בלבד, זמנים מעטים. חדש: ספריית KosherJava, זמנים הלכתיים מלאים עם תיקון גובה, אך קוד צד-שלישי שעלול להיבדל מעט בזמנים גבוליים' });
    /* #33: התראה לפני זמן נבחר */
    /* כמה התראות במקביל (משוב 15.10): רשימת זמנים מופרדת בפסיקים ב-kiosk.zmanimAlert */
    var zAlerts = String(st.zmanimAlert || '').toLowerCase().split(',');
    fields.push({ name: 'zalertmin', label: 'התראה: כמה דקות לפני הזמן (מסמנים "התראה" ליד כל זמן ברשימה למטה)', value: String(st.zmanimAlertMinutes || 10), dir: 'ltr' });
    fields.push({ name: 'zelev', label: 'גובה מעל פני הים במיקום (מטרים)', value: String(st.zmanimElevation || 0), dir: 'ltr', placeholder: '0',
      hint: 'משפיע רק כשנבחר מנוע הגיבוי "חדש" (KosherJava) למעלה. 0 עד 5000 מטר' });
    fields.push({ name: 'diaspora', type: 'checkbox', label: 'חוץ לארץ (יום טוב שני)', value: !!st.zmanimDiaspora,
      hint: 'משפיע על זיהוי כניסת/יציאת חג של יום טוב שני של גלויות' });
    fields.push({ name: 'candle', label: 'הדלקת נרות: דקות לפני השקיעה', value: String(st.candleLightingMinutes != null ? st.candleLightingMinutes : 18), dir: 'ltr', placeholder: '18' });
    fields.push({ name: 'zshowHead', type: 'note', label: 'אילו זמנים להציג בשומר המסך, ועל אילו לקבל התראה:' });
    ZMAN_FIELDS.forEach(function (z) {
      fields.push({ name: 'zm_' + z.id, type: 'checkpair', label: z.label, value: zmanShow.indexOf(z.id.toLowerCase()) >= 0,
        name2: 'za_' + z.id, label2: 'התראה', value2: zAlerts.indexOf(z.id.toLowerCase()) >= 0 });
    });
    /* משוב 15.10: כל פריט בשומר המסך — מתג נפרד (במקום תפריט "תוכן"), כולל ההקדשה */
    var rcOld = st.restContent || 'full';
    function rcDef(k, d) { return st[k] != null ? !!st[k] : d; }
    fields.push({ name: 'restParts', type: 'pills', label: 'מה להציג בשומר המסך', items: [
      { name: 'rsVerse', label: 'פסוק', value: rcDef('restShowVerse', rcOld === 'full' || rcOld === 'verse') },
      { name: 'rsClock', label: 'שעה ותאריך', value: rcDef('restShowClock', true) },
      { name: 'rsZmanim', label: 'זמני היום', value: rcDef('restShowZmanim', rcOld === 'full' || rcOld === 'zmanim') },
      { name: 'rsLogo', label: 'סמל', value: rcDef('restShowLogo', true) },
      { name: 'rsDed', label: 'הקדשה', value: rcDef('restShowDedication', true) },
      { name: 'parsha', label: 'פרשת השבוע', value: !!st.showParsha },
      { name: 'dafyomi', label: 'דף יומי', value: !!st.showDafYomi }
    ] });
    fields.push({ name: 'verseText', label: 'פסוק מותאם אישית בשומר המסך', value: st.restVerseText || '', placeholder: "שִׁבְתִּי בְּבֵית ה' כָּל יְמֵי חַיַּי",
      hint: "הפסוק שיוצג כשהאפשרות \"פסוק\" מסומנת. ריק = ברירת המחדל: שִׁבְתִּי בְּבֵית ה' כָּל יְמֵי חַיַּי" });
    fields.push({ name: 'noteText', type: 'textarea', rows: 3, label: 'כיתוב אישי בשומר המסך', value: st.restNoteText || '',
      placeholder: 'למשל: סדר הלימוד היום, הודעה למשפחה, חיזוק…', hint: 'מוצג בריבוע ממוסגר בצד שמול זמני היום. ריק = לא מוצג' });
    fields.push({ name: 'lockImg', type: 'checkbox', label: 'להציג גם במסך הנעילה של Windows', value: !!st.lockScreenImage,
      hint: 'תמונת מסך הנעילה של המחשב תהיה כמו שומר המסך (פסוק, סמל, זמני היום, הקדשה), ומתעדכנת כל חצי שעה. "זרקור Windows" (תמונות מתחלפות של מיקרוסופט) מכובה אוטומטית' });
    fields.push({ name: 'dedText', type: 'textarea', rows: 3, label: 'נוסח ההקדשה בשומר המסך', value: st.restDedicationText || '',
      placeholder: 'ריק = ההקדשה הקבועה. שורה ראשונה קטנה, שורה שנייה מודגשת (שם), השאר קטן',
      hint: 'אפשר לכתוב הקדשה אחרת או לעילוי נשמת יקיריכם. עד 4 שורות' });
    /* d992415 (יאיר) — "גם וגם": סגנון ההקדשה ונר מרצד לבחירת המשתמש. ברירת מחדל לפי 15.10: קבועה, בלי תזוזה */
    fields.push({ name: 'dedStyle', type: 'select', label: 'סגנון ההקדשה', value: st.restDedicationStyle || 'fixed',
      options: [{ value: 'fixed', label: 'קבועה בתחתית הצד שמול הזמנים' }, { value: 'column', label: 'עמודה ממוסגרת עם נר' }],
      hint: 'שני הסגנונות מוצגים בצד שמול זמני היום, בגבולות קבועים' });
    fields.push({ name: 'candleFlicker', type: 'checkbox', label: 'נר מרצד (בסגנון עמודה)', value: !!st.restCandleFlicker,
      hint: 'כבוי (ברירת מחדל): הנר דולק בלי תזוזה' });
    fields.push({ name: 'zmSide', type: 'select', label: 'עמודת זמני היום בשומר המסך', value: st.restZmanimSide || 'right', hint: 'הזמנים מוצגים בעמודה בצד, וההקדשה בתחתית בלי חפיפה', options: [{ value: 'right', label: 'ימין' }, { value: 'left', label: 'שמאל' }] });
    fields.push({ more: true, name: 'yzMonth', type: 'select', label: 'יום השנה (הנצחה): חודש עברי', value: st.yahrzeitMonth || '',
      options: [{ value: '', label: '(כבוי)' }].concat(YAHRZEIT_MONTHS.map(function (m) { return { value: m, label: m }; })),
      hint: 'מציג שורה מיוחדת בשומר המסך ביום הזה בכל שנה. ריק = כבוי. לא קשור בהכרח להקדשה הקבועה שבתוסף' });
    fields.push({ more: true, name: 'yzDay', type: 'select', label: 'יום השנה: יום בחודש', value: String(st.yahrzeitDay || 0),
      options: [{ value: '0', label: '(כבוי)' }].concat(YAHRZEIT_DAYS.map(function (d, i) {
        /* גרש (׳) אחרי אות בודדת, גרשיים (״) לפני האות האחרונה כשיש יותר מאחת */
        var label = d.length === 1 ? d + '׳' : d.slice(0, -1) + '״' + d.slice(-1);
        if (window.I18N && I18N.isEn && I18N.isEn()) label = String(i + 1); /* #69: באנגלית — מספר */
        return { value: String(i + 1), label: label };
      })) });
    fields.push({ more: true, name: 'yzLabel', label: 'יום השנה: טקסט (למשל "לעילוי נשמת ...")', value: st.yahrzeitLabel || '', placeholder: '' });
    return { fields: fields, cityByName: cityByName };
  };
  SS.screensaverPayload = function (x) {
    return {
      restScreen: !!x.rest, saverEnabled: !!x.saverOn, saverWhenUnlocked: !!x.saverUnl, bootCover: !!x.bootCover, restIdleMinutes: +String(x.idle).trim() || 0, restZmanimSide: x.zmSide, showParsha: !!x.parsha, showDafYomi: !!x.dafyomi, restShowVerse: !!x.rsVerse, restVerseText: (x.verseText || '').trim(), restShowClock: !!x.rsClock, restShowZmanim: !!x.rsZmanim, restShowLogo: !!x.rsLogo, restShowDedication: !!x.rsDed, restDedicationStyle: x.dedStyle === 'column' ? 'column' : 'fixed', restCandleFlicker: !!x.candleFlicker, restDedicationText: String(x.dedText || '').slice(0, 400), lockScreenImage: !!x.lockImg, restNoteText: String(x.noteText || '').slice(0, 600),
      zmanimLocation: x.zloc.trim(), zmanimCity: (x.zcity || '').trim(),
      zmanimElevation: +String(x.zelev).trim() || 0, zmanimAlert: ZMAN_FIELDS.filter(function (z) { return x['za_' + z.id]; }).map(function (z) { return z.id; }).join(','), zmanimAlertMinutes: +String(x.zalertmin).trim() || 10, zmanimFallbackEngine: x.zengine,
      zmanimDiaspora: !!x.diaspora, candleLightingMinutes: +String(x.candle).trim() || 0,
      zmanimShow: ZMAN_FIELDS.filter(function (z) { return x['zm_' + z.id]; }).map(function (z) { return z.id; }),
      yahrzeitMonth: (x.yzMonth || '').trim(), yahrzeitDay: +String(x.yzDay).trim() || 0, yahrzeitLabel: (x.yzLabel || '').trim()
    };
  };

  async function editSettings(body, st) {
    var all = st.allSections || Object.keys(SECTION_NAMES);
    if (SS.isOfflineMode && SS.isOfflineMode()) all = all.filter(function (id) { return ['wifi', 'bluetooth', 'airplane'].indexOf(id) < 0; }); /* מצב מנותק: בלי רשת */
    var open = st.openSections || [];


    var fields = [
      { name: 'ext', label: 'סיומות קבצים שמותר לפתוח בתוכנות אחרות', value: (st.allowOpenExtensions || []).join(', '), dir: 'ltr', placeholder: 'pdf, txt' },
      { name: 'path', label: 'נתיב אוצריא (ריק = זיהוי אוטומטי)', value: st.otzariaPath || '', dir: 'ltr', placeholder: 'C:\\Program Files\\Otzaria\\otzaria.exe' },
      { name: 'clock', type: 'checkbox', label: 'שעון ותאריך עברי צפים', value: st.clock !== false, hint: 'שורת המשימות מוסתרת בנעילה, ולכן מוצג שעון קטן בפינת המסך. לחיצה עליו מחזירה לאוצריא' },
      { name: 'venue', label: 'שם המחשב/בית הכנסת (אופציונלי)', value: st.venueName || '', placeholder: '', hint: 'מוצג במרכז המסך מתחת לשעון, במסך המנוחה. ריק = מוצגת רק השעה' },
    ];
    fields.push({ name: 'side', type: 'checkbox', label: 'סרגל תוכנות בצד המסך', value: st.sideBar !== false, hint: 'פס צר בשולי המסך שמתרחב כשמתקרבים אליו: אוצריא והתוכנות התורניות המותרות, עם סימון מה פתוח. לחיצה מעבירה לחזית, או פותחת אם סגורה' });
    fields.push({ name: 'barProgs', type: 'checkbox', label: 'תוכנות מותרות בשורת המשימות של הנעילה', value: st.barPrograms != null ? !!st.barPrograms : st.sideBar === false, hint: 'כשנבחרה "שורת משימות" בשדה "שעון / שורת משימות": סמלי התוכנות מוצגים בשורה, ליד השם "אחת שאלתי" (במקום או בנוסף לסרגל הצד)' });
    fields.push({ name: 'sideIcons', type: 'checkbox', label: 'סרגל התוכנות: אייקונים בלבד', value: !!st.sideBarIconsOnly, hint: 'סרגל צר יותר, בלי שמות. כבוי (ברירת מחדל): אייקון ולידו שם התוכנה' });
    fields.push({ name: 'sideKb', type: 'checkbox', label: 'סרגל התוכנות: מקלדת וירטואלית', value: !!st.sideBarKeyboard, hint: 'למסך מגע בלי מקלדת: מוסיף לסרגל כפתור שפותח את המקלדת שעל המסך של Windows, והיא מותרת בזמן הנעילה' });
    fields.push({ name: 'sidePos', type: 'select', label: 'מיקום סרגל התוכנות', value: st.sideBarPosition || 'right', hint: 'לאיזה צד מסך הסרגל מוצמד', options: [{ value: 'right', label: 'ימין' }, { value: 'left', label: 'שמאל' }, { value: 'top', label: 'למעלה' }, { value: 'bottom', label: 'למטה' }] });
    fields.push({ name: 'reset', type: 'checkbox', label: 'ביטול נעילה בכיבוי המחשב', value: !!st.resetOnShutdown, hint: 'כבוי (ברירת מחדל): הנעילה נשמרת וחוזרת אחרי הפעלה מחדש. מסומן: בכיבוי או התנתקות הנעילה מתבטלת והמחשב עולה רגיל. לא פועל עם נעילה מוקשחת' });
    fields.push({ name: 'fs', type: 'checkbox', label: 'אוצריא במסך מלא', value: st.fullscreenOtzaria !== false, hint: 'בלי כותרת ומסגרת חלון' });
    fields.push({ name: 'ro', type: 'checkbox', label: 'מצב קריאה בלבד לקבצים', value: !!st.readOnly, hint: 'חוסם שינוי, מחיקה והעברה של קבצים בסייר בזמן נעילה' });
    fields.push({ name: 'tfa', type: 'checkbox', label: 'אימות דו-שלבי (Windows Hello)', value: !!st.twoFactor,
      hint: (st.twoFactorAvailable ? 'ביטול הנעילה ושינוי ההגדרות ידרשו גם זיהוי פנים/טביעת אצבע/PIN, בנוסף לסיסמה. קוד השחזור ימשיך לעבוד לחירום.' : '⚠ Windows Hello אינו מוגדר במחשב הזה — הגדירו אותו ב-Windows כדי להפעיל') });
    /* פתיחה זמנית: אפשר להפעיל רק כשהאימות הדו-שלבי פעיל (או מסומן עכשיו בטופס הזה) */
    var tu = st.tempUnlock || {};
    var tfaNow = !!(tu.twoFactorOn != null ? tu.twoFactorOn : st.twoFactor);
    var TU_HINT_ON = 'סיסמה נפרדת, שאפשר למסור למי שצריך את המחשב לזמן קצר, בלי לגלות את סיסמת הנעילה. כל הנעילה (גם סייר הקבצים) נפתחת לזמן שנקבע כאן, ואז חוזרת מעצמה. לא מתקבלת כסיסמת נעילה.';
    var TU_HINT_OFF = 'זמין רק כשהאימות הדו-שלבי (Windows Hello) מופעל — סמנו קודם את האימות הדו-שלבי. אם הוא יכובה, הפתיחה הזמנית תיעצר עד שיופעל שוב (ההגדרות נשמרות).';
    var sc = st.schedule || {};
    fields.push({ name: 'sched', type: 'checkbox', label: 'תזמון נעילה לפי שעות', value: !!sc.enabled, hint: 'נעילה אוטומטית בכל יום בין שעה לשעה (למשל 20:00 עד 02:00, כולל אחרי חצות). מחוץ לטווח המחשב פועל רגיל. ביטול ידני בתוך הטווח מדלג על יתרת הטווח (החלון הבא יחול כרגיל)' });
    /* #56/#57: זמנים מוכנים (סדרים) — בחירה ממלאת שעת התחלה וסיום. סדרים שנשמרו כאן נזכרים (ss.sedarim) */
    function parseExtraSched(t) {
      var out = [], ok = true;
      String(t || '').split(/[,;\n]/).map(function (s) { return s.trim(); }).filter(Boolean).forEach(function (s) {
        var m = /^(\d{1,2}:\d{2})\s*[-–]\s*(\d{1,2}:\d{2})$/.exec(s);
        if (m && m[1] !== m[2]) out.push({ start: m[1], end: m[2] }); else ok = false;
      });
      return ok ? out : null;
    }
    var SEDARIM = [
      { label: "סדר א'", start: '09:00', end: '13:30' }, { label: "סדר ב'", start: '15:30', end: '19:00' },
      { label: "סדר ג'", start: '20:00', end: '22:00' }, { label: 'לילה', start: '22:00', end: '06:00' }
    ];
    try { (JSON.parse(localStorage.getItem('ss.sedarim') || '[]') || []).forEach(function (s) { if (s && s.start && s.end) SEDARIM.push(s); }); } catch (e) { /* כלום */ }
    fields.push({ name: 'schedPreset', type: 'select', label: 'זמנים מוכנים', value: '',
      options: [{ value: '', label: '— בחירה —' }].concat(SEDARIM.map(function (s, i) { return { value: String(i), label: s.label + ' (' + s.start + '–' + s.end + ')' }; })),
      hint: 'ממלא את שעות ההתחלה והסיום. אפשר לשנות אחר כך' });
    fields.push({ name: 'schedStart', label: 'שעת התחלה', type: 'time', value: sc.start || '20:00', dir: 'ltr' });
    fields.push({ name: 'schedEnd', label: 'שעת סיום', type: 'time', value: sc.end || '02:00', dir: 'ltr' });
    /* C4: כמה זמני נעילה — חלונות נוספים מעבר לראשי */
    fields.push({ name: 'schedExtra', label: 'זמני נעילה נוספים (הוסף זמן)', dir: 'ltr', placeholder: '08:00-13:00, 15:00-19:00',
      value: (sc.extra || []).map(function (w) { return w.start + '-' + w.end; }).join(', '),
      hint: 'כל זמן בתבנית התחלה-סיום, מופרדים בפסיק. חלים בנוסף לזמן הראשי (בנעילה הרכה; רכיב ההקשחה אוכף רק את הראשי)' });
    /* סבב G (6): ימי שבוע לזמני הנעילה (ריק = כל הימים) */
    var DAYN = ['א', 'ב', 'ג', 'ד', 'ה', 'ו', 'ש'];
    /* #69: באנגלית מציגים ומקבלים קיצורים אנגליים; העברית תמיד מתקבלת */
    var DAYN_EN = ['su', 'mo', 'tu', 'we', 'th', 'fr', 'sa'];
    var dayEn = !!(window.I18N && I18N.isEn && I18N.isEn());
    var dayLbl = function (d) { return dayEn ? DAYN_EN[d].charAt(0).toUpperCase() + DAYN_EN[d].charAt(1) : DAYN[d]; };
    fields.push({ name: 'schedDays', label: 'ימים בשבוע לנעילה המתוזמנת', placeholder: dayEn ? 'Su Mo Tu We Th Fr Sa' : 'א ב ג ד ה ו ש',
      value: (sc.days && sc.days.length ? sc.days : [0, 1, 2, 3, 4, 5, 6]).map(dayLbl).join(' '),
      hint: 'אותיות הימים מופרדות ברווח, למשל „א ב ג ד ה” — בלי שבת ויום שישי. חל על כל זמני הנעילה. חלון שנמשך אחרי חצות שייך ליום שבו התחיל' });
    /* #60: קורא סדרים מתוכן קובץ ייצוא (JSON גמיש או שורות טקסט) ומוסיף ל-ss.sedarim. מחזיר כמה נוספו */
    function importSedarim(text) {
      var found = [];
      var reTime = /([01]?\d|2[0-3]):([0-5]\d)/g;
      function pushItem(label, start, end) {
        if (!/^\d{1,2}:\d{2}$/.test(start || '') || !/^\d{1,2}:\d{2}$/.test(end || '') || start === end) return;
        found.push({ label: String(label || 'סדר').slice(0, 30), start: start, end: end });
      }
      var data = null; try { data = JSON.parse(text); } catch (e) { data = null; }
      function walk(o) {
        if (!o || typeof o !== 'object') return;
        if (Array.isArray(o)) { o.forEach(walk); return; }
        var s = o.start || o.from || o.begin, e = o.end || o.to || o.finish;
        var lbl = o.label || o.name || o.title || o.seder;
        if (s && e) pushItem(lbl, String(s).trim(), String(e).trim());
        Object.keys(o).forEach(function (k) { if (o[k] && typeof o[k] === 'object') walk(o[k]); });
      }
      if (data) walk(data);
      else String(text).split(/\r?\n/).forEach(function (line) { // חלופת טקסט: "שם 09:00-13:30"
        var t = line.match(reTime); if (t && t.length >= 2) pushItem(line.replace(reTime, '').replace(/[-–,:]/g, ' ').trim(), t[0], t[1]);
      });
      if (!found.length) return 0;
      var mine = []; try { mine = JSON.parse(localStorage.getItem('ss.sedarim') || '[]') || []; } catch (e) { mine = []; }
      var seen = {}; mine.forEach(function (s) { seen[s.start + '-' + s.end] = 1; });
      var added = 0;
      found.forEach(function (s) { var k = s.start + '-' + s.end; if (!seen[k]) { seen[k] = 1; mine.push(s); added++; } });
      try { localStorage.setItem('ss.sedarim', JSON.stringify(mine.slice(-20))); } catch (e) { /* כלום */ }
      return added;
    }
    function parseDays(t) {
      var out = []; var ok = true;
      String(t || '').replace(/[׳'",]/g, ' ').split(/\s+/).filter(Boolean).forEach(function (w) {
        var i = DAYN.indexOf(w); if (i < 0) i = DAYN_EN.indexOf(w.slice(0, 2).toLowerCase());
        if (i < 0) ok = false; else if (out.indexOf(i) < 0) out.push(i);
      });
      return ok && out.length ? out.sort() : null;
    }
    fields.push({ name: 'schedWarn', type: 'checkbox', label: 'התראה לפני נעילה מתוזמנת', value: sc.warn !== false, hint: 'אם יושבים מול המחשב — תופיע התראה 5 דקות ודקה לפני הנעילה, עם אפשרות לבטל את הנעילה הקרובה בסיסמה' });
    /* #63: הצגת זמני הנעילה בלוח השנה של אוצריא (מתג; הפרסום עצמו נעשה בתוך אוצריא דרך publishedData) */
    fields.push({ name: 'schedCalendar', type: 'checkbox', label: 'הצג את זמני הנעילה בלוח השנה של אוצריא', value: !!sc.showInCalendar, hint: 'כל זמני הנעילה המתוזמנים יופיעו כאירועים בלוח השנה של אוצריא לשבוע הקרוב, ויתעדכנו אוטומטית. כיבוי המתג מסיר אותם' });
    fields.push({
      name: 'pos', type: 'select', label: 'שעון / שורת משימות', value: st.clockPosition || 'bottom-left',
      options: [
        { value: 'bottom-left', label: 'למטה-שמאל' },
        { value: 'bottom-right', label: 'למטה-ימין' },
        { value: 'top-left', label: 'למעלה-שמאל' },
        { value: 'top-right', label: 'למעלה-ימין' },
        { value: 'bar-bottom', label: 'שורת משימות למטה (אחת שאלתי, תוכנות, שעון ותאריך)' },
        { value: 'bar-top', label: 'שורת משימות למעלה' }
      ]
    });
    /* משוב 3.9: שדות הפתיחה הזמנית נשמטו ב-c0c7cd7 (הקבוצה נשארה ריקה והשמירה נכשלה על "משך בדקות") — שוחזרו מ-3.8.0 */
    fields.push({ name: 'tu', type: 'checkbox', label: 'פתיחה זמנית בסיסמה נפרדת', value: !!tu.enabled,
      disabled: !tfaNow && !tu.enabled, hint: '<span data-tu-hint>' + (tfaNow ? TU_HINT_ON : TU_HINT_OFF) + '</span>' });
    fields.push({ name: 'tuMin', label: 'פתיחה זמנית: משך בדקות (1–60)', value: String(tu.minutes || 30), dir: 'ltr', placeholder: '30' });
    fields.push({ name: 'tuPw', label: tu.configured ? 'סיסמת פתיחה זמנית חדשה (ריק = ללא שינוי)' : 'סיסמת פתיחה זמנית', type: 'password', dir: 'ltr' });
    fields.push({ name: 'tuPw2', label: 'אימות סיסמת הפתיחה הזמנית', type: 'password', dir: 'ltr' });
    /* משוב 3.9: השורה המקורית (c0c7cd7) הפנתה ל-secIdx/secFields שלא הוגדרו → ReferenceError, ו"הגדרות נעילה" נתקע ב"טוען".
       כאן נבנות תיבות "מה פתוח בזמן נעילה" (הקבוצה למטה מסדרת אותן לפי השם sec_<id>) */
    var openSet = st.openSections || [];
    all.forEach(function (id) {
      fields.push({ name: 'sec_' + id, type: 'checkbox', label: SECTION_NAMES[id] || id, value: openSet.indexOf(id) >= 0 });
    });
    /* 049b564 (יאיר): הטופס מחולק לקבוצות מתקפלות לפי נושא (ר' ui.fieldsHtml) — כל קבוצה סגורה חוץ מהראשונה.
       השדות נבנים כמו קודם (כולל שינויי 15.10) ומסודרים כאן לתוך הקבוצות. סיסמת הנעילה מעל הקבוצות (חובה לשמירה). */
    var GROUPS = [
      { g: { type: 'group', icon: 'display', label: 'תצוגה ושולחן עבודה', hint: 'שעון, שם המחשב, סרגל התוכנות ומסך מלא' }, n: ['clock', 'pos', 'venue', 'fs', 'side', 'barProgs', 'sideIcons', 'sidePos'] },
      { g: { type: 'group', icon: 'edit', label: 'קבצים ותוכן', hint: 'נתיב אוצריא, סיומות מותרות, קריאה-בלבד' }, n: ['path', 'ext', 'ro'] },
      { g: { type: 'group', icon: 'shield', label: 'אבטחה', hint: 'אימות דו-שלבי, ביטול נעילה בכיבוי' }, n: ['tfa', 'reset'] },
      { g: { type: 'group', icon: 'key', label: 'פתיחה זמנית בסיסמה נפרדת', hint: 'לתת למישהו את המחשב לזמן קצר בלי לגלות את הסיסמה הראשית' }, n: ['tu', 'tuMin', 'tuPw', 'tuPw2'] },
      { g: { type: 'group', icon: 'unlock', label: 'מה פתוח בזמן נעילה', hint: 'אילו מסכי הגדרות נגישים לתלמיד בזמן נעילה' }, n: all.map(function (id) { return 'sec_' + id; }) },
      { g: { type: 'group', icon: 'clock', label: 'תזמון נעילה לפי שעות', hint: 'נעילה אוטומטית מתחילה ונגמרת בשעה קבועה כל יום' }, n: ['sched', 'schedPreset', 'schedStart', 'schedEnd', 'schedExtra', 'schedDays', 'schedWarn', 'schedCalendar'] }
    ];
    var byName = {}; fields.forEach(function (f) { byName[f.name] = f; });
    var used = {}, grouped = [];
    GROUPS.forEach(function (G) { grouped.push(G.g); G.n.forEach(function (n) { if (byName[n]) { grouped.push(byName[n]); used[n] = 1; } }); });
    var rest = fields.filter(function (f) { return !used[f.name]; });
    fields = [];
    fields.push({ name: 'pw', label: st.hasPassword ? 'סיסמת נעילה' : 'סיסמת נעילה (עוד לא הוגדרה — אפשר להשאיר ריק)', type: 'password', dir: 'ltr' });
    fields = fields.concat(rest, grouped);
    var slot = body.querySelector('[data-slot=kset]'); if (!slot) return;
    setTimeout(function () {
      var ps = slot.querySelector('[name=schedPreset]'); if (!ps) return;
      ps.addEventListener('change', function () {
        var s = SEDARIM[+ps.value]; if (!s || ps.value === '') return;
        var a = slot.querySelector('[name=schedStart]'), b = slot.querySelector('[name=schedEnd]'), c = slot.querySelector('[name=sched]');
        if (a) a.value = s.start; if (b) b.value = s.end; if (c) c.checked = true;
      });
      /* #60: ייבוא סדרים מקובץ הייצוא של התוסף "שמירת סדרים" (אין API לקריאה ישירה בין תוספים) */
      var btn = document.createElement('button');
      btn.type = 'button'; btn.className = 'btn subtle'; btn.style.marginTop = '4px';
      btn.textContent = 'ייבוא סדרים מקובץ';
      var fi = document.createElement('input'); fi.type = 'file'; fi.accept = '.json,.txt,application/json'; fi.style.display = 'none';
      btn.addEventListener('click', function () { fi.click(); });
      fi.addEventListener('change', function () {
        var f = fi.files && fi.files[0]; if (!f) return;
        var rd = new FileReader();
        rd.onload = function () {
          var added = importSedarim(String(rd.result || ''));
          if (added > 0) { ui.toast('יובאו ' + added + ' סדרים — נשמרו ברשימת הזמנים המוכנים', 'success'); }
          else ui.toast('לא נמצאו סדרים בקובץ (מצופה JSON עם label/start/end או start-end)', 'warn');
          fi.value = '';
        };
        rd.readAsText(f);
      });
      ps.parentNode.appendChild(btn); ps.parentNode.appendChild(fi);
    }, 0);
    var ok = await ui.inlineForm(slot, {
      title: 'הגדרות נעילה', icon: 'edit',
      body: '<p class="muted">מה פתוח לתלמיד בזמן נעילה, ואיך נראה המסך. הגדרות שלא סומנו מוסתרות ונחסמות גם בגשר.</p>',
      fields: fields,
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (st.hasPassword && !x.pw) return 'יש להקליד את סיסמת הנעילה';
        if (['bottom-left', 'bottom-right', 'top-left', 'top-right', 'bar-bottom', 'bar-top'].indexOf(x.pos) < 0) return 'מיקום שעון לא תקין';
        if ((x.venue || '').trim().length > 60) return 'שם המחשב/בית הכנסת: עד 60 תווים';
        if (['right', 'left', 'top', 'bottom'].indexOf(x.sidePos) < 0) return 'מיקום סרגל לא תקין';
        if (x.sched && (!/^\d{1,2}:\d{2}$/.test(x.schedStart || '') || !/^\d{1,2}:\d{2}$/.test(x.schedEnd || ''))) return 'יש להזין שעת התחלה ושעת סיום לתזמון';
        if (x.sched && x.schedStart === x.schedEnd) return 'שעת ההתחלה והסיום של התזמון זהות';
        if (parseDays(x.schedDays) === null) return 'ימים בשבוע: יש להקליד אותיות ימים (א–ש) מופרדות ברווח';
        if (parseExtraSched(x.schedExtra) === null) return 'זמני נעילה נוספים: יש להקליד בתבנית 08:00-13:00, מופרדים בפסיק';
        if (!/^\d{1,2}$/.test(String(x.tuMin).trim()) || +x.tuMin < 1 || +x.tuMin > 60) return 'פתיחה זמנית: משך בדקות בין 1 ל-60';
        if (x.tuPw && x.tuPw.length < 6) return 'סיסמת הפתיחה הזמנית חייבת להכיל לפחות 6 תווים';
        if (x.tuPw !== x.tuPw2) return 'סיסמאות הפתיחה הזמנית אינן תואמות';
        if (x.tuPw && x.tuPw === x.pw) return 'סיסמת הפתיחה הזמנית חייבת להיות שונה מסיסמת הנעילה';
        if (x.tu && !tu.enabled && !tu.configured && !x.tuPw) return 'יש לבחור סיסמת פתיחה זמנית לפני ההפעלה';
        if (x.tu && !tu.enabled && !x.tfa) return 'פתיחה זמנית דורשת אימות דו-שלבי פעיל';
        var bad = parseExt(x.ext).filter(function (e) { return !/^[a-z0-9]{1,10}$/.test(e); });
        return bad.length ? 'סיומת לא תקינה: ' + bad[0] : '';
      },
      onOpen: function (form) {
        /* הפתיחה הזמנית תלויה באימות הדו-שלבי: סימון/ביטול שלו בטופס משנה מיד את זמינות המתג */
        var tfa = form.querySelector('[name=tfa]'), tuCb = form.querySelector('[name=tu]'), hint = form.querySelector('[data-tu-hint]');
        function syncTu() {
          var on = !!(tfa && tfa.checked);
          /* מתג שכבר דלוק נשאר ניתן לכיבוי גם בלי 2FA (הוא פשוט רדום); הפעלה חדשה רק עם 2FA */
          ui.lockField(tuCb, !on && !tu.enabled);
          if (hint) hint.textContent = on ? TU_HINT_ON : TU_HINT_OFF;
        }
        if (tfa && tuCb) { tfa.addEventListener('change', syncTu); syncTu(); }
      },
      onSubmit: async function (x) {
        if (x.sched && !SEDARIM.some(function (s) { return s.start === x.schedStart && s.end === x.schedEnd; })) {
          try { var mine = JSON.parse(localStorage.getItem('ss.sedarim') || '[]') || []; mine.push({ label: 'שמור', start: x.schedStart, end: x.schedEnd }); localStorage.setItem('ss.sedarim', JSON.stringify(mine.slice(-6))); } catch (e) { /* כלום */ }
        }
        await bcall('/kiosk/settings', {
          password: x.pw, allowOpenExtensions: parseExt(x.ext), otzariaPath: x.path.trim(),
          readOnly: !!x.ro, twoFactor: !!x.tfa, clock: !!x.clock, venueName: (x.venue || '').trim(), fullscreenOtzaria: !!x.fs, sideBar: !!x.side, barPrograms: !!x.barProgs, sideBarIconsOnly: !!x.sideIcons, sideBarKeyboard: !!x.sideKb, sideBarPosition: x.sidePos, resetOnShutdown: !!x.reset, clockPosition: x.pos,
          openSections: all.filter(function (id) { return x['sec_' + id]; }),
          schedule: { enabled: !!x.sched, start: x.schedStart, end: x.schedEnd, warn: !!x.schedWarn, extra: parseExtraSched(x.schedExtra) || [], days: parseDays(x.schedDays) || [], showInCalendar: !!x.schedCalendar },
          tempUnlockEnabled: !!x.tu, tempUnlockMinutes: +String(x.tuMin).trim() || 30, tempUnlockPassword: x.tuPw || ''
        });
        return true;
      }
    });
    if (ok) { ui.toast('ההגדרות נשמרו', 'success'); if (SS.pushZmanimNow) SS.pushZmanimNow(); if (SS.pushLockCalendar) SS.pushLockCalendar(); load(body); }
  }

  /* #136.4: כרטיס עצמאי -> תת-חלק בתוך ריבוע המידע המשותף */
  function infoSub(h) {
    if (!h) return '';
    return h.replace(/class="card( subtle)?"/g, 'class="info-sub"').replace(/class="card-title"/g, 'class="info-sub-title"');
  }

  document.addEventListener('input', function (e) {
    var t = e.target;
    if (!t || !t.classList || !t.classList.contains('prog-search')) return;
    var q = t.value.trim().toLowerCase();
    Array.prototype.forEach.call(t.parentNode.querySelectorAll('.prog-item'), function (it) {
      it.hidden = !!q && (it.getAttribute('data-name') || '').indexOf(q) < 0;
    });
  });

  function programsCard(progs, found, autoTorah) {
    var rows = progs.map(function (p) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(p.name) + '</b>' + (p.auto ? ' <span class="muted small">(זוהתה אוטומטית)</span>' : '') + (p.running ? ' <span class="muted small">· פתוחה עכשיו</span>' : '') +
        '<span class="muted path"><bdi dir="ltr">' + ui.esc(p.path) + '</bdi>' + (p.exists ? '' : ' · הקובץ לא נמצא') + '</span></div>' +
        /* סדר הכפתורים (RTL): הסרה (X) מימין, ואחריה סגירה, ופתיחה צמודה לשמאל */
        '<div class="btn-group prog-actions">' +
        (p.auto ? '' : '<button class="btn btn-tonal btn-x" data-act="prog-del-' + ui.esc(p.id) + '" aria-label="הסרה" title="הסרה">' + ui.icon('close') + '</button>') +
        (p.running ? '<button class="btn btn-tonal" data-act="prog-close-' + ui.esc(p.id) + '">' + ui.icon('close') + 'סגירה</button>' : '') +
        '<button class="btn btn-tonal" data-act="prog-open-' + ui.esc(p.id) + '"' + (p.exists ? '' : ' disabled') + '>' + ui.icon('computer') + (p.running ? 'העבר לחזית' : 'פתיחה') + '</button>' +
        '</div></div>';
    }).join('');
    /* רשימה ארוכה של תוכנות שזוהו — קומפקטית: נפתחת בלחיצה, עם חיפוש וגלילה, במקום עשרות כפתורים */
    /* תפריט נפתח אחד: בראשו הוספה ידנית מהמחשב, מתחתיו חיפוש ורשימת תוכנות שנמצאו בתפריט התחל (הצעות בלבד) */
    var suggest = '<details class="prog-suggest"><summary>' + ui.icon('edit') + 'הוספת תוכנה</summary>' +
      '<button type="button" class="prog-item prog-manual" data-act="prog-add">' + ui.icon('edit') + '<span>הוספת תוכנה / רכיב מהמחשב ידנית…</span></button>' +
      (found.length ? '<input type="search" class="prog-search" placeholder="חיפוש בין ' + found.length + ' התוכנות שנמצאו במחשב…" aria-label="חיפוש תוכנה">' +
      '<div class="prog-list">' + found.map(function (f, i) {
        return '<button type="button" class="prog-item" data-act="prog-found-' + i + '" data-name="' + ui.esc(String(f.name || '').toLowerCase()) + '">' + ui.icon('computer') + '<span>' + ui.esc(f.name) + '</span></button>';
      }).join('') + '</div>' : '') + '</details>';
    return '<div class="card"><div class="card-title">תוכנות מותרות לפתיחה</div>' +
      '<p class="muted">תוכנות לימוד (כתבי הקודש, אוצר החכמה, בר אילן, Word ועוד) שאפשר לפתוח מכאן גם בזמן נעילה. ההוספה וההסרה דורשות את סיסמת הנעילה. בסיום העבודה סוגרים את התוכנה וחוזרים לאוצריא.</p>' +
      /* #5: הזיהוי האוטומטי עבר לטאב "תוכנות לימוד" */
      (rows || '<p class="muted">עדיין לא הוגדרו תוכנות.</p>') +
      suggest + '</div>';
  }

  async function openProgram(p) {
    try {
      await bcall('/kiosk/programs/launch', { id: p.id }, { timeoutMs: 30000 });
      ui.toast('התוכנה נפתחת…', 'success');
    } catch (err) { ui.toast(friendlyErr(err), 'error'); }
  }

  async function addProgram(body, preset, reload) {
    var ok = await ui.dialog({
      title: 'הוספת תוכנה מותרת', icon: 'computer',
      body: '<p class="muted">התוכנה תופיע ברשימה ותיפתח מתוך אוצריא גם בזמן נעילה. מומלץ להוסיף רק תוכנות לימוד. אם התוכנה נפתחת דרך תהליך נוסף (מפעיל נפרד), הוסיפו את שמו בשדה „תהליכים נוספים”, כדי שהחלון לא יוחזר לרקע.</p>',
      fields: [
        { name: 'name', label: 'שם להצגה', value: preset.name || '' },
        { name: 'path', label: 'נתיב קובץ ההפעלה (exe)', value: preset.path || '', dir: 'ltr', placeholder: 'C:\\Program Files\\...\\program.exe' },
        { name: 'procs', label: 'תהליכים נוספים (לא חובה)', value: preset.procs || '', dir: 'ltr', placeholder: 'launcher, helper' },
        { name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'הוספה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.name.trim()) return 'יש להקליד שם';
        if (!/^[A-Za-z]:\\.+\.exe$/i.test(x.path.trim().replace(/^"|"$/g, ''))) return 'יש להקליד נתיב מלא לקובץ exe';
        return x.pw ? '' : 'יש להקליד את סיסמת הנעילה';
      },
      onSubmit: async function (x) {
        await bcall('/kiosk/programs/add', { password: x.pw, name: x.name.trim(), path: x.path.trim(), procs: (x.procs || '').trim() });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה נוספה', 'success'); (reload || load)(body); }
  }

  /* #60: הטאב "תוכנות תורניות" משתמש באותם רכיבים (כולל "העבר לחזית") */
  SS.kioskProg = { card: programsCard, add: addProgram, remove: removeProgram, open: openProgram };
  SS.kioskToggleAuto = function (body, current, reload) { return toggleAuto(body, current, reload); };
  async function toggleAuto(body, current, reload) {
    var ok = await ui.dialog({
      title: current ? 'כיבוי הזיהוי האוטומטי' : 'הדלקת הזיהוי האוטומטי', icon: 'computer',
      body: '<p>' + (current
        ? 'התוכנות שזוהו אוטומטית יוסרו מהרשימה ולא יהיה אפשר לפתוח אותן בזמן נעילה (תוכנות שהוספתם ידנית יישארו).'
        : 'הגשר יחפש במחשב תוכנות תורניות (אוצר החכמה, בר אילן, כתבי הקודש ועוד) ואת Word, ויוסיף אותן לרשימה המותרת בזמן נעילה.') + '</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: current ? 'כיבוי הזיהוי' : 'הדלקה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        await bcall('/kiosk/programs/auto', { password: x.pw, enabled: !current }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast(current ? 'הזיהוי האוטומטי כובה' : 'הזיהוי האוטומטי הודלק', 'success'); (reload || load)(body); }
  }

  async function removeProgram(body, p, reload) {
    var ok = await ui.dialog({
      title: 'הסרת תוכנה מהרשימה', icon: 'warning',
      body: '<p>להסיר את <b>' + ui.esc(p.name) + '</b> מרשימת התוכנות המותרות? התוכנה עצמה לא תימחק.</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'הסרה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        await bcall('/kiosk/programs/remove', { password: x.pw, id: p.id });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה הוסרה', 'success'); (reload || load)(body); }
  }

  function parseExt(s) {
    return String(s || '').split(/[,;\s]+/).map(function (e) { return e.trim().toLowerCase().replace(/^\*?\./, ''); })
      .filter(Boolean).filter(function (e, i, a) { return a.indexOf(e) === i; });
  }
})();
