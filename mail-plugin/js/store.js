/* דואר — אחסון מקומי (IndexedDB) לעבודה מלאה בלי גשר ובלי רשת.
   מאגרים: folders, messages (סיכום + גוף), atts (קבצים מצורפים), drafts, outbox (ממתין למסירה לגשר), contacts. */
(function () {
  'use strict';
  var MX = window.MX = window.MX || {};
  var DB = 'mx-mail', VER = 1, db = null;
  var STORES = { folders: 'id', messages: 'key', atts: 'key', drafts: 'id', outbox: 'id', contacts: 'id' };

  function open() {
    if (db) return Promise.resolve(db);
    return new Promise(function (res, rej) {
      var r = indexedDB.open(DB, VER);
      r.onupgradeneeded = function () {
        var d = r.result;
        Object.keys(STORES).forEach(function (s) {
          if (!d.objectStoreNames.contains(s)) {
            var os = d.createObjectStore(s, { keyPath: STORES[s] });
            if (s === 'messages') { os.createIndex('folder', 'folder'); }
            if (s === 'atts') { os.createIndex('msg', 'msg'); }
          }
        });
      };
      r.onsuccess = function () { db = r.result; res(db); };
      r.onerror = function () { rej(r.error); };
    });
  }
  function tx(store, mode, fn) {
    return open().then(function (d) {
      return new Promise(function (res, rej) {
        var t = d.transaction(store, mode), os = t.objectStore(store), out;
        out = fn(os);
        t.oncomplete = function () { res(out && out.result !== undefined && out instanceof IDBRequest ? out.result : out); };
        t.onerror = function () { rej(t.error); };
      });
    });
  }
  function req(r) { return new Promise(function (res, rej) { r.onsuccess = function () { res(r.result); }; r.onerror = function () { rej(r.error); }; }); }

  var S = MX.store = {
    ok: typeof indexedDB !== 'undefined',
    get: function (s, k) { return open().then(function (d) { return req(d.transaction(s).objectStore(s).get(k)); }); },
    all: function (s) { return open().then(function (d) { return req(d.transaction(s).objectStore(s).getAll()); }); },
    put: function (s, v) { return tx(s, 'readwrite', function (os) { os.put(v); }); },
    putMany: function (s, arr) { return tx(s, 'readwrite', function (os) { arr.forEach(function (v) { os.put(v); }); }); },
    del: function (s, k) { return tx(s, 'readwrite', function (os) { os.delete(k); }); },
    clear: function (s) { return tx(s, 'readwrite', function (os) { os.clear(); }); },
    byIndex: function (s, idx, val) { return open().then(function (d) { return req(d.transaction(s).objectStore(s).index(idx).getAll(val)); }); },
    msgKey: function (folder, uid) { return folder + '\u0001' + uid; },

    /* שמירת רשימת הודעות של תיקייה + גיזום לפי ההגדרה "כמה הודעות לשמור" */
    saveList: function (folder, items, keep) {
      return S.byIndex('messages', 'folder', folder).then(function (old) {
        var map = {}; old.forEach(function (m) { map[m.uid] = m; });
        var rows = items.map(function (m) {
          var o = map[m.uid] || {};
          return Object.assign({}, o, m, { key: S.msgKey(folder, m.uid), folder: folder });
        });
        return S.putMany('messages', rows).then(function () { return S.prune(folder, keep); });
      });
    },
    prune: function (folder, keep) {
      if (!keep || keep <= 0) return Promise.resolve();
      return S.byIndex('messages', 'folder', folder).then(function (all) {
        all.sort(function (a, b) { return b.uid - a.uid; });
        var drop = all.slice(keep);
        if (!drop.length) return;
        return tx('messages', 'readwrite', function (os) { drop.forEach(function (m) { os.delete(m.key); }); })
          .then(function () { return Promise.all(drop.map(function (m) { return S.dropAtts(m.key); })); });
      });
    },
    dropAtts: function (msgKey) {
      return S.byIndex('atts', 'msg', msgKey).then(function (a) { return tx('atts', 'readwrite', function (os) { a.forEach(function (x) { os.delete(x.key); }); }); });
    },
    list: function (folder) {
      return S.byIndex('messages', 'folder', folder).then(function (all) { return all.sort(function (a, b) { return b.uid - a.uid; }); });
    },
    attsSize: function () { return S.all('atts').then(function (a) { return a.reduce(function (n, x) { return n + (x.size || 0); }, 0); }); },
    /* גודל משוער של כל הנתונים השמורים */
    usage: function () {
      if (navigator.storage && navigator.storage.estimate) return navigator.storage.estimate().then(function (e) { return e.usage || 0; });
      return S.attsSize();
    },
    uid: function () { return Date.now().toString(36) + Math.random().toString(36).slice(2, 8); }
  };
})();
