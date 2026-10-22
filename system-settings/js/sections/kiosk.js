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
    var st;
    try { st = await SS.bridge.call('/kiosk/status'); }
    catch (e) { body.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    if (SS.bridge.health) { SS.bridge.health.kioskActive = !!st.active; SS.setStatus(true); if (SS.syncLock) SS.syncLock(SS.bridge.health); }

    /* ארבע הקריאות האלה בלתי תלויות זו בזו (ולא ב-st) - היו רצות ברצף (await אחד אחרי השני), כך
       שזמן הטעינה הכולל היה סכום הזמנים של כל חמש הקריאות ביחד. "זיהוי תוכנות" (סריקת כוננים) ו-
       "מצב BIOS" (שאילתות WMI) הן הכי איטיות, ולכן הרגישו שהכל "תקוע". עכשיו רצות במקביל - הזמן
       הכולל הוא רק הזמן של האיטית מביניהן, לא הסכום */
    var results = await Promise.all([
      SS.bridge.call('/kiosk/programs').catch(function () { return null; }),
      SS.bridge.call('/kiosk/programs/detect').catch(function () { return null; }),
      SS.bridge.call('/pair/list', {}).catch(function () { return null; }),
      SS.bridge.call('/bios/status', {}).catch(function () { return null; })
    ]);
    var pr = results[0], det = results[1], pairsRes = results[2], bios = results[3];

    var progs = (pr && pr.programs) || [];
    st.autoTorah = !!(pr && pr.autoTorah);
    var found = (det && det.found) || [];
    found = found.filter(function (f) {
      return !progs.some(function (p) { return p.path.toLowerCase() === f.path.toLowerCase(); });
    });
    var pairs = (pairsRes && pairsRes.clients) || [];

    render(body, st, progs, found, pairs, bios);
  }

  /* ספירה לאחור חיה של הפתיחה הזמנית בכרטיס המצב (מתאפסת בכל רינדור) */
  var tempTimer = null;

  function render(body, st, progs, found, pairs, bios) {
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

    html += '<details class="card explain"><summary class="card-title">מה עושה מצב נעילה?</summary><ul class="feature-list">' +
      '<li>' + ui.icon('shield') + '<span><b>רק אוצריא פתוחה.</b> אם מישהו סוגר אותה — היא נפתחת מחדש מעצמה ונשארת בחזית.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>שורת המשימות ותפריט התחל מוסתרים,</b> כך שאי אפשר לפתוח תוכנות אחרות.</span></li>' +
      '<li>' + ui.icon('key') + '<span><b>מקשי מעבר חסומים:</b> Win, ‏Alt+Tab, ‏Alt+F4, ‏Ctrl+Esc ומנהל המשימות.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>יציאה רק עם סיסמה</b> — מכאן, מתוך התוסף. הנעילה חוזרת אוטומטית גם אחרי הפעלה מחדש.</span></li>' +
      '</ul></details>';

    html += '<details class="card explain"><summary class="card-title">מה קורה בכיבוי, בהפעלה מחדש ובחשבון Windows אחר?</summary><ul class="feature-list">' +
      '<li>' + ui.icon('lock') + '<span><b>כיבוי או הפעלה מחדש:</b> כברירת מחדל הנעילה <b>נשמרת</b>. הגשר עולה עם Windows וננעל שוב, ואוצריא נפתחת. אם רוצים שבכיבוי הנעילה תתאפס, מסמנים "ביטול נעילה בכיבוי המחשב" בהגדרות הנעילה (לא פועל עם נעילה מוקשחת).</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>חשבון Windows אחר:</b> נעילה רגילה חלה רק בחשבון שבו הופעלה. מחשבון אחר אפשר לעצור את הגשר. <b>נעילה מוקשחת</b> (אופציונלית) מפעילה את הגשר גם בחשבונות אחרים וחוסמת בהם את מנהל המשימות.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>הכי בטוח לעמדת לימוד:</b> חשבון Windows <b>רגיל</b> (בלי הרשאות מנהל) ללא גישה להגדרות המחשב, שבו מפעילים את הנעילה; חשבון המנהל נשאר עם סיסמה נפרדת.</span></li>' +
      '<li>' + ui.icon('key') + '<span><b>חירום:</b> קיצור Ctrl+Alt+Shift+F12 פותח חלון קוד שחזור לביטול הנעילה כולה. בלי הקוד אי אפשר לבטל, כדי שלא יהיה קיצור שעוקף את הנעילה.</span></li>' +
      '</ul></details>';

    if ((st.warnings || []).indexOf('admin') >= 0) {
      html += '<div class="card"><div class="card-title">' + ui.icon('warning') + 'אזהרה: חשבון עם הרשאות מנהל</div>' +
        '<p class="muted">המחשב מופעל בחשבון Windows עם הרשאות מנהל, שממנו אפשר לעקוף את הנעילה. ' +
        'מומלץ ליצור חשבון משתמש <b>רגיל (ללא הרשאות מנהל)</b> לעמדת הלימוד, ולהפעיל בו את הנעילה.</p></div>';
    }

    html += '<div class="card subtle"><div class="card-title">זו נעילה "רכה" — להקשחה מלאה</div>' +
      '<p class="muted">הנעילה רצה בהרשאות המשתמש ומונעת שימוש מזדמן. מי שמבין במחשבים יכול לעקוף אותה. ' +
      'כדי שתעמוד גם מול ניסיון מכוון, השלימו שכבות שאינן תלויות בתוכנה:</p>' +
      '<ul class="feature-list">' +
      '<li>' + ui.icon('key') + '<span><b>סיסמת BIOS/UEFI</b> וביטול אתחול מ-USB — חוסם כניסה ל-Safe Mode ואתחול מהתקן חיצוני. ' +
      'ב-Lenovo וב-HP (דגמים עסקיים) אפשר לנסות להפעיל אוטומטית בכרטיס „הקשחת BIOS/UEFI” למטה; ' +
      'ב-Dell וב-Asus, וכשהניסיון האוטומטי לא מצליח, יש להגדיר ידנית בהגדרות ה-BIOS של המחשב.</span></li>' +
      '<li>' + ui.icon('shield') + '<span><b>חשבון משתמש רגיל</b> ללא הרשאות מנהל, וחשבון המנהל עם סיסמה שהתלמיד אינו יודע.</span></li>' +
      '<li>' + ui.icon('computer') + '<span><b>סינון תוכן על המחשב עצמו</b> (למשל נטפרי), ולא רק ברמת הרשת — כך מעבר לרשת אחרת אינו עוקף את הסינון.</span></li>' +
      '<li>' + ui.icon('lock') + '<span><b>Safe Mode:</b> במצב בטוח הגשר אינו עולה והנעילה אינה חלה. סיסמת BIOS שחוסמת את תפריט האתחול סוגרת גם את זה.</span></li>' +
      '</ul></div>';

    if (!st.hasPassword) {
      html += '<div class="card"><div class="card-title">שלב ראשון: הגדרת סיסמה</div>' +
        '<p class="muted">לפני הפעלת הנעילה צריך לבחור סיסמה. רק מי שיודע אותה יוכל לבטל את הנעילה או לשנות הגדרות.</p>' +
        '<form class="form-grid" data-form="setpw" novalidate>' +
        '<label class="field"><span class="field-label">סיסמה</span><input class="input" type="password" name="pw" autocomplete="new-password" dir="ltr"></label>' +
        '<label class="field"><span class="field-label">אימות סיסמה</span><input class="input" type="password" name="pw2" autocomplete="new-password" dir="ltr"></label>' +
        '<div class="form-msg" hidden></div>' +
        '<div class="row-actions"><button class="btn btn-primary btn-lg" type="submit">' + ui.icon('key') + 'שמירת סיסמה</button></div>' +
        '<p class="muted small">לפחות ' + MIN_PW + ' תווים. אחרי השמירה יוצג קוד שחזור חד-פעמי — חשוב לרשום אותו.</p>' +
        '</form></div>';
    } else {
      html += '<div class="card"><div class="card-title">סיסמה והגדרות</div>' +
        '<div class="setting-row"><div class="setting-text"><b>שינוי סיסמה</b><span class="muted">החלפת סיסמת הנעילה (נדרשת הסיסמה הנוכחית)</span></div>' +
        '<button class="btn btn-tonal" data-act="chpw">' + ui.icon('edit') + 'שינוי</button></div>' +
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
        '<div class="setting-row"><div class="setting-text"><b>נתיב אוצריא</b><span class="muted path"><bdi dir="ltr">' + ui.esc(st.otzariaPath || 'זיהוי אוטומטי') + '</bdi></span></div></div>' +
        '</div>';
      html += '<div class="card"><div class="card-title">הגדרות נעילה</div><div data-slot="kset">' + ui.loading('טוען הגדרות…') + '</div></div>';
      html += programsCard(progs, found, !!st.autoTorah);
      html += fxCard();
      html += hardCard(st.hard || {});
      html += biosCard(bios);
      html += pairCard(pairs);
      html += '<div class="card subtle"><div class="card-title">שכחתם את הסיסמה?</div>' +
        '<p class="muted">בחלון „ביטול נעילה” אפשר להקליד גם את <b>קוד השחזור</b> שקיבלתם בהגדרת הסיסמה. במקרה חירום אפשר להריץ משורת הפקודה:</p>' +
        '<code class="cmd" dir="ltr">OtzariaBridge.exe --unlock &lt;קוד-שחזור&gt;</code>' +
        '<p class="muted small">אפשרות נוספת: הפעלה ב-Safe Mode, שבו הגשר אינו עולה והנעילה אינה חלה.</p></div>';
    }

    body.innerHTML = html;
    if (!active && tu.active) startTempCountdown(body, tu.secondsLeft);
    wire(body, st, progs, found);
    wireHard(body, st, pairs);
    wireBios(body, bios);
  }

  function wire(body, st, progs, found) {
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
          var r = await SS.bridge.call('/kiosk/set-password', { newPassword: pw });
          if (r && r.recoveryCode) await showRecovery(r.recoveryCode);
          else ui.toast('הסיסמה נשמרה', 'success');
          load(body);
        } catch (err) {
          msg.textContent = ui.errText(err); msg.hidden = false; btn.disabled = false;
        }
      });
    }
    on('enable', function () { enable(body, st); });
    on('disable', function () { disable(body); });
    on('temp-unlock', function () { tempUnlock(body, st); });
    on('temp-end', function () { tempEnd(body); });
    on('chpw', function () { changePw(body); });
    editSettings(body, st); /* הגדרות הנעילה מוצגות ישירות בעמוד, לא מאחורי כפתור */
    on('settings3', function () { SS.show('screensaver'); });
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
    var pretty = code.replace(/(.{4})(?=.)/g, '$1-');
    return ui.dialog({
      title: 'קוד השחזור שלכם', icon: 'key', tone: 'recovery', dismissible: false,
      body:
        '<p>זהו <b>קוד שחזור חד-פעמי</b>. הוא יוצג <b>רק עכשיו</b> ולא יהיה אפשר לראות אותו שוב.</p>' +
        '<div class="recovery-code" dir="ltr">' + ui.esc(pretty) +
        '<button type="button" class="btn btn-text" data-copy-code title="העתקת הקוד">' + ui.icon('copy') + '</button></div>' +
        '<p>רשמו אותו על דף ושמרו במקום בטוח, <b>לא ליד המחשב</b>. אם תשכחו את הסיסמה, אפשר לבטל את הנעילה עם הקוד הזה בחלון „ביטול נעילה”, או מחלון פקודה:</p>' +
        '<code class="cmd" dir="ltr">OtzariaBridge.exe --unlock ' + ui.esc(code) + '</code>',
      fields: [{ name: 'ack', type: 'checkbox', label: 'רשמתי את הקוד' }],
      actions: [{ label: 'סיום', value: true, kind: 'primary' }],
      validate: function (v) { return v.ack ? '' : 'יש לסמן „רשמתי את הקוד” לפני הסגירה'; },
      onOpen: function (form) {
        var btn = form.querySelector('button[type=submit]');
        var cb = form.querySelector('input[name=ack]');
        btn.disabled = true;
        cb.addEventListener('change', function () { btn.disabled = !cb.checked; });
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
        await SS.bridge.call('/kiosk/enable', { password: v.pw, strict: !!v.strict, blockTaskManager: !!v.tm }, { timeoutMs: 30000 });
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
        await SS.bridge.call('/kiosk/disable', { password: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){2}$/.test(pw) ? pw.replace(/-/g, '') : x.pw }, { timeoutMs: 30000 });
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
        await SS.bridge.call('/kiosk/temp-unlock', { password: x.pw }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast('המחשב נפתח זמנית', 'success'); load(body); }
  }

  async function tempEnd(body) {
    if (!(await ui.confirm('נעילה עכשיו', '<p>לסיים את הפתיחה הזמנית ולהחזיר את הנעילה מיד?</p>', 'נעילה עכשיו'))) return;
    try { await SS.bridge.call('/kiosk/temp-unlock/end', {}, { timeoutMs: 30000 }); ui.toast('הנעילה הוחזרה', 'success'); }
    catch (e) { ui.error(e); }
    load(body);
  }

  async function changePw(body) {
    var ok = await ui.dialog({
      title: 'שינוי סיסמת נעילה', icon: 'key',
      fields: [
        { name: 'old', label: 'סיסמה נוכחית', type: 'password', dir: 'ltr' },
        { name: 'n1', label: 'סיסמה חדשה', type: 'password', dir: 'ltr' },
        { name: 'n2', label: 'אימות סיסמה חדשה', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        if (!x.old) return 'יש להקליד את הסיסמה הנוכחית';
        if (x.n1.length < MIN_PW) return 'הסיסמה החדשה חייבת להכיל לפחות ' + MIN_PW + ' תווים';
        if (x.n1 !== x.n2) return 'הסיסמאות החדשות אינן תואמות';
        return '';
      },
      onSubmit: async function (x) {
        var r = await SS.bridge.call('/kiosk/set-password', { oldPassword: x.old, newPassword: x.n1 });
        return r || true;
      }
    });
    if (ok) {
      if (ok.recoveryCode) await showRecovery(ok.recoveryCode);
      ui.toast('הסיסמה עודכנה', 'success');
      load(body);
    }
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
      (h.blockUsbStorage ? ' · אחסון USB חסום' : '') + '</span></div>' + actions + '</div>' +
      '<p class="muted small">הרכיב אינו משנה את הכניסה ל-Windows ואינו רץ במצב בטוח, כך שאינו יכול להשבית את המחשב. ' +
      'גם הוא אינו מחליף סיסמת BIOS. פרטים: docs/HARDLOCK.md במאגר.</p></div>';
  }

  function pairCard(pairs) {
    var rows = pairs.map(function (c) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(c.name || c.client) + '</b><span class="muted">חובר: ' +
        ui.esc((c.created || '').replace('T', ' ')) + ' · שימוש אחרון: ' + ui.esc((c.lastUsed || '').replace('T', ' ')) + '</span></div>' +
        '<button class="btn btn-tonal" data-act="unpair-' + ui.esc(c.id) + '">' + ui.icon('close') + 'ניתוק</button></div>';
    }).join('');
    /* סטטוס פשוט למעלה (כמה תוספים מחוברים) - הרשימה המלאה עם זמנים/ניתוק רק למי שבאמת צריך,
       מאחורי "פרטים נוספים". אם יש כפילויות (למשל אחרי שינוי מזהה תוסף - הישן נשאר מחובר כ"יתום"
       עד שמנתקים אותו ידנית), זה בדיוק המקום לנקות אותן */
    var status = pairs.length
      ? '<span class="chip ok">' + ui.icon('check') + pairs.length + ' תוספים מחוברים</span>'
      : '<span class="chip warn">' + ui.icon('warning') + 'אין תוספים מחוברים</span>';
    return '<div class="card"><div class="card-title">תוספים מחוברים לגשר</div>' +
      '<p class="muted">מגרסה 3.0 רק תוסף שאושר בחלון של הגשר יכול להשתמש בו. תוכנה אחרת במחשב לא יכולה לגשת לקבצים ולהגדרות בלי אישור כזה.</p>' +
      status +
      '<details class="more-details"><summary>פרטים נוספים (' + pairs.length + ')</summary><div class="more-details-body">' +
      (rows || '<p class="muted">אין תוספים מחוברים.</p>') +
      '<p class="muted small">רואים כפילות (אותו שם פעמיים)? כנראה חיבור ישן משארית התקנה קודמת - אפשר לנתק אותו בבטחה, השני ימשיך לעבוד.</p>' +
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
    var manualBtn = '<div class="row-actions"><button class="btn btn-tonal" data-act="bios-manual">' + ui.icon('info') + 'הדרכה ידנית (כל היצרנים)</button></div>';

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
    head += '<div class="row-actions">' +
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
        return await SS.bridge.call('/bios/harden', {
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
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/install', { password: x.pw }, { timeoutMs: 200000 }); return true; }
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
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/settings', { password: x.pw, blockUsbStorage: !!x.usb }); return true; }
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
        onSubmit: async function (x) { await SS.bridge.call('/kiosk/hard/uninstall', { password: x.pw }); return true; }
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
        try { await SS.bridge.call('/pair/revoke', { id: c.id, password: x.pw }); load(body); }
        catch (e) { ui.error(e); }
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
    var zmanShow = st.zmanimShow || DEFAULT_ZMAN_SHOW;
    /* רשימת ערים מלוח השנה של אוצריא עצמה (calendar.getCities) — אם זמינה. קואורדינטות
       נשארות תמיד כאפשרות מתקדמת ("מותאם אישית") גם כשיש רשימת ערים */
    var cities = [];
    if (window.Otzaria) {
      try {
        var r = await window.Otzaria.call('calendar.getCities', {});
        var list = (r && r.data !== undefined ? r.data : r) || [];
        if (Array.isArray(list)) cities = list;
      } catch (e) { cities = []; }
    }
    var cityOptions = [{ value: '', label: 'מותאם אישית (קואורדינטות)' }].concat(
      cities.map(function (c) { return { value: c.name, label: c.name + (c.country ? ' (' + c.country + ')' : '') }; })
    );
    var cityByName = {};
    cities.forEach(function (c) { cityByName[c.name] = c; });
    var fields = [
      { name: 'rest', type: 'checkbox', label: 'מסך מנוחה כשאוצריא ממוזערת', value: st.restScreen !== false, hint: 'סמל אוצריא, שעה ותאריך במסך מלא. לחיצה בכל מקום מחזירה לאוצריא' },
      { name: 'idle', label: 'שומר מסך אחרי דקות ללא פעילות (0 = כבוי)', value: String(st.restIdleMinutes || 0), dir: 'ltr', placeholder: '0', hint: 'מסך המנוחה מופיע גם כשאוצריא פתוחה, אחרי הזמן הזה בלי מקלדת ועכבר. עד 240 דקות' }
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
      hint: 'ישן: קירוב NOAA פשוט, הנץ/שקיעה בגובה פני הים בלבד, זמנים מעטים. חדש: ספריית KosherJava, זמנים הלכתיים מלאים עם תיקון גובה — <b>אך קוד צד-שלישי שעלול להיבדל מעט בזמנים גבוליים</b>' });
    fields.push({ name: 'zelev', label: 'גובה מעל פני הים במיקום (מטרים)', value: String(st.zmanimElevation || 0), dir: 'ltr', placeholder: '0',
      hint: 'משפיע רק כשנבחר מנוע הגיבוי "חדש" (KosherJava) למעלה. 0 עד 5000 מטר' });
    fields.push({ name: 'diaspora', type: 'checkbox', label: 'חוץ לארץ (יום טוב שני)', value: !!st.zmanimDiaspora,
      hint: 'משפיע על זיהוי כניסת/יציאת חג של יום טוב שני של גלויות' });
    fields.push({ name: 'candle', label: 'הדלקת נרות: דקות לפני השקיעה', value: String(st.candleLightingMinutes != null ? st.candleLightingMinutes : 18), dir: 'ltr', placeholder: '18' });
    fields.push({ name: 'zshowHead', type: 'note', label: 'אילו זמנים להציג בשומר המסך:' });
    ZMAN_FIELDS.forEach(function (z) {
      fields.push({ name: 'zm_' + z.id, type: 'checkbox', label: z.label, value: zmanShow.indexOf(z.id) >= 0 });
    });
    fields.push({ name: 'parsha', type: 'checkbox', label: 'פרשת השבוע בשומר המסך', value: !!st.showParsha,
      hint: 'שם הפרשה הקרובה (השבת הנוכחית/הבאה), מחושב על ידי ספריית Zmanim/KosherJava' });
    fields.push({ name: 'dafyomi', type: 'checkbox', label: 'דף יומי בשומר המסך', value: !!st.showDafYomi,
      hint: 'מסכת ודף נוכחיים לפי לוח דף היומי הסטנדרטי' });
    fields.push({ name: 'zmSide', type: 'select', label: 'עמודת זמני היום בשומר המסך', value: st.restZmanimSide || 'right', hint: 'הזמנים מוצגים בעמודה בצד; ההקדשה האישית (אם הוגדרה למטה) מוצגת בעמודה בצד הנגדי, בלי חפיפה', options: [{ value: 'right', label: 'ימין' }, { value: 'left', label: 'שמאל' }] });
    fields.push({ name: 'dedication', label: 'הקדשה אישית בשומר המסך + נר דולק (אופציונלי)', value: st.dedicationText || '', placeholder: '',
      hint: 'מוצגת בעמודה בצד הנגדי לזמני היום, עם נר מרצד. ריק = לא מוצג כלל' });
    fields.push({ name: 'yzMonth', type: 'select', label: 'יום השנה (הנצחה): חודש עברי', value: st.yahrzeitMonth || '',
      options: [{ value: '', label: '(כבוי)' }].concat(YAHRZEIT_MONTHS.map(function (m) { return { value: m, label: m }; })),
      hint: 'מציג שורה מיוחדת בשומר המסך ביום הזה בכל שנה. ריק = כבוי. לא קשור בהכרח להקדשה הקבועה שבתוסף' });
    fields.push({ name: 'yzDay', type: 'select', label: 'יום השנה: יום בחודש', value: String(st.yahrzeitDay || 0),
      options: [{ value: '0', label: '(כבוי)' }].concat(YAHRZEIT_DAYS.map(function (d, i) {
        /* גרש (׳) אחרי אות בודדת, גרשיים (״) לפני האות האחרונה כשיש יותר מאחת */
        var label = d.length === 1 ? d + '׳' : d.slice(0, -1) + '״' + d.slice(-1);
        return { value: String(i + 1), label: label };
      })) });
    fields.push({ name: 'yzLabel', label: 'יום השנה: טקסט (למשל "לעילוי נשמת ...")', value: st.yahrzeitLabel || '', placeholder: '' });
    return { fields: fields, cityByName: cityByName };
  };
  SS.screensaverPayload = function (x) {
    return {
      restScreen: !!x.rest, restIdleMinutes: +String(x.idle).trim() || 0, restZmanimSide: x.zmSide,
      zmanimLocation: x.zloc.trim(), zmanimCity: (x.zcity || '').trim(),
      zmanimElevation: +String(x.zelev).trim() || 0, zmanimFallbackEngine: x.zengine,
      zmanimDiaspora: !!x.diaspora, candleLightingMinutes: +String(x.candle).trim() || 0,
      zmanimShow: ZMAN_FIELDS.filter(function (z) { return x['zm_' + z.id]; }).map(function (z) { return z.id; }),
      showParsha: !!x.parsha, showDafYomi: !!x.dafyomi,
      yahrzeitMonth: (x.yzMonth || '').trim(), yahrzeitDay: +String(x.yzDay).trim() || 0, yahrzeitLabel: (x.yzLabel || '').trim(),
      dedicationText: (x.dedication || '').trim()
    };
  };

  async function editSettings(body, st) {
    var all = st.allSections || Object.keys(SECTION_NAMES);
    var open = st.openSections || [];

    /* הטופס מחולק לקבוצות מתקפלות לפי נושא (ר' ui.fieldsHtml) - לפני זה הכל הוצג פרוש ביחד
       (כ-25 שדות), כבד מדי לדפדוף. כל קבוצה סגורה כברירת מחדל חוץ מהראשונה */
    var tu = st.tempUnlock || {};
    var tfaNow = !!(tu.twoFactorOn != null ? tu.twoFactorOn : st.twoFactor);
    var TU_HINT_ON = 'סיסמה נפרדת, שאפשר למסור למי שצריך את המחשב לזמן קצר, בלי לגלות את סיסמת הנעילה. כל הנעילה (גם סייר הקבצים) נפתחת לזמן שנקבע כאן, ואז חוזרת מעצמה. לא מתקבלת כסיסמת נעילה.';
    var TU_HINT_OFF = 'זמין רק כשהאימות הדו-שלבי (Windows Hello) מופעל — סמנו קודם את האימות הדו-שלבי. אם הוא יכובה, הפתיחה הזמנית תיעצר עד שיופעל שוב (ההגדרות נשמרות).';
    var sc = st.schedule || {};

    var fields = [
      { type: 'group', icon: 'display', label: 'תצוגה ושולחן עבודה', open: true,
        hint: 'שעון, שם המחשב, סרגל התוכנות ומסך מלא' },
      { name: 'clock', type: 'checkbox', label: 'שעון ותאריך עברי צפים', value: st.clock !== false, hint: 'שורת המשימות מוסתרת בנעילה, ולכן מוצג שעון קטן בפינת המסך. לחיצה עליו מחזירה לאוצריא' },
      {
        name: 'pos', type: 'select', label: 'מיקום השעון', value: st.clockPosition || 'bottom-left',
        options: [
          { value: 'bottom-left', label: 'למטה-שמאל' },
          { value: 'bottom-right', label: 'למטה-ימין' },
          { value: 'top-left', label: 'למעלה-שמאל' },
          { value: 'top-right', label: 'למעלה-ימין' }
        ]
      },
      { name: 'venue', label: 'שם המחשב/בית הכנסת (אופציונלי)', value: st.venueName || '', placeholder: '', hint: 'מוצג במרכז המסך מתחת לשעון, במסך המנוחה. ריק = מוצגת רק השעה' },
      { name: 'fs', type: 'checkbox', label: 'אוצריא במסך מלא', value: st.fullscreenOtzaria !== false, hint: 'בלי כותרת ומסגרת חלון' },
      { name: 'side', type: 'checkbox', label: 'סרגל תוכנות בצד המסך', value: st.sideBar !== false, hint: 'פס צר בשולי המסך שמתרחב כשמתקרבים אליו: אוצריא והתוכנות התורניות המותרות, עם סימון מה פתוח. לחיצה מעבירה לחזית, או פותחת אם סגורה' },
      { name: 'sideIcons', type: 'checkbox', label: 'סרגל התוכנות: אייקונים בלבד', value: !!st.sideBarIconsOnly, hint: 'סרגל צר יותר, בלי שמות. כבוי (ברירת מחדל): אייקון ולידו שם התוכנה' },
      { name: 'sidePos', type: 'select', label: 'מיקום סרגל התוכנות', value: st.sideBarPosition || 'right', hint: 'לאיזה צד מסך הסרגל מוצמד', options: [{ value: 'right', label: 'ימין' }, { value: 'left', label: 'שמאל' }, { value: 'top', label: 'למעלה' }, { value: 'bottom', label: 'למטה' }] },

      { type: 'group', icon: 'edit', label: 'קבצים ותוכן', hint: 'נתיב אוצריא, סיומות מותרות, קריאה-בלבד, Excel' },
      { name: 'path', label: 'נתיב אוצריא (ריק = זיהוי אוטומטי)', value: st.otzariaPath || '', dir: 'ltr', placeholder: 'C:\\Program Files\\Otzaria\\otzaria.exe' },
      { name: 'ext', label: 'סיומות קבצים שמותר לפתוח בתוכנות אחרות', value: (st.allowOpenExtensions || []).join(', '), dir: 'ltr', placeholder: 'pdf, txt' },
      { name: 'ro', type: 'checkbox', label: 'מצב קריאה בלבד לקבצים', value: !!st.readOnly, hint: 'חוסם שינוי, מחיקה והעברה של קבצים בסייר בזמן נעילה' },
      { name: 'xls', type: 'checkbox', label: 'אפשור צפייה בקובצי Excel (xlsx) בסייר הקבצים', value: !!st.allowExcelDocs, hint: 'ברירת מחדל: בסייר הקבצים אפשר לצפות ולפתוח רק מסמכי Word. מצגות PowerPoint חסומות תמיד ואין אפשרות להתיר אותן; סימון כאן מתיר גם גיליונות Excel' },

      { type: 'group', icon: 'shield', label: 'אבטחה', hint: 'אימות דו-שלבי, ביטול נעילה בכיבוי' },
      { name: 'tfa', type: 'checkbox', label: 'אימות דו-שלבי (Windows Hello)', value: !!st.twoFactor,
        hint: (st.twoFactorAvailable ? 'ביטול הנעילה ושינוי ההגדרות ידרשו גם זיהוי פנים/טביעת אצבע/PIN, בנוסף לסיסמה. קוד השחזור ימשיך לעבוד לחירום.' : '⚠ Windows Hello אינו מוגדר במחשב הזה — הגדירו אותו ב-Windows כדי להפעיל') },
      { name: 'reset', type: 'checkbox', label: 'ביטול נעילה בכיבוי המחשב', value: !!st.resetOnShutdown, hint: 'כבוי (ברירת מחדל): הנעילה נשמרת וחוזרת אחרי הפעלה מחדש. מסומן: בכיבוי או התנתקות הנעילה מתבטלת והמחשב עולה רגיל. לא פועל עם נעילה מוקשחת' },

      { type: 'group', icon: 'key', label: 'פתיחה זמנית בסיסמה נפרדת', hint: 'לתת למישהו את המחשב לזמן קצר בלי לגלות את הסיסמה הראשית' },
      { name: 'tu', type: 'checkbox', label: 'פתיחה זמנית בסיסמה נפרדת', value: !!tu.enabled,
        disabled: !tfaNow && !tu.enabled, hint: '<span data-tu-hint>' + (tfaNow ? TU_HINT_ON : TU_HINT_OFF) + '</span>' },
      { name: 'tuMin', label: 'פתיחה זמנית: משך בדקות (1–60)', value: String(tu.minutes || 30), dir: 'ltr', placeholder: '30' },
      { name: 'tuPw', label: tu.configured ? 'סיסמת פתיחה זמנית חדשה (ריק = ללא שינוי)' : 'סיסמת פתיחה זמנית', type: 'password', dir: 'ltr' },
      { name: 'tuPw2', label: 'אימות סיסמת הפתיחה הזמנית', type: 'password', dir: 'ltr' },

      { type: 'group', icon: 'unlock', label: 'מה פתוח בזמן נעילה', hint: 'אילו מסכי הגדרות נגישים לתלמיד בזמן נעילה' },

      { type: 'group', icon: 'clock', label: 'תזמון נעילה לפי שעות', hint: 'נעילה אוטומטית מתחילה ונגמרת בשעה קבועה כל יום' },
      { name: 'sched', type: 'checkbox', label: 'תזמון נעילה לפי שעות', value: !!sc.enabled, hint: 'נעילה אוטומטית בכל יום בין שעה לשעה (למשל 20:00 עד 02:00, כולל אחרי חצות). מחוץ לטווח המחשב פועל רגיל. ביטול ידני בתוך הטווח יחזור לנעילה' },
      { name: 'schedStart', label: 'שעת התחלה', type: 'time', value: sc.start || '20:00', dir: 'ltr' },
      { name: 'schedEnd', label: 'שעת סיום', type: 'time', value: sc.end || '02:00', dir: 'ltr' },
      { name: 'schedWarn', type: 'checkbox', label: 'התראה לפני נעילה מתוזמנת', value: sc.warn !== false, hint: 'אם יושבים מול המחשב — תופיע התראה 5 דקות ודקה לפני הנעילה, עם אפשרות לבטל את הנעילה הקרובה בסיסמה' },
    ];
    /* "מה פתוח בזמן נעילה" מוכנס בתוך הקבוצה שלו למעלה - לא ב-push נפרד, כדי שישאר באותו field-group-body */
    var secIdx = fields.findIndex(function (f) { return f.label === 'מה פתוח בזמן נעילה'; });
    var secFields = all.map(function (id) {
      return { name: 'sec_' + id, type: 'checkbox', label: 'פתוח בנעילה: ' + (SECTION_NAMES[id] || id), value: open.indexOf(id) >= 0 };
    });
    fields.splice.apply(fields, [secIdx + 1, 0].concat(secFields));
    /* בראש הרשימה (לפני הקבוצה הראשונה): שדה שאחרי {type:'group'} נכנס לתוך הקבוצה, ובסוף הרשימה זו
       קבוצה סגורה ("תזמון נעילה") - הסיסמה נעלמה מהעין והשמירה נכשלה עם "יש להקליד את סיסמת הנעילה" */
    fields.unshift({ name: 'pw', label: st.hasPassword ? 'סיסמת נעילה (נדרשת לשמירה)' : 'סיסמת נעילה (עוד לא הוגדרה — אפשר להשאיר ריק)', type: 'password', dir: 'ltr' });
    var slot = body.querySelector('[data-slot=kset]'); if (!slot) return;
    var ok = await ui.inlineForm(slot, {
      title: 'הגדרות נעילה', icon: 'edit',
      body: '<p class="muted">מה פתוח לתלמיד בזמן נעילה, ואיך נראה המסך. הגדרות שלא סומנו מוסתרות ונחסמות גם בגשר.</p>',
      fields: fields,
      actions: [{ label: 'ביטול', value: false }, { label: 'שמירה', value: true, kind: 'primary' }],
      validate: function (x) {
        /* לפני שהוגדרה סיסמת נעילה בכלל, אין עדיין מה לאמת מולו - הגשר מקבל שמירת העדפות גם בלי סיסמה */
        if (st.hasPassword && !x.pw) return 'יש להקליד את סיסמת הנעילה';
        if (['bottom-left', 'bottom-right', 'top-left', 'top-right'].indexOf(x.pos) < 0) return 'מיקום שעון לא תקין';
        if (x.venue.trim().length > 60) return 'שם המחשב/בית הכנסת: עד 60 תווים';
        if (['right', 'left', 'top', 'bottom'].indexOf(x.sidePos) < 0) return 'מיקום סרגל לא תקין';
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
        await SS.bridge.call('/kiosk/settings', {
          password: x.pw, allowOpenExtensions: parseExt(x.ext), otzariaPath: x.path.trim(),
          readOnly: !!x.ro, allowExcelDocs: !!x.xls, twoFactor: !!x.tfa, clock: !!x.clock, venueName: x.venue.trim(), fullscreenOtzaria: !!x.fs, sideBar: !!x.side, sideBarIconsOnly: !!x.sideIcons, sideBarPosition: x.sidePos, resetOnShutdown: !!x.reset, clockPosition: x.pos,
          openSections: all.filter(function (id) { return x['sec_' + id]; }),
          schedule: { enabled: !!x.sched, start: x.schedStart, end: x.schedEnd, warn: !!x.schedWarn },
          tempUnlockEnabled: !!x.tu, tempUnlockMinutes: +String(x.tuMin).trim() || 30, tempUnlockPassword: x.tuPw || ''
        });
        return true;
      }
    });
    if (ok) { ui.toast('ההגדרות נשמרו', 'success'); if (SS.pushZmanimNow) SS.pushZmanimNow(); load(body); }
  }

  /* מדיניות סייר הקבצים נערכת בתוסף "סייר קבצים" עצמו; כאן רק מפנים אליה */
  function fxCard() {
    return '<div class="card subtle"><div class="card-title">נעילה והגבלה של סייר הקבצים</div>' +
      '<p class="muted">אפשר לנעול את סייר הקבצים בסיסמת המנהל, או להגביל אותו לתיקיות מסוימות (למשל שולחן העבודה). ההגדרה נמצאת בתוסף „סייר קבצים”: הגדרות ← נעילה והגבלה, ונאכפת בגשר.</p></div>';
  }

  function programsCard(progs, found, autoTorah) {
    var rows = progs.map(function (p) {
      return '<div class="setting-row"><div class="setting-text"><b>' + ui.esc(p.name) + '</b>' + (p.auto ? ' <span class="muted small">(זוהתה אוטומטית)</span>' : '') + (p.running ? ' <span class="muted small">· פתוחה עכשיו</span>' : '') +
        '<span class="muted path"><bdi dir="ltr">' + ui.esc(p.path) + '</bdi>' + (p.exists ? '' : ' · הקובץ לא נמצא') + '</span></div>' +
        '<button class="btn btn-tonal" data-act="prog-open-' + ui.esc(p.id) + '"' + (p.exists ? '' : ' disabled') + '>' + ui.icon('computer') + (p.running ? 'העבר לחזית' : 'פתיחה') + '</button>' +
        (p.auto ? '' : '<button class="btn btn-tonal" data-act="prog-del-' + ui.esc(p.id) + '" aria-label="הסרה">' + ui.icon('close') + '</button>') + '</div>';
    }).join('');
    var suggest = found.map(function (f, i) {
      return '<button class="btn btn-tonal" data-act="prog-found-' + i + '">' + ui.icon('edit') + 'הוספת ' + ui.esc(f.name) + '</button>';
    }).join('');
    return '<div class="card"><div class="card-title">תוכנות מותרות לפתיחה</div>' +
      '<p class="muted">תוכנות לימוד (כתבי הקודש, אוצר החכמה, בר אילן, Word ועוד) שאפשר לפתוח מכאן גם בזמן נעילה. ההוספה וההסרה דורשות את סיסמת הנעילה. בסיום העבודה סוגרים את התוכנה וחוזרים לאוצריא.</p>' +
      /* #5: הזיהוי האוטומטי עבר לטאב "תוכנות לימוד" */
      (rows || '<p class="muted">עדיין לא הוגדרו תוכנות.</p>') +
      '<div class="row-actions"><button class="btn btn-tonal" data-act="prog-add">' + ui.icon('edit') + 'הוספת תוכנה</button>' + suggest + '</div></div>';
  }

  async function openProgram(p) {
    try {
      await SS.bridge.call('/kiosk/programs/launch', { id: p.id }, { timeoutMs: 30000 });
      ui.toast('התוכנה נפתחת…', 'success');
    } catch (err) { ui.toast(ui.errText(err), 'error'); }
  }

  async function addProgram(body, preset) {
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
        var pth = x.path.trim().replace(/^"|"$/g, '');
        /* אפליקציות חנות (פתקים דביקים/שעון) מזוהות לפי shell:AppsFolder\...; אחרת נדרש נתיב exe מלא */
        if (!/^shell:AppsFolder\\[\w.\-]+![\w.\-]+$/i.test(pth) && !/^[A-Za-z]:\\.+\.exe$/i.test(pth)) return 'יש להקליד נתיב מלא לקובץ exe';
        return x.pw ? '' : 'יש להקליד את סיסמת הנעילה';
      },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/programs/add', { password: x.pw, name: x.name.trim(), path: x.path.trim(), procs: (x.procs || '').trim() });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה נוספה', 'success'); load(body); }
  }

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
        await SS.bridge.call('/kiosk/programs/auto', { password: x.pw, enabled: !current }, { timeoutMs: 30000 });
        return true;
      }
    });
    if (ok) { ui.toast(current ? 'הזיהוי האוטומטי כובה' : 'הזיהוי האוטומטי הודלק', 'success'); (reload || load)(body); }
  }

  async function removeProgram(body, p) {
    var ok = await ui.dialog({
      title: 'הסרת תוכנה מהרשימה', icon: 'warning',
      body: '<p>להסיר את <b>' + ui.esc(p.name) + '</b> מרשימת התוכנות המותרות? התוכנה עצמה לא תימחק.</p>',
      fields: [{ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' }],
      actions: [{ label: 'ביטול', value: false }, { label: 'הסרה', value: true, kind: 'primary' }],
      validate: function (x) { return x.pw ? '' : 'יש להקליד את סיסמת הנעילה'; },
      onSubmit: async function (x) {
        await SS.bridge.call('/kiosk/programs/remove', { password: x.pw, id: p.id });
        return true;
      }
    });
    if (ok) { ui.toast('התוכנה הוסרה', 'success'); load(body); }
  }

  function parseExt(s) {
    return String(s || '').split(/[,;\s]+/).map(function (e) { return e.trim().toLowerCase().replace(/^\*?\./, ''); })
      .filter(Boolean).filter(function (e, i, a) { return a.indexOf(e) === i; });
  }
})();
