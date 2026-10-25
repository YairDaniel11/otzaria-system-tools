/* sdk-extras.js - הרשאות SDK של אוצריא 0.9.97+ (ROADMAP "חובה לפני אריזה").
   כל קריאה עטופה ב-try: באוצריא ישנה, או כשהמשתמש כיבה הרשאה, פשוט לא קורה כלום.
   משותף לשני התוספים (עותק זהה ב-system-settings/js/sdk-extras.js). */
(function () {
  'use strict';
  var O = window.Otzaria;
  var X = window.SdkX = {};
  var SELF = document.documentElement.getAttribute('data-plugin-id') || '';

  function call(m, a) { try { return O ? Promise.resolve(O.call(m, a || {})).catch(function () { return null; }) : Promise.resolve(null); } catch (e) { return Promise.resolve(null); } }
  function on(ev, fn) { try { if (O && O.on) O.on(ev, fn); } catch (e) { /* ignore */ } }
  function un(r) { return r && typeof r === 'object' && 'data' in r ? r.data : r; }
  X.call = call;

  /* התראות: notifications.system, ואם אין - הודעה בתוך האפליקציה */
  /* #69: שם הקריאה הנכון הוא notifications.sendSystem (notifications.system הוא שם ההרשאה — הקריאה הקודמת תמיד נכשלה) */
  function tr(s) { return window.I18N && I18N.t ? I18N.t(s) : s; }
  X.notify = async function (title, body) {
    title = tr(title); body = body ? tr(body) : '';
    var r = await call('notifications.sendSystem', { title: title, body: body });
    if (r == null) await call('notifications.showInApp', { message: title + (body ? ' — ' + body : ''), type: 'info' });
  };

  /* לוח: clipboard.read, ואם אין - ה-API של הדפדפן */
  X.readClipboard = async function () {
    var r = un(await call('clipboard.read', {}));
    var t = r && (r.text != null ? r.text : (typeof r === 'string' ? r : ''));
    if (!t && navigator.clipboard && navigator.clipboard.readText) { try { t = await navigator.clipboard.readText(); } catch (e) { t = ''; } }
    return (t || '').trim();
  };

  /* גופנים מקומיים (fonts.local.read) לצופה Word: מחזיר את הגופן הראשון מהרשימה שמותקן */
  var fontsCache = null;
  X.matchFont = async function (families) {
    if (!fontsCache) {
      var r = un(await call('fonts.listInstalled', {}));
      fontsCache = (Array.isArray(r) ? r : (r && r.fonts) || []).map(function (f) { return String(f.family || f.name || f).toLowerCase(); });
    }
    for (var i = 0; i < (families || []).length; i++) if (fontsCache.indexOf(String(families[i]).toLowerCase()) >= 0) return families[i];
    return null;
  };

  /* שינוי שפה/ערכת נושא חי (events.subscribe:settings.changed) */
  on('settings.changed', function (p) {
    if (window.SS && SS.syncOffline) SS.syncOffline(); /* מצב מנותק השתנה באוצריא — מסתירים/מחזירים רשת מיד */
    /* #69: רק כשהשפה באמת התחלפה, ובלי לשמור העדפה (אחרת "אוטומטי" היה הופך לקבוע, וכל שינוי הגדרה באוצריא טען את הדף מחדש) */
    var lang = p && (p.locale || p.language || (p.settings && p.settings.locale) || (p.key === 'key-language' && p.value));
    if (lang && window.I18N && I18N.lang && (!I18N.pref || I18N.pref() === 'auto')) {
      var want = /^en/i.test(lang) ? 'en' : 'he';
      if (want !== I18N.lang()) { try { location.reload(); } catch (e) { /* כלום */ } }
    }
  });

  /* מעקב אחר הרשאות שכובו (plugin.permissions_changed + app.getGrantedPermissions) */
  var granted = null;
  async function checkPerms(first) {
    var r = un(await call('app.getGrantedPermissions', {}));
    var list = Array.isArray(r) ? r : (r && r.permissions);
    if (!Array.isArray(list)) return;
    if (granted && !first) {
      var lost = granted.filter(function (p) { return list.indexOf(p) < 0; });
      if (lost.length) X.notify('הרשאה כובתה', tr('חלק מהיכולות לא יעבדו:') + ' ' + lost.join(', ') + '. ' + tr('אפשר להפעיל מחדש בהגדרות התוספים של אוצריא.'));
    }
    granted = list;
  }
  on('plugin.permissions_changed', function () { checkPerms(false); });
  checkPerms(true);

  /* #69: קיצורי מקלדת מוצהרים במניפסט (contributes.startup.shortcuts) — אוצריא מעירה את התוסף גם כשהדף לא פתוח,
     והמשתמש יכול לשנות/לבטל אותם בהגדרות → קיצורי מקשים. כאן רק מאזינים לאירוע app.command ({ command, shortcutId }) */
  var cmds = {};
  on('app.command', function (p) { var f = p && (cmds[p.command] || cmds[p.shortcutId]); if (f) f(p); });
  X.onCommand = function (command, fn) { cmds[command] = fn; };
  X.openSelf = function (param) { return call('plugin.openSelf', param == null ? {} : { param: param }); };

  /* ספרים מתוסף בחיפוש הספרייה (library.books.provide): קובצי PDF/Word מתיקיות שנבחרו */
  X.setLibraryBooks = function (books, provider) { return call('library.setProviderBooks', { provider: provider || 'systemsettings', books: books }); };
  X.onLibraryBookOpen = function (fn) { on('library.providerBook.openRequested', fn); };

  X.self = SELF;
})();
