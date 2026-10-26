/* סייר קבצים - מדיניות הנעילה וההגבלה: לקוח לגשר (/fs/policy/*), מסך נעילה וטופס המנהל.
   האכיפה האמיתית נעשית בגשר; כאן רק משקפים אותה. בתצוגה בדפדפן (בלי אוצריא) פועל דמה בזיכרון. */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var esc = function (s) { return FX.esc(s); };
  var ic = function (n, c) { return FX.icon(n, c); };
  var t = function (s) { return FX.t ? FX.t(s) : s; };

  var state = null;       /* תשובת /fs/policy/get האחרונה, או null (אין גשר) */
  var timer = null;

  /* ---------- דמה לדפדפן (סיסמה: 1234) ---------- */
  var mock = { mode: 'off', roots: [], minutes: 15, until: 0 };
  var MOCK_DEFAULTS = [
    { id: 'desktop', name: 'שולחן העבודה', path: 'C:\\Users\\demo\\Desktop' },
    { id: 'documents', name: 'מסמכים', path: 'C:\\Users\\demo\\Documents' },
    { id: 'downloads', name: 'הורדות', path: 'C:\\Users\\demo\\Downloads' },
    { id: 'pictures', name: 'תמונות', path: 'C:\\Users\\demo\\Pictures' },
    { id: 'music', name: 'מוזיקה', path: 'C:\\Users\\demo\\Music' },
    { id: 'videos', name: 'סרטונים', path: 'C:\\Users\\demo\\Videos' }
  ];
  function isMock() { return !window.Otzaria; }
  function mockState() {
    var left = Math.max(0, Math.ceil((mock.until - Date.now()) / 1000));
    var eff = mock.mode === 'off' ? 'open' : mock.mode === 'locked' ? (left > 0 ? 'open' : 'locked') : (mock.roots.length ? 'restricted' : (left > 0 ? 'open' : 'locked'));
    return {
      mode: mock.mode, effective: eff, locked: eff === 'locked', fallback: mock.mode === 'restricted' && !mock.roots.length,
      roots: mock.mode === 'restricted' ? mock.roots.slice() : [], activeRoots: mock.mode === 'restricted' ? mock.roots.slice() : [],
      invalidRoots: [], unlockMinutes: mock.minutes, unlockedSeconds: left, passwordConfigured: true, defaults: MOCK_DEFAULTS
    };
  }
  function mockCheck(pw) {
    if (pw !== '1234') throw FX.OpError('wrong_password', 'סיסמה שגויה (בדמה: 1234)');
  }

  function bridgeUp() { return !!(FX.Bridge && FX.Bridge.port); }
  function available() { return isMock() || bridgeUp(); }

  var P = FX.Policy = {
    available: available,
    state: function () { return state; },
    locked: function () { return !!(state && state.locked); },
    restricted: function () { return !!(state && state.effective === 'restricted'); },

    /* האם נתיב מותר לפי המדיניות הנוכחית (רק לנוחות הממשק; הגשר אוכף) */
    pathAllowed: function (path) {
      if (!state || state.effective !== 'restricted' || !path) return true;
      if (path === '::recycle' || path.indexOf('::recycle') === 0) return true;
      return (state.activeRoots || []).some(function (r) { return FX.win.isInside(path, r); });
    },

    refresh: async function () {
      try {
        if (isMock()) state = mockState();
        else if (bridgeUp()) state = await FX.Bridge.call('/fs/policy/get', {});
        else state = null;
      } catch (e) {
        /* גשר ישן בלי המדיניות: מתנהגים כפתוח */
        state = null;
      }
      schedule();
      return state;
    },

    set: async function (body) {
      if (isMock()) {
        mockCheck(body.password);
        mock.mode = body.mode; mock.roots = (body.roots || []).slice(); mock.minutes = body.unlockMinutes || 15; mock.until = 0;
        return P.refresh();
      }
      state = await FX.Bridge.call('/fs/policy/set', body);
      schedule();
      return state;
    },

    unlock: async function (password) {
      if (isMock()) { mockCheck(password); mock.until = Date.now() + mock.minutes * 60000; return P.refresh(); }
      state = await FX.Bridge.call('/fs/policy/unlock', { password: password });
      schedule();
      return state;
    },

    lock: async function () {
      if (isMock()) { mock.until = 0; return P.refresh(); }
      state = await FX.Bridge.call('/fs/policy/lock', {});
      schedule();
      return state;
    },

    /* ---------- מסך נעילה ---------- */
    showLock: function () {
      var shell = document.querySelector('.fx-shell');
      var box = document.getElementById('fx-lock');
      if (!shell || !box) return;
      /* כבר מוצג: לא בונים מחדש (בנייה מחדש מוחקת את מה שהוקלד ומאבדת פוקוס אחרי כל ספרה) */
      if (box.querySelector('#fx-lock-pw')) { shell.classList.add('is-locked'); return; }
      var mins = state && state.unlockMinutes || 15;
      box.innerHTML = '<div class="fx-lock-card">' +
        '<div class="fx-lock-ic">' + ic('lock') + '</div>' +
        '<h2>סייר הקבצים נעול</h2>' +
        '<p class="muted">כדי לפתוח אותו יש להזין את סיסמת המנהל. הסייר יינעל שוב אוטומטית כעבור ' + esc(mins) + ' דקות.</p>' +
        (state && state.fallback ? '<p class="muted small">המנהל הגביל את הסייר לתיקיות שאינן קיימות עוד, ולכן הוא נשאר נעול.</p>' : '') +
        '<form class="fx-lock-form" autocomplete="off">' +
        '<label class="field"><span class="field-label">סיסמת מנהל</span>' +
        '<input class="input" type="password" id="fx-lock-pw" autocomplete="current-password" dir="ltr" aria-describedby="fx-lock-err"></label>' +
        '<div class="form-msg" id="fx-lock-err" role="alert" hidden></div>' +
        '<button type="submit" class="btn btn-primary">פתיחה</button></form></div>';
      shell.classList.add('is-locked');
      var form = box.querySelector('form'), pw = box.querySelector('#fx-lock-pw'), err = box.querySelector('#fx-lock-err');
      form.addEventListener('submit', async function (ev) {
        ev.preventDefault();
        var btn = form.querySelector('button');
        btn.disabled = true; err.hidden = true;
        try {
          await P.unlock(pw.value);
          pw.value = '';
          P.hideLock();
          if (FX.app && FX.app.afterPolicy) await FX.app.afterPolicy();
        } catch (e) {
          err.textContent = FX.errorText(e); err.hidden = false;
          pw.select();
        }
        btn.disabled = false;
      });
      setTimeout(function () { try { pw.focus(); } catch (e) { /* כלום */ } }, 30);
    },
    hideLock: function () {
      var shell = document.querySelector('.fx-shell');
      if (shell) shell.classList.remove('is-locked');
      var box = document.getElementById('fx-lock');
      if (box) box.innerHTML = '';
    },

    /* ---------- טופס המנהל (מסך ההגדרות) ---------- */
    editorHtml: function () {
      if (!available()) {
        return '<div class="fx-set-card"><div class="fx-set-title">' + ic('lock') + 'נעילה והגבלה של הסייר</div>' +
          '<p class="muted">הגדרות הנעילה דורשות את תוכנת חסימת אוצריא. בלעדיה הסייר מוגבל ממילא לתיקיות שאישרתם.</p></div>';
      }
      var s = state || { mode: 'off', roots: [], unlockMinutes: 15, defaults: [], effective: 'open' };
      var chosen = (s.roots || []).map(function (r) { return String(r).toLowerCase(); });
      var defs = s.defaults || [];
      var defPaths = defs.map(function (d) { return d.path.toLowerCase(); });
      var rows = defs.map(function (d) { return rootRow(d.name, d.path, chosen.indexOf(d.path.toLowerCase()) >= 0); });
      (s.roots || []).forEach(function (r) { if (defPaths.indexOf(String(r).toLowerCase()) < 0) rows.push(rootRow('', r, true)); });
      function mode(v, title, hint) {
        return '<label class="fx-pol-mode"><input type="radio" name="fx-pol-mode" value="' + v + '"' + (s.mode === v ? ' checked' : '') + '>' +
          '<span><b>' + title + '</b><span class="muted small">' + hint + '</span></span></label>';
      }
      /* מצב נוכחי בצבע: ירוק = פתוח, אדום = נעול, כתום = מוגבל לתיקיות */
      var polTemp = s.tempUnlockSeconds > 0 && s.mode !== 'off';
      var status = 'מצב נוכחי: ' + (s.effective === 'locked' ? '<span class="pol-state bad"><i></i>נעול</span>' : s.effective === 'restricted' ? '<span class="pol-state mid"><i></i>מוגבל לתיקיות שנבחרו</span>' :
        '<span class="pol-state ok"><i></i>פתוח</span>' + (polTemp ? ' (המחשב פתוח זמנית)' : ''));
      var warn = (s.invalidRoots || []).length
        ? '<div class="form-msg">תיקיות שאינן קיימות עוד מדולגות: ' + esc(s.invalidRoots.join(', ')) + (s.fallback ? '. אין תיקיות תקפות, ולכן הסייר נעול.' : '') + '</div>' : '';
      return '<div class="fx-set-card fx-pol"><div class="fx-set-title">' + ic('lock') + 'נעילה והגבלה של הסייר</div>' +
        '<p class="muted small">שינוי ההגדרות דורש את סיסמת המנהל (או קוד השחזור). המדיניות נאכפת בגשר, ולכן גם תוסף אחר לא יוכל לעקוף אותה.</p>' +
        '<p class="small" id="fx-pol-status"><b>' + status + '</b></p>' + warn +
        (s.passwordConfigured === false ? '<div class="form-msg">עדיין לא הוגדרה סיסמת נעילה. יש להגדיר אותה בהגדרות מערכת ← נעילה.</div>' : '') +
        '<form id="fx-pol-form" autocomplete="off">' +
        '<fieldset class="fx-pol-fs"><legend>מצב</legend>' +
        mode('off', 'כבוי', 'הסייר פועל כרגיל') +
        mode('restricted', 'מוגבל לתיקיות', 'אפשר לעיין, לקרוא ולכתוב רק בתיקיות שנבחרו. כוננים ותיקיות אחרות אינם נגישים') +
        mode('locked', 'נעול עד הזנת סיסמה', 'הסייר לא זמין עד שמזינים את סיסמת המנהל') + '</fieldset>' +
        '<fieldset class="fx-pol-fs" id="fx-pol-roots"><legend>תיקיות מותרות (למצב מוגבל)</legend>' +
        '<p class="muted small">סמנו את התיקיות שמותר לפתוח. הבחירה חלה רק כשהמצב הוא "מוגבל לתיקיות".</p>' +
        '<div class="fx-pol-list">' + rows.join('') + '</div>' +
        '<div class="fx-pol-add"><input class="input" id="fx-pol-custom" dir="ltr" placeholder="C:\\Users\\Name\\Folder" aria-label="הוספת תיקייה מותאמת">' +
        '<button type="button" class="btn btn-tonal" data-pol="add">הוספת תיקייה</button></div></fieldset>' +
        '<div class="fx-pol-fs"><label class="field fx-pol-min"><span class="field-label">דקות פתוח אחרי הזנת סיסמה (מצב נעול)</span>' +
        '<input class="input" id="fx-pol-min" type="number" min="1" max="1440" value="' + esc(s.unlockMinutes || 15) + '" dir="ltr" aria-describedby="fx-pol-min-h"></label>' +
        '<p class="muted small" id="fx-pol-min-h">אחרי הזנת הסיסמה הסייר נשאר פתוח לזמן הזה ונועל את עצמו שוב. בין 1 ל-1440 דקות.</p></div>' +
        '<label class="field"><span class="field-label">סיסמת מנהל (נדרשת לשמירה)</span>' +
        '<input class="input" id="fx-pol-pw" type="password" dir="ltr" autocomplete="current-password" aria-describedby="fx-pol-err"></label>' +
        '<div class="form-msg" id="fx-pol-err" role="alert" hidden></div>' +
        '<div class="fx-set-actions"><button type="submit" class="btn btn-primary">שמירה</button>' +
        (s.mode === 'locked' && s.effective === 'open' ? '<button type="button" class="btn btn-tonal" data-pol="lock">נעילה מיידית</button>' : '') +
        '</div></form></div>';
    },

    bindEditor: function (root) {
      var form = root.querySelector('#fx-pol-form');
      if (!form) return;
      function showErr(m) { var er = root.querySelector('#fx-pol-err'); er.textContent = m; er.hidden = !m; }
      if (!root.__polBound) root.addEventListener('click', async function (ev) {
        var b = ev.target.closest('[data-pol]');
        if (!b) return;
        var list = root.querySelector('.fx-pol-list');
        if (b.dataset.pol === 'add') {
          var inp = root.querySelector('#fx-pol-custom');
          var v = (inp.value || '').trim().replace(/\//g, '\\').replace(/^"(.*)"$/, '$1');
          if (!/^([A-Za-z]:\\|\\\\)/.test(v)) { showErr('יש להזין נתיב מלא, למשל C:\\Users\\שם\\Desktop'); return; }
          showErr('');
          var dup = Array.prototype.find.call(list.querySelectorAll('input[data-path]'), function (x) { return x.dataset.path.toLowerCase() === v.toLowerCase(); });
          if (dup) dup.checked = true; else list.insertAdjacentHTML('beforeend', rootRow('', v, true));
          inp.value = '';
        } else if (b.dataset.pol === 'lock') {
          if (!(await FX.ui.confirm('נעילה מיידית', '<p>הסייר ייחסם עכשיו, וכדי לפתוח אותו שוב יצטרכו את סיסמת המנהל.</p>', 'נעילה', 'danger'))) return;
          try { await P.lock(); if (FX.app && FX.app.afterPolicy) await FX.app.afterPolicy(); }
          catch (e) { showErr(FX.errorText(e)); }
        }
      });
      root.__polBound = true;
      form.addEventListener('submit', async function (ev) {
        ev.preventDefault();
        showErr('');
        var modeEl = form.querySelector('input[name=fx-pol-mode]:checked');
        var roots = Array.prototype.map.call(form.querySelectorAll('.fx-pol-list input[type=checkbox]:checked'), function (x) { return x.dataset.path; });
        var mode = modeEl ? modeEl.value : 'off';
        if (mode === 'restricted' && !roots.length) { showErr('במצב מוגבל יש לבחור לפחות תיקייה אחת'); return; }
        var minutes = parseInt(form.querySelector('#fx-pol-min').value, 10);
        if (!(minutes >= 1 && minutes <= 1440)) { showErr('מספר הדקות חייב להיות בין 1 ל-1440'); return; }
        if (!form.querySelector('#fx-pol-pw').value) { showErr('יש להקליד את סיסמת המנהל כדי לשמור שינויים'); return; }
        if (mode !== (state && state.mode || 'off')) {
          var msgs = {
            locked: ['נעילת הסייר', '<p>הסייר ייחסם עכשיו לכל מי שאין לו את סיסמת המנהל.</p>', 'נעילה'],
            off: ['כיבוי ההגבלה', '<p>הסייר יחזור להיות פתוח לגמרי, בלי נעילה ובלי הגבלה לתיקיות.</p>', 'כיבוי'],
            restricted: ['הגבלה לתיקיות', '<p>הסייר יאפשר גישה רק לתיקיות שסימנתם ברשימה.</p>', 'הגבלה']
          }[mode];
          if (msgs && !(await FX.ui.confirm(msgs[0], msgs[1], msgs[2], mode === 'off' ? 'danger' : undefined))) return;
        }
        var btn = form.querySelector('button[type=submit]');
        btn.disabled = true;
        try {
          await P.set({ password: form.querySelector('#fx-pol-pw').value, mode: mode, roots: roots, unlockMinutes: minutes });
          form.querySelector('#fx-pol-pw').value = '';
          FX.ui && FX.ui.toast(mode === 'locked' ? 'הסייר ננעל. מעכשיו נדרשת סיסמה כדי לפתוח אותו' : mode === 'restricted' ? 'הסייר הוגבל לתיקיות שנבחרו' : 'ההגבלה כובתה, הסייר פתוח', 'success');
          root.innerHTML = P.editorHtml();
          P.bindEditor(root);
          if (FX.app && FX.app.afterPolicy) await FX.app.afterPolicy();
        } catch (e) {
          showErr(FX.errorText(e));
        }
        btn.disabled = false;
      });
    }
  };

  function rootRow(name, path, checked) {
    return '<label class="fx-pol-root"><input type="checkbox" data-path="' + esc(path) + '"' + (checked ? ' checked' : '') + '>' +
      '<span>' + (name ? '<b>' + esc(t(name)) + '</b> ' : '') + '<span class="fx-pol-path" dir="ltr">' + esc(path) + '</span></span></label>';
  }

  /* נעילה אוטומטית בתום הזמן שנפתח */
  function schedule() {
    clearTimeout(timer);
    if (state && state.unlockedSeconds > 0 && state.mode !== 'off') {
      timer = setTimeout(async function () {
        await P.refresh();
        if (P.locked()) P.showLock();
      }, Math.min(state.unlockedSeconds * 1000 + 400, 2147000000));
    }
  }
})();
