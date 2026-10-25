/* sections/about.js - plugin and bridge info, download link */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  /* #59: אין בדיקת עדכונים בתוך התוסף — היא קיימת בגשר ("אחת שאלתי" במגש ← אודות) */

  SS.sections.push({
    id: 'about', title: 'אודות', icon: 'info',
    mount: function (body) {
      body.innerHTML =
        '<div class="card about-hero"><div class="about-ic">' + ui.icon('computer') + '</div><div>' +
        '<img src="icon/brand.svg" alt="אחת שאלתי" width="144" height="144" style="display:block;margin:0 auto 12px">' +
        '<h3>הגדרות מערכת</h3><p class="muted">גרסה ' + ui.esc(SS.VERSION) + ' · מאת חד בדרא, יאיר דניאל</p>' +
        '<p>כל הגדרות המחשב מתוך אוצריא: מסך, שמע, חשמל, שעה ותאריך, מקלדת, עכבר, מדפסות, USB, בלוטות׳ ורשת — ומצב נעילה שהופך את המחשב לעמדת לימוד באוצריא ובתוכנות הלימוד בלבד.</p></div></div>' +
        '<div class="card about-memorial"><p>לעילוי נשמת הנערה הצדקנית</p><h3>אסתר גיטה דינר בת רבי מיכאל שלום הלוי שיבדלט"א</h3><p>תהא נשמתה צרורה בצרור החיים. תנצב"ה</p></div>' +
        '<div class="card"><div class="card-title">אחת שאלתי</div><div data-slot="bridge">' + ui.loading() + '</div>' +
        '</div>' +
        '<div class="card"><div class="card-title">התקנת הגשר</div><ol class="steps">' +
        '<li>מורידים את <b dir="ltr">OtzariaBridge.exe</b> מהקישור.</li>' +
        '<li>מפעילים פעם אחת. הגשר מעתיק את עצמו ל-<span dir="ltr">%LOCALAPPDATA%\\OtzariaBridge</span>, נרשם להפעלה אוטומטית ומופיע כאייקון ליד השעון.</li>' +
        '<li>הגשר מאזין רק למחשב המקומי (127.0.0.1) ואינו נגיש מהרשת.</li>' +
        '<li>להסרה: <code dir="ltr">OtzariaBridge.exe --uninstall</code></li></ol>' +
        '<p class="muted small">עמוד ההוצאות (הגרסה האחרונה של הגשר): <bdi dir="ltr" class="path">' + ui.esc(SS.BRIDGE_URL) + '</bdi></p>' +
        '<div class="row-actions full"><button class="btn btn-primary" data-act="dl">' + ui.icon('download') + 'הורדת הגשר</button>' +
        '<button class="btn btn-tonal" data-act="copy">' + ui.icon('link') + 'העתקת הקישור</button></div></div>';
      body.querySelector('[data-act=copy]').addEventListener('click', function () {
        SS.copyText(SS.BRIDGE_URL).then(function (ok) { ui.toast(ok ? 'הקישור הועתק' : 'ההעתקה נכשלה: ' + SS.BRIDGE_URL, ok ? 'success' : 'error'); });
      });
      body.querySelector('[data-act=dl]').addEventListener('click', function () { SS.openUrl(SS.BRIDGE_URL); });
    },
    load: async function (body) {
      var slot = body.querySelector('[data-slot=bridge]');
      var h = null;
      try { h = await SS.bridge.probe(); } catch (e) { h = null; }
      SS.setStatus(!!h);
      if (!h) {
        slot.innerHTML = '<div class="state-box error">' + ui.icon('warning') + '<div><b>הגשר אינו מחובר</b></div></div>';
        return;
      }
      slot.innerHTML = '<dl class="info-grid">' +
        '<dt>מצב</dt><dd><span class="chip ok">מחובר</span>' + (SS.bridge.isMock ? ' <span class="chip">תצוגה מקדימה</span>' : '') + '</dd>' +
        '<dt>גרסת הגשר</dt><dd dir="ltr">' + ui.esc(h.version) + '</dd>' +
        '<dt>פורט</dt><dd dir="ltr">127.0.0.1:' + ui.esc(h.port || SS.bridge.port) + '</dd>' +
        '<dt>מערכת</dt><dd dir="ltr">' + ui.esc(h.platform || '') + '</dd>' +
        '<dt>מצב נעילה</dt><dd>' + (h.kioskActive ? 'פעיל' : 'לא פעיל') + '</dd></dl>' +
        '<div class="setting-row"><div class="setting-text"><b>שליחת תקלות אוטומטית</b><span class="muted">קריסות ושגיאות של הגשר נשלחות למפתחת דרך מערכת הדיווח של אוצריא, בלי סיסמאות. בלי חיבור – יישלחו כשיחזור.</span></div>' + ui.switchHtml('ss-autoreport', true, 'שליחת תקלות אוטומטית') + '</div>';
      var sw = slot.querySelector('#ss-autoreport');
      /* מסך "מה נשלח" — בסיס מינימלי: ממתינים ונשלחו, תוכן מלא, מחיקה לפני שליחה */
      var sentBox = document.createElement('div');
      slot.appendChild(sentBox);
      async function loadSent() {
        var l = null;
        try { l = await SS.bridge.call('/diag/list', {}, { timeoutMs: 15000 }); } catch (e) { sentBox.innerHTML = ''; return; }
        var items = (l && l.items) || [];
        sentBox.innerHTML = '<div class="card-title">מה נשלח</div>' + (items.length ? items.map(function (x) {
          return '<details class="setting-row"><summary><span class="chip' + (x.sent ? ' ok' : '') + '">' + (x.sent ? 'נשלח' : 'ממתין') + '</span> <bdi dir="ltr">' +
            ui.esc(String(x.date || '').replace('T', ' ')) + '</bdi> · ' + ui.esc(x.kind || '') +
            (x.sent ? '' : ' <button class="btn btn-tonal" data-del="' + ui.esc(x.id) + '">' + ui.icon('close') + 'מחיקה</button>') +
            '</summary><pre class="path" dir="auto" style="white-space:pre-wrap">' + ui.esc(x.text || '') + '</pre></details>';
        }).join('') : '<p class="muted">אין דיווחים.</p>');
        Array.prototype.forEach.call(sentBox.querySelectorAll('[data-del]'), function (b) {
          b.addEventListener('click', function (e) {
            e.preventDefault();
            SS.bridge.call('/diag/delete', { ids: [b.getAttribute('data-del')] }, { timeoutMs: 15000 }).then(loadSent, loadSent);
          });
        });
      }
      loadSent();
      SS.bridge.call('/diag/pending', {}, { timeoutMs: 15000 }).then(function (p) { if (p) ui.setSwitch(sw, p.enabled !== false); }).catch(function () { /* גשר ישן */ });
      sw.addEventListener('click', function () {
        var on = sw.getAttribute('aria-checked') !== 'true';
        SS.bridge.call('/diag/ack', { ids: [], enabled: on }, { timeoutMs: 15000 })
          .then(function (x) { ui.setSwitch(sw, !!(x && x.enabled)); })
          .catch(function () { ui.toast('נדרש גשר בגרסה חדשה יותר', 'info'); });
      });
    }
  });
})();
