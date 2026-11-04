/* sections/kiosk-tabs.js - חלוקת טאב "מצב נעילה" ללשוניות פנימיות (סקירה / הגדרות / אבטחה ושחזור / הסברים).
   קובץ נפרד שלא משנה את kiosk.js: אחרי כל רינדור הוא רק מסמן כרטיסים קיימים ומסתיר את אלה שלא בלשונית הנבחרת
   (בלי להזיז או ליצור מחדש — האירועים והלוגיקה של kiosk.js נשארים כפי שהם). כרטיס הסטטוס תמיד גלוי למעלה. */
(function () {
  'use strict';
  var SS = window.SS || {};
  var ui = SS.ui;
  var sec = (SS.sections || []).find(function (s) { return s.id === 'kiosk'; });
  if (!sec || !ui) return;

  var TABS = [
    ['overview', 'סקירה', 'shield'],
    ['settings', 'הגדרות', 'settings'],
    ['security', 'אבטחה ושחזור', 'key'],
    ['help', 'הסברים', 'info']
  ];
  var current = 'overview';

  function classify(el) {
    if (el.classList.contains('status-card')) return 'top';
    if (el.classList.contains('health-panel')) return 'overview';
    if (el.classList.contains('emergency') || el.classList.contains('info-group')) return 'security';
    if (el.classList.contains('explain-group')) return 'help';
    var t = (el.querySelector('.card-title') || {}).textContent || '';
    if (/קוד השחזור עוד לא נשמר|אשף הגדרה/.test(t)) return 'top';   /* דחוף — לא מוסתר */
    return 'settings';
  }

  function apply(body) {
    var bar = body.querySelector(':scope > .k-tabs');
    var cards = Array.prototype.filter.call(body.children, function (c) { return c !== bar; });
    var count = {};
    cards.forEach(function (c) {
      var g = c.dataset.ktab || (c.dataset.ktab = classify(c));
      count[g] = (count[g] || 0) + 1;
    });
    if (!count[current]) current = TABS.filter(function (t) { return count[t[0]]; }).map(function (t) { return t[0]; })[0] || 'overview';
    cards.forEach(function (c) { c.classList.toggle('k-off', c.dataset.ktab !== 'top' && c.dataset.ktab !== current); });
    bar.querySelectorAll('[data-ktab-btn]').forEach(function (b) {
      var on = b.dataset.ktabBtn === current;
      b.hidden = !count[b.dataset.ktabBtn];
      b.classList.toggle('on', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
      b.tabIndex = on ? 0 : -1;
    });
    var on = bar.querySelector('.on');
    if (on) { bar.style.setProperty('--k-x', on.offsetLeft + 'px'); bar.style.setProperty('--k-w', on.offsetWidth + 'px'); }
  }

  function build(body) {
    if (!body.querySelector(':scope > .status-card') || body.querySelector(':scope > .k-tabs')) return;
    var bar = document.createElement('div');
    bar.className = 'k-tabs'; bar.setAttribute('role', 'tablist'); bar.setAttribute('aria-label', 'חלקי מצב הנעילה');
    bar.innerHTML = '<span class="k-ind" aria-hidden="true"></span>' + TABS.map(function (t) {
      return '<button type="button" role="tab" class="k-tab" data-ktab-btn="' + t[0] + '">' + ui.icon(t[2]) + '<span>' + t[1] + '</span></button>';
    }).join('');
    var anchor = body.querySelector(':scope > .status-card');
    while (anchor.nextElementSibling && classify(anchor.nextElementSibling) === 'top') anchor = anchor.nextElementSibling;
    anchor.after(bar);
    bar.addEventListener('click', function (e) {
      var b = e.target.closest('[data-ktab-btn]'); if (!b) return;
      current = b.dataset.ktabBtn; apply(body);
      body.scrollTop = 0;
    });
    bar.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      var vis = Array.prototype.filter.call(bar.querySelectorAll('[data-ktab-btn]'), function (b) { return !b.hidden; });
      var i = vis.indexOf(bar.querySelector('.on'));
      var rtl = getComputedStyle(bar).direction === 'rtl';
      var n = vis[(i + ((e.key === 'ArrowLeft') !== rtl ? 1 : -1) + vis.length) % vis.length];
      if (n) { n.click(); n.focus(); e.preventDefault(); }
    });
    apply(body);
    requestAnimationFrame(function () { apply(body); });
  }

  var origMount = sec.mount;
  sec.mount = function (body) {
    origMount.call(this, body);
    if (body.__kTabsObs) return;
    body.__kTabsObs = new MutationObserver(function () { build(body); });
    body.__kTabsObs.observe(body, { childList: true });
  };
})();
