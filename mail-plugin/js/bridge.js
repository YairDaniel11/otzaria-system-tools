/* דואר - לקוח לגשר "אחת שאלתי" (מועתק מ-file-explorer/js/bridge.js, בלי ספק הקבצים) */
(function () {
  var FX = window.MX = window.MX || {};
  FX.OpError = FX.OpError || function (code, message) { var e = new Error(message || code); e.code = code; return e; };

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
  var token = null, tokenLoaded = false, pairing = null, pairFail = null;
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
      /* 04.11: אחרי צימוד שנכשל לא שולחים בקשה חדשה מכל קריאה (הציף "בקשות צימוד תכופות מדי") — 30 שניות המתנה */
      if (pairFail && Date.now() - pairFail.at < 30000) throw pairFail.err;
      pairing = (async function () {
        FX.ui && FX.ui.toast(window.I18N && window.I18N.isEn() ? 'Approve the connection in the window that opened on the computer' : 'יש לאשר את החיבור בחלון שנפתח במחשב', 'info');
        var d = await request(Bridge.port, '/pair/request', { client: 'mail', name: 'דואר' }, { timeoutMs: 130000 });
        await saveToken(d.token);
      })();
      try { await pairing; pairFail = null; }
      catch (e) { if (e && typeof e === 'object') e.expected = true; pairFail = { at: Date.now(), err: e }; throw e; }
      finally { pairing = null; }
    },

    call: async function (path, body, opts) {
      if (!Bridge.port) throw FX.OpError('unreachable', 'הגשר אינו מחובר');
      await loadToken();
      try { return await request(Bridge.port, path, body, opts); }
      catch (e) {
        if (!e || e.code !== 'unpaired') throw e;
        await Bridge.pair();
        try { return await request(Bridge.port, path, body, opts); }
        catch (e2) {
          /* אחרי אישור מוצלח הגשר עדיין דוחה: לא פותחים חלון אישור נוסף (מונע לולאת "אשר שוב ושוב") */
          if (e2 && e2.code === 'unpaired') throw FX.OpError('pair_failed', 'החיבור אושר אבל הגשר לא קיבל אותו. יש לסגור ולפתוח מחדש את אחת שאלתי ולנסות שוב');
          throw e2;
        }
      }
    },

  };
})();
