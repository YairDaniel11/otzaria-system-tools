/* sections/cursor.js - כרטיס "מצביע וסמן טקסט" בתוך מקטע העכבר: גודל, צבע, איתור ב-Ctrl, שובל, הסתרה בהקלדה, עובי סמן טקסט.
   קובץ נפרד שעוטף את מקטע 'mouse' (בלי לשנות את mouse.js), כך שהרשאות הנעילה של המקטע חלות גם עליו.
   נתיבי גשר: /sys/mouse/cursor/get, /sys/mouse/cursor/set (bridge/CursorApi.cs). */
(function () {
  'use strict';
  var SS = window.SS || {};
  var ui = SS.ui;
  var sec = (SS.sections || []).find(function (s) { return s.id === 'mouse'; });
  if (!sec || !ui) return;

  sec.keywords += ' מצביע סמן גודל צבע שחור הפוך איתור כונטרול ctrl שובל עובי סמן טקסט cursor pointer caret';
  var SW = ['#2F6FDE', '#D93025', '#188038', '#F29900', '#A142F4', '#E8710A', '#00A3BF', '#FFFFFF', '#000000'];

  var origMount = sec.mount, origLoad = sec.load;
  sec.mount = function (body) {
    origMount.call(this, body);
    var c = document.createElement('div');
    c.className = 'card'; c.dataset.card = 'cursor';
    c.innerHTML = '<div class="card-title">' + ui.icon('mouse') + 'מצביע וסמן טקסט</div><div data-slot="cursor">' + ui.loading() + '</div>';
    body.insertBefore(c, body.querySelector('[data-card=tp]'));
  };
  sec.load = function (body) {
    var p = origLoad.apply(this, arguments);
    loadCursor(body);
    return p;
  };

  function loadCursor(body) {
    var slot = body.querySelector('[data-slot=cursor]');
    if (!slot) return;
    SS.bridge.call('/sys/mouse/cursor/get').then(function (d) { render(body, slot, d); }, function () {
      slot.innerHTML = '<p class="muted small">' + ui.icon('info') + ' הגדרות המצביע יופיעו כאן אחרי עדכון הגשר לגרסה הבאה.</p>';
    });
  }

  function send(body, slot, patch) {
    slot.classList.add('is-busy');
    return SS.bridge.call('/sys/mouse/cursor/set', patch).then(function (d) {
      slot.classList.remove('is-busy'); return d;
    }, function (e) { slot.classList.remove('is-busy'); ui.error(e, 'מצביע'); loadCursor(body); });
  }

  function row(k, title, sub, on) {
    return '<div class="setting-row"><div class="setting-text"><b>' + title + '</b><span class="muted">' + sub + '</span></div>' +
      ui.switchHtml('cur-' + k, !!on, title) + '</div>';
  }

  function render(body, slot, d) {
    var styles = [['default', 'לבן (רגיל)', true], ['black', 'שחור', d.blackAvailable], ['inverted', 'הפוך', d.invertedAvailable], ['custom', 'צבעוני', true]];
    var prevC = d.style === 'custom' ? d.color : d.style === 'black' ? '#000000' : '#FFFFFF';
    slot.innerHTML =
      '<div class="cur-preview" aria-hidden="true"><div class="cur-arrow" style="--cur-c:' + ui.esc(prevC) + ';--cur-s:' + d.size + '"></div>' +
      '<span class="muted small">תצוגה מקדימה</span></div>' +
      '<div class="setting-text"><b>גודל המצביע</b></div>' +
      '<div class="slider-row"><input type="range" class="range" data-k="size" min="1" max="15" step="1" value="' + d.size + '" aria-label="גודל המצביע"><output class="range-val" dir="ltr">' + d.size + '</output></div>' +
      '<div class="setting-text"><b>סגנון וצבע</b></div><div class="preset-row">' + styles.filter(function (s) { return s[2]; }).map(function (s) {
        return '<button class="btn ' + (s[0] === d.style ? 'btn-primary' : 'btn-tonal') + '" data-style="' + s[0] + '">' + s[1] + '</button>';
      }).join('') + '</div>' +
      '<div class="cur-swatches"' + (d.style === 'custom' ? '' : ' hidden') + '>' + SW.map(function (c) {
        return '<button class="cur-sw' + (c === String(d.color).toUpperCase() ? ' on' : '') + '" style="--sw:' + c + '" data-color="' + c + '" title="' + c + '" aria-label="צבע ' + c + '"></button>';
      }).join('') + '<label class="cur-sw cur-sw-pick" title="צבע אחר"><input type="color" value="' + ui.esc(d.color) + '" aria-label="בחירת צבע אחר"></label></div>' +
      row('sonar', 'הצגת מיקום המצביע בלחיצה על Ctrl', 'עיגולים מסביב למצביע כשלוחצים ומשחררים Ctrl — כשהמצביע „נעלם”', d.sonar) +
      row('vanish', 'הסתרת המצביע בזמן הקלדה', 'המצביע חוזר מיד כשמזיזים את העכבר', d.vanish) +
      row('trails', 'שובל מצביע', 'עקבות קצרות אחרי המצביע שמקלות לעקוב אחריו', d.trails > 0) +
      '<div class="setting-text"><b>עובי סמן הטקסט</b><span class="muted">הקו המהבהב בתיבות טקסט</span></div>' +
      '<div class="slider-row"><input type="range" class="range" data-k="caretWidth" min="1" max="20" step="1" value="' + d.caretWidth + '" aria-label="עובי סמן הטקסט">' +
      '<output class="range-val" dir="ltr"><i class="cur-caret" style="--cw:' + d.caretWidth + '"></i>' + d.caretWidth + '</output></div>' +
      '<div class="row-actions"><button class="btn btn-tonal" data-act="cur-reset">איפוס מצביע וסמן</button></div>';

    slot.querySelectorAll('.range').forEach(function (r) {
      if (SS.paintRange) SS.paintRange(r);
      var out = r.parentNode.querySelector('.range-val');
      var push = ui.debounce(function (v) { var p = {}; p[r.dataset.k] = v; send(body, slot, p); }, 300);
      r.addEventListener('input', function () {
        if (SS.paintRange) SS.paintRange(r);
        var v = +r.value;
        out.innerHTML = r.dataset.k === 'caretWidth' ? '<i class="cur-caret" style="--cw:' + v + '"></i>' + v : String(v);
        if (r.dataset.k === 'size') slot.querySelector('.cur-arrow').style.setProperty('--cur-s', v);
        push(v);
      });
    });
    function re(x) { if (x) render(body, slot, x); }
    slot.querySelectorAll('[data-style]').forEach(function (b) {
      b.addEventListener('click', function () { send(body, slot, { style: b.dataset.style }).then(re); });
    });
    slot.querySelectorAll('[data-color]').forEach(function (b) {
      b.addEventListener('click', function () { send(body, slot, { style: 'custom', color: b.dataset.color }).then(re); });
    });
    var pick = slot.querySelector('.cur-sw-pick input');
    pick.addEventListener('change', function () { send(body, slot, { style: 'custom', color: pick.value }).then(re); });
    ['sonar', 'vanish', 'trails'].forEach(function (k) {
      var sw = slot.querySelector('#cur-' + k);
      var on = k === 'trails' ? d.trails > 0 : !!d[k];
      sw.addEventListener('click', function () {
        on = !on; ui.setSwitch(sw, on);
        var p = {}; p[k] = k === 'trails' ? (on ? 5 : 0) : on;
        send(body, slot, p);
      });
    });
    slot.querySelector('[data-act=cur-reset]').addEventListener('click', function () {
      send(body, slot, { size: 1, style: 'default', sonar: false, vanish: true, trails: 0, caretWidth: 1 }).then(re);
    });
  }
})();
