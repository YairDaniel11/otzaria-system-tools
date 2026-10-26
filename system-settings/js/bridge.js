/* bridge.js - connection to the local Otzaria Bridge (Windows helper service).
   All traffic goes through Otzaria.call network.fetchStream.
   When window.Otzaria is missing (plain browser), a mock bridge with fake state is used. */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});

  var PORTS = [47917, 47918, 47919, 47920, 47921];
  var HEADERS = { 'X-Otzaria-Bridge': '1', 'Content-Type': 'application/json; charset=utf-8' };

  function BridgeError(code, message, status) {
    var e = new Error(message || code);
    e.code = code || 'internal';
    e.status = status || 0;
    return e;
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

  /* ---------- real transport ---------- */
  async function rawRequest(url, method, bodyObj, timeoutMs) {
    var headers = Object.assign({}, HEADERS);
    if (token) headers['X-Otzaria-Token'] = token;
    var params = { url: url, method: method, headers: headers, timeoutMs: Math.min(Math.max(timeoutMs || 15000, 1000), 120000) };
    if (method !== 'GET') params.body = JSON.stringify(bodyObj || {});
    var it = window.Otzaria.call('network.fetchStream', params);
    if (it && typeof it.then === 'function') it = await it;
    if (it && it.success === false) {
      var er = it.error || {};
      throw BridgeError(er.code === 'error.timeout' ? 'timeout' : 'transport', er.message || er.code);
    }
    if (!it || typeof it[Symbol.asyncIterator] !== 'function') {
      throw BridgeError('transport', 'fetchStream returned no stream');
    }
    var status = 0;
    var text = '';
    try {
      for await (var chunk of it) {
        if (!chunk) continue;
        if (chunk.type === 'response') { status = chunk.status; continue; }
        if (chunk.type === 'data' && typeof chunk.body === 'string') text += chunk.body;
      }
    } catch (err) {
      var msg = (err && (err.message || err.code)) || String(err);
      var code = /timeout/i.test(msg) ? 'timeout' : 'transport';
      throw BridgeError(code, msg);
    }
    var json = null;
    try { json = text ? JSON.parse(text) : null; } catch (e) { json = null; }
    if (!json) throw BridgeError('bad_response', 'HTTP ' + status, status);
    return { status: status, json: json };
  }

  var real = {
    port: null,
    async probe() {
      for (var i = 0; i < PORTS.length; i++) {
        var p = PORTS[i];
        try {
          await loadToken();
          var r = await rawRequest("http://127.0.0.1:" + p + "/health", "GET", null, 5000);
          if (r.json && r.json.ok && r.json.data && r.json.data.name === 'otzaria-bridge') {
            real.port = p;
            return r.json.data;
          }
        } catch (e) { /* next port */ }
      }
      real.port = null;
      return null;
    },
    /* בקשת צימוד: הגשר מציג חלון אישור במחשב (עד 2 דקות) */
    async pair() {
      if (pairing) return pairing;
      pairing = (async function () {
        var r = await rawRequest("http://127.0.0.1:" + real.port + "/pair/request", "POST",
          { client: 'system-settings', name: 'הגדרות מערכת' }, 120000);
        if (!r.json.ok) { var er = r.json.error || {}; throw BridgeError(er.code || 'forbidden', er.message || '', r.status); }
        await saveToken(r.json.data.token);
      })();
      try { await pairing; } finally { pairing = null; }
    },
    async call(path, body, opts) {
      if (!real.port) throw BridgeError('offline', 'bridge not connected');
      await loadToken();
      var res = await real.callOnce(path, body, opts);
      if (res.unpaired) {
        /* #69: מופע הרקע לא פותח חלון צימוד בגשר בלי שהמשתמש ביקש */
        if (SS.bridge.noPair) throw BridgeError('forbidden', 'not paired');
        /* קריאות מקבילות חולקות בקשת צימוד אחת — ההודעה "יש לאשר" מוצגת רק פעם אחת */
        var first = !pairing;
        if (first) SS.bridge.onPairing && SS.bridge.onPairing(true);
        try { await real.pair(); } finally { if (first) SS.bridge.onPairing && SS.bridge.onPairing(false); }
        res = await real.callOnce(path, body, opts);
      }
      if (res.error) throw res.error;
      return res.data;
    },
    async callOnce(path, body, opts) {
      var timeout = (opts && opts.timeoutMs) || 15000;
      var r;
      try {
        r = await rawRequest("http://127.0.0.1:" + real.port + path, "POST", body || {}, timeout);
      } catch (e) {
        if (e.code === 'transport') SS.bridge.onLost && SS.bridge.onLost();
        throw e;
      }
      if (r.json.ok) return { data: r.json.data };
      var err = r.json.error || {};
      if (err.code === 'unpaired') return { unpaired: true };
      return { error: BridgeError(err.code || 'internal', err.message || '', r.status) };
    }
  };

  /* ---------- mock bridge (browser preview) ---------- */
  function delay(ms) { return new Promise(function (res) { setTimeout(res, ms); }); }
  function clone(o) { return JSON.parse(JSON.stringify(o)); }

  var M = {
    audio: { volume: 42, muted: false, deviceName: 'רמקולים (Realtek High Definition Audio)' },
    devices: [
      { id: 'spk', name: 'רמקולים (Realtek High Definition Audio)', isDefault: true },
      { id: 'hdmi', name: 'מסך LG (NVIDIA High Definition Audio)', isDefault: false },
      { id: 'bt', name: 'אוזניות Soundcore Life Q30', isDefault: false }
    ],
    display: { brightness: 70, supported: true },
    radios: [
      { kind: 'bluetooth', name: 'Bluetooth', state: 'on' },
      { kind: 'wifi', name: 'Wi-Fi', state: 'on' }
    ],
    bt: [
      { id: 'bt1', name: 'אוזניות Soundcore Life Q30', paired: true, connected: true, kind: 'audio' },
      { id: 'bt2', name: 'עכבר Logitech M650', paired: true, connected: false, kind: 'input' }
    ],
    btFound: [
      { id: 'bt3', name: 'רמקול JBL Flip 6', paired: false, connected: false, kind: 'audio' },
      { id: 'bt4', name: 'מקלדת Keychron K3', paired: false, connected: false, kind: 'input' }
    ],
    wifi: {
      connected: 'Beit-Midrash',
      networks: [
        { ssid: 'Beit-Midrash', signal: 92, secured: true, connected: true, known: true },
        { ssid: 'Kollel-5G', signal: 71, secured: true, connected: false, known: true },
        { ssid: 'Shalom_Guest', signal: 54, secured: false, connected: false, known: false },
        { ssid: 'Cohen-Home', signal: 33, secured: true, connected: false, known: false },
        { ssid: 'HOTBOX-4A21', signal: 12, secured: true, connected: false, known: false }
      ]
    },
    kiosk: {
      active: false, hasPassword: false, strict: false, autoStartBridge: true,
      otzariaPath: 'C:\\Program Files\\Otzaria\\otzaria.exe', blockTaskManager: true,
      allowOpenExtensions: ['pdf', 'txt'], warnings: [], readOnly: false, allowExcelDocs: false, twoFactor: false, twoFactorAvailable: true, clock: true, clockPosition: 'bottom-left', venueName: '', dedicationText: '',
      restScreen: true, restIdleMinutes: 0, zmanimLocation: '', zmanimCity: '', zmanimShow: [], zmanimDiaspora: false,
      candleLightingMinutes: 18, zmanimElevation: 0, zmanimFallbackEngine: 'noaa', showParsha: true, showDafYomi: true, yahrzeitMonth: '', yahrzeitDay: 0, yahrzeitLabel: '', fullscreenOtzaria: true,
      sideBar: true, sideBarIconsOnly: false, sideBarKeyboard: false, sideBarPosition: 'right', restZmanimSide: 'right', resetOnShutdown: false,
      activatedBy: 'manual',
      /* פתיחה זמנית (תואם ל-Kiosk.TempUnlockStatus בגשר); active/secondsLeft/until מחושבים ב-/kiosk/status */
      tempUnlock: { enabled: false, configured: false, minutes: 30, maxMinutes: 60, twoFactorOn: false, usable: false,
        active: false, secondsLeft: 0, until: '', service: false, serviceOutdated: false },
      schedule: { enabled: false, start: '20:00', end: '02:00', warn: true },
      openSections: ['audio', 'display', 'energy', 'keyboard', 'mouse', 'printer'],
      allSections: ['audio', 'display', 'energy', 'keyboard', 'mouse', 'printer', 'time', 'wifi', 'bluetooth', 'airplane', 'usb', 'power']
    },
    programs: [],
    autoTorah: false,
    password: null,
    recovery: null,
    tempPassword: null,   /* סיסמת הפתיחה הזמנית בדמה */
    tempUntil: 0,         /* זמן הסיום (ms), 0 = אין פתיחה ממתינה */
    fails: 0,
    lockedUntil: 0
  };

  function mockErr(code, message) { return BridgeError(code, message, 400); }

  /* דמה: מחשב את שדות הפתיחה הזמנית, ובתום הזמן (או כשהפכה לרדומה) מחזיר את הנעילה — כמו TempTick בגשר */
  function tempSync() {
    var tu = M.kiosk.tempUnlock;
    tu.twoFactorOn = !!M.kiosk.twoFactor;
    tu.usable = tu.enabled && tu.configured && tu.twoFactorOn;
    var left = M.tempUntil ? Math.max(0, Math.ceil((M.tempUntil - Date.now()) / 1000)) : 0;
    if (M.tempUntil && (left === 0 || !tu.usable)) { M.tempUntil = 0; M.kiosk.active = true; left = 0; }
    tu.secondsLeft = left;
    tu.active = left > 0;
    tu.until = left > 0 ? new Date(M.tempUntil).toISOString() : '';
  }

  function checkPw(pw, allowRecovery) {
    if (Date.now() < M.lockedUntil) {
      throw mockErr('forbidden', 'יותר מדי ניסיונות שגויים. נסו שוב בעוד ' + Math.ceil((M.lockedUntil - Date.now()) / 1000) + ' שניות.');
    }
    var ok = pw === M.password || (allowRecovery && pw && pw.toUpperCase() === M.recovery);
    if (!ok) {
      M.fails++;
      if (M.fails >= 5) { M.fails = 0; M.lockedUntil = Date.now() + 60000; }
      throw mockErr('wrong_password', 'הסיסמה שגויה');
    }
    M.fails = 0;
  }

  var mockRoutes = {
    '/sys/info': function () {
      return { computerName: 'BEIT-MIDRASH-PC', userName: 'talmid', os: 'Windows 11 Pro 23H2',
        battery: { percent: 78, charging: true }, time: new Date().toISOString() };
    },
    '/sys/audio/get': function () { return clone(M.audio); },
    '/sys/audio/set': function (b) {
      if (typeof b.volume === 'number') M.audio.volume = Math.max(0, Math.min(100, Math.round(b.volume)));
      if (typeof b.muted === 'boolean') M.audio.muted = b.muted;
      return clone(M.audio);
    },
    '/sys/audio/devices': function () { return { devices: clone(M.devices) }; },
    '/sys/audio/set-default': function (b) {
      var d = M.devices.find(function (x) { return x.id === b.id; });
      if (!d) throw mockErr('not_found', 'ההתקן לא נמצא');
      M.devices.forEach(function (x) { x.isDefault = x.id === b.id; });
      M.audio.deviceName = d.name;
      return { ok: true };
    },
    '/sys/display/get': function () { return clone(M.display); },
    '/sys/display/set': function (b) { M.display.brightness = Math.round(b.brightness); return clone(M.display); },
    '/sys/radios/get': function () { return { radios: clone(M.radios) }; },
    '/sys/radios/set': function (b) {
      M.radios.forEach(function (r) { if (r.kind === b.kind) r.state = b.on ? 'on' : 'off'; });
      if (b.kind === 'wifi' && !b.on) { M.wifi.connected = null; M.wifi.networks.forEach(function (n) { n.connected = false; }); }
      if (b.kind === 'bluetooth' && !b.on) M.bt.forEach(function (d) { d.connected = false; });
      return { radios: clone(M.radios) };
    },
    '/sys/bluetooth/devices': async function (b) {
      var on = M.radios[0].state === 'on';
      if (!on) return { devices: [] };
      if (b.scan) { await delay(2200); return { devices: clone(M.bt.concat(M.btFound.filter(function (f) { return !M.bt.some(function (x) { return x.id === f.id; }); }))) }; }
      return { devices: clone(M.bt) };
    },
    '/sys/bluetooth/pair': async function (b) {
      await delay(900);
      var f = M.btFound.find(function (x) { return x.id === b.id; });
      if (!f) throw mockErr('not_found', 'המכשיר לא נמצא');
      var d = clone(f); d.paired = true; d.connected = true;
      M.bt.push(d);
      return { paired: true };
    },
    '/sys/bluetooth/unpair': function (b) {
      M.bt = M.bt.filter(function (x) { return x.id !== b.id; });
      return { unpaired: true };
    },
    '/sys/wifi/networks': function () {
      if (M.radios[1].state !== 'on') return { connected: null, networks: [] };
      return clone(M.wifi);
    },
    '/sys/wifi/connect': async function (b) {
      await delay(1200);
      var n = M.wifi.networks.find(function (x) { return x.ssid === b.ssid; });
      if (!n) throw mockErr('not_found', 'הרשת לא נמצאה');
      if (n.secured && !n.known && (!b.password || b.password.length < 8)) throw mockErr('bad_request', 'סיסמת הרשת שגויה');
      M.wifi.networks.forEach(function (x) { x.connected = x.ssid === b.ssid; });
      n.known = true;
      M.wifi.connected = b.ssid;
      return { connected: true };
    },
    '/sys/wifi/disconnect': function () {
      M.wifi.connected = null;
      M.wifi.networks.forEach(function (x) { x.connected = false; });
      return { ok: true };
    },
    '/sys/power': function (b) {
      if (M.kiosk.active && b.action !== 'lock') throw mockErr('locked', 'הפעולה חסומה במצב נעילה');
      return { ok: true };
    },
    '/kiosk/status': function () { tempSync(); return Object.assign(clone(M.kiosk), { hard: clone(M.hard) }); },
    '/kiosk/temp-unlock': function (b) {
      tempSync();
      var tu = M.kiosk.tempUnlock;
      if (!M.kiosk.twoFactor) throw mockErr('forbidden', 'פתיחה זמנית פועלת רק כשהאימות הדו-שלבי (Windows Hello) מופעל');
      if (!tu.enabled || !tu.configured) throw mockErr('bad_request', 'הפתיחה הזמנית כבויה או שלא הוגדרה לה סיסמה. אפשר להפעיל אותה בהגדרות הנעילה');
      if (!b.password || b.password !== M.tempPassword) throw mockErr('wrong_password', 'סיסמת הפתיחה הזמנית שגויה');
      if (!M.kiosk.active) throw mockErr('bad_request', 'המחשב אינו נעול כרגע');
      M.tempUntil = Date.now() + tu.minutes * 60000;
      M.kiosk.active = false;
      tempSync();
      return Object.assign(clone(M.kiosk), { hard: clone(M.hard) });
    },
    '/kiosk/temp-unlock/end': function () {
      if (M.tempUntil) { M.tempUntil = 0; M.kiosk.active = true; }
      tempSync();
      return Object.assign(clone(M.kiosk), { hard: clone(M.hard) });
    },
    '/kiosk/set-password': function (b) {
      if (!b.newPassword || b.newPassword.length < 6) throw mockErr('bad_request', 'הסיסמה חייבת להכיל לפחות 6 תווים');
      if (M.kiosk.hasPassword) {
        checkPw(b.oldPassword, false);
        M.password = b.newPassword;
        return { ok: true };
      }
      M.password = b.newPassword;
      M.kiosk.hasPassword = true;
      var abc = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
      var code = '';
      for (var i = 0; i < 12; i++) code += abc[Math.floor(Math.random() * abc.length)];
      M.recovery = code;
      return { ok: true, recoveryCode: code };
    },
    '/kiosk/recovery/backup/status': function () { return { left: M.backup ? M.backup.length : 0 }; },
    '/kiosk/recovery/backup': function (b) {
      checkPw(b.password, true);
      var A = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
      M.backup = [];
      for (var i = 0; i < 10; i++) { var c = ''; for (var j = 0; j < 12; j++) c += A.charAt(Math.floor(Math.random() * A.length)); M.backup.push(c.replace(/(.{4})(?=.)/g, '$1-')); }
      return { ok: true, codes: M.backup.slice(), path: 'Documents\\אחת שאלתי - קודי גיבוי לשחזור.txt', left: 10 };
    },
    '/kiosk/recovery/keep': function () { return { ok: true, path: 'E:\\אחת שאלתי - קוד שחזור.txt' }; },
    '/kiosk/enable': function (b) {
      if (!M.kiosk.hasPassword) throw mockErr('bad_request', 'יש להגדיר סיסמה תחילה');
      checkPw(b.password, false);
      M.kiosk.active = true;
      M.kiosk.strict = !!b.strict;
      M.kiosk.blockTaskManager = b.blockTaskManager !== false;
      return { active: true };
    },
    '/kiosk/disable': function (b) {
      checkPw(b.password, true);
      M.kiosk.active = false;
      M.kiosk.strict = false;
      return { active: false };
    },
    '/kiosk/settings': function (b) {
      checkPw(b.password, false);
      if (Array.isArray(b.allowOpenExtensions)) M.kiosk.allowOpenExtensions = b.allowOpenExtensions.slice();
      if (typeof b.otzariaPath === 'string') M.kiosk.otzariaPath = b.otzariaPath;
      if (typeof b.venueName === 'string') M.kiosk.venueName = b.venueName.slice(0, 60);
      if (typeof b.dedicationText === 'string') M.kiosk.dedicationText = b.dedicationText.slice(0, 120);
      ['readOnly', 'allowExcelDocs', 'twoFactor', 'clock', 'restScreen', 'fullscreenOtzaria', 'sideBar', 'sideBarIconsOnly', 'sideBarKeyboard', 'resetOnShutdown', 'showParsha', 'showDafYomi'].forEach(function (k) { if (typeof b[k] === 'boolean') M.kiosk[k] = b[k]; });
      /* פתיחה זמנית: אותם כללים כמו בגשר (Kiosk.ApplyTempUnlockSettings) */
      var tu = M.kiosk.tempUnlock;
      if (typeof b.tempUnlockPassword === 'string' && b.tempUnlockPassword) {
        if (b.tempUnlockPassword.length < 6) throw mockErr('bad_request', 'סיסמת הפתיחה הזמנית חייבת להכיל לפחות 6 תווים');
        if (b.tempUnlockPassword === M.password || (M.recovery && b.tempUnlockPassword.toUpperCase() === M.recovery))
          throw mockErr('bad_request', 'סיסמת הפתיחה הזמנית חייבת להיות שונה מסיסמת הנעילה ומקוד השחזור');
      }
      if (b.tempUnlockEnabled === true && !tu.enabled) {
        if (!M.kiosk.twoFactor) throw mockErr('bad_request', 'פתיחה זמנית דורשת אימות דו-שלבי (Windows Hello) פעיל. הפעילו קודם את האימות הדו-שלבי ונסו שוב.');
        if (!tu.configured && !b.tempUnlockPassword) throw mockErr('bad_request', 'יש לבחור סיסמת פתיחה זמנית לפני ההפעלה');
      }
      if (typeof b.tempUnlockPassword === 'string' && b.tempUnlockPassword) { M.tempPassword = b.tempUnlockPassword; tu.configured = true; }
      if (typeof b.tempUnlockEnabled === 'boolean') tu.enabled = b.tempUnlockEnabled;
      if (typeof b.tempUnlockMinutes === 'number') tu.minutes = Math.max(1, Math.min(60, b.tempUnlockMinutes | 0));
      if (typeof b.restIdleMinutes === 'number') M.kiosk.restIdleMinutes = Math.max(0, Math.min(240, b.restIdleMinutes | 0));
      if (b.restZmanimSide === 'left' || b.restZmanimSide === 'right') M.kiosk.restZmanimSide = b.restZmanimSide;
      if (typeof b.zmanimLocation === 'string') M.kiosk.zmanimLocation = b.zmanimLocation;
      if (typeof b.zmanimCity === 'string') M.kiosk.zmanimCity = b.zmanimCity;
      if (typeof b.zmanimDiaspora === 'boolean') M.kiosk.zmanimDiaspora = b.zmanimDiaspora;
      if (typeof b.zmanimElevation === 'number') M.kiosk.zmanimElevation = Math.max(0, Math.min(5000, b.zmanimElevation));
      if (typeof b.zmanimFallbackEngine === 'string') M.kiosk.zmanimFallbackEngine = b.zmanimFallbackEngine;
      if (typeof b.candleLightingMinutes === 'number') M.kiosk.candleLightingMinutes = Math.max(0, Math.min(90, b.candleLightingMinutes | 0));
      if (Array.isArray(b.zmanimShow)) M.kiosk.zmanimShow = b.zmanimShow.slice();
      if (typeof b.yahrzeitMonth === 'string') M.kiosk.yahrzeitMonth = b.yahrzeitMonth;
      if (typeof b.yahrzeitDay === 'number') M.kiosk.yahrzeitDay = Math.max(0, Math.min(30, b.yahrzeitDay | 0));
      if (typeof b.yahrzeitLabel === 'string') M.kiosk.yahrzeitLabel = b.yahrzeitLabel;
      if (b.clockPosition) M.kiosk.clockPosition = b.clockPosition;
      if (b.sideBarPosition) M.kiosk.sideBarPosition = b.sideBarPosition;
      if (Array.isArray(b.openSections)) M.kiosk.openSections = b.openSections.slice();
      if (b.schedule && typeof b.schedule === 'object') {
        if (typeof b.schedule.enabled === 'boolean') M.kiosk.schedule.enabled = b.schedule.enabled;
        if (typeof b.schedule.warn === 'boolean') M.kiosk.schedule.warn = b.schedule.warn;
        if (/^\d{1,2}:\d{2}$/.test(b.schedule.start || '')) M.kiosk.schedule.start = b.schedule.start;
        if (/^\d{1,2}:\d{2}$/.test(b.schedule.end || '')) M.kiosk.schedule.end = b.schedule.end;
      }
      tempSync();
      return clone(M.kiosk);
    },
    '/kiosk/zmanim-cache': function () { return { ok: true }; },
    '/kiosk/programs': function () {
      var list = clone(M.programs);
      if (M.autoTorah) list.push({ id: 'auto-0001', name: 'Word', path: 'C:\\Program Files\\Microsoft Office\\root\\Office16\\WINWORD.EXE', procs: [], exists: true, auto: true });
      return { autoTorah: !!M.autoTorah, programs: list };
    },
    '/kiosk/programs/auto': function (b) {
      checkPw(b.password, false);
      M.autoTorah = !!b.enabled;
      return { autoTorah: M.autoTorah };
    },
    '/kiosk/programs/detect': function () {
      return { found: [{ name: 'Word', path: 'C:\\Program Files\\Microsoft Office\\root\\Office16\\WINWORD.EXE', procs: 'WINWORD' }] };
    },
    '/kiosk/programs/launch': function (b) {
      if (!M.programs.some(function (p) { return p.id === b.id; })) throw mockErr('not_found', 'התוכנה אינה ברשימת התוכנות המותרות');
      return { opened: true };
    },
    '/kiosk/programs/add': function (b) {
      checkPw(b.password, false);
      if (M.programs.some(function (p) { return p.path.toLowerCase() === String(b.path).toLowerCase(); })) throw mockErr('exists', 'התוכנה כבר ברשימה');
      M.programs.push({ id: Math.random().toString(16).slice(2, 10), name: b.name, path: b.path,
        procs: String(b.procs || '').split(/[,;\s]+/).filter(Boolean), exists: true });
      return { programs: clone(M.programs) };
    },
    '/kiosk/programs/remove': function (b) {
      checkPw(b.password, false);
      M.programs = M.programs.filter(function (p) { return p.id !== b.id; });
      return { programs: clone(M.programs) };
    }
  };


  /* ---------- mock: מדיניות סייר הקבצים (browser preview) ---------- */
  M.fx = { mode: 'off', roots: [], minutes: 15, until: 0 };
  function fxState() {
    var left = Math.max(0, Math.ceil((M.fx.until - Date.now()) / 1000));
    tempSync();
    var tleft = M.kiosk.tempUnlock.secondsLeft;   /* פתיחה זמנית של המחשב: גם הסייר פתוח לגמרי */
    var eff = M.fx.mode !== 'off' && tleft > 0 ? 'open' : M.fx.mode === 'off' ? 'open' : M.fx.mode === 'locked' ? (left > 0 ? 'open' : 'locked') : (M.fx.roots.length ? 'restricted' : (left > 0 ? 'open' : 'locked'));
    return { mode: M.fx.mode, effective: eff, locked: eff === 'locked', fallback: M.fx.mode === 'restricted' && !M.fx.roots.length,
      roots: M.fx.roots.slice(), activeRoots: M.fx.roots.slice(), invalidRoots: [], unlockMinutes: M.fx.minutes, unlockedSeconds: left, tempUnlockSeconds: tleft,
      passwordConfigured: !!M.password, defaults: [{ id: 'desktop', name: 'שולחן העבודה', path: 'C:\\Users\\talmid\\Desktop' }] };
  }
  mockRoutes['/fs/policy/get'] = function () { return fxState(); };
  mockRoutes['/fs/policy/set'] = function (b) {
    if (!M.password) throw mockErr('no_password', 'יש להגדיר סיסמת נעילה לפני שינוי מדיניות סייר הקבצים');
    checkPw(b.password, true);
    if (['off', 'restricted', 'locked'].indexOf(b.mode) < 0) throw mockErr('bad_request', 'mode חייב להיות off, restricted או locked');
    if (b.mode === 'restricted' && !(b.roots || []).length) throw mockErr('bad_request', 'במצב מוגבל יש לבחור לפחות תיקייה אחת');
    M.fx = { mode: b.mode, roots: (b.roots || []).slice(), minutes: Math.min(Math.max(parseInt(b.unlockMinutes, 10) || 15, 1), 1440), until: 0 };
    return fxState();
  };
  mockRoutes['/fs/policy/unlock'] = function (b) { checkPw(b.password, false); M.fx.until = Date.now() + M.fx.minutes * 60000; return fxState(); };
  mockRoutes['/fs/policy/lock'] = function () { M.fx.until = 0; return fxState(); };

  /* ---------- mock: USB, keyboard, energy (browser preview) ---------- */
  M.usb = [
    { letter: 'E:', label: 'ספריית תורה', model: 'SanDisk Ultra USB 3.0', fs: 'NTFS', sizeBytes: 64e9, freeBytes: 21e9, ready: true },
    { letter: 'F:', label: '', model: 'Kingston DataTraveler', fs: 'FAT32', sizeBytes: 16e9, freeBytes: 15e9, ready: true }
  ];
  M.kb = { layouts: [{ id: '0002040D', name: 'עברית (ישראל) — עברית', lang: 'he' }, { id: '00020409', name: 'English (United States) — US', lang: 'en' }],
    current: '0002040D', delay: 1, speed: 31, stickyKeys: false, stickyHotkey: true, filterKeys: false, toggleKeys: false, capsLock: false, numLock: true,
    backlight: { supported: true, name: 'Keyboard Backlight', enabled: true, brightness: 60, vendor: 'Mock', model: 'Preview' } };
  M.energy = { battery: { percent: 64, charging: false, saverOn: false, minutesLeft: 190 },
    plans: [{ id: 'a1841308-3541-4fab-bc81-f71556f20b4a', name: 'חיסכון בחשמל', active: false }, { id: '381b4222-f694-41f0-9685-ff5bb260df2e', name: 'מאוזן', active: true }, { id: '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c', name: 'ביצועים גבוהים', active: false }],
    screenOff: { ac: 600, dc: 300 }, sleepAfter: { ac: 1800, dc: 900 }, saverThreshold: 20 };
  mockRoutes['/sys/usb/list'] = function () { return { drives: clone(M.usb) }; };
  mockRoutes['/sys/usb/eject'] = function (b) {
    var i = M.usb.findIndex(function (d) { return d.letter === b.letter; });
    if (i < 0) throw mockErr('not_found', 'הכונן ' + b.letter + ' אינו התקן USB מחובר');
    if (b.letter === 'F:' && !M.usbTried) { M.usbTried = true; throw mockErr('busy', 'הכונן בשימוש. סגרו קבצים ותוכנות שפתוחים ממנו (כולל חלונות סייר) ונסו שוב.'); }
    M.usb.splice(i, 1);
    return { ok: true, letter: b.letter, ejected: true };
  };
  function kbGet() { var k = clone(M.kb); delete k.backlight; return k; }
  mockRoutes['/sys/keyboard/get'] = function () { return kbGet(); };
  mockRoutes['/sys/keyboard/set'] = function (b) {
    ['delay', 'speed', 'stickyKeys', 'stickyHotkey', 'filterKeys', 'toggleKeys', 'capsLock', 'numLock'].forEach(function (k) { if (b[k] !== undefined) M.kb[k] = b[k]; });
    if (b.layout) M.kb.current = b.layout;
    return kbGet();
  };
  mockRoutes['/sys/keyboard/backlight/get'] = function () { return clone(M.kb.backlight); };
  mockRoutes['/sys/keyboard/backlight/set'] = function (b) {
    if (b.enabled !== undefined) M.kb.backlight.enabled = b.enabled;
    if (b.brightness !== undefined) M.kb.backlight.brightness = Math.round(b.brightness);
    return clone(M.kb.backlight);
  };
  mockRoutes['/sys/energy/get'] = function () { return clone(M.energy); };
  mockRoutes['/sys/energy/set'] = function (b) {
    if (b.plan) M.energy.plans.forEach(function (p) { p.active = p.id === b.plan; });
    if (b.screenOffAc !== undefined) M.energy.screenOff.ac = b.screenOffAc;
    if (b.screenOffDc !== undefined) M.energy.screenOff.dc = b.screenOffDc;
    if (b.sleepAfterAc !== undefined) M.energy.sleepAfter.ac = b.sleepAfterAc;
    if (b.sleepAfterDc !== undefined) M.energy.sleepAfter.dc = b.sleepAfterDc;
    if (b.saverThreshold !== undefined) M.energy.saverThreshold = Math.round(b.saverThreshold);
    return clone(M.energy);
  };

  /* ---------- mock: 2.0 (mouse, airplane, printers, time, default language, update) ---------- */
  M.mouse = { speed: 10, leftHanded: false, doubleClickMs: 500, wheelLines: 3, precision: true,
    touchpad: { taps: true, twoFingerRightClick: true, naturalScroll: false, sensitivity: 2, cursorSpeed: 10 } };
  mockRoutes['/sys/mouse/get'] = function () { return clone(M.mouse); };
  mockRoutes['/sys/mouse/set'] = function (b) {
    Object.keys(b).forEach(function (k) { if (k === 'touchpad') Object.assign(M.mouse.touchpad, b.touchpad); else M.mouse[k] = b[k]; });
    return clone(M.mouse);
  };
  M.airplane = false;
  mockRoutes['/sys/airplane/get'] = function () {
    return { supported: true, on: M.airplane, radios: M.radios.map(function (r) { return { kind: r.kind, name: r.name, on: r.state === 'on' }; }) };
  };
  mockRoutes['/sys/airplane/set'] = function (b) {
    M.airplane = !!b.on; M.radios.forEach(function (r) { r.state = b.on ? 'off' : 'on'; });
    return mockRoutes['/sys/airplane/get']();
  };
  M.printers = [
    { name: 'HP LaserJet Pro M404', isDefault: true, offline: false, network: true, status: 3 },
    { name: 'Microsoft Print to PDF', isDefault: false, offline: false, network: false, status: 3 },
    { name: 'Canon PIXMA (בית המדרש)', isDefault: false, offline: true, network: true, status: 7 }
  ];
  mockRoutes['/sys/printers/list'] = function () { return { printers: clone(M.printers), windowsManagesDefault: false }; };
  mockRoutes['/sys/printers/set-default'] = function (b) { M.printers.forEach(function (p) { p.isDefault = p.name === b.name; }); return mockRoutes['/sys/printers/list'](); };
  mockRoutes['/sys/printers/test-page'] = function () { return { ok: true }; };
  M.time = { zone: 'Israel Standard Time', clock24: true, autoSync: true, dst: false };
  mockRoutes['/sys/time/get'] = function () {
    return Object.assign(clone(M.time), { now: new Date().toISOString().slice(0, 19), zoneName: '(UTC+02:00) Jerusalem',
      zones: [{ id: 'Israel Standard Time', name: '(UTC+02:00) Jerusalem' }, { id: 'GMT Standard Time', name: '(UTC+00:00) London' }, { id: 'Eastern Standard Time', name: '(UTC-05:00) New York' }] });
  };
  mockRoutes['/sys/time/set'] = function (b) {
    if (b.dateTime) throw mockErr('forbidden', 'שינוי השעה ידנית דורש הרשאות מנהל. אפשר לסנכרן מול האינטרנט או לשנות את אזור הזמן.');
    if (b.zone) M.time.zone = b.zone; if (typeof b.clock24 === 'boolean') M.time.clock24 = b.clock24;
    return mockRoutes['/sys/time/get']();
  };
  mockRoutes['/sys/time/sync'] = function () { return mockRoutes['/sys/time/get'](); };
  M.defLang = '040D:0002040D';
  mockRoutes['/sys/keyboard/default-language/get'] = function () {
    return { current: M.defLang, methods: [{ id: '040D:0002040D', lang: 'he', name: 'עברית' }, { id: '0409:00000409', lang: 'en-US', name: 'English (United States)' }] };
  };
  mockRoutes['/sys/keyboard/default-language/set'] = function (b) { M.defLang = b.id; return mockRoutes['/sys/keyboard/default-language/get'](); };
  M.hard = { installed: false, running: false, configured: false, active: false, blockUsbStorage: false, version: null, needsUpdate: false, sessions: 1 };
  M.pairs = [{ id: 'a1b2c3', client: 'system-settings', name: 'הגדרות מערכת', created: '2026-10-12T02:10:00', lastUsed: '2026-10-12T02:10:00' },
    { id: 'd4e5f6', client: 'file-explorer', name: 'סייר קבצים', created: '2026-10-12T02:11:00', lastUsed: '2026-10-12T02:11:00' }];
  mockRoutes['/pair/list'] = function () { return { clients: clone(M.pairs) }; };
  mockRoutes['/pair/revoke'] = function (b) { M.pairs = M.pairs.filter(function (c) { return c.id !== b.id; }); return { clients: clone(M.pairs) }; };
  mockRoutes['/kiosk/hard/status'] = function () { return clone(M.hard); };
  mockRoutes['/kiosk/hard/install'] = function (b) {
    checkPw(b.password, false);
    M.hard = { installed: true, running: true, configured: true, active: M.kiosk.active, blockUsbStorage: false, version: '1.0.5', needsUpdate: false, sessions: 1 };
    return clone(M.hard);
  };
  mockRoutes['/kiosk/hard/settings'] = function (b) { checkPw(b.password, false); M.hard.blockUsbStorage = !!b.blockUsbStorage; return clone(M.hard); };
  mockRoutes['/kiosk/hard/uninstall'] = function (b) { checkPw(b.password, true); M.hard = { installed: false, running: false, configured: false, active: false, blockUsbStorage: false, version: null, needsUpdate: false, sessions: 1 }; return clone(M.hard); };

  /* ---------- mock: הקשחת BIOS/UEFI (browser preview) ---------- */
  M.bios = {
    vendor: 'lenovo', manufacturer: 'LENOVO', model: '20XW0013IL', supported: true,
    wmiReachable: true, wmiError: null,
    usbBootSetting: { name: 'USB Boot', currentValue: 'Enable', possibleValues: ['Enable', 'Disable'] },
    bootMenuAuthSetting: { name: 'Prompt for Admin authentication on F9 (Boot Menu)', currentValue: 'Disable', possibleValues: ['Disable', 'Enable'] },
    passwordRequiresPhysicalPresence: false,
    note: 'ב-Lenovo מרבית הדגמים דורשים שתהיה כבר מוגדרת סיסמת Supervisor לפני ששינוי הגדרות אחרות (כמו חסימת USB) יתקבל ב-firmware. ' +
      'לכן מוצע קודם להגדיר סיסמת BIOS, ורק אחר כך לחסום אתחול USB/לנעול את תפריט האתחול.'
  };
  mockRoutes['/bios/status'] = function () { return clone(M.bios); };
  mockRoutes['/bios/harden'] = function (b) {
    checkPw(b.password, true);
    if (!b.setPassword && !b.blockUsbBoot && !b.lockBootMenu)
      throw mockErr('bad_request', 'יש לבחור לפחות פעולה אחת: הגדרת סיסמת BIOS, חסימת אתחול USB, או נעילת תפריט האתחול');
    var steps = [];
    if (b.setPassword) {
      if (!b.biosPassword || b.biosPassword.length < 4) throw mockErr('bad_request', 'סיסמת ה-BIOS חייבת להכיל לפחות 4 תווים');
      steps.push({ id: 'password', attempted: true, success: true, message: 'סיסמת ה-BIOS הוגדרה בהצלחה (דמה בלבד — אין כאן מחשב אמיתי)' });
    }
    if (b.blockUsbBoot) {
      if (M.bios.usbBootSetting) {
        M.bios.usbBootSetting.currentValue = 'Disable';
        steps.push({ id: 'usbBoot', attempted: true, success: true, settingName: M.bios.usbBootSetting.name,
          message: 'ההגדרה „' + M.bios.usbBootSetting.name + '” הוגדרה ל-„Disable” ונשמרה (דמה בלבד)' });
      } else {
        steps.push({ id: 'usbBoot', attempted: false, success: false, settingName: null, message: 'לא נמצאה הגדרה מתאימה במחשב זה' });
      }
    }
    if (b.lockBootMenu) {
      if (M.bios.bootMenuAuthSetting) {
        M.bios.bootMenuAuthSetting.currentValue = 'Enable';
        steps.push({ id: 'bootMenuAuth', attempted: true, success: true, settingName: M.bios.bootMenuAuthSetting.name,
          message: 'ההגדרה „' + M.bios.bootMenuAuthSetting.name + '” הוגדרה ל-„Enable” ונשמרה (דמה בלבד)' });
      } else {
        steps.push({ id: 'bootMenuAuth', attempted: false, success: false, settingName: null, message: 'לא נמצאה הגדרה מתאימה במחשב זה' });
      }
    }
    return { vendor: M.bios.vendor, manufacturer: M.bios.manufacturer, model: M.bios.model, steps: steps };
  };
  mockRoutes['/sys/update-check'] = function () {
    return { current: '1.0.5', latest: '1.0.5', newer: false, canInstall: false, url: 'https://github.com/Hadabadra/achat-shaalti/releases/latest' };
  };
  mockRoutes['/sys/update-install'] = function () { return { started: false, message: 'הגשר מעודכן (1.0.3)' }; };

  var mock = {
    port: 47917,
    async probe() {
      await delay(250);
      if (/[?&]offline=1/.test(location.search)) return null;
      return { name: 'otzaria-bridge', version: '1.0.5', port: 47917, auth: 'token', paired: true, kioskActive: M.kiosk.active, platform: 'windows' };
    },
    async call(path, body) {
      await delay(180);
      var fn = mockRoutes[path];
      if (!fn) throw mockErr('not_found', 'נתיב לא קיים');
      return await fn(body || {});
    }
  };

  var impl = window.Otzaria ? real : mock;

  SS.bridge = {
    isMock: !window.Otzaria,
    health: null,
    onLost: null,
    onPairing: null,
    get port() { return impl.port; },
    async probe() {
      var h = await impl.probe();
      SS.bridge.health = h;
      return h;
    },
    call: function (path, body, opts) { return impl.call(path, body, opts); },
    Error: BridgeError
  };
})();
