/* palette.js - פלטת פקודות (Ctrl+K / Ctrl+P): חיפוש וקפיצה לכל מקטע הגדרות ולפעולות הסרגל העליון.
   קובץ נפרד, נטען אחרי app.js; משתמש רק ב-SS.sections, SS.show ו-SS.sectionVisible (מכבד את הרשאות הנעילה). */
(function () {
  'use strict';
  var SS = window.SS || {};
  var ui = SS.ui;
  if (!ui) return;
  var scrim, input, list, items = [], sel = 0;

  var ACTIONS = [
    ['top-refresh', 'רענון', 'refresh', 'רענון טעינה מחדש'],
    ['top-lang', 'החלפת שפה (English)', 'language', 'שפה אנגלית english language'],
    ['top-compact', 'מצב קומפקטי', 'display', 'קומפקטי אייקונים'],
    ['top-feedback', 'שליחת הערה', 'info', 'משוב הערה דיווח באג'],
    ['top-about', 'אודות', 'info', 'אודות גרסה']
  ];

  function norm(s) { return String(s || '').toLowerCase(); }
  function visible(s) { return !SS.sectionVisible || SS.sectionVisible(s.id); }

  function collect(q) {
    q = norm(q).trim();
    var out = [];
    (SS.sections || []).filter(visible).forEach(function (s) {
      var hay = norm(s.title + ' ' + (s.keywords || '') + ' ' + (window.I18N ? window.I18N.t(s.title) : ''));
      var score = !q ? 1 : norm(s.title).indexOf(q) === 0 ? 3 : norm(s.title).indexOf(q) >= 0 ? 2 : hay.indexOf(q) >= 0 ? 1 : 0;
      if (score) out.push({ score: score, kind: 'הגדרה', title: s.title, icon: s.icon, run: function () { SS.show(s.id); } });
    });
    ACTIONS.forEach(function (a) {
      var b = document.getElementById(a[0]);
      if (!b || b.hidden) return;
      if (q && norm(a[1] + ' ' + a[3]).indexOf(q) < 0) return;
      out.push({ score: 0.5, kind: 'פעולה', title: a[1], icon: a[2], run: function () { b.click(); } });
    });
    return out.sort(function (x, y) { return y.score - x.score; }).slice(0, 30);
  }

  function render() {
    items = collect(input.value);
    if (sel >= items.length) sel = Math.max(0, items.length - 1);
    list.innerHTML = items.length ? items.map(function (it, i) {
      return '<button type="button" role="option" class="pal-item' + (i === sel ? ' on' : '') + '" data-i="' + i + '" aria-selected="' + (i === sel) + '">' +
        ui.icon(it.icon) + '<span class="pal-t">' + ui.esc(it.title) + '</span><span class="pal-k">' + it.kind + '</span></button>';
    }).join('') : '<div class="pal-empty muted">לא נמצא דבר</div>';
    var on = list.querySelector('.on'); if (on) on.scrollIntoView({ block: 'nearest' });
  }

  function open() {
    if (!scrim) build();
    sel = 0; input.value = '';
    scrim.hidden = false;
    requestAnimationFrame(function () { scrim.classList.add('open'); input.focus(); });
    render();
  }
  function close() {
    if (!scrim || scrim.hidden) return;
    scrim.classList.remove('open');
    setTimeout(function () { scrim.hidden = true; }, 180);
  }
  function run(i) { var it = items[i]; if (!it) return; close(); setTimeout(it.run, 30); }

  function build() {
    scrim = document.createElement('div');
    scrim.className = 'pal-scrim'; scrim.hidden = true;
    scrim.innerHTML = '<div class="pal" role="dialog" aria-modal="true" aria-label="חיפוש מהיר">' +
      '<div class="pal-head">' + ui.icon('search') + '<input class="pal-in" type="text" placeholder="לאן לעבור? הקלידו שם הגדרה או פעולה…" aria-label="חיפוש מהיר" autocomplete="off">' +
      '<kbd>Esc</kbd></div><div class="pal-list" role="listbox"></div>' +
      '<div class="pal-foot muted small"><span><kbd>↑</kbd><kbd>↓</kbd> מעבר</span><span><kbd>Enter</kbd> פתיחה</span><span><kbd>Ctrl</kbd>+<kbd>K</kbd> חיפוש מהיר</span></div></div>';
    document.body.appendChild(scrim);
    input = scrim.querySelector('.pal-in'); list = scrim.querySelector('.pal-list');
    scrim.addEventListener('mousedown', function (e) { if (e.target === scrim) close(); });
    input.addEventListener('input', function () { sel = 0; render(); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { sel = Math.min(items.length - 1, sel + 1); render(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { sel = Math.max(0, sel - 1); render(); e.preventDefault(); }
      else if (e.key === 'Enter') { run(sel); e.preventDefault(); }
      else if (e.key === 'Escape') { close(); e.preventDefault(); }
    });
    list.addEventListener('click', function (e) { var b = e.target.closest('.pal-item'); if (b) run(+b.dataset.i); });
    list.addEventListener('mousemove', function (e) {
      var b = e.target.closest('.pal-item'); if (!b || +b.dataset.i === sel) return;
      sel = +b.dataset.i; list.querySelectorAll('.pal-item').forEach(function (x, i) { x.classList.toggle('on', i === sel); });
    });
  }

  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'k' || e.key === 'K' || e.code === 'KeyK')) {
      e.preventDefault(); if (scrim && !scrim.hidden) close(); else open();
    }
  });

  /* כפתור בסרגל העליון */
  function addBtn() {
    var bar = document.querySelector('.topbar'), spacer = bar && bar.querySelector('.topbar-spacer');
    if (!spacer || document.getElementById('top-palette')) return;
    var b = document.createElement('button');
    b.className = 'pal-trigger'; b.id = 'top-palette'; b.type = 'button';
    b.title = 'חיפוש מהיר (Ctrl+K)'; b.setAttribute('aria-label', 'חיפוש מהיר');
    b.innerHTML = ui.icon('search') + '<span>חיפוש מהיר</span><kbd>Ctrl K</kbd>';
    b.addEventListener('click', open);
    spacer.after(b);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', addBtn); else addBtn();
  SS.openPalette = open;
})();
