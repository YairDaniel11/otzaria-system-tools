/* דואר — אנשי קשר: ייבוא vCard (.vcf) ו-CSV (ייצוא של Google Contacts / Outlook), ייצוא vCard.
   ייבוא מגוגל: חשבון Google של לוח השנה באוצריא חסום לתוספים (key-google-calendar-*), לכן הייבוא עצמאי:
   contacts.google.com ← "ייצוא" ← Google CSV או vCard ← ייבוא הקובץ כאן. בלי חיבור לחשבון ובלי אינטרנט בתוסף. */
(function () {
  'use strict';
  var MX = window.MX = window.MX || {};

  function unfold(t) { return t.replace(/\r\n/g, '\n').replace(/\n[ \t]/g, ''); }
  function vDecode(v) { return v.replace(/\\n/gi, '\n').replace(/\\,/g, ',').replace(/\\;/g, ';').replace(/\\\\/g, '\\'); }
  function qp(v) { try { return decodeURIComponent(v.replace(/=\n/g, '').replace(/=([0-9A-F]{2})/gi, '%$1')); } catch (e) { return v; } }

  function parseVcf(text) {
    var out = [], cur = null;
    unfold(text).split('\n').forEach(function (line) {
      if (/^BEGIN:VCARD/i.test(line)) { cur = { name: '', emails: [], phones: [], org: '', note: '' }; return; }
      if (/^END:VCARD/i.test(line)) { if (cur && (cur.name || cur.emails.length || cur.phones.length)) out.push(cur); cur = null; return; }
      if (!cur) return;
      var i = line.indexOf(':'); if (i < 0) return;
      var head = line.slice(0, i), val = line.slice(i + 1), key = head.split(';')[0].replace(/^item\d+\./i, '').toUpperCase();
      if (/ENCODING=QUOTED-PRINTABLE/i.test(head)) val = qp(val);
      val = vDecode(val).trim();
      if (key === 'FN') cur.name = val;
      else if (key === 'N' && !cur.name) cur.name = val.split(';').slice(0, 2).reverse().filter(Boolean).join(' ').trim();
      else if (key === 'EMAIL' && val) cur.emails.push(val);
      else if (key === 'TEL' && val) cur.phones.push(val);
      else if (key === 'ORG') cur.org = val.replace(/;/g, ' ').trim();
      else if (key === 'NOTE') cur.note = val;
    });
    return out;
  }

  function csvRows(text) {
    var rows = [], row = [], f = '', q = false;
    text = text.replace(/^\uFEFF/, '');
    for (var i = 0; i < text.length; i++) {
      var c = text[i];
      if (q) { if (c === '"') { if (text[i + 1] === '"') { f += '"'; i++; } else q = false; } else f += c; }
      else if (c === '"') q = true;
      else if (c === ',') { row.push(f); f = ''; }
      else if (c === '\n' || c === '\r') { if (c === '\r' && text[i + 1] === '\n') i++; row.push(f); rows.push(row); row = []; f = ''; }
      else f += c;
    }
    if (f || row.length) { row.push(f); rows.push(row); }
    return rows.filter(function (r) { return r.some(function (x) { return x.trim(); }); });
  }
  function parseCsv(text) {
    var rows = csvRows(text); if (rows.length < 2) return [];
    var h = rows[0].map(function (x) { return x.trim().toLowerCase(); });
    function col(re) { var r = []; h.forEach(function (x, i) { if (re.test(x)) r.push(i); }); return r; }
    var em = col(/e-?mail.*(value|address)|^e-?mail/), ph = col(/phone.*value|phone|טלפון/), nm = col(/^(name|display name|full name|שם)$/),
      first = col(/^(first name|given name|שם פרטי)$/), last = col(/^(last name|family name|surname|שם משפחה)$/), org = col(/organization.*name|^company|^organization$|חברה/), note = col(/^notes?$|הערות/);
    return rows.slice(1).map(function (r) {
      function v(ix) { return ix.map(function (i) { return (r[i] || '').trim(); }).filter(Boolean); }
      var split = function (arr) { var o = []; arr.forEach(function (x) { x.split(/\s*:::\s*/).forEach(function (y) { if (y) o.push(y); }); }); return o; };
      var name = v(nm)[0] || (v(first).concat(v(last))).join(' ');
      return { name: name, emails: split(v(em)), phones: split(v(ph)), org: v(org)[0] || '', note: v(note)[0] || '' };
    }).filter(function (c) { return c.name || c.emails.length || c.phones.length; });
  }

  function vEsc(s) { return String(s || '').replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;'); }
  function toVcf(list) {
    return list.map(function (c) {
      var l = ['BEGIN:VCARD', 'VERSION:3.0', 'FN:' + vEsc(c.name), 'N:;' + vEsc(c.name) + ';;;'];
      (c.emails || []).forEach(function (e) { l.push('EMAIL;TYPE=INTERNET:' + vEsc(e)); });
      (c.phones || []).forEach(function (p) { l.push('TEL:' + vEsc(p)); });
      if (c.org) l.push('ORG:' + vEsc(c.org));
      if (c.note) l.push('NOTE:' + vEsc(c.note));
      l.push('END:VCARD');
      return l.join('\r\n');
    }).join('\r\n') + '\r\n';
  }

  MX.contacts = {
    parse: function (name, text) { return /\.vcf$/i.test(name) || /BEGIN:VCARD/i.test(text) ? parseVcf(text) : parseCsv(text); },
    toVcf: toVcf,
    /* מיזוג לפי כתובת מייל ראשונה (או שם) — ייבוא חוזר לא יוצר כפילויות */
    merge: function (existing, incoming) {
      var byKey = {}, added = 0, updated = 0;
      function k(c) { return ((c.emails && c.emails[0]) || c.name || '').toLowerCase(); }
      existing.forEach(function (c) { byKey[k(c)] = c; });
      var out = [];
      incoming.forEach(function (c) {
        var key = k(c); if (!key) return;
        var o = byKey[key];
        if (o) {
          o.name = o.name || c.name; o.org = o.org || c.org; o.note = o.note || c.note;
          o.emails = Array.from(new Set((o.emails || []).concat(c.emails || [])));
          o.phones = Array.from(new Set((o.phones || []).concat(c.phones || [])));
          out.push(o); updated++;
        } else { c.id = MX.store.uid(); byKey[key] = c; out.push(c); added++; }
      });
      return { rows: out, added: added, updated: updated };
    }
  };
})();
