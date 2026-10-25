/* סייר קבצים - לקוח לתוכנת חסימת אוצריא (שירות Windows מקומי) וספק קבצים מלא */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};

  var PORTS = [47917, 47918, 47919, 47920, 47921];
  var HOST = 'http://127.0.0.1:';

  /* פותח זרם network.fetchStream ומחזיר איטרטור אסינכרוני */
  async function openStream(params) {
    if (!window.Otzaria) throw FX.OpError('unavailable', 'אוצריא אינה זמינה');
    var r = window.Otzaria.call('network.fetchStream', params);
    if (r && typeof r[Symbol.asyncIterator] === 'function') return r;
    r = await r;
    if (r && typeof r[Symbol.asyncIterator] === 'function') return r;
    if (r && r.success === false) {
      var er = r.error || {};
      throw FX.OpError(er.code || 'unreachable', er.message || 'הבקשה לגשר נכשלה');
    }
    if (r && r.success && r.data && typeof r.data[Symbol.asyncIterator] === 'function') return r.data;
    throw FX.OpError('unreachable', 'תשובה לא צפויה מ-network.fetchStream');
  }


  /* ---------- צימוד לגשר (3.0.0, בעיה #2): מפתח אקראי שהגשר נותן אחרי אישור המשתמש ---------- */
  var TOKEN_KEY = 'bridgeToken';
  var token = null, tokenLoaded = false, pairing = null;
  async function hostCall(method, params) {
    var r = await window.Otzaria.call(method, params || {});
    if (r && r.success === false) throw new Error((r.error && r.error.message) || method);
    return r && Object.prototype.hasOwnProperty.call(r, 'data') ? r.data : r;
  }
  async function loadToken() {
    if (tokenLoaded) return token;
    tokenLoaded = true;
    try { var v = await hostCall('storage.get', { key: TOKEN_KEY }); token = typeof v === 'string' && v.length >= 32 ? v : null; }
    catch (e) { token = null; }
    return token;
  }
  async function saveToken(t) {
    token = t;
    try { await hostCall('storage.set', { key: TOKEN_KEY, value: t }); } catch (e) { /* נשאר בזיכרון */ }
  }

  function buildParams(port, path, body, timeoutMs, method) {
    var p = {
      url: HOST + port + path,
      method: method || 'POST',
      headers: token ? { 'X-Otzaria-Bridge': '1', 'X-Otzaria-Token': token } : { 'X-Otzaria-Bridge': '1' },
      timeoutMs: Math.min(Math.max(timeoutMs || 30000, 1000), 120000)
    };
    if (p.method === 'POST') {
      p.headers['Content-Type'] = 'application/json; charset=utf-8';
      p.body = JSON.stringify(body || {});
    }
    return p;
  }

  function parseEnvelope(status, text) {
    var env = null;
    try { env = JSON.parse(text); } catch (e) { env = null; }
    if (!env || typeof env !== 'object') {
      throw FX.OpError(status >= 400 ? 'internal' : 'bad_response', 'תשובה לא תקינה מהגשר (HTTP ' + status + ')');
    }
    if (env.ok) return env.data;
    var er = env.error || {};
    throw FX.OpError(er.code || 'internal', er.message || ('שגיאת גשר (HTTP ' + status + ')'));
  }

  async function request(port, path, body, opts) {
    opts = opts || {};
    var it;
    var status = 0, text = '';
    try {
      it = await openStream(buildParams(port, path, body, opts.timeoutMs, opts.method));
      for await (var ch of it) {
        if (!ch) continue;
        if (ch.type === 'response') { status = ch.status; continue; }
        if (ch.type === 'data' && ch.body) text += ch.body;
      }
    } catch (e) {
      var code = String(e && e.code || '');
      if (/timeout/.test(code)) throw FX.OpError('timeout', 'הגשר לא הגיב בזמן');
      if (/permission/.test(code)) throw FX.OpError('permission_denied', 'לתוסף אין הרשאת גישה לשירות מקומי (network.localhost)');
      throw FX.OpError('unreachable', (e && e.message) || 'אין חיבור לגשר');
    }
    return parseEnvelope(status, text);
  }

  var Bridge = FX.Bridge = {
    port: null,
    info: null,
    policy: null,

    /* סריקת הפורטים ב-GET /health; מחזיר מידע או null */
    probe: async function () {
      await loadToken();
      var tries = PORTS.map(function (port) {
        return request(port, '/health', null, { method: 'GET', timeoutMs: 2500 })
          .then(function (d) { return d && d.name === 'otzaria-bridge' ? { port: port, info: d } : null; })
          .catch(function () { return null; });
      });
      var results = await Promise.all(tries);
      for (var i = 0; i < results.length; i++) {
        if (results[i]) {
          Bridge.port = results[i].port;
          Bridge.info = results[i].info;
          Bridge.policy = null; /* חיבור חדש: מדיניות המנהל (אם השתנתה) תיטען מחדש בפעם הבאה שתידרש */
          return results[i].info;
        }
      }
      Bridge.port = null;
      Bridge.info = null;
      Bridge.policy = null;
      return null;
    },

    /* מדיניות מסמכי Office שהמנהל קבע (kiosk.allowExcelDocs). נטענת פעם אחת ונשמרת במטמון עד חיבור מחדש.
       כשאין גשר, או כשהבקשה נכשלת, ברירת המחדל היא חסימה (Word בלבד) - כמו בגשר עצמו */
    loadPolicy: async function () {
      if (Bridge.policy) return Bridge.policy;
      try {
        var st = await Bridge.call('/kiosk/status', {});
        Bridge.policy = { allowExcelDocs: !!(st && st.allowExcelDocs) };
      } catch (e) {
        Bridge.policy = { allowExcelDocs: false };
      }
      return Bridge.policy;
    },

    /* בקשת צימוד: הגשר מציג חלון אישור במחשב (עד 2 דקות) */
    pair: async function () {
      if (pairing) return pairing;
      pairing = (async function () {
        FX.ui && FX.ui.toast(window.I18N && window.I18N.isEn() ? 'Approve the connection in the window that opened on the computer' : 'יש לאשר את החיבור בחלון שנפתח במחשב', 'info');
        var d = await request(Bridge.port, '/pair/request', { client: 'file-explorer', name: 'סייר קבצים' }, { timeoutMs: 130000 });
        await saveToken(d.token);
      })();
      try { await pairing; } finally { pairing = null; }
    },

    call: async function (path, body, opts) {
      if (!Bridge.port) throw FX.OpError('unreachable', 'הגשר אינו מחובר');
      await loadToken();
      try { return await request(Bridge.port, path, body, opts); }
      catch (e) {
        if (!e || e.code !== 'unpaired') throw e;
        await Bridge.pair();
        return request(Bridge.port, path, body, opts);
      }
    },

    /* זרם NDJSON: onItem נקרא לכל שורה. isCancelled מאפשר יציאה מוקדמת שמבטלת את הבקשה */
    stream: async function (path, body, onItem, isCancelled, opts) {
      if (!Bridge.port) throw FX.OpError('unreachable', 'הגשר אינו מחובר');
      await loadToken();
      if (!token) await Bridge.pair();
      opts = opts || {};
      var it = await openStream(buildParams(Bridge.port, path, body, opts.timeoutMs || 120000));
      var pending = '', status = 0, ok = true, errText = '', cancelled = false;
      try {
        for await (var ch of it) {
          if (isCancelled && isCancelled()) { cancelled = true; break; }
          if (!ch) continue;
          if (ch.type === 'response') { status = ch.status; ok = !!ch.ok; continue; }
          if (ch.type !== 'data' || !ch.body) continue;
          if (!ok) { errText += ch.body; continue; }
          pending += ch.body;
          var lines = pending.split('\n');
          pending = lines.pop() || '';
          for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (!line) continue;
            var obj;
            try { obj = JSON.parse(line); } catch (e) { continue; }
            onItem(obj);
          }
          if (isCancelled && isCancelled()) { cancelled = true; break; }
        }
      } catch (e) {
        if (!cancelled) {
          if (/timeout/.test(String(e && e.code))) throw FX.OpError('timeout', 'החיפוש ארך זמן רב מדי והופסק');
          throw FX.OpError('unreachable', (e && e.message) || 'החיבור לגשר נקטע');
        }
      }
      if (!ok) parseEnvelope(status, errText);
      if (!cancelled && pending.trim()) {
        try { onItem(JSON.parse(pending)); } catch (e) { /* שורה חלקית אחרונה */ }
      }
      return { cancelled: cancelled };
    }
  };

  /* ===== ספק קבצים מבוסס גשר ===== */
  var KNOWN_ICON = { desktop: 'desktop', documents: 'documents', downloads: 'downloads', pictures: 'pictures', music: 'music', videos: 'videos', home: 'home', recycle: 'trash' };
  /* נתיב וירטואלי לסל המיחזור (אינו נתיב Windows אמיתי) */
  var RECYCLE_PATH = '::recycle';

  FX.BridgeProvider = {
    kind: 'bridge',
    caps: {
      copy: true, move: true, mkdir: true, newFile: true, rename: true, recycle: true, permanentDelete: true,
      open: true, reveal: true, clipboardSync: true, contentSearch: true, hidden: true, properties: true, editPath: true
    },
    rootLabel: 'המחשב הזה',
    rootPath: '',
    sep: '\\',

    roots: async function () {
      var d = await Bridge.call('/fs/roots', {});
      var drives = (d && d.drives || []).map(function (x) {
        return {
          name: (x.label ? x.label + ' ' : '') + '(' + String(x.path).slice(0, 2).toUpperCase() + ')',
          path: x.path, type: 'drive', driveType: x.type,
          totalBytes: x.totalBytes, freeBytes: x.freeBytes
        };
      });
      var known = (d && d.known || []).map(function (k) {
        return { id: k.id, name: FX.t ? FX.t(k.name) : k.name, path: k.path, icon: KNOWN_ICON[k.id] || 'folder' };
      });
      known.push({ id: 'recycle', name: FX.t ? FX.t('סל המיחזור') : 'סל המיחזור', path: RECYCLE_PATH, icon: KNOWN_ICON.recycle });
      return { drives: drives, known: known };
    },

    list: async function (path, opts) {
      opts = opts || {};
      if (!path) {
        var r = await FX.BridgeProvider.roots();
        return { path: '', parent: null, entries: r.drives, isRoot: true, known: r.known };
      }
      if (path === RECYCLE_PATH) {
        var rb = await Bridge.call('/recycle/list', {}, { timeoutMs: 60000 });
        return {
          path: RECYCLE_PATH, parent: '', recycle: true,
          entries: (rb && rb.items || []).map(function (x) {
            return {
              name: x.name, path: RECYCLE_PATH + '/' + x.id, type: x.type === 'dir' ? 'dir' : 'file',
              size: x.size, modified: x.deleted, ext: x.ext, recycleId: x.id, origPath: x.originalPath
            };
          })
        };
      }
      var d = await Bridge.call('/fs/list', { path: path, showHidden: !!opts.showHidden }, { timeoutMs: 60000 });
      return {
        path: d.path || path,
        parent: d.parent == null ? '' : d.parent,
        entries: (d.entries || []).map(function (e) {
          return { name: e.name, path: e.path, type: e.type, size: e.size, modified: e.modified, ext: e.ext, hidden: !!e.hidden };
        })
      };
    },

    displayPath: function (p) { return p || 'המחשב הזה'; },
    parentOf: function (p) { return FX.win.parent(p); },
    nameOf: function (p) { return FX.win.name(p); },
    join: function (dir, name) { return FX.win.join(dir, name); },
    segments: function (p) { return FX.win.segments(p); },
    same: function (a, b) { return FX.win.same(a, b); },
    isInside: function (c, p) { return FX.win.isInside(c, p); },
    normalizeInput: function (s) {
      var p = String(s || '').trim().replace(/\//g, '\\').replace(/^"(.*)"$/, '$1');
      if (/^[A-Za-z]:$/.test(p)) p += '\\';
      if (p.length > 3) p = p.replace(/\\+$/, '');
      return p;
    },

    stat: function (path) { return Bridge.call('/fs/stat', { path: path }, { timeoutMs: 60000 }); },
    mkdir: function (dir, name) { return Bridge.call('/fs/mkdir', { path: FX.win.join(dir, name) }); },
    newTextFile: function (dir, name) { return Bridge.call('/fs/write-text', { path: FX.win.join(dir, name), content: '', overwrite: false }); },
    rename: function (path, newName) { return Bridge.call('/fs/rename', { path: path, newName: newName }); },
    setAttributes: function (path, attrs) { return Bridge.call('/fs/attributes', { path: path, hidden: !!attrs.hidden, readOnly: !!attrs.readOnly }); },
    copy: function (sources, destDir, conflict) {
      return Bridge.call('/fs/copy', { sources: sources, destDir: destDir, conflict: conflict || 'rename' }, { timeoutMs: 120000 });
    },
    move: function (sources, destDir, conflict) {
      return Bridge.call('/fs/move', { sources: sources, destDir: destDir, conflict: conflict || 'rename' }, { timeoutMs: 120000 })
        .then(function (r) { return { moved: r.moved, skipped: r.skipped, failed: r.failed }; });
    },
    remove: function (paths, permanent) {
      return Bridge.call('/fs/delete', { paths: paths, permanent: !!permanent }, { timeoutMs: 120000 });
    },
    recycleRestore: function (ids) {
      return Bridge.call('/recycle/restore', { ids: ids, conflict: 'rename' }, { timeoutMs: 120000 });
    },
    recycleDelete: function (ids) {
      return Bridge.call('/recycle/delete', { ids: ids }, { timeoutMs: 120000 });
    },
    recycleEmpty: function () {
      return Bridge.call('/recycle/empty', {}, { timeoutMs: 120000 });
    },
    read: async function (entry) {
      var d = await Bridge.call('/fs/read', { path: entry.path, maxBytes: 5242880 }, { timeoutMs: 60000 });
      var out = { name: d.name, size: d.size, mime: d.mime, truncated: !!d.truncated };
      if (d.encoding === 'utf8') { out.text = d.content; }
      else if (d.encoding === 'base64') { out.dataUrl = 'data:' + (d.mime || 'application/octet-stream') + ';base64,' + d.content; }
      return out;
    },
    open: function (path, o) { return Bridge.call('/fs/open', { path: path, openWith: !!(o && o.openWith) }); },
    reveal: function (path) { return Bridge.call('/fs/reveal', { path: path }); },
    clipboardGet: function () { return Bridge.call('/fs/clipboard-get', {}); },
    clipboardSet: function (paths, cut) { return Bridge.call('/fs/clipboard-set', { paths: paths, cut: !!cut }); },

    /* חיפוש זורם */
    search: async function (root, opts, handlers, isCancelled) {
      /* במסך "המחשב הזה" אין תיקייה: מחפשים בכל הכוננים, אחד אחרי השני (הגשר דורש root) */
      if (!root) {
        var rr = await FX.BridgeProvider.roots();
        var total = { cancelled: false, truncated: false, scanned: 0, hits: 0, complete: true };
        var left = opts.maxResults || 500;
        for (var di = 0; di < rr.drives.length && left > 0; di++) {
          if (isCancelled && isCancelled()) { total.cancelled = true; break; }
          var sub = Object.assign({}, opts, { maxResults: left });
          var r1 = await FX.BridgeProvider.search(rr.drives[di].path, sub, {
            hit: function (h) { left--; total.hits++; handlers.hit(h); },
            progress: function (p) { handlers.progress({ scanned: total.scanned + (p.scanned || 0), current: p.current }); }
          }, isCancelled);
          total.scanned += r1.scanned || 0;
          total.cancelled = total.cancelled || !!r1.cancelled;
          total.truncated = total.truncated || !!r1.truncated;
          total.complete = total.complete && !!r1.complete;
          if (r1.cancelled) break;
        }
        if (left <= 0) total.truncated = true;
        return total;
      }
      var body = {
        root: root,
        query: opts.query || '*',
        extensions: opts.extensions || [],
        maxResults: opts.maxResults || 500,
        includeDirs: opts.includeDirs !== false
      };
      if (opts.contentQuery) body.contentQuery = opts.contentQuery;
      var summary = null;
      return Bridge.stream('/fs/search', body, function (obj) {
        if (!obj || !obj.type) return;
        if (obj.type === 'hit' || obj.type === 'dir' || obj.type === 'file') {
          var t = obj.type === 'hit' ? (obj.entryType || obj.kind || obj.itemType || guessType(obj)) : obj.type;
          handlers.hit({ name: obj.name, path: obj.path, type: t === 'dir' ? 'dir' : 'file', size: obj.size, modified: obj.modified, snippet: obj.snippet });
        } else if (obj.type === 'progress') handlers.progress({ scanned: obj.scanned, current: obj.current });
        else if (obj.type === 'done') { summary = obj; handlers.progress({ scanned: obj.scanned, current: '' }); }
      }, isCancelled, { timeoutMs: 120000 }).then(function (r) {
        return {
          cancelled: r.cancelled || !!(summary && summary.cancelled),
          truncated: !!(summary && summary.truncated),
          scanned: summary ? summary.scanned : null,
          hits: summary ? summary.hits : null,
          complete: !!summary
        };
      });
    }
  };

  /* בחוזה, שורת hit מכילה פעמיים את המפתח type (hit וסוג הפריט).
     JSON.parse שומר את המופע האחרון, ולכן לרוב מתקבל dir או file.
     אם התקבל hit, ננחש את הסוג משדות חלופיים או מהשם */
  function guessType(obj) {
    if (obj.isDir === true || obj.dir === true) return 'dir';
    if (obj.size == null && !/\.[^\\.]+$/.test(obj.name || '')) return 'dir';
    return 'file';
  }
})();
