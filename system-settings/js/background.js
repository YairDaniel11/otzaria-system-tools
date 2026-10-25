/* background.js — #69: מופע הרקע של "הגדרות מערכת". עושה עבודה קצרה ומכבה את עצמו (plugin.backgroundDone).
   לא מציג ממשק ולא מבקש צימוד: אם התוסף עוד לא צומד לגשר — לא עושים כלום. */
(function () {
  'use strict';
  var O = window.Otzaria;
  if (!O) return;
  var SS = window.SS;
  SS.bridge.noPair = true;
  var running = null;

  async function work(what) {
    try {
      var tok = await O.call('storage.get', { key: 'bridgeToken' });
      tok = tok && tok.data !== undefined ? tok.data : tok;
      if (typeof tok !== 'string' || tok.length < 32) return;      /* לא צומד — בלי חלון צימוד מהרקע */
      try { if (window.I18N && I18N.init) await I18N.init(); } catch (e) { /* כותרות בעברית */ }
      if (!(await SS.bridge.probe())) return;                        /* הגשר לא רץ */
      await SS.sync.zmanim();
      if (what !== 'city') await SS.sync.lockCalendar();
    } catch (e) { /* עבודת רקע אינה קריטית */ }
  }
  function run(what) {
    if (running) return running;
    running = work(what).finally(function () {
      running = null;
      try { O.call('plugin.backgroundDone', {}); } catch (e) { /* כלום */ }
    });
    return running;
  }

  O.on('plugin.boot', function () { run('startup'); });
  O.on('calendar.city_changed', function () { run('city'); });
})();

/* 4ד: לחיצה על "ספר" של הגדרות מערכת בספרייה כשהדף סגור — פותחים את התוסף במסך המתאים */
(function () {
  var O = window.Otzaria;
  if (!O || !O.on) return;
  O.on('library.providerBook.openRequested', function (ev) {
    var id = ev && (ev.id || (ev.data && ev.data.id));
    if (id) { try { O.call('plugin.openSelf', { param: { section: id } }); } catch (e) { /* כלום */ } }
  });
})();
