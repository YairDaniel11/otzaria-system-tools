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
        '<div class="card"><div class="card-title">דיווחים</div>' + (window.Feedback && window.Feedback.crashToggleHtml ? window.Feedback.crashToggleHtml() : '') + '</div>' +
        '<div class="card"><div class="card-title">אחת שאלתי</div><div data-slot="bridge">' + ui.loading() + '</div>' +
        '</div>' +
        '<div class="card"><div class="card-title">יומן שינויים</div>' + (window.renderChangelog ? window.renderChangelog(ui.esc) : '') + '</div>' +
        '<div class="card"><div class="card-title">עדכון מקומי (בלי גיטהאב)</div>' +
        '<p class="muted">למי שגיטהאב חסום ואין לו רשת: מורידים בדרך אחרת (ממחשב אחר, דוא״ל וכדומה) ארבעה קבצים מהוצאה אחת: <b dir="ltr">OtzariaBridge.exe</b>, <b dir="ltr">SHA256SUMS.txt</b>, <b dir="ltr">SHA256SUMS.txt.sig</b>, <b dir="ltr">BRIDGE_VERSION.txt</b>. בוחרים את כולם יחד. הגשר מאמת את החתימה ואת ה-SHA256 לפני ההתקנה, ודוחה כל חבילה בלי חתימה תקפה או בגרסה שאינה חדשה יותר.</p>' +
        '<div class="row-actions full"><button class="btn btn-tonal" data-act="local-pick">' + ui.icon('download') + 'בחירת קבצי עדכון</button>' +
        '<button class="btn btn-primary" data-act="local-install" disabled>' + ui.icon('shield') + 'אימות והתקנה</button></div>' +
        '<div data-slot="local-files" class="muted small"></div></div>' +
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
      /* עדכון מקומי: הגשר פותח חלון בחירה, מאמת חתימה ומתקין */
      var picked = null;
      var pickBtn = body.querySelector('[data-act=local-pick]');
      var instBtn = body.querySelector('[data-act=local-install]');
      var filesSlot = body.querySelector('[data-slot=local-files]');
      function nm(p) { return p ? ui.esc(String(p).split(/[\/]/).pop()) : '<b>חסר</b>'; }
      pickBtn.addEventListener('click', function () {
        SS.bridge.call('/sys/update-local-pick', {}, { timeoutMs: 310000 }).then(function (r) {
          if (!r || r.cancelled) return;
          picked = r;
          filesSlot.innerHTML = '<ul dir="ltr"><li>exe: ' + nm(r.exe) + '</li><li>sums: ' + nm(r.sums) + '</li><li>sig: ' + nm(r.sig) + '</li><li>version: ' + nm(r.bridgeVer) + '</li></ul>' +
            (r.complete ? '' : '<p>חסרים קבצים - בחרו את כל ארבעת הקבצים יחד.</p>');
          instBtn.disabled = !r.complete;
        }).catch(function (e) { ui.toast(ui.errText(e), 'error'); });
      });
      instBtn.addEventListener('click', function () {
        if (!picked || !picked.complete) return;
        instBtn.disabled = true;
        SS.bridge.call('/sys/update-local-install', { paths: picked.paths }, { timeoutMs: 180000 }).then(function (r) {
          ui.toast(r && r.started ? 'החתימה אומתה. מתקין גרסה ' + r.version + ' והגשר יופעל מחדש...' : ((r && r.message) || 'לא הותקן'), r && r.started ? 'success' : 'info');
        }).catch(function (e) { ui.toast(ui.errText(e), 'error'); instBtn.disabled = false; });
      });
    },
    load: async function (body) {
      var slot = body.querySelector('[data-slot=bridge]');
      var h = null;
      try { h = await SS.bridge.probe(); } catch (e) { h = null; }
      SS.setStatus(!!h);
      if (!h) {
        slot.innerHTML = '<div class="state-box">' + ui.icon('info') + '<div><b>זמין כשאחת שאלתי פועלת</b></div></div>';
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
          .catch(function (e) {
            /* 04.11: ההודעה הקודמת ("נדרש גשר בגרסה חדשה יותר") הוצגה בכל כישלון, גם כשהתוסף לא מצומד */
            var c = e && e.code;
            ui.toast(c === 'not_found' ? 'הגשר המותקן ישן ואינו תומך בזה — יש לעדכן את אחת שאלתי' : ui.errText(e), 'info');
          });
      });
    }
  });
})();
