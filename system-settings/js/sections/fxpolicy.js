/* sections/fxpolicy.js - #15 (13.10.2026): הגדרות הנעילה של סייר הקבצים, אותן הגדרות כמו בסייר (/fs/policy/*) */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  SS.sections.push({
    id: 'fxpolicy', title: 'נעילת סייר הקבצים', icon: 'lock',
    mount: function (body) { body.innerHTML = ui.loading('טוען…'); },
    load: load
  });

  var MODES = [['off', 'פתוח (ללא הגבלה)'], ['restricted', 'מוגבל לתיקיות שנבחרו'], ['locked', 'נעול (נפתח בסיסמה לזמן קצוב)']];
  var EFFECTIVE_LABELS = { open: 'פתוח', restricted: 'מוגבל לתיקיות שנבחרו', locked: 'נעול' };

  async function load(body) {
    var st;
    try { st = await SS.bridge.call('/fs/policy/get', {}); }
    catch (e) { body.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    var ks = {}; try { ks = await SS.bridge.call('/kiosk/status', {}) || {}; } catch (e) { /* כלום */ }
    var roots = (st.roots || []).slice();
    var cand = (st.defaults || []).map(function (d) { return { name: d.name, path: d.path }; });
    roots.forEach(function (r) { if (!cand.some(function (c) { return c.path.toLowerCase() === r.toLowerCase(); })) cand.push({ name: '', path: r }); });
    var fields = [
      { name: 'mode', label: 'מצב הסייר', type: 'select', value: st.mode || 'off', options: MODES.map(function (m) { return { value: m[0], label: m[1] }; }) },
      { type: 'note', label: 'תיקיות מותרות במצב מוגבל:' }
    ];
    cand.forEach(function (c, i) {
      fields.push({ name: 'r' + i, type: 'checkbox', label: (c.name ? c.name + ' — ' : '') + c.path, value: roots.some(function (r) { return r.toLowerCase() === c.path.toLowerCase(); }) });
    });
    fields.push({ name: 'extra', label: 'תיקייה נוספת (נתיב מלא, לא חובה)', dir: 'ltr', placeholder: 'D:\\לימוד' });
    fields.push({ name: 'min', label: 'דקות פתיחה בסיסמה', value: String(st.unlockMinutes || 15), dir: 'ltr' });
    /* C5: הועבר מהגדרות הנעילה הכלליות */
    fields.push({ name: 'xls', type: 'checkbox', label: 'אפשור צפייה בקובצי ‪Excel (xlsx)‬ בסייר הקבצים', value: !!ks.allowExcelDocs, hint: 'ברירת מחדל: בסייר הקבצים אפשר לצפות ולפתוח רק מסמכי Word. מצגות PowerPoint חסומות תמיד ואין אפשרות להתיר אותן; סימון כאן מתיר גם גיליונות Excel' });
    fields.push({ name: 'pw', label: 'סיסמת נעילה', type: 'password', dir: 'ltr' });
    body.innerHTML = '<div class="card"><div class="card-title">נעילת סייר הקבצים</div>' +
      '<p class="muted">אותן הגדרות שבסייר הקבצים: מה מותר לגלוש בסייר. מצב נוכחי: ' + (function (m) { return m === 'locked' ? '<span class="pol-state bad"><i></i>נעול</span>' : m === 'restricted' ? '<span class="pol-state mid"><i></i>מוגבל לתיקיות שנבחרו</span>' : '<span class="pol-state ok"><i></i>פתוח</span>'; })(st.effective || st.mode) + '</p>' +
      '<p><button class="btn" type="button" data-act="open-fx">' + ui.icon('lock') + 'פתיחת הגדרות הנעילה בסייר הקבצים</button></p><div data-slot="f"></div></div>';
    var ofx = body.querySelector('[data-act=open-fx]');
    if (ofx) ofx.addEventListener('click', function () {
      try { window.Otzaria.call('plugin.openOther', { pluginId: 'com.chadbedera.fileexplorer', param: { view: 'settings', cat: 'security' } }); } catch (e) { /* כלום */ }
    });
    await ui.inlineForm(body.querySelector('[data-slot=f]'), {
      fields: fields,
      validate: function (x) {
        if (!x.pw) return 'יש להקליד את סיסמת הנעילה';
        if (!(+x.min >= 1 && +x.min <= 1440)) return 'מספר הדקות חייב להיות בין 1 ל-1440';
        if (x.extra.trim() && !/^([A-Za-z]:\\|\\\\)/.test(x.extra.trim())) return 'יש להזין נתיב מלא';
        return '';
      },
      onSubmit: async function (x) {
        var sel = cand.filter(function (c, i) { return x['r' + i]; }).map(function (c) { return c.path; });
        if (x.extra.trim()) sel.push(x.extra.trim().replace(/\//g, '\\'));
        if (x.mode === 'restricted' && !sel.length) throw new Error('במצב מוגבל יש לבחור לפחות תיקייה אחת');
        await SS.bridge.call('/fs/policy/set', { password: x.pw, mode: x.mode, roots: sel, unlockMinutes: +x.min });
        if (!!x.xls !== !!ks.allowExcelDocs) await SS.bridge.call('/kiosk/settings', { password: x.pw, allowExcelDocs: !!x.xls });
      }
    });
    ui.toast('מדיניות הסייר נשמרה', 'success');
    load(body);
  }
})();
