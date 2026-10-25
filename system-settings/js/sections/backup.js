/* sections/backup.js - גיבוי ושחזור הגדרות (חבילה [ב], מימוש מינימלי): הגדרות הגשר לקובץ JSON בשולחן העבודה */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  async function doExport() {
    try {
      var r = await SS.bridge.call('/sys/settings-backup/export', {}, { timeoutMs: 30000 });
      ui.toast('הגיבוי נשמר: ' + ui.isolateLtr(r.path), 'success');
    } catch (e) { ui.error(e, 'הגיבוי נכשל'); }
  }

  async function doImport() {
    var v = await ui.dialog({
      title: 'שחזור הגדרות', icon: 'info',
      body: '<p>הדביקו את הנתיב המלא לקובץ הגיבוי (‎.json‎). סיסמאות וקודי שחזור אינם נשמרים בגיבוי ואינם משתנים.</p>',
      fields: [
        { name: 'path', label: 'נתיב קובץ הגיבוי', dir: 'ltr' },
        { name: 'pw', label: 'סיסמת נעילה (אם הוגדרה)', type: 'password', dir: 'ltr' }
      ],
      actions: [{ label: 'ביטול', value: false }, { label: 'שחזור', value: true, kind: 'primary' }],
      validate: function (x) { return x.path ? '' : 'יש להזין נתיב'; }
    });
    if (!v) return;
    try {
      var r = await SS.bridge.call('/sys/settings-backup/import', { path: v.path.replace(/^"|"$/g, ''), password: v.pw }, { timeoutMs: 30000 });
      ui.toast('שוחזרו ' + r.restored + ' הגדרות', 'success');
    } catch (e) { ui.error(e, 'השחזור נכשל'); }
  }

  SS.sections.push({
    id: 'backup', title: 'גיבוי ושחזור', icon: 'download',
    mount: function (body) {
      body.innerHTML = '<div class="card"><div class="card-title">גיבוי ושחזור הגדרות</div>' +
        '<p class="muted">הגדרות הגשר (נעילה, שומר מסך, תוכנות, זמנים) נשמרות לקובץ אחד בשולחן העבודה. סיסמאות, קודים וצימודים לא נכללים.</p>' +
        '<div class="row-actions full"><button class="btn btn-primary" data-act="exp">' + ui.icon('download') + 'גיבוי עכשיו</button>' +
        '<button class="btn btn-tonal" data-act="imp">' + ui.icon('refresh') + 'שחזור מקובץ</button></div></div>';
      body.querySelector('[data-act=exp]').addEventListener('click', doExport);
      body.querySelector('[data-act=imp]').addEventListener('click', doImport);
    }
  });
})();
