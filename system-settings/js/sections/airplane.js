/* sections/airplane.js - airplane mode: all radios off, and back to the previous state */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  SS.sections.push({
    id: 'airplane', title: 'מצב טיסה', icon: 'airplane', group: 'network', network: true,
    keywords: 'מצב טיסה משדרים אלחוטי airplane flight radio',
    mount: function (body) {
      body.innerHTML = '<div class="card"><div data-slot="ap">' + ui.loading() + '</div></div>';
    },
    load: load
  });

  async function load(body) {
    var slot = body.querySelector('[data-slot=ap]');
    var d;
    try { d = await SS.bridge.call('/sys/airplane/get'); }
    catch (e) { slot.innerHTML = ui.errorBox(e, function () { load(body); }); return; }
    if (!d.supported) {
      slot.innerHTML = '<div class="state-box">' + ui.icon('info') + '<div><b>אין משדרים במחשב זה</b><p class="muted">לא נמצאו Wi-Fi או בלוטות׳ שאפשר לכבות.</p></div></div>';
      return;
    }
    slot.innerHTML =
      '<div class="setting-row big"><div class="setting-text"><b>מצב טיסה</b><span class="muted">מכבה את כל התקשורת האלחוטית: Wi-Fi, בלוטות׳ וסלולר. בכיבוי חוזרים המשדרים שהיו דולקים.</span></div>' +
      '<span class="chip state-chip ' + (d.on ? 'ok' : 'off') + '"><i class="dot"></i>' + (d.on ? 'פעיל' : 'כבוי') + '</span>' + ui.switchHtml('sw-ap', d.on, 'מצב טיסה') + '</div>' +
      '<ul class="list">' + (d.radios || []).map(function (r) {
        return '<li class="list-item"><span class="li-icon">' + ui.icon(r.kind === 'bluetooth' ? 'bluetooth' : r.kind === 'wifi' ? 'wifi' : 'device') + '</span>' +
          '<span class="li-text"><b>' + ui.esc(r.name) + '</b></span><span class="chip state-chip ' + (r.on ? 'ok' : 'off') + '"><i class="dot"></i>' + (r.on ? 'דולק' : 'כבוי') + '</span></li>';
      }).join('') + '</ul>';
    slot.querySelector('#sw-ap').addEventListener('click', async function () {
      var b = this; b.disabled = true;
      try { await SS.bridge.call('/sys/airplane/set', { on: !d.on }, { timeoutMs: 30000 }); ui.toast(!d.on ? 'מצב טיסה הופעל' : 'מצב טיסה כובה', 'success'); }
      catch (e) { ui.error(e, 'מצב טיסה'); }
      load(body);
    });
  }
})();
