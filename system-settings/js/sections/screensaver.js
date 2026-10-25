/* sections/screensaver.js - טאב "שומר מסך": מסך מנוחה, זמן המתנה, זמני היום (עיר/מיקום, אילו זמנים,
   עמודה בימין/שמאל) ויום השנה. כל ההגדרות מוצגות ישירות בעמוד — לא מאחורי כפתור.
   השדות והשמירה משותפים עם kiosk.js (SS.screensaverFields / SS.screensaverPayload). */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  SS.sections.push({
    id: 'screensaver', title: 'שומר מסך', icon: 'clock',
    mount: function (body) { body.innerHTML = ui.loading('טוען הגדרות שומר מסך…'); },
    load: load
  });

  async function load(body) {
    var st;
    try { st = await SS.bridge.call('/kiosk/status'); }
    catch (e) { body.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    var built = await SS.screensaverFields(st);
    var fields = built.fields.concat([
      { name: 'pw', label: 'סיסמת נעילה (אם הוגדרה)', type: 'password', dir: 'ltr' }
    ]);
    body.innerHTML =
      '<div class="card"><div class="card-title">שומר מסך ומסך מנוחה</div>' +
      '<p class="muted">מסך המנוחה מציג שעה ותאריך עברי במרכז; זמני היום בעמודה בצד שבוחרים כאן, וההקדשה והכיתוב האישי בצד שמולם — קבועה או עמודה עם נר, לבחירתכם.</p>' +
      '<form class="ss-form" novalidate><div class="dlg-fields">' + ui.fieldsHtml(fields) + '</div>' +
      '<div class="row-actions"><button class="btn btn-primary btn-lg" type="submit">' + ui.icon('check') + 'שמירה</button>' +
      (st.active ? '<button class="btn btn-lg ss-unlock" type="button">' + ui.icon('unlock') + 'ביטול הנעילה</button>' : '') + '</div>' +
      '</form></div>';
    var form = body.querySelector('form');
    var sel = form.querySelector('[name=zcity]'), loc = form.querySelector('[name=zloc]');
    if (sel && loc) sel.addEventListener('change', function () {
      var c = built.cityByName[sel.value];
      if (c && typeof c.lat === 'number' && typeof c.lng === 'number') loc.value = c.lat + ', ' + c.lng;
    });
    /* D1: ביטול הנעילה ישירות מהגדרות השומר (עם הסיסמה שבשדה למטה, או קוד שחזור) */
    var unl = form.querySelector('.ss-unlock');
    if (unl) unl.addEventListener('click', async function () {
      var pw = (form.querySelector('[name=pw]').value || '').trim();
      if (!pw) { ui.toast('יש להקליד את סיסמת הנעילה בשדה הסיסמה', 'error'); return; }
      unl.disabled = true;
      try {
        await SS.bridge.call('/kiosk/disable', { password: /^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){2}$/.test(pw) ? pw.replace(/-/g, '') : pw }, { timeoutMs: 30000 });
        ui.toast('הנעילה בוטלה', 'success'); load(body);
      } catch (err) { ui.error(err, 'ביטול הנעילה נכשל'); unl.disabled = false; }
    });
    form.addEventListener('submit', async function (e) {
      e.preventDefault();
      var x = ui.fieldValues(form, fields);
      var btn = form.querySelector('button[type=submit]');
      btn.disabled = true;
      try {
        var payload = SS.screensaverPayload(x);
        payload.password = x.pw;
        await SS.bridge.call('/kiosk/settings', payload);
        ui.toast('הגדרות שומר המסך נשמרו', 'success');
        if (SS.pushZmanimNow) SS.pushZmanimNow();
        load(body);
      } catch (err) { ui.error(err, 'השמירה נכשלה'); btn.disabled = false; }
    });
  }
})();
