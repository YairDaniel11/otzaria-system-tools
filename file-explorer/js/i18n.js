/* i18n.js - שכבת תרגום לאנגלית. המחרוזות בקוד נשארות בעברית; כשהשפה אנגלית,
   צומתי טקסט ותכונות (title, placeholder, aria-label) מתורגמים לפי מילון מדויק ותבניות.
   השפה: העדפה שמורה (he/en/auto), אחרת שפת אוצריא (app.getLocale), אחרת שפת הדפדפן. */
(function () {
  'use strict';
  var I = window.I18N = window.I18N || {};
  var dict = {}, pats = [], lang = 'he', obs = null;
  var KEY = I.storageKey || 'i18n.lang';
  var ATTRS = ['title', 'placeholder', 'aria-label'];
  var SKIP = '.fx-pre, .fx-text, .fx-doc, .fx-name, input, textarea, [data-noi18n]';
  var HEB = /[֐-׿]/;

  I.add = function (d, p) {
    Object.keys(d || {}).forEach(function (k) { dict[k] = d[k]; });
    (p || []).forEach(function (x) { pats.push(x); });
    frags = null; fcache = {};
  };

  /* #69: מחרוזת שהורכבה מכמה חלקים (טקסט + מספר + טקסט) — מתרגמים כל חלק עברי מוכר בנפרד.
     רק התאמה של מילים שלמות (לא באמצע מילה), מהארוך לקצר; מתקבל רק כשלא נשאר עברית בכלל. */
  var frags = null, fcache = {};
  var HEBL = /[\u05D0-\u05EA\u05F3\u05F4]/;
  function fragTr(s) {
    if (Object.prototype.hasOwnProperty.call(fcache, s)) return fcache[s];
    if (!frags) frags = Object.keys(dict).filter(function (k) { return k.length >= 2 && HEB.test(k); }).sort(function (a, b) { return b.length - a.length; });
    var out = s;
    for (var i = 0; i < frags.length && HEB.test(out); i++) {
      var k = frags[i], at = 0, j;
      while ((j = out.indexOf(k, at)) >= 0) {
        var before = j > 0 ? out.charAt(j - 1) : '', after = out.charAt(j + k.length);
        if ((before && HEBL.test(before)) || (after && HEBL.test(after))) { at = j + 1; continue; }
        out = out.slice(0, j) + dict[k] + out.slice(j + k.length);
        at = j + dict[k].length;
      }
    }
    var r = HEB.test(out) ? null : out;
    fcache[s] = r;
    return r;
  }
  I.lang = function () { return lang; };
  I.isEn = function () { return lang === 'en'; };

  function tr(s) {
    if (!s || !HEB.test(s)) return null;
    var m = /^(\s*)([\s\S]*?)(\s*)$/.exec(s);
    var core = m[2];
    if (Object.prototype.hasOwnProperty.call(dict, core)) return m[1] + dict[core] + m[3];
    for (var i = 0; i < pats.length; i++) {
      var r = pats[i][0].exec(core);
      if (r) {
        var out = typeof pats[i][1] === 'function' ? pats[i][1](r) : core.replace(pats[i][0], pats[i][1]);
        return m[1] + out + m[3];
      }
    }
    var f = fragTr(core);
    return f == null ? null : m[1] + f + m[3];
  }
  I.t = function (s) {
    if (lang !== 'en' || s == null) return s;
    var r = tr(String(s));
    return r == null ? s : r;
  };

  function skip(el) { return el && el.closest && el.closest(SKIP); }
  function doText(n) {
    if (skip(n.parentElement)) return;
    var r = tr(n.nodeValue);
    if (r != null && r !== n.nodeValue) n.nodeValue = r;
  }
  function doAttrs(el) {
    if (el.closest && el.closest('[data-noi18n]')) return;
    for (var i = 0; i < ATTRS.length; i++) {
      var v = el.getAttribute(ATTRS[i]);
      if (v) { var r = tr(v); if (r != null && r !== v) el.setAttribute(ATTRS[i], r); }
    }
  }
  function walk(root) {
    if (!root) return;
    if (root.nodeType === 3) { doText(root); return; }
    if (root.nodeType !== 1) return;
    doAttrs(root);
    var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
    var n;
    while ((n = w.nextNode())) {
      if (n.nodeType === 3) doText(n); else doAttrs(n);
    }
  }

  function apply() {
    var html = document.documentElement;
    html.lang = lang === 'en' ? 'en' : 'he';
    html.dir = lang === 'en' ? 'ltr' : 'rtl';
    if (lang !== 'en') return;
    if (document.title) document.title = I.t(document.title);
    walk(document.body);
    if (obs) return;
    obs = new MutationObserver(function (list) {
      for (var i = 0; i < list.length; i++) {
        var m = list[i];
        if (m.type === 'childList') { for (var j = 0; j < m.addedNodes.length; j++) walk(m.addedNodes[j]); }
        else if (m.type === 'characterData') doText(m.target);
        else if (m.type === 'attributes') doAttrs(m.target);
      }
    });
    obs.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ATTRS });
  }

  function norm(l) {
    l = String(l || '').toLowerCase();
    if (/^he|^iw/.test(l)) return 'he';
    if (/^en/.test(l)) return 'en';
    return '';
  }
  I.pref = function () {
    try { return localStorage.getItem(KEY) || 'auto'; } catch (e) { return 'auto'; }
  };
  /* קובע שפה: מההעדפה, אחרת מאוצריא, אחרת מהדפדפן. מחזיר Promise */
  I.init = async function (payload) {
    var p = I.pref();
    var l = p === 'he' || p === 'en' ? p : '';
    if (!l && payload) l = norm(payload.locale || (payload.app && payload.app.locale) || (payload.settings && payload.settings.locale));
    if (!l && window.Otzaria) {
      try {
        var r = await window.Otzaria.call('app.getLocale', {});
        var d = r && (r.data || r);
        l = norm(d && (d.locale || d.languageCode || d.language || d));
      } catch (e) { l = ''; }
    }
    if (!l) l = norm(navigator.language) === 'en' && !/he|iw/i.test((navigator.languages || []).join(',')) ? 'en' : 'he';
    lang = l;
    apply();
    return lang;
  };
  /* החלפת שפה ידנית: שומר וטוען מחדש, כדי לחזור לטקסט המקורי בבטחה */
  I.set = function (l) {
    try { localStorage.setItem(KEY, l); } catch (e) { /* כלום */ }
    location.reload();
  };
  I.toggleLabel = function () { return lang === 'en' ? 'עברית' : 'English'; };
  I.toggle = function () { I.set(lang === 'en' ? 'he' : 'en'); };
})();
