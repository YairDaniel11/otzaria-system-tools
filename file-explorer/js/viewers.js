/* viewers.js - צופים פנימיים: PDF (pdf.js) ו-Word (jszip + document.xml), בשכבה על מסך הסייר */
(function () {
  'use strict';
  var FX = window.FX = window.FX || {};
  var MAX_BYTES = 50 * 1024 * 1024;

  /* ---------- קריאת קובץ כבתים, משני הספקים ---------- */
  function b64ToBytes(b64) {
    var bin = atob(b64);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  FX.readBytes = async function (prov, entry) {
    if (prov.kind === 'bridge') {
      var d = await FX.Bridge.call('/fs/read', { path: entry.path, maxBytes: MAX_BYTES }, { timeoutMs: 120000 });
      if (d.truncated) throw FX.OpError('too_large', FX.t('הקובץ גדול מדי לתצוגה (מעל 50MB)'));
      if (d.encoding === 'utf8') return new TextEncoder().encode(d.content);
      return b64ToBytes(d.content || '');
    }
    var r = await prov.read(entry);
    var src = r.url || r.dataUrl;
    if (!src) throw FX.OpError('unsupported', FX.t('אין אפשרות לקרוא את הקובץ'));
    var resp = await fetch(src);
    if (!resp.ok) throw FX.OpError('internal', FX.t('קריאת הקובץ נכשלה') + ' (HTTP ' + resp.status + ')');
    return new Uint8Array(await resp.arrayBuffer());
  };

  /* ---------- שכבת צפייה משותפת ---------- */
  var cur = null;
  function closeViewer() {
    if (!cur) return;
    try { if (cur.cleanup) cur.cleanup(); } catch (e) { /* כלום */ }
    cur.el.remove();
    document.removeEventListener('keydown', cur.onKey, true);
    var prevFocus = cur.prevFocus;
    cur = null;
    /* החזרת פוקוס לאלמנט שהיה ממוקד לפני פתיחת הצופה (למשל השורה בסייר שנפתחה) */
    if (prevFocus && prevFocus.focus && document.contains(prevFocus)) prevFocus.focus();
  }
  function openShell(entry, opts) {
    var prevFocus = document.activeElement;
    closeViewer();
    var ic = FX.icon;
    var el = FX.ui.el(
      '<div class="fx-viewer" role="dialog" aria-modal="true" aria-label="' + FX.esc(entry.name) + '">' +
      '<div class="fx-vbar">' +
      '<button class="icon-btn" data-v="close" title="' + FX.t('סגירה') + ' (Esc)" aria-label="' + FX.t('סגירה') + '">' + ic('close') + '</button>' +
      '<button class="icon-btn" data-v="prev" title="' + FX.t('הקובץ הקודם') + ' (Alt+→)" aria-label="' + FX.t('הקובץ הקודם') + '" hidden>' + ic('chevron') + '</button>' +
      '<button class="icon-btn" data-v="next" title="' + FX.t('הקובץ הבא') + ' (Alt+←)" aria-label="' + FX.t('הקובץ הבא') + '" hidden><span style="display:inline-flex;transform:scaleX(-1)">' + ic('chevron') + '</span></button>' +
      '<b class="fx-vtitle" title="' + FX.esc(entry.name) + '">' + FX.esc(entry.name) + '</b>' +
      '<span class="fx-vtools">' + (opts.tools || '') + '</span>' +
      (opts.canOpen ? '<button class="btn btn-text" data-v="ext">' + ic('open') + FX.t('פתיחה ביישום') + '</button>' : '') +
      '</div><div class="fx-vbody" tabindex="0">' + FX.ui.loading(FX.t('טוען…')) + '</div></div>');
    document.body.appendChild(el);
    /* מעבר בין קבצים באותה תיקייה */
    function sibling(dir) {
      if (!FX.app || !FX.app.siblings) return null;
      var l = FX.app.siblings(entry), i = l.findIndex(function (x) { return x.path === entry.path; });
      return i < 0 ? null : (l[i + dir] || null);
    }
    function go(dir) { var n = sibling(dir); if (n) { closeViewer(); FX.app.open(n); } }
    var bp = el.querySelector('[data-v=prev]'), bn = el.querySelector('[data-v=next]');
    if (sibling(-1)) bp.hidden = false;
    if (sibling(1)) bn.hidden = false;
    bp.addEventListener('click', function () { go(-1); });
    bn.addEventListener('click', function () { go(1); });
    var state = {
      el: el, body: el.querySelector('.fx-vbody'), cleanup: null, prevFocus: prevFocus,
      onKey: function (e) {
        if (e.key === 'Escape' && !document.querySelector('#dialogs .dlg-scrim')) { e.preventDefault(); e.stopPropagation(); closeViewer(); return; }
        /* לכידת פוקוס: Tab/Shift+Tab מסתובבים רק בתוך שכבת הצופה, לא חוזרים אל הסייר שמאחוריה.
           כשיש דיאלוג פתוח מעל הצופה (למשל מאפיינים/אישור) — הוא זה שלוכד את הפוקוס, לא הצופה. */
        if (!document.querySelector('#dialogs .dlg-scrim')) FX.ui.trapTab(el, e);
        if (e.altKey && (e.key === 'ArrowRight' || e.key === 'ArrowLeft') && !document.querySelector('#dialogs .dlg-scrim')) {
          e.preventDefault(); e.stopPropagation(); go(e.key === 'ArrowRight' ? -1 : 1); return;
        }
        if (opts.onKey) opts.onKey(e);
      }
    };
    document.addEventListener('keydown', state.onKey, true);
    el.querySelector('[data-v=close]').addEventListener('click', closeViewer);
    var ext = el.querySelector('[data-v=ext]');
    if (ext) ext.addEventListener('click', function () { opts.onOpenExternal(); });
    state.body.focus();
    cur = state;
    return state;
  }
  function fail(state, e) {
    state.body.innerHTML = '<div class="fx-empty">' + FX.icon('warning') + '<span>' + FX.esc(FX.errorText(e)) + '</span></div>';
  }
  FX.closeViewer = closeViewer;

  /* ---------- PDF ---------- */
  FX.viewPdf = async function (prov, entry) {
    var lib = window.pdfjsLib;
    var zoom = 1, doc = null, pages = [], io = null, fitWidth = true;
    var tools =
      '<button class="icon-btn" data-z="-" title="' + FX.t('הקטנה') + '" aria-label="' + FX.t('הקטנה') + '">−</button>' +
      '<span class="fx-vzoom" data-z="v">100%</span>' +
      '<button class="icon-btn" data-z="+" title="' + FX.t('הגדלה') + '" aria-label="' + FX.t('הגדלה') + '">+</button>' +
      '<button class="btn btn-text" data-z="fit">' + FX.t('רוחב מלא') + '</button>' +
      '<span class="fx-vpage"><input class="input" type="number" min="1" value="1" aria-label="' + FX.t('עמוד') + '"> / <span data-n>…</span></span>';
    var st = openShell(entry, {
      tools: tools,
      canOpen: prov.caps.open,
      onOpenExternal: function () { prov.open(entry.path).catch(function (e) { FX.ui.toast(FX.errorText(e), 'error'); }); },
      onKey: function (e) {
        if ((e.ctrlKey || e.metaKey) && (e.key === '+' || e.key === '=')) { e.preventDefault(); setZoom(zoom * 1.2); }
        else if ((e.ctrlKey || e.metaKey) && e.key === '-') { e.preventDefault(); setZoom(zoom / 1.2); }
      }
    });
    if (!lib) { fail(st, FX.OpError('unsupported', FX.t('רכיב ה-PDF לא נטען'))); return; }
    lib.GlobalWorkerOptions.workerSrc = 'vendor/pdf.worker.min.js';
    try {
      var bytes = await FX.readBytes(prov, entry);
      doc = await lib.getDocument({ data: bytes, isEvalSupported: false }).promise;
    } catch (e) {
      if (e && e.name === 'PasswordException') e = FX.OpError('locked', FX.t('הקובץ מוגן בסיסמה'));
      fail(st, e); return;
    }
    if (cur !== st) { doc.destroy(); return; }
    st.cleanup = function () { if (io) io.disconnect(); doc.destroy(); };
    st.el.querySelector('[data-n]').textContent = doc.numPages;
    st.body.innerHTML = '<div class="fx-pages"></div>';
    var wrap = st.body.firstChild;
    var first = await doc.getPage(1);
    var base = first.getViewport({ scale: 1 });

    function targetScale() {
      if (!fitWidth) return zoom;
      var w = st.body.clientWidth - 32;
      return Math.max(0.25, w / base.width);
    }
    function layout() {
      var s = targetScale();
      zoom = s;
      st.el.querySelector('[data-z=v]').textContent = Math.round(s * 100) + '%';
      pages.forEach(function (p) {
        p.el.style.width = Math.floor(p.w * s) + 'px';
        p.el.style.height = Math.floor(p.h * s) + 'px';
        p.rendered = 0;
        p.el.innerHTML = '';
      });
      if (io) { pages.forEach(function (p) { io.unobserve(p.el); io.observe(p.el); }); }
    }
    for (var i = 1; i <= doc.numPages; i++) {
      var pe = document.createElement('div');
      pe.className = 'fx-page';
      pe.dataset.p = i;
      wrap.appendChild(pe);
      pages.push({ n: i, el: pe, w: base.width, h: base.height, rendered: 0 });
    }
    async function render(p) {
      var s = zoom;
      if (p.rendered === s) return;
      p.rendered = s;
      var page = await doc.getPage(p.n);
      var vp = page.getViewport({ scale: s });
      if (p.w !== vp.width / s) { p.w = vp.width / s; p.h = vp.height / s; p.el.style.width = Math.floor(vp.width) + 'px'; p.el.style.height = Math.floor(vp.height) + 'px'; }
      var dpr = window.devicePixelRatio || 1;
      var c = document.createElement('canvas');
      c.width = Math.floor(vp.width * dpr); c.height = Math.floor(vp.height * dpr);
      c.style.width = Math.floor(vp.width) + 'px'; c.style.height = Math.floor(vp.height) + 'px';
      await page.render({ canvasContext: c.getContext('2d'), viewport: vp, transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null }).promise;
      if (p.rendered !== s || cur !== st) return;
      p.el.innerHTML = '';
      p.el.appendChild(c);
    }
    io = new IntersectionObserver(function (ents) {
      ents.forEach(function (en) {
        if (!en.isIntersecting) return;
        var p = pages[+en.target.dataset.p - 1];
        render(p).catch(function () { /* עמוד פגום — מדלגים */ });
      });
    }, { root: st.body, rootMargin: '600px 0px' });
    layout();

    var pageInp = st.el.querySelector('.fx-vpage input');
    pageInp.max = doc.numPages;
    st.body.addEventListener('scroll', FX.debounce(function () {
      var top = st.body.scrollTop + st.body.clientHeight / 3;
      for (var k = 0; k < pages.length; k++) {
        if (pages[k].el.offsetTop + pages[k].el.offsetHeight > top) { pageInp.value = k + 1; break; }
      }
    }, 120));
    pageInp.addEventListener('change', function () {
      var n = Math.max(1, Math.min(doc.numPages, parseInt(pageInp.value, 10) || 1));
      pages[n - 1].el.scrollIntoView({ block: 'start' });
    });
    function setZoom(z) {
      fitWidth = false;
      var ratio = st.body.scrollTop / Math.max(1, st.body.scrollHeight);
      zoom = Math.max(0.25, Math.min(5, z));
      layout();
      st.body.scrollTop = ratio * st.body.scrollHeight;
    }
    st.el.querySelector('.fx-vtools').addEventListener('click', function (e) {
      var b = e.target.closest('[data-z]');
      if (!b) return;
      if (b.dataset.z === '+') setZoom(zoom * 1.2);
      else if (b.dataset.z === '-') setZoom(zoom / 1.2);
      else if (b.dataset.z === 'fit') { fitWidth = true; layout(); }
    });
    var onResize = FX.debounce(function () { if (fitWidth && cur === st) layout(); }, 200);
    window.addEventListener('resize', onResize);
    var prevCleanup = st.cleanup;
    st.cleanup = function () { window.removeEventListener('resize', onResize); prevCleanup(); };
  };

  /* ---------- Word ---------- */
  var W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';
  function wAttr(el, name) { return el ? (el.getAttributeNS(W, name) || el.getAttribute('w:' + name)) : null; }
  function kids(el, name) {
    var out = [];
    for (var n = el.firstChild; n; n = n.nextSibling) if (n.nodeType === 1 && n.localName === name) out.push(n);
    return out;
  }
  function kid(el, name) { return el ? kids(el, name)[0] || null : null; }

  function runHtml(r) {
    var rpr = kid(r, 'rPr');
    var txt = '';
    for (var n = r.firstChild; n; n = n.nextSibling) {
      if (n.nodeType !== 1) continue;
      if (n.localName === 't') txt += FX.esc(n.textContent);
      else if (n.localName === 'tab') txt += '\t';
      else if (n.localName === 'br' || n.localName === 'cr') txt += '<br>';
    }
    if (!txt) return '';
    if (rpr) {
      var on = function (k) { var e = kid(rpr, k); return e && wAttr(e, 'val') !== '0' && wAttr(e, 'val') !== 'false'; };
      if (on('b')) txt = '<b>' + txt + '</b>';
      if (on('i')) txt = '<i>' + txt + '</i>';
      var u = kid(rpr, 'u');
      if (u && wAttr(u, 'val') !== 'none') txt = '<u>' + txt + '</u>';
      var va = kid(rpr, 'vertAlign');
      if (va && wAttr(va, 'val') === 'superscript') txt = '<sup>' + txt + '</sup>';
      if (va && wAttr(va, 'val') === 'subscript') txt = '<sub>' + txt + '</sub>';
    }
    return txt;
  }
  function paraHtml(p) {
    var ppr = kid(p, 'pPr');
    var style = ppr && kid(ppr, 'pStyle') ? String(wAttr(kid(ppr, 'pStyle'), 'val') || '') : '';
    var jc = ppr && kid(ppr, 'jc') ? wAttr(kid(ppr, 'jc'), 'val') : '';
    var list = ppr && kid(ppr, 'numPr');
    var html = '';
    (function walk(el) {
      for (var n = el.firstChild; n; n = n.nextSibling) {
        if (n.nodeType !== 1) continue;
        if (n.localName === 'r') html += runHtml(n);
        else if (n.localName === 'hyperlink' || n.localName === 'ins' || n.localName === 'smartTag' || n.localName === 'sdt' || n.localName === 'sdtContent') walk(n);
      }
    })(p);
    var m = /heading\s*([1-6])|^([1-6])$|כותרת\s*([1-6])/i.exec(style);
    var tag = m ? 'h' + (m[1] || m[2] || m[3]) : (/title/i.test(style) ? 'h1' : 'p');
    var cls = [];
    if (jc === 'center') cls.push('c');
    else if (jc === 'both' || jc === 'distribute') cls.push('j');
    if (list) cls.push('li');
    if (!html) return '<p class="empty">&nbsp;</p>';
    return '<' + tag + (cls.length ? ' class="' + cls.join(' ') + '"' : '') + ' dir="auto">' + html + '</' + tag + '>';
  }
  function tableHtml(tbl) {
    return '<table class="fx-doc-tbl">' + kids(tbl, 'tr').map(function (tr) {
      return '<tr>' + kids(tr, 'tc').map(function (tc) {
        var span = kid(kid(tc, 'tcPr'), 'gridSpan');
        return '<td' + (span ? ' colspan="' + (parseInt(wAttr(span, 'val'), 10) || 1) + '"' : '') + '>' + blocks(tc) + '</td>';
      }).join('') + '</tr>';
    }).join('') + '</table>';
  }
  function blocks(el) {
    var out = '';
    for (var n = el.firstChild; n; n = n.nextSibling) {
      if (n.nodeType !== 1) continue;
      if (n.localName === 'p') out += paraHtml(n);
      else if (n.localName === 'tbl') out += tableHtml(n);
      else if (n.localName === 'sdt') out += blocks(kid(n, 'sdtContent') || n);
    }
    return out;
  }
  FX.docxToHtml = async function (bytes) {
    if (!window.JSZip) throw FX.OpError('unsupported', FX.t('רכיב ה-Word לא נטען'));
    var zip = await window.JSZip.loadAsync(bytes);
    var f = zip.file('word/document.xml');
    if (!f) throw FX.OpError('bad_request', FX.t('הקובץ אינו מסמך Word תקין (docx)'));
    var xml = new DOMParser().parseFromString(await f.async('string'), 'application/xml');
    /* גופני המסמך (fonts.local.read): נשמרים להתאמה מול הגופנים המותקנים */
    FX.docFonts = [];
    Array.prototype.forEach.call(xml.getElementsByTagNameNS(W, 'rFonts'), function (r) {
      ['cs', 'ascii', 'hAnsi'].forEach(function (k) { var v = r.getAttributeNS(W, k); if (v && FX.docFonts.indexOf(v) < 0 && FX.docFonts.length < 8) FX.docFonts.push(v); });
    });
    var body = xml.getElementsByTagNameNS(W, 'body')[0];
    if (!body) throw FX.OpError('bad_request', FX.t('הקובץ אינו מסמך Word תקין (docx)'));
    return blocks(body);
  };

  FX.viewDocx = async function (prov, entry, startCols) {
    var cols = startCols === 1 ? 1 : 2;
    var st = openShell(entry, {
      tools: '<button class="btn btn-text" data-c="1">' + FX.t('טור אחד') + '</button><button class="btn btn-text" data-c="2">' + FX.t('שני טורים') + '</button>',
      canOpen: prov.caps.open,
      onOpenExternal: function () { prov.open(entry.path).catch(function (e) { FX.ui.toast(FX.errorText(e), 'error'); }); }
    });
    if (/\.doc$/i.test(entry.name)) {
      fail(st, FX.OpError('unsupported', FX.t('קובצי doc ישנים אינם נתמכים בתצוגה הפנימית. אפשר לפתוח אותם ביישום חיצוני.')));
      return;
    }
    var html;
    try { html = await FX.docxToHtml(await FX.readBytes(prov, entry)); } catch (e) { fail(st, e); return; }
    if (cur !== st) return;
    st.body.innerHTML = '<article class="fx-doc">' + (html || '<p>' + FX.t('המסמך ריק') + '</p>') + '</article>';
    var art = st.body.firstChild;
    if (window.SdkX && FX.docFonts && FX.docFonts.length) SdkX.matchFont(FX.docFonts).then(function (fam) { if (fam && art) art.style.fontFamily = '"' + fam + '", inherit'; });
    function apply() {
      art.classList.toggle('two', cols === 2);
      Array.prototype.forEach.call(st.el.querySelectorAll('[data-c]'), function (b) { b.classList.toggle('on', +b.dataset.c === cols); });
    }
    st.el.querySelector('.fx-vtools').addEventListener('click', function (e) {
      var b = e.target.closest('[data-c]');
      if (!b) return;
      cols = +b.dataset.c;
      try { localStorage.setItem('fx.docCols', String(cols)); } catch (er) { /* כלום */ }
      apply();
    });
    apply();
  };

  /* ---------- טקסט: צופה פנימי ---------- */
  FX.viewText = async function (prov, entry) {
    var st = openShell(entry, {
      canOpen: prov.caps.open,
      onOpenExternal: function () { prov.open(entry.path).catch(function (e) { FX.ui.toast(FX.errorText(e), 'error'); }); }
    });
    var d;
    try { d = await prov.read(entry); } catch (e) { fail(st, e); return; }
    if (cur !== st) return;
    if (d.tooLarge) { fail(st, FX.OpError('too_large', FX.t('הקובץ גדול מדי לתצוגה'))); return; }
    st.body.innerHTML = '<pre class="fx-text" dir="auto"></pre>' + (d.truncated ? '<p class="muted small fx-vnote">' + FX.t('הקובץ גדול — מוצג החלק הראשון בלבד.') + '</p>' : '');
    st.body.firstChild.textContent = d.text || '';
  };

  /* ---------- הדגשת מילת חיפוש בתוך תצוגה (חיפוש בתוכן) ---------- */
  FX.highlightIn = function (root, q) {
    q = String(q || '').trim();
    if (!root || !q) return 0;
    var low = q.toLowerCase(), marks = [];
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
    var nodes = [], n;
    while ((n = walker.nextNode())) { if (n.nodeValue && n.nodeValue.toLowerCase().indexOf(low) >= 0) nodes.push(n); if (nodes.length > 400) break; }
    nodes.forEach(function (node) {
      var text = node.nodeValue, lt = text.toLowerCase(), pos = 0, frag = document.createDocumentFragment(), at;
      while ((at = lt.indexOf(low, pos)) >= 0) {
        if (at > pos) frag.appendChild(document.createTextNode(text.slice(pos, at)));
        var m = document.createElement('mark'); m.className = 'fx-hit'; m.textContent = text.slice(at, at + q.length);
        frag.appendChild(m); marks.push(m); pos = at + q.length;
      }
      if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
      node.parentNode.replaceChild(frag, node);
    });
    if (marks.length) {
      var idx = 0;
      var go = function (d) {
        marks[idx].classList.remove('cur');
        idx = (idx + d + marks.length) % marks.length;
        marks[idx].classList.add('cur');
        marks[idx].scrollIntoView({ block: 'center' });
      };
      marks[0].classList.add('cur');
      setTimeout(function () { marks[0].scrollIntoView({ block: 'center' }); }, 30);
      /* F3 / Shift+F3: להתאמה הבאה/הקודמת בתוך הצופה */
      var kd = function (e) {
        if (!document.body.contains(root)) { document.removeEventListener('keydown', kd, true); return; }
        if (e.key === 'F3') { e.preventDefault(); go(e.shiftKey ? -1 : 1); }
      };
      document.addEventListener('keydown', kd, true);
      FX.ui.toast(FX.t('נמצאו') + ' ' + marks.length + ' ' + FX.t('התאמות (F3 להתאמה הבאה)'), 'info');
    }
    return marks.length;
  };

  /* ---------- שמע ווידאו ---------- */
  FX.viewMedia = async function (prov, entry, kind) {
    var ext = FX.extOf(entry.name), url = null;
    var st = openShell(entry, {
      canOpen: prov.caps.open,
      onOpenExternal: function () { prov.open(entry.path).catch(function (e) { FX.ui.toast(FX.errorText(e), 'error'); }); }
    });
    var bytes;
    try { bytes = await FX.readBytes(prov, entry); } catch (e) { fail(st, e); return; }
    if (cur !== st) return;
    url = URL.createObjectURL(new Blob([bytes], { type: FX.mimeOf(ext) || (kind === 'video' ? 'video/mp4' : 'audio/mpeg') }));
    st.cleanup = function () { URL.revokeObjectURL(url); };
    st.body.innerHTML = '<div class="fx-media">' + (kind === 'video'
      ? '<video controls autoplay playsinline></video>' : '<div class="fx-media-ic">' + FX.icon('audio') + '</div><audio controls autoplay></audio>') + '<p class="muted small fx-media-err" hidden></p></div>';
    var el = st.body.querySelector('video,audio');
    el.addEventListener('error', function () {
      var m = st.body.querySelector('.fx-media-err');
      m.hidden = false;
      m.textContent = FX.t('הפורמט אינו נתמך בנגן הפנימי.') + (prov.caps.open ? ' ' + FX.t('אפשר לפתוח אותו ביישום (הכפתור למעלה).') : '');
    });
    el.src = url;
  };

  /* ---------- עזרי zip / XML ---------- */
  function xmlDoc(text) { return new DOMParser().parseFromString(text, 'application/xml'); }
  async function zipOf(prov, entry) {
    if (!window.JSZip) throw FX.OpError('unsupported', FX.t('רכיב הפענוח אינו זמין'));
    return window.JSZip.loadAsync(await FX.readBytes(prov, entry));
  }
  function colIndex(ref) {
    var m = /^([A-Z]+)/.exec(ref || ''), n = 0;
    if (!m) return 0;
    for (var i = 0; i < m[1].length; i++) n = n * 26 + (m[1].charCodeAt(i) - 64);
    return n - 1;
  }
  function tabsShell(st, names, render) {
    st.body.innerHTML = '<div class="fx-sheet-tabs"></div><div class="fx-sheet-body"></div>';
    var bar = st.body.firstChild, body = st.body.lastChild;
    function show(i) {
      Array.prototype.forEach.call(bar.children, function (b, k) { b.classList.toggle('on', k === i); });
      body.innerHTML = ''; render(i, body);
    }
    names.forEach(function (n, i) {
      var b = document.createElement('button'); b.className = 'btn btn-text'; b.textContent = n;
      b.addEventListener('click', function () { show(i); }); bar.appendChild(b);
    });
    show(0);
  }

  /* ---------- Excel (xlsx) ---------- */
  FX.viewSheet = async function (prov, entry, find) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var z = await zipOf(prov, entry);
      if (cur !== st) return;
      var shared = [];
      var ss = z.file('xl/sharedStrings.xml');
      if (ss) xmlDoc(await ss.async('string')).querySelectorAll('si').forEach(function (si) {
        shared.push(Array.prototype.map.call(si.querySelectorAll('t'), function (t) { return t.textContent; }).join(''));
      });
      var wb = xmlDoc(await z.file('xl/workbook.xml').async('string'));
      var rels = xmlDoc(await z.file('xl/_rels/workbook.xml.rels').async('string'));
      var relMap = {};
      rels.querySelectorAll('Relationship').forEach(function (r) { relMap[r.getAttribute('Id')] = r.getAttribute('Target'); });
      var sheets = Array.prototype.map.call(wb.querySelectorAll('sheet'), function (sh) {
        var rid = sh.getAttribute('r:id') || sh.getAttributeNS('http://schemas.openxmlformats.org/officeDocument/2006/relationships', 'id');
        var t = relMap[rid] || '';
        return { name: sh.getAttribute('name'), path: 'xl/' + t.replace(/^\/?(xl\/)?/, '') };
      });
      if (!sheets.length) throw FX.OpError('unsupported', FX.t('לא נמצאו גיליונות'));
      tabsShell(st, sheets.map(function (x) { return x.name; }), async function (i, box) {
        box.innerHTML = FX.ui.loading(FX.t('טוען…'));
        var f = z.file(sheets[i].path);
        if (!f) { box.textContent = FX.t('הגיליון ריק'); return; }
        var doc = xmlDoc(await f.async('string')), rows = [], maxC = 0;
        doc.querySelectorAll('row').forEach(function (row) {
          if (rows.length >= 1000) return;
          var cells = [];
          row.querySelectorAll('c').forEach(function (c) {
            var ci = colIndex(c.getAttribute('r')), t = c.getAttribute('t'), v = c.querySelector('v'), val = '';
            if (t === 's' && v) val = shared[+v.textContent] || '';
            else if (t === 'inlineStr') val = Array.prototype.map.call(c.querySelectorAll('t'), function (x) { return x.textContent; }).join('');
            else if (v) val = v.textContent;
            if (ci < 60) { cells[ci] = val; maxC = Math.max(maxC, ci + 1); }
          });
          rows.push(cells);
        });
        var h = '<table class="fx-doc-tbl fx-xl"><tbody>' + rows.map(function (r) {
          var tds = ''; for (var c = 0; c < maxC; c++) tds += '<td>' + FX.esc(r[c] == null ? '' : r[c]) + '</td>';
          return '<tr>' + tds + '</tr>';
        }).join('') + '</tbody></table>' + (rows.length >= 1000 ? '<p class="muted small fx-vnote">' + FX.t('מוצגות 1000 השורות הראשונות.') + '</p>' : '');
        box.innerHTML = h;
        if (find) FX.highlightIn(box, find);
      });
    } catch (e) { fail(st, e); }
  };

  /* ---------- PowerPoint (pptx) ---------- */
  FX.viewSlides = async function (prov, entry, find) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var z = await zipOf(prov, entry);
      if (cur !== st) return;
      var names = Object.keys(z.files).filter(function (n) { return /^ppt\/slides\/slide\d+\.xml$/.test(n); })
        .sort(function (a, b) { return parseInt(a.match(/(\d+)\.xml/)[1], 10) - parseInt(b.match(/(\d+)\.xml/)[1], 10); });
      var out = '';
      for (var i = 0; i < names.length; i++) {
        var doc = xmlDoc(await z.file(names[i]).async('string'));
        var paras = Array.prototype.map.call(doc.getElementsByTagName('a:p'), function (p) {
          return Array.prototype.map.call(p.getElementsByTagName('a:t'), function (t) { return t.textContent; }).join('');
        }).filter(Boolean);
        out += '<section class="fx-slide"><h4>' + FX.t('שקף') + ' ' + (i + 1) + '</h4>' + (paras.length ? paras.map(function (x) { return '<p>' + FX.esc(x) + '</p>'; }).join('') : '<p class="muted">—</p>') + '</section>';
      }
      st.body.innerHTML = '<article class="fx-doc fx-slides" dir="auto">' + (out || '<p>' + FX.t('המצגת ריקה') + '</p>') + '</article>';
      if (find) FX.highlightIn(st.body, find);
    } catch (e) { fail(st, e); }
  };

  /* ---------- OpenDocument (odt, ods, odp): טקסט ---------- */
  FX.viewOdf = async function (prov, entry, find) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var z = await zipOf(prov, entry);
      if (cur !== st) return;
      var doc = xmlDoc(await z.file('content.xml').async('string'));
      var out = [];
      doc.querySelectorAll('h, p').forEach(function (n) { var t = n.textContent.trim(); if (t) out.push('<p>' + FX.esc(t) + '</p>'); });
      st.body.innerHTML = '<article class="fx-doc" dir="auto">' + (out.join('') || '<p>' + FX.t('המסמך ריק') + '</p>') + '</article>';
      if (find) FX.highlightIn(st.body, find);
    } catch (e) { fail(st, e); }
  };

  /* ---------- RTF: חילוץ טקסט ---------- */
  FX.rtfToText = function (src) {
    var dec = null; try { dec = new TextDecoder('windows-1255'); } catch (e) { dec = null; }
    var out = src.replace(/\\'([0-9a-fA-F]{2})/g, function (_, h) { var b = parseInt(h, 16); return dec ? dec.decode(new Uint8Array([b])) : String.fromCharCode(b); })
      .replace(/\\u(-?\d+)\??/g, function (_, n) { n = +n; if (n < 0) n += 65536; return String.fromCharCode(n); })
      .replace(/\\(par|line)\b ?/g, '\n').replace(/\\tab\b ?/g, '\t')
      .replace(/\{\\\*[^{}]*\}/g, '').replace(/\{\\(fonttbl|colortbl|stylesheet|info)[^]*?\}\}/g, '')
      .replace(/\\[a-zA-Z]+-?\d* ?/g, '').replace(/[{}]/g, '').replace(/\\([\\{}])/g, '$1');
    return out.trim();
  };
  FX.viewRtf = async function (prov, entry, find) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var bytes = await FX.readBytes(prov, entry);
      if (cur !== st) return;
      st.body.innerHTML = '<pre class="fx-text" dir="auto"></pre>';
      st.body.firstChild.textContent = FX.rtfToText(new TextDecoder('latin1').decode(bytes));
      if (find) FX.highlightIn(st.body, find);
    } catch (e) { fail(st, e); }
  };

  /* ---------- EPUB ---------- */
  FX.viewEpub = async function (prov, entry, find) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var z = await zipOf(prov, entry);
      if (cur !== st) return;
      var cont = xmlDoc(await z.file('META-INF/container.xml').async('string'));
      var opfPath = cont.querySelector('rootfile').getAttribute('full-path');
      var opf = xmlDoc(await z.file(opfPath).async('string'));
      var base = opfPath.indexOf('/') >= 0 ? opfPath.replace(/[^/]*$/, '') : '';
      var manifest = {};
      opf.querySelectorAll('manifest > item').forEach(function (it) { manifest[it.getAttribute('id')] = it.getAttribute('href'); });
      var out = '';
      var refs = opf.querySelectorAll('spine > itemref');
      for (var i = 0; i < refs.length && i < 200; i++) {
        var href = manifest[refs[i].getAttribute('idref')];
        var f = href && z.file(decodeURIComponent(base + href));
        if (!f) continue;
        var d = new DOMParser().parseFromString(await f.async('string'), 'text/html');
        d.querySelectorAll('h1,h2,h3,h4,p,li').forEach(function (n) {
          var t = n.textContent.replace(/\s+/g, ' ').trim();
          if (t) out += (/^H/.test(n.tagName) ? '<h4>' : '<p>') + FX.esc(t) + (/^H/.test(n.tagName) ? '</h4>' : '</p>');
        });
      }
      st.body.innerHTML = '<article class="fx-doc" dir="auto">' + (out || '<p>' + FX.t('הספר ריק') + '</p>') + '</article>';
      if (find) FX.highlightIn(st.body, find);
    } catch (e) { fail(st, e); }
  };

  /* ---------- רשימת תוכן של ארכיון zip ---------- */
  FX.viewZip = async function (prov, entry) {
    var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    try {
      var z = await zipOf(prov, entry);
      if (cur !== st) return;
      var names = Object.keys(z.files);
      st.body.innerHTML = '<table class="fx-doc-tbl fx-xl"><thead><tr><th>' + FX.t('שם') + '</th><th>' + FX.t('גודל') + '</th></tr></thead><tbody>' +
        names.slice(0, 2000).map(function (n) {
          var f = z.files[n], sz = f._data && f._data.uncompressedSize != null ? FX.formatSize(f._data.uncompressedSize) : '';
          return '<tr><td dir="ltr">' + FX.esc(n) + '</td><td>' + (f.dir ? '' : FX.esc(sz)) + '</td></tr>';
        }).join('') + '</tbody></table><p class="muted small fx-vnote">' + FX.formatNumber(names.length) + ' ' + FX.t('פריטים בארכיון') + '</p>';
    } catch (e) { fail(st, e); }
  };

  /* ---------- סוג לא מוכר: אם זה טקסט — מציגים כטקסט; אחרת כרטיס פרטים עם פתיחה ביישום ---------- */
  FX.viewUnknown = async function (prov, entry) {
    var bytes = null;
    if (entry.size == null || entry.size < 5 * 1024 * 1024) { try { bytes = await FX.readBytes(prov, entry); } catch (e) { bytes = null; } }
    if (bytes && bytes.length) {
      var head = bytes.subarray(0, Math.min(bytes.length, 8192)), bin = false;
      for (var i = 0; i < head.length; i++) if (head[i] === 0) { bin = true; break; }
      if (!bin) {
        var text;
        try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
        catch (e) { try { text = new TextDecoder('windows-1255').decode(bytes); } catch (e2) { text = null; } }
        if (text != null) {
          var st = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
          st.body.innerHTML = '<pre class="fx-text" dir="auto"></pre>';
          st.body.firstChild.textContent = text.slice(0, 2000000);
          return;
        }
      }
    }
    var s2 = openShell(entry, { canOpen: prov.caps.open, onOpenExternal: function () { prov.open(entry.path).catch(function () {}); } });
    var k = FX.kindOf(entry);
    s2.body.innerHTML = '<div class="fx-empty fx-unknown"><span class="fx-ic k-' + k + '">' + FX.icon(FX.kindInfo(k).icon) + '</span><b data-noi18n>' + FX.esc(entry.name) + '</b>' +
      '<span class="muted">' + FX.esc(FX.typeLabel(entry)) + (entry.size != null ? ' · ' + FX.esc(FX.formatSize(entry.size)) : '') + '</span>' +
      '<span class="muted small">' + FX.t('אין צופה פנימי לסוג הזה.') + (prov.caps.open ? ' ' + FX.t('אפשר לפתוח אותו ביישום המתאים ב-Windows.') : '') + '</span>' +
      (prov.caps.open ? '<button class="btn btn-primary" data-x="1">' + FX.icon('open') + FX.t('פתיחה ביישום') + '</button>' : '') + '</div>';
    var b = s2.body.querySelector('[data-x]');
    if (b) b.addEventListener('click', function () { prov.open(entry.path).catch(function (e) { FX.ui.toast(FX.errorText(e), 'error'); }); });
  };

  /* חיפוש-בתוך-תצוגה לטקסט ול-Word: עוטפים את הצופים הקיימים */
  var _vt = FX.viewText, _vd = FX.viewDocx;
  FX.viewText = async function (prov, entry, find) {
    await _vt(prov, entry);
    if (find && cur) FX.highlightIn(cur.body, find);
  };
  FX.viewDocx = async function (prov, entry, startCols, find) {
    await _vd(prov, entry, startCols);
    if (find && cur) FX.highlightIn(cur.body, find);
  };
})();
