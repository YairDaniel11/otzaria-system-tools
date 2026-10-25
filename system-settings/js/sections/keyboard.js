/* sections/keyboard.js - layouts, typing speed, accessibility keys and keyboard backlight */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var DELAYS = ['250', '500', '750', '1000'];

  SS.sections.push({
    id: 'keyboard', title: 'מקלדת', icon: 'keyboard',
    mount: function (body) {
      body.innerHTML =
        '<div class="card"><div class="card-title">שפת הקלדה</div><div data-slot="lay">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">שפת ברירת מחדל</div><div data-slot="def">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">מהירות הקשה</div><div data-slot="speed">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">מקשים</div><div data-slot="keys">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">תאורת מקלדת</div><div data-slot="bl">' + ui.loading() + '</div></div>';
    },
    load: async function (body) {
      await Promise.all([loadMain(body), loadBacklight(body), loadDefault(body)]);
    }
  });

  async function send(patch, body) {
    try { render(body, await SS.bridge.call('/sys/keyboard/set', patch)); }
    catch (e) { ui.error(e, 'הגדרות מקלדת'); loadMain(body); }
  }

  async function loadMain(body) {
    var d;
    try { d = await SS.bridge.call('/sys/keyboard/get'); }
    catch (e) {
      var retry = function () { loadMain(body); };
      ['lay', 'speed', 'keys'].forEach(function (k) { body.querySelector('[data-slot=' + k + ']').innerHTML = ui.errorBox(e, retry); });
      return;
    }
    render(body, d);
  }

  function render(body, d) {
    /* ----- layouts ----- */
    var lay = body.querySelector('[data-slot=lay]');
    lay.innerHTML = '<select class="input kb-select" data-lay-sel aria-label="שפת הקלדה">' + (d.layouts || []).map(function (l) {
      return '<option value="' + ui.esc(l.id) + '"' + (l.id === d.current ? ' selected' : '') + '>' + ui.esc(l.name) + '</option>';
    }).join('') + '</select>' +
      '<p class="muted small">ההחלפה חלה על החלון הפעיל (אוצריא). בתוכנות אחרות אפשר להחליף עם Alt+Shift.</p>';
    var layBtn = lay.querySelector('[data-lay-sel]');
    layBtn.addEventListener('change', async function () {
      try { await SS.bridge.call('/sys/keyboard/set', { layout: layBtn.value }); }
      catch (e) { ui.error(e, 'החלפת שפה'); return; }
      setTimeout(function () { loadMain(body); }, 350);
    });

    /* ----- typing speed ----- */
    var sp = body.querySelector('[data-slot=speed]');
    function cps(v) { return (2.5 + v * 27.5 / 31).toFixed(1); }
    sp.innerHTML =
      '<div class="setting-text"><b>השהיה לפני חזרה</b></div>' +
      '<div class="slider-row"><input type="range" class="range" data-r="delay" min="0" max="3" step="1" value="' + d.delay + '" aria-label="השהיה לפני חזרה">' +
      '<output class="range-val" dir="ltr">' + DELAYS[d.delay] + ' ms</output></div>' +
      '<div class="setting-text"><b>קצב חזרה</b></div>' +
      '<div class="slider-row"><input type="range" class="range" data-r="speed" min="0" max="31" step="1" value="' + d.speed + '" aria-label="קצב חזרה">' +
      '<output class="range-val" dir="ltr">' + cps(d.speed) + ' /s</output></div>' +
      '<div class="row-actions"><button class="btn btn-tonal" data-act="reset">איפוס לברירת מחדל</button></div>';
    sp.querySelectorAll('.range').forEach(function (r) {
      SS.paintRange(r);
      var out = r.parentNode.querySelector('.range-val');
      var push = ui.debounce(function (v) {
        var p = {}; p[r.dataset.r] = v;
        SS.bridge.call('/sys/keyboard/set', p).catch(function (e) { ui.error(e, 'מהירות הקשה'); });
      }, 250);
      r.addEventListener('input', function () {
        SS.paintRange(r);
        out.textContent = r.dataset.r === 'delay' ? DELAYS[r.value] + ' ms' : cps(+r.value) + ' /s';
        push(+r.value);
      });
    });
    sp.querySelector('[data-act=reset]').addEventListener('click', function () { send({ delay: 1, speed: 31 }, body); });

    /* ----- keys ----- */
    var ROWS = [
      ['capsLock', 'Caps Lock', 'מצב אותיות גדולות באנגלית'],
      ['numLock', 'Num Lock', 'מקלדת המספרים בצד ימין'],
      ['stickyKeys', 'מקשים דביקים', 'מאפשר ללחוץ Shift, Ctrl או Alt בזה אחר זה במקום יחד'],
      ['stickyHotkey', 'קיצור למקשים דביקים', 'לחיצה על Shift חמש פעמים מציגה חלון הפעלה. מומלץ לכבות במחשב משותף'],
      ['filterKeys', 'מקשי סינון', 'מתעלם מהקשות קצרות וחוזרות'],
      ['toggleKeys', 'צליל בנעילות', 'צפצוף כשמפעילים Caps Lock או Num Lock']
    ];
    var keys = body.querySelector('[data-slot=keys]');
    keys.innerHTML = ROWS.map(function (r) {
      return '<div class="setting-row"><div class="setting-text"><b>' + r[1] + '</b><span class="muted">' + r[2] + '</span></div>' +
        ui.switchHtml('sw-' + r[0], !!d[r[0]], r[1]) + '</div>';
    }).join('');
    ROWS.forEach(function (r) {
      var sw = keys.querySelector('#sw-' + r[0]);
      sw.addEventListener('click', function () {
        var p = {}; p[r[0]] = !d[r[0]];
        send(p, body);
      });
    });
  }

  /* השפה שנבחרת בכל חלון חדש ובכניסה ל-Windows */
  async function loadDefault(body) {
    var slot = body.querySelector('[data-slot=def]');
    var d;
    try { d = await SS.bridge.call('/sys/keyboard/default-language/get', {}, { timeoutMs: 25000 }); }
    catch (e) { slot.innerHTML = ui.errorBox(e, function () { loadDefault(body); }); return; }
    var list = d.methods || [];
    if (!list.length) { slot.innerHTML = '<div class="empty">לא נמצאו שפות מותקנות.</div>'; return; }
    var cur = (d.current || '').toUpperCase();
    slot.innerHTML = '<select class="input kb-select" data-def-sel aria-label="שפת ברירת מחדל">' +
      '<option value=""' + (!cur ? ' selected' : '') + '>לפי רשימת השפות</option>' +
      list.map(function (m) {
        return '<option value="' + ui.esc(m.id) + '"' + (m.id.toUpperCase() === cur ? ' selected' : '') + '>' + ui.esc(m.name) + '</option>';
      }).join('') + '</select>' +
      '<p class="muted small">השפה שתיבחר אוטומטית בכל חלון חדש ובכניסה ל-Windows. למשל עברית, כדי שאפשר יהיה לחפש באוצריא מיד.</p>';
    var defSel = slot.querySelector('[data-def-sel]');
    defSel.addEventListener('change', async function () {
      try { await SS.bridge.call('/sys/keyboard/default-language/set', { id: defSel.value }, { timeoutMs: 25000 }); ui.toast('שפת ברירת המחדל נשמרה', 'success'); }
      catch (e) { ui.error(e, 'שפת ברירת מחדל'); }
      loadDefault(body);
    });
  }

  async function loadBacklight(body) {
    var slot = body.querySelector('[data-slot=bl]');
    var d;
    try { d = await SS.bridge.call('/sys/keyboard/backlight/get'); }
    catch (e) { slot.innerHTML = ui.errorBox(e, function () { loadBacklight(body); }); return; }
    if (!d.supported) {
      var who = [d.vendor, d.model].filter(Boolean).join(' ');
      slot.innerHTML = '<div class="state-box">' + ui.icon('info') + '<div><b>לא נתמך במחשב זה</b>' +
        '<p class="muted">ל-Windows אין ממשק אחיד לתאורת מקלדת רגילה, והשליטה בה תלויה ביצרן המחשב' + (who ? ' (' + ui.esc(who) + ')' : '') +
        '. בדרך כלל אפשר לשנות אותה במקש Fn במקלדת עצמה' + (d.hint ? ': <b dir="ltr">' + ui.esc(d.hint) + '</b>' : '') +
        '. השליטה מכאן זמינה במחשבי ASUS ובמקלדות עם תמיכה בתקן HID LampArray.</p></div></div>';
      return;
    }
    slot.innerHTML =
      '<div class="setting-row"><div class="setting-text"><b>תאורה</b><span class="muted" dir="ltr">' + ui.esc(d.name || '') + '</span></div>' +
      ui.switchHtml('sw-bl', d.enabled, 'תאורה') + '</div>' +
      '<div class="slider-row"><span class="icon-btn big static" aria-hidden="true">' + ui.icon('sun') + '</span>' +
      '<input type="range" class="range" min="0" max="100" step="1" value="' + d.brightness + '" aria-label="בהירות התאורה">' +
      '<output class="range-val">' + d.brightness + '%</output></div>';
    var sw = slot.querySelector('#sw-bl'), r = slot.querySelector('.range'), out = slot.querySelector('.range-val');
    SS.paintRange(r);
    var on = d.enabled;
    sw.addEventListener('click', async function () {
      var next = !on;
      try { var x = await SS.bridge.call('/sys/keyboard/backlight/set', { enabled: next }); on = x.enabled; ui.setSwitch(sw, on); }
      catch (e) { ui.error(e, 'תאורת מקלדת'); }
    });
    var push = ui.debounce(function (v) {
      SS.bridge.call('/sys/keyboard/backlight/set', { brightness: v }).catch(function (e) { ui.error(e, 'תאורת מקלדת'); });
    }, 250);
    r.addEventListener('input', function () { out.textContent = r.value + '%'; SS.paintRange(r); push(+r.value); });
  }
})();
