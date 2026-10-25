// tools/i18n-missing.js — מחרוזות עבריות בקוד התוסף שאין להן תרגום אנגלי.
// שימוש: node tools/i18n-missing.js system-settings   (או file-explorer)
// בודק כל מחרוזת עם עברית בקובצי js/html (חוץ ממילון/תשתית) מול I.t אחרי טעינת המילון.
'use strict';
const fs = require('fs'), path = require('path'), vm = require('vm');
const dir = process.argv[2] || 'system-settings';
const store = {};
const win = { localStorage: { getItem: k => store[k] || null, setItem: (k, v) => { store[k] = v; } } };
const doc = { documentElement: { setAttribute() {}, getAttribute() { return ''; }, lang: '', dir: '' }, body: null,
  querySelectorAll: () => [], createTreeWalker: () => ({ nextNode: () => null }) };
const ctx = vm.createContext({ localStorage: win.localStorage, location: { reload() {} }, window: win, document: doc, navigator: { language: 'en' }, MutationObserver: function () { return { observe() {}, disconnect() {} }; }, NodeFilter: { SHOW_TEXT: 4, SHOW_ELEMENT: 1 }, console });
ctx.window.window = ctx.window;
for (const f of ['i18n.js', 'lang-en.js']) vm.runInContext(fs.readFileSync(path.join(dir, 'js', f), 'utf8').replace(/window\./g, 'window.'), ctx);
const I = ctx.window.I18N;
store['i18n.lang'] = 'en'; Object.keys(store).length; (async () => {})();
for (const k of ['i18n.lang', 'ss.lang', 'fx.lang']) store[k] = 'en';
main();
async function main() {
try { await I.init(); } catch (e) { /* DOM stubs */ }
if (I.lang() !== 'en') { console.error('could not switch to en'); process.exit(2); }

const files = [];
(function walk(d) {
  for (const n of fs.readdirSync(d)) {
    const p = path.join(d, n);
    if (fs.statSync(p).isDirectory()) { if (n !== 'vendor') walk(p); }
    else if (/\.(js|html)$/.test(n) && !/^(i18n|lang-en)\.js$/.test(n)) files.push(p);
  }
})(dir);

const HEB = /[֐-׿]/;
const out = new Map();
for (const f of files) {
  const src = fs.readFileSync(f, 'utf8');
  const lines = src.split('\n');
  lines.forEach((line, i) => {
    if (/^\s*(\/\/|\/\*|\*)/.test(line)) return;
    const code = line.replace(/\/\/.*$/, '').replace(/\/\*.*?\*\//g, '');
    const re = f.endsWith('.html') ? />([^<>]*[֐-׿][^<>]*)<|(?:title|placeholder|aria-label)="([^"]*)"/g : /'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)"/g;
    let m;
    while ((m = re.exec(code))) {
      const s = (m[1] != null ? m[1] : m[2] || '').replace(/\\'/g, "'").replace(/\\"/g, '"').trim();
      if (!s || !HEB.test(s) || /^[.#\[]/.test(s)) continue;
      if (/['"]\s*\+\s*$/.test(code.slice(0, m.index + m[0].length + 3)) && s.length < 3) continue;
      // מחרוזת HTML: בזמן ריצה כל צומת טקסט וכל title/placeholder/aria-label מתורגם בנפרד
      const parts = [];
      s.replace(/(?:title|placeholder|aria-label)=\\?["']([^"'\\]*)/g, (_, a) => { parts.push(a); return ''; });
      s.replace(/<[^>]*>?/g, '\u0000').replace(/^[^<]*>/, '').split('\u0000').forEach(x => parts.push(x));
      for (const raw of parts) {
        const t = raw.replace(/&[a-z]+;/g, ' ').trim();
        if (!t || !HEB.test(t)) continue;
        const r = I.t(t);
        if ((r === t || HEB.test(r)) && !out.has(t)) out.set(t, `${path.relative('.', f)}:${i + 1}`);
      }
    }
  });
}
for (const [s, loc] of out) console.log(loc + '\t' + s);
console.error(out.size + ' untranslated');
}
