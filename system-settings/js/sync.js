/* sync.js — #69: מה שרק תוסף בתוך אוצריא יכול לעשות ולהעביר לגשר: זמני היום לשומר המסך (calendar.*)
   וזמני הנעילה בלוח השנה (publishedData.*). משותף לדף התוסף (app.js) ולמופע הרקע (background.html),
   שרץ בעליית אוצריא כשהמשתמש אישר "הרצה ברקע" — כך הזמנים מתעדכנים גם בלי לפתוח את הגדרות מערכת. */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  SS.sync = {};
  function unwrapOtzaria(r) { return r && r.data !== undefined ? r.data : r; }

  SS.sync.zmanim = async function () {
    if (!window.Otzaria) return;
    var st;
    try { st = await SS.bridge.call('/kiosk/status'); } catch (e) { return; }
    var city = (st.zmanimCity || '').trim();
    var loc = (st.zmanimLocation || '').trim();
    var args = { inIsrael: !st.zmanimDiaspora };
    if (city) args.city = city;
    else {
      var m = /^(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)$/.exec(loc);
      if (m) { args.lat = +m[1]; args.lng = +m[2]; }
      else return; /* אין עיר ואין קואורדינטות — אין מה לשלוח */
    }
    var dailyTimes = null, jewishDate = null, jewishDateTomorrow = null;
    try { dailyTimes = unwrapOtzaria(await window.Otzaria.call('calendar.getDailyTimes', args)); } catch (e) { dailyTimes = null; }
    try { jewishDate = unwrapOtzaria(await window.Otzaria.call('calendar.getJewishDate', {})); } catch (e) { jewishDate = null; }
    try {
      var t = new Date(); t.setDate(t.getDate() + 1);
      jewishDateTomorrow = unwrapOtzaria(await window.Otzaria.call('calendar.getJewishDate', { date: t.toISOString().slice(0, 10) }));
    } catch (e) { jewishDateTomorrow = null; }
    if (!dailyTimes && !jewishDate) return;
    try {
      await SS.bridge.call('/kiosk/zmanim-cache', {
        dailyTimes: dailyTimes || {}, jewishDate: jewishDate || {}, jewishDateTomorrow: jewishDateTomorrow || {}
      });
    } catch (e) { /* המטמון אינו קריטי; ננסה שוב בפעם הבאה */ }
  };

  var EVT_PREFIX = 'achat-shaalti:lock:';
  function isoWithOffset(d) {
    var off = -d.getTimezoneOffset(), sg = off >= 0 ? '+' : '-', a = Math.abs(off);
    function p(n) { return (n < 10 ? '0' : '') + n; }
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + 'T' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':00' +
      sg + p(Math.floor(a / 60)) + ':' + p(a % 60);
  }
  function isoLocal(d) { return isoWithOffset(d).slice(0, 19); }
  function hhmm(d) { return (d.getHours() < 10 ? '0' : '') + d.getHours() + ':' + (d.getMinutes() < 10 ? '0' : '') + d.getMinutes(); }
  function lockTitle(w) {
    var en = window.I18N && I18N.isEn && I18N.isEn();
    return (en ? 'Achat Shaalti lock ' : 'נעילת אחת שאלתי ') + hhmm(w.start) + '–' + hhmm(w.end);
  }
  function lockWindows(sc) {
    var wins = [{ start: sc.start, end: sc.end }].concat(sc.extra || []);
    var days = (sc.days && sc.days.length) ? sc.days : [0, 1, 2, 3, 4, 5, 6];
    var out = [];
    for (var i = 0; i < 7; i++) {
      var day = new Date(); day.setHours(0, 0, 0, 0); day.setDate(day.getDate() + i);
      if (days.indexOf(day.getDay()) < 0) continue;
      wins.forEach(function (w, wi) {
        var ms = /^(\d{1,2}):(\d{2})$/.exec(w.start || ''), me = /^(\d{1,2}):(\d{2})$/.exec(w.end || '');
        if (!ms || !me) return;
        var s = new Date(day); s.setHours(+ms[1], +ms[2], 0, 0);
        var e = new Date(day); e.setHours(+me[1], +me[2], 0, 0);
        if (e <= s) e.setDate(e.getDate() + 1); /* חוצה חצות */
        out.push({ key: EVT_PREFIX + i + ':' + wi, start: s, end: e });
      });
    }
    return out;
  }
  SS.sync.lockCalendar = async function () {
    if (!window.Otzaria) return;
    var st; try { st = await SS.bridge.call('/kiosk/status'); } catch (e) { return; }
    var sc = st.schedule || {};
    var want = (sc.showInCalendar && sc.enabled) ? lockWindows(sc) : [];
    var wantKeys = {};
    for (var i = 0; i < want.length; i++) {
      wantKeys[want[i].key] = 1;
      try {
        await window.Otzaria.call('publishedData.upsert', {
          type: 'calendar.event', scope: 'global', key: want[i].key,
          /* #69 (בדיקת לוח השנה מול קוד אוצריא, plugin_calendar_adapter.dart): אוצריא מציגה רק את התאריך ואת הכותרת —
             השעה ו-endsAt אינם מוצגים, ולכן השעות נכנסות לכותרת. התאריך נקבע מ-DateTime.tryParse, שממיר זמן עם
             היסט ל-UTC (00:30+03:00 היה נופל ביום הקודם) — לכן שולחים זמן מקומי בלי היסט. importance שאינו
             high/medium/low היה מתווסף לתיאור ("(normal)") — לכן לא נשלח. */
          payload: { title: lockTitle(want[i]), startsAt: isoLocal(want[i].start), endsAt: isoLocal(want[i].end), source: (window.I18N && I18N.isEn && I18N.isEn()) ? 'Achat Shaalti' : 'אחת שאלתי' }
        });
      } catch (e) { /* פרסום אירוע בודד אינו קריטי */ }
    }
    /* הסרת אירועים שלנו שכבר לא רלוונטיים (כיבוי, שינוי שעות, או ישנים) */
    try {
      var own = unwrapOtzaria(await window.Otzaria.call('publishedData.listOwn')) || [];
      for (var j = 0; j < own.length; j++) {
        var k = own[j] && own[j].key;
        if (typeof k === 'string' && k.indexOf(EVT_PREFIX) === 0 && !wantKeys[k])
          try { await window.Otzaria.call('publishedData.remove', { type: 'calendar.event', scope: 'global', key: k }); } catch (e) { /* לא קריטי */ }
      }
    } catch (e) { /* listOwn לא זמין — מדלגים */ }
  };

})();
