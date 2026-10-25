/* sections/energy.js - battery status, power plan, screen and sleep timeouts, battery saver */
(function () {
  'use strict';
  var SS = (window.SS = window.SS || {});
  var ui = SS.ui;
  SS.sections = SS.sections || [];

  var MINUTES = [1, 2, 5, 10, 15, 30, 60, 0];
  var SAVER = [[0, 'כבוי'], [20, '20%'], [30, '30%'], [50, '50%'], [100, 'תמיד בלי חשמל']];

  SS.sections.push({
    id: 'energy', title: 'חשמל וסוללה', icon: 'battery',
    mount: function (body) {
      body.innerHTML =
        '<div class="card"><div class="card-title">מצב הסוללה</div><div data-slot="bat">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">תוכנית צריכה</div><div data-slot="plan">' + ui.loading() + '</div></div>' +
        '<div class="card"><div class="card-title">כיבוי מסך והשהיה</div><div data-slot="time">' + ui.loading() + '</div></div>' +
        '<div class="card" data-card="saver" hidden><div class="card-title">חיסכון בסוללה</div><div data-slot="saver"></div></div>';
    },
    load: async function (body) {
      var d;
      try { d = await SS.bridge.call('/sys/energy/get', {}, { timeoutMs: 30000 }); }
      catch (e) {
        var retry = function () { SS.sections.find(function (s) { return s.id === 'energy'; }).load(body); };
        ['bat', 'plan', 'time'].forEach(function (k) { body.querySelector('[data-slot=' + k + ']').innerHTML = ui.errorBox(e, retry); });
        return;
      }
      render(body, d);
    }
  });

  function minLabel(m) { return m === 0 ? 'לעולם לא' : (m === 1 ? 'דקה' : (m === 60 ? 'שעה' : m + ' דקות')); }

  async function apply(body, patch) {
    try { render(body, await SS.bridge.call('/sys/energy/set', patch, { timeoutMs: 30000 })); }
    catch (e) { ui.error(e, 'הגדרות חשמל'); SS.sections.find(function (s) { return s.id === 'energy'; }).load(body); }
  }

  function presetRow(opts, current, attr, extra) {
    return '<div class="preset-row">' + opts.map(function (o) {
      return '<button class="btn ' + (o[0] === current ? 'btn-primary' : 'btn-tonal') + '" ' + attr + '="' + o[0] + '"' + (extra || '') + '>' + o[1] + '</button>';
    }).join('') + '</div>';
  }

  function render(body, d) {
    var b = d.battery;

    /* ----- status ----- */
    var bat = body.querySelector('[data-slot=bat]');
    if (!b) {
      bat.innerHTML = '<div class="state-box">' + ui.icon('plug') + '<div><b>אין סוללה</b><p class="muted">זה מחשב שולחני או שאין סוללה שמזוהה במחשב.</p></div></div>';
    } else {
      var left = b.minutesLeft != null && !b.charging ? ' · נשארו בערך ' + Math.floor(b.minutesLeft / 60) + ' שעות ו-' + (b.minutesLeft % 60) + ' דקות' : '';
      bat.innerHTML = '<span class="battery"><span class="battery-shell"><i style="width:' + Math.max(4, Math.min(100, b.percent)) + '%"></i></span>' +
        b.percent + '%' + (b.charging ? ' · בטעינה' : '') + '</span>' +
        '<p class="muted">' + (b.saverOn ? 'חיסכון בסוללה פעיל כעת' : 'חיסכון בסוללה כבוי כעת') + left + '</p>';
    }

    /* ----- power plan ----- */
    var plan = body.querySelector('[data-slot=plan]');
    var plans = d.plans || [];
    var active = (plans.find(function (p) { return p.active; }) || {}).id;
    plan.innerHTML = plans.length
      ? presetRow(plans.map(function (p) { return [p.id, ui.esc(p.name)]; }), active, 'data-plan')
      : '<div class="empty">לא נמצאו תוכניות צריכה.</div>';
    plan.querySelectorAll('[data-plan]').forEach(function (x) {
      x.addEventListener('click', function () { apply(body, { plan: x.dataset.plan }); });
    });

    /* ----- timeouts ----- */
    var time = body.querySelector('[data-slot=time]');
    var parts = [];
    [['screenOff', 'כיבוי המסך'], ['sleepAfter', 'מעבר למצב שינה']].forEach(function (g) {
      var v = d[g[0]];
      if (!v) return;
      var srcs = b ? [['dc', 'על סוללה'], ['ac', 'בחיבור לחשמל']] : [['ac', 'כיבוי אחרי']];
      srcs.forEach(function (s) {
        var secs = v[s[0]];
        var cur = secs % 60 === 0 ? secs / 60 : -1;
        /* תפריט נפתח במקום שורת כפתורים: ארבע שורות של שבעה כפתורים היו עומס */
        var opts = MINUTES.map(function (m) { return '<option value="' + m + '"' + (m === cur ? ' selected' : '') + '>' + minLabel(m) + '</option>'; }).join('');
        if (cur === -1) opts = '<option value="" selected disabled>' + secs + ' שניות</option>' + opts;
        parts.push('<div class="setting-row"><div class="setting-text"><b>' + g[1] + '</b><span class="muted">' + s[1] + '</span></div>' +
          '<select class="input time-select" data-t data-k="' + g[0] + (s[0] === 'ac' ? 'Ac' : 'Dc') + '" aria-label="' + g[1] + ' — ' + s[1] + '">' + opts + '</select></div>');
      });
    });
    time.innerHTML = parts.length ? parts.join('') : '<div class="empty">ההגדרות האלה אינן זמינות במחשב זה.</div>';
    time.querySelectorAll('[data-t]').forEach(function (x) {
      x.addEventListener('change', function () {
        if (x.value === '') return;
        var p = {}; p[x.dataset.k] = (+x.value) * 60;
        apply(body, p);
      });
    });

    /* ----- battery saver ----- */
    var card = body.querySelector('[data-card=saver]');
    card.hidden = !(b && d.saverThreshold != null);
    if (!card.hidden) {
      var sv = card.querySelector('[data-slot=saver]');
      sv.innerHTML = '<p class="muted">החיסכון מופעל אוטומטית כשהטעינה יורדת מתחת לאחוז שנבחר, ומפחית בהירות ופעילות ברקע.</p>' +
        presetRow(SAVER, d.saverThreshold, 'data-s');
      sv.querySelectorAll('[data-s]').forEach(function (x) {
        x.addEventListener('click', function () { apply(body, { saverThreshold: +x.dataset.s }); });
      });
    }
  }
})();
