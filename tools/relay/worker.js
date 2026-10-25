// #65 — מתווך דיווחי תקלות: מקבל דיווח מהגשר ופותח issue ב-GitHub. הטוקן נשמר רק כאן (GH_TOKEN, Secret).
// בלי KV: כפילות נבדקת בחיפוש ב-GitHub (fingerprint פתוח), מגבלה יומית לפי issues עם auto-report מהיום,
// ומגבלה לכל IP בזיכרון ה-Worker (5 לשעה, הגנה בסיסית). גוף ≤ 8000 תווים.
const ipHits = new Map();
const DAILY_MAX = 30;

export default {
  async fetch(req, env) {
    if (req.method !== 'POST') return new Response('POST only', { status: 405 });
    let d;
    try { d = await req.json(); } catch { return new Response('bad json', { status: 400 }); }
    const { title, body, fingerprint, version, kind, plugin } = d || {};
    // #70: repo לפי רשימה סגורה (GH_REPOS, מופרד בפסיקים); ברירת מחדל GH_REPO
    const allowed = String(env.GH_REPOS || env.GH_REPO).split(',').map(x => x.trim()).filter(Boolean);
    const want = typeof d.repo === 'string' && d.repo ? (d.repo.includes('/') ? d.repo : 'e0548433917-gif/' + d.repo) : env.GH_REPO;
    if (!allowed.includes(want)) return new Response('repo not allowed', { status: 400 });
    const manual = kind === 'manual';
    if (typeof title !== 'string' || typeof body !== 'string' || !/^[0-9a-f]{16}$/.test(fingerprint || '') ||
        !/^\d+\.\d+\.\d+/.test(version || '') || body.length > 8000 || title.length > 120)
      return new Response('invalid', { status: 400 });

    const ip = req.headers.get('cf-connecting-ip') || 'x', hour = Math.floor(Date.now() / 3600e3);
    const key = ip + ':' + hour, n = ipHits.get(key) || 0;
    if (n >= 5) return new Response('rate', { status: 429 });
    ipHits.set(key, n + 1);
    if (ipHits.size > 5000) ipHits.clear();

    const repo = want;
    const gh = (path, init = {}) => fetch('https://api.github.com' + path, {
      ...init,
      headers: { authorization: 'Bearer ' + env.GH_TOKEN, 'user-agent': 'achat-shaalti-relay', accept: 'application/vnd.github+json', ...(init.headers || {}) }
    });
    const search = async q => {
      const r = await gh('/search/issues?per_page=1&q=' + encodeURIComponent(q));
      return r.ok ? (await r.json()).total_count : 0;
    };

    if (await search(`repo:${repo} is:issue is:open in:body fp-${fingerprint}`)) return new Response('duplicate', { status: 409 });
    const today = new Date().toISOString().slice(0, 10);
    if (await search(`repo:${repo} is:issue label:${manual ? 'user-feedback' : 'auto-report'} created:>=${today}`) >= DAILY_MAX) return new Response('rate', { status: 429 });

    const r = await gh(`/repos/${repo}/issues`, {
      method: 'POST',
      body: JSON.stringify({
        title: title.replace(/[@#]/g, ''),
        body: (manual ? 'משוב משתמש מהתוסף ' + String(plugin || '').slice(0, 60).replace(/[@#]/g, '') + ' (גשר ' + version + ')' : 'דיווח תקלה אוטומטי מהגשר (גרסה ' + version + ')') + '\n\n```\n' + body.replace(/```/g, "'''").replace(/@/g, '(at)') + '\n```\n\nfp-' + fingerprint,
        labels: [manual ? 'user-feedback' : 'auto-report']
      })
    });
    if (!r.ok) return new Response('github ' + r.status, { status: 502 });
    return new Response('ok', { status: 201 });
  }
};
