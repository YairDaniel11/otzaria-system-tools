# תוכנת חסימת אוצריא (Otzaria Blocking Software) — חוזה API v1

> #69 (next): `POST /kiosk/recovery/backup {password, mode:"file"|"print"|"none"}` — יוצר 10 קודי גיבוי חד-פעמיים (מחליף קודמים; סיסמה או קוד ראשי + 2FA), מחזיר `{codes[], path, left}` פעם אחת בלבד; `POST /kiosk/recovery/backup/status` → `{left}`. קוד גיבוי מתקבל ב-`/kiosk/disable`, ב-`/kiosk/set-password` (`oldPassword`), ב-`--unlock`, ב-F12 ובהסרה — ונשרף. שירות ההקשחה: `op=set-backup {password, codes:["salt|hash"]}`, ו-`status.backupLeft`; `disable`/`set-password` שורפים קוד גיבוי בעצמם.
> 3.8.0: `POST /kiosk/recovery/keep {code, mode:"file"|"print"}` — שמירת קוד השחזור לקובץ במסמכים/הדפסה; `/kiosk/status` מחזיר `recoverySaved` ו-`schedule.days`; `/kiosk/settings` מקבל `schedule.days` (מערך 0–6, ריק=כל הימים). `/kiosk/enable` נדחה כל עוד `recoverySaved=false`.

שירות Windows מקומי שהתוספים "סייר קבצים" ו"הגדרות מערכת" פונים אליו דרך
`Otzaria.call('network.fetchStream', ...)` עם ההרשאה `network.localhost`.

## חיבור

- כתובת: `http://127.0.0.1:<port>` — פורט ברירת מחדל **47917**. אם תפוס, הגשר מנסה
  47918..47921 ומשתמש בראשון הפנוי. התוסף סורק 47917..47921 ב-`GET /health`.
- **כל בקשה** חייבת לכלול את הכותרת `X-Otzaria-Bridge: 1`. בקשה עם כותרת `Origin`
  שאינה `null`/ריקה — נדחית ב-403 (הגנה מפני אתרי אינטרנט שפונים ל-localhost).
  בקשות OPTIONS (preflight) נדחות ב-403.
- כל הגופים JSON (`Content-Type: application/json; charset=utf-8`). בקשות POST בלבד,
  חוץ מ-`GET /health`.
- תשובה: `{ "ok": true, "data": ... }` או `{ "ok": false, "error": { "code": "...", "message": "הודעה בעברית" } }`
  עם סטטוס HTTP מתאים. קודי שגיאה: `bad_request`, `not_found`, `forbidden`, `exists`,
  `locked` (הפעולה חסומה במצב נעילה), `wrong_password`, `unsupported`, `internal`,
  `fx_locked` (סייר הקבצים נעול עד הזנת סיסמה, HTTP 423) ו-`fx_outside_policy` (הנתיב מחוץ לתיקיות שהמנהל התיר, HTTP 403).
- נתיבים: נתיבי Windows מלאים (`C:\Users\...`). מחרוזת UTF-8 רגילה.

## /health (GET)
`data = { name: "otzaria-bridge", version: "1.0.0", port, kioskActive: bool, platform: "windows" }`

## קבצים — /fs/*

| נתיב | גוף | data |
|---|---|---|
| `/fs/roots` | `{}` | `{ drives: [{path, label, type, totalBytes, freeBytes}], known: [{id, name, path}] }` — known: desktop, documents, downloads, pictures, music, videos, home |
| `/fs/list` | `{ path, showHidden?: false }` | `{ path, parent: string\|null, entries: [{ name, path, type: "dir"\|"file", size, modified (ISO UTC), ext, hidden }] }` — תיקיות קודם, אח"כ קבצים, לפי שם |
| `/fs/stat` | `{ path }` | `{ name, path, type, size, created, modified, readOnly, hidden, itemCount? }` |
| `/fs/mkdir` | `{ path }` | `{ path }` — `exists` אם קיים |
| `/fs/rename` | `{ path, newName }` | `{ path }` |
| `/fs/attributes` | `{ path, hidden?: bool, readOnly?: bool }` | כמו `/fs/stat` — מחזיר את המאפיינים אחרי השינוי. נחסם באזורים מוגנים ובשורש כונן (מ-1.0.0) |
| `/fs/copy` | `{ sources: [path], destDir, conflict: "skip"\|"overwrite"\|"rename" }` | `{ copied: n, skipped: n, failed: [{path, message}] }` — תיקיות רקורסיבית. `rename` = "שם (2).ext" |
| `/fs/move` | אותו גוף כמו copy | `{ moved, skipped, failed }` |
| `/fs/delete` | `{ paths: [path], permanent?: false }` | `{ deleted, failed }` — ברירת מחדל לסל המיחזור |
| `/fs/read` | `{ path, maxBytes?: 5242880 }` | `{ name, size, mime, encoding: "utf8"\|"base64", content, truncated }` — טקסט (txt, md, json, csv, xml, html, js, css, log, ini) כ-utf8, תמונות ו-pdf כ-base64 |
| `/fs/write-text` | `{ path, content, overwrite?: false }` | `{ path, size }` |
| `/fs/open` | `{ path, openWith? }` | `{ opened: true, chooser? }` — פתיחה ביישום ברירת המחדל של Windows; `openWith: true` מציג את חלון "פתח באמצעות" (לא בנעילה/מצב מוגבל). **חסום (`locked`) במצב נעילה**, אלא אם הסיומת ב-`kiosk.allowOpenExtensions` |
| `/fs/reveal` | `{ path }` | פתיחת סייר Windows על הקובץ. חסום במצב נעילה |
| `/fs/clipboard-get` | `{}` | `{ paths: [path], cut: bool }` — רשימת קבצים מלוח Windows (FileDropList) |
| `/fs/clipboard-set` | `{ paths, cut }` | `{ ok: true }` — כדי שהעתקה בתוסף תהיה זמינה גם בסייר Windows ולהפך |
| `/fs/search` | `{ root, query, extensions?: [], maxResults?: 500, includeDirs?: true, contentQuery?: string }` | **NDJSON זורם** (Content-Type `application/x-ndjson`): שורה לכל תוצאה `{ "type":"hit", name, path, type, size, modified }`, שורות התקדמות `{ "type":"progress", scanned, current }` כל ~300ms, ובסוף `{ "type":"done", scanned, hits, truncated, cancelled }`. `query` תומך ב-`*` ו-`?`; בלי תווים מיוחדים = "מכיל" ללא תלות ברישיות. `contentQuery` = חיפוש בתוך קבצי טקסט עד 2MB. מדלג על תיקיות מערכת ועל שגיאות הרשאה |

אזורים מוגנים: מחיקה/העברה/שינוי שם של `C:\Windows`, `C:\Program Files*`, `C:\ProgramData`,
שורש כונן, ותיקיית הגשר עצמה — `forbidden`.

## מדיניות סייר הקבצים — /fs/policy/*

מצב `off` (ברירת מחדל), `restricted` (רק בתיקיות שנבחרו) או `locked` (נעול עד הזנת סיסמת מנהל, ואז פתוח `unlockMinutes` דקות).
המדיניות נאכפת בגשר על **כל** נתיב ש-`/fs/*` ו-`/recycle/*` מקבלים: הנתיב מנורמל וקנוני (בלי `..`, בלי `\\?\`/התקנים/זרמי נתונים חלופיים,
צמתים וקישורים סימבוליים ושמות 8.3 מפוענחים) ונבדק בהשוואה לא רגישה לרישיות עם גבול תיקייה. כשל בפענוח = חסום. בהעתקה/העברה/שינוי שם נבדק גם היעד.
במצב `restricted`: `/fs/roots` מחזיר רק את התיקיות המותרות, `parent` מחוץ להן מוסתר, `/fs/reveal` ופתיחת תיקייה/קיצור דרך חסומים,
הסל מסונן לפי המיקום המקורי ו-`/recycle/empty` חסום. תיקייה מותרת שלא קיימת מדולגת; אם לא נשארה אף אחת, המצב הוא `locked` ולא פתוח.

| נתיב | גוף | data |
|---|---|---|
| `/fs/policy/get` | `{}` | `{ mode, effective: "open"\|"restricted"\|"locked", locked, fallback, roots, activeRoots, invalidRoots, unlockMinutes, unlockedSeconds, passwordConfigured, defaults: [{id,name,path}] }` — לא דורש סיסמה |
| `/fs/policy/set` | `{ password, mode, roots: [path], unlockMinutes?: 15 }` | כמו get. דורש סיסמת נעילה או קוד שחזור (נעילת ניסיונות כמו שאר הנתיבים); אם אין סיסמה — `no_password` |
| `/fs/policy/unlock` | `{ password }` | כמו get. פותח זמנית (מצב `locked`). הפתיחה בזיכרון בלבד: הפעלה מחדש של הגשר נועלת |
| `/fs/policy/lock` | `{}` | כמו get. נועל מיד |

## סל המיחזור — /recycle/*

הסל של המשתמש הנוכחי בכל הכוננים הקבועים והנתיקים (`\$Recycle.Bin\<SID>`: קובצי `$I` למטא-נתונים ו-`$R` לתוכן).
מזהה פריט (`id`) הוא `<אות כונן>:<שם קובץ $I>` ומאומת בגשר — לא ניתן להצביע על נתיב כלשהו מחוץ לסל.

| נתיב | גוף | data |
|---|---|---|
| `/recycle/list` | `{}` | `{ items: [{ id, name, originalPath, size, deleted (ISO UTC), type: "dir"\|"file", ext, drive }] }` — החדש ראשון |
| `/recycle/restore` | `{ ids: [id], conflict?: "rename"\|"skip" }` | `{ restored, skipped, failed: [{id, message}] }` — משחזר למיקום המקורי (יוצר תיקיות חסרות). אם השם תפוס: `rename` (ברירת מחדל) = "שם (2).ext", או `skip`. לא משחזר לאזורים מוגנים |
| `/recycle/delete` | `{ ids: [id] }` | `{ deleted, failed }` — מחיקה לצמיתות של פריטים נבחרים מהסל |
| `/recycle/empty` | `{}` | `{ emptied: true }` — ריקון כל הסל (`SHEmptyRecycleBin`, בלי חלונות אישור; האישור הוא בתוסף) |

## זיהוי טקסט (OCR) של Windows — /ocr/*

מנוע `Windows.Media.Ocr` המובנה. לעברית נדרשת חבילת השפה `Language.OCR~~~he-IL` (הגדרות ← זמן ושפה ← שפה ← עברית ← אפשרויות שפה, או `Add-WindowsCapability -Online -Name Language.OCR~~~he-IL` כמנהל). בלי החבילה הגשר מחזיר `unsupported` והלקוח אמור לחזור למנוע שלו.

| נתיב | גוף | data |
|---|---|---|
| `/ocr/status` | `{}` | `{ available, languages: ["he-IL", ...], hebrew: bool, hebrewCapability, installHint, maxImageDimension }` |
| `/ocr/recognize` | `{ image: base64 \| data URL (PNG/JPEG/BMP), lang?: "he-IL" }` | `{ text, lines: [{ text, words: [{ text, x, y, w, h }] }], width, height, lang }` — קואורדינטות בפיקסלים של התמונה. בלי `lang` — שפות פרופיל המשתמש. תמונה מעל `maxImageDimension` בצד כלשהו → `bad_request` (הלקוח מקטין) |

## מערכת — /sys/*

| נתיב | גוף | data |
|---|---|---|
| `/sys/info` | `{}` | `{ computerName, userName, os, battery: {percent, charging} \| null, time }` |
| `/sys/audio/get` | `{}` | `{ volume: 0-100, muted, deviceName }` |
| `/sys/audio/set` | `{ volume?, muted? }` | אותו מבנה כמו get |
| `/sys/audio/devices` | `{}` | `{ devices: [{ id, name, isDefault }] }` |
| `/sys/audio/set-default` | `{ id }` | `{ ok }` (אם נכשל — `unsupported`) |
| `/sys/display/get` | `{}` | `{ brightness: 0-100 \| null, supported: bool }` — WMI, במחשבים ניידים |
| `/sys/display/set` | `{ brightness }` | אותו מבנה |
| `/sys/radios/get` | `{}` | `{ radios: [{ kind: "bluetooth"\|"wifi"\|"other", name, state: "on"\|"off"\|"disabled"\|"unknown" }] }` |
| `/sys/radios/set` | `{ kind, on }` | `{ radios }` |
| `/sys/bluetooth/devices` | `{ scan?: false }` | `{ devices: [{ id, name, paired, connected, kind }] }` — `scan:true` מחפש גם מכשירים לא מוצמדים (עד ~10 שניות) |
| `/sys/bluetooth/pair` | `{ id }` | `{ paired }` |
| `/sys/bluetooth/unpair` | `{ id }` | `{ unpaired }` |
| `/sys/wifi/networks` | `{}` | `{ connected: string\|null, networks: [{ ssid, signal (0-100), secured, connected, known }] }` |
| `/sys/wifi/connect` | `{ ssid, password? }` | `{ connected }` |
| `/sys/wifi/disconnect` | `{}` | `{ ok }` |
| `/sys/usb/list` | `{}` | `{ drives: [{ letter, label, model, fs, sizeBytes, freeBytes, ready }] }` |
| `/sys/usb/eject` | `{ letter }` | `{ ok, letter, ejected }`. שגיאות: `busy` (הכונן בשימוש), `forbidden` (נדרשות הרשאות מנהל), `not_found` |
| `/sys/keyboard/get` | `{}` | `{ layouts: [{id, name, lang}], current, delay: 0-3, speed: 0-31, stickyKeys, stickyHotkey, filterKeys, toggleKeys, capsLock, numLock }` |
| `/sys/keyboard/set` | כל תת-קבוצה של השדות מ-get, ו-`layout` (מזהה פריסה) | אותו מבנה כמו get |
| `/sys/keyboard/backlight/get` | `{}` | `{ supported, name, enabled, brightness: 0-100, vendor, model }` |
| `/sys/keyboard/backlight/set` | `{ enabled?, brightness? }` | אותו מבנה כמו get. `unsupported` אם אין מקלדת תומכת |
| `/sys/energy/get` | `{}` | `{ battery: {percent, charging, saverOn, minutesLeft} \| null, plans: [{id, name, active}], screenOff: {ac, dc} \| null, sleepAfter: {ac, dc} \| null, saverThreshold \| null }` (זמנים בשניות, 0 = לעולם לא) |
| `/sys/energy/set` | `{ plan?, screenOffAc?, screenOffDc?, sleepAfterAc?, sleepAfterDc?, saverThreshold? (0-100) }` | אותו מבנה כמו get |
| `/sys/power` | `{ action: "sleep"\|"restart"\|"shutdown"\|"lock" }` | `{ ok }` |

| `/sys/update-check` | `{ force?: false }` | `{ current, latest, tag, newer: bool, url, releasesUrl }` — משווה לגרסה האחרונה של הגשר בעמוד ההוצאות בגיטהאב (תוצאה נשמרת 10 דקות). הגשר לא מוריד ולא מחליף את עצמו. אין חיבור לאינטרנט → `internal` (502) |

## נעילה (קיוסק) — /kiosk/*

| נתיב | גוף | data |
|---|---|---|
| `/kiosk/status` | `{}` | `{ active, hasPassword, strict, autoStartBridge, otzariaPath, blockTaskManager, allowOpenExtensions: [] }` |
| `/kiosk/set-password` | `{ oldPassword?, newPassword }` | `{ ok, recoveryCode? }` — בהגדרה הראשונה מוחזר **קוד שחזור** חד-פעמי (12 תווים) להצגה פעם אחת. סיסמה ≥ 4 תווים. hash: PBKDF2-SHA256, 200k איטרציות, salt אקראי. אחרי 5 ניסיונות שגויים — השהיה של 60 שניות |
| `/kiosk/enable` | `{ password, strict?: false, blockTaskManager?: true }` | `{ active: true }` |
| `/kiosk/disable` | `{ password }` (או קוד שחזור) | `{ active: false }` |
| `/kiosk/settings` | `{ password, allowOpenExtensions?, otzariaPath? }` | סטטוס |
| `/kiosk/temp-unlock` | `{ password }` — **סיסמת הפתיחה הזמנית** (לא סיסמת הנעילה ולא קוד השחזור) | סטטוס. פותח את כל הנעילה (וגם את מדיניות סייר הקבצים) ל-`kiosk.tempUnlockMinutes` דקות. שגיאות: `forbidden` (האימות הדו-שלבי כבוי), `bad_request` (כבויה/לא מוגדרת/המחשב לא נעול/כבר פתוח), `wrong_password` (כולל השהיה אחרי 5 ניסיונות, משותפת עם סיסמת הנעילה), `unsupported` (רכיב הקשחה ישן) |
| `/kiosk/temp-unlock/end` | `{}` | סטטוס. סיום מוקדם — מחזיר את הנעילה מיד. בלי סיסמה (הכיוון הבטוח בלבד) |

### פתיחה זמנית בסיסמה נפרדת
שדות ב-`/kiosk/settings` (אחרי סיסמת הנעילה ו-Windows Hello, כמו כל הגדרה):
`tempUnlockEnabled` (bool), `tempUnlockMinutes` (1–60, ברירת מחדל 30), `tempUnlockPassword` (לפחות 6 תווים; ריק = ללא שינוי; חייבת להיות שונה מסיסמת הנעילה ומקוד השחזור).
הפעלה (מכבוי לדולק) נדחית כשהאימות הדו-שלבי כבוי. אם האימות הדו-שלבי מכובה אחר כך — הפתיחה "רדומה": אי אפשר להשתמש בה, פתיחה שרצה מסתיימת והנעילה חוזרת; הסיסמה, המשך והמתג נשמרים.

`/kiosk/status` כולל `tempUnlock: { enabled, configured, minutes, maxMinutes, twoFactorOn, usable, active, secondsLeft, until, service, serviceOutdated }`.
בזמן פתיחה זמנית `active` של הנעילה הוא `false` (והנעילה חוזרת מעצמה בתום הזמן), ו-`/fs/policy/get` מחזיר `effective: "open"` ו-`tempUnlockSeconds`.

אחסון: `kiosk.tempUnlockHash`/`kiosk.tempUnlockSalt` (PBKDF2 כמו סיסמת הנעילה), `kiosk.tempUnlockEnabled`, `kiosk.tempUnlockMinutes`, ובלי רכיב הקשחה גם `kiosk.tempUnlockUntil`.
כשרכיב ההקשחה מותקן הוא מקור האמת: הגיבוב, המתג, המשך וזמן הסיום (`tempUnlockUntil`) נשמרים ב-`hardlock.json`, והשירות הוא שמחזיר את האכיפה בתום הזמן.
**בלי רכיב הקשחה** הגשר מחזיר את הנעילה בעצמו, וזו נקודת תורפה ידועה: `config.json` כתיב למשתמש, ומי שהמחשב פתוח לפניו יכול לערוך אותו.

### תוכנות מותרות לפתיחה ממצב נעילה — /kiosk/programs/*
הרשימה נשמרת ב-`kiosk.programs` ומוגדרת בסיסמה בלבד. הפתיחה היא תמיד לפי `id` מהרשימה, לא לפי נתיב חופשי.

| נקודת קצה | גוף | תשובה |
|---|---|---|
| `/kiosk/programs` | `{}` | `{ autoTorah, programs: [{ id, name, path, procs: [], exists, auto }] }` |
| `/kiosk/programs/launch` | `{ id }` | `{ opened: true }` — פותח ומביא את החלון לחזית. בלי סיסמה (הפתיחה מוגבלת לרשימה) |
| `/kiosk/programs/add` | `{ password, name, path, procs? }` | `{ programs }` — `path` מלא לקובץ `exe`; `procs` = שמות תהליכים נוספים (מופרדים בפסיק) |
| `/kiosk/programs/remove` | `{ password, id }` | `{ programs }` |
| `/kiosk/programs/auto` | `{ password, enabled }` | `{ autoTorah, programs }` — זיהוי אוטומטי של תוכנות תורניות ו-Word (ברירת מחדל: כבוי, `kiosk.autoTorahPrograms`). הסריקה לפי רשימת התוכנות המותקנות ב-Windows (שם תצוגה עם מילות מפתח; אפשר להרחיב ב-`kiosk.autoTorahKeywords`). תוצאות מסומנות `auto: true`, מזהה `auto-…`, ואי אפשר להסיר אותן ידנית |
| `/kiosk/programs/detect` | `{}` | `{ found: [{ name, path, procs }] }` — הצעות לפי הרישום של Windows (כרגע: Word) |

- בזמן שתוכנה מהרשימה בחזית (לפי שם הקובץ או `procs`), המעקב של הנעילה לא מחזיר את אוצריא לחזית. אחרי הפתיחה יש 15 שניות חסד.
  כשהתוכנה נסגרת — אוצריא חוזרת לחזית.
- אי אפשר להוסיף מעטפות ומנהלי מערכת (`explorer`, `cmd`, `powershell`, `regedit`, `taskmgr` ועוד). מקסימום 20 תוכנות.

נעילה רכה (תמיד): סגירת explorer.exe (שורת המשימות ותפריט התחל), hook מקלדת שחוסם
Win / Alt+Tab / Alt+Esc / Ctrl+Esc / Alt+F4 על חלון אוצריא, מעקב שמחזיר את אוצריא לחזית
ומפעיל אותה מחדש אם נסגרה, וחסימת מנהל המשימות (HKCU Policies DisableTaskMgr).
המעקב גם שומר על אוצריא ממוקסמת: בלי מזעור ובלי חלון קטן, ואם נשאר פס ריק במקום שורת המשימות הוא ממקסם מחדש
(לכל היותר פעם ב-8 שניות). חלון במסך מלא של אוצריא עצמה לא נוגעים בו. כיבוי: `kiosk.maximizeOtzaria = false` בהגדרות הגשר.
נעילה קפדנית (`strict`): בנוסף מחליפה את מעטפת Windows של המשתמש באוצריא
(HKCU Winlogon Shell) — תקף מהכניסה הבאה. הנעילה נשמרת ומופעלת מחדש בכל הפעלת הגשר.
שחזור חירום: `OtzariaBridge.exe --unlock <recoveryCode>` מחלון פקודה, או Safe Mode
(הגשר לא עולה ב-Safe Mode, ולכן הנעילה הרכה לא חלה שם).

### זמני היום לשומר המסך — גיבוי מקומי
`POST /kiosk/zmanim-cache` — תוסף "הגדרות מערכת" דוחף לכאן מה שקיבל מ-`calendar.getDailyTimes`/`getJewishDate`
של אוצריא (מקור ראשי). כשאין מטמון טרי (אוצריא סגורה או ישנה מדי), `bridge/Zmanim.cs` מציג גיבוי מקומי לפי
`kiosk.zmanimFallbackEngine`: `"noaa"` (ברירת מחדל, קירוב אסטרונומי פשוט) או `"kosherjava"` — המשתמש בחבילת
ה-NuGet `Zmanim` גרסה 1.5.0, הפורט הרשמי ל-.NET של פרויקט **KosherJava** מאת Eliyahu Hershfeld
(http://www.kosherjava.com/zmanim-project/), ברישיון LGPL-2.1. ראו גם `README.md` וחלון "אודות" בגשר.

## הפעלה

- `OtzariaBridge.exe` בהפעלה הראשונה: מעתיק את עצמו ל-`%LOCALAPPDATA%\OtzariaBridge\`,
  רושם הפעלה אוטומטית (HKCU Run, ארגומנט `--background`), ועולה עם אייקון במגש.
- `--uninstall`: מבטל נעילה (דורש `--password` אם יש), מסיר Run ותיקייה.
- קובץ הגדרות: `%LOCALAPPDATA%\OtzariaBridge\config.json`.

## נוסף בגרסה 2.0.0

| נקודת קצה | גוף הבקשה | תשובה |
|---|---|---|
| `/sys/mouse/get` | — | `speed` (1-20), `leftHanded`, `doubleClickMs`, `wheelLines`, `precision`, `touchpad` (או null: `taps`, `twoFingerRightClick`, `naturalScroll`, `sensitivity` 0-3, `cursorSpeed`) |
| `/sys/mouse/set` | אותם שדות (חלקי), `touchpad: {...}` | כמו get. משטח המגע נשמר ב-HKCU ומוחל בכניסה הבאה |
| `/sys/airplane/get` | — | `supported`, `on`, `radios[{kind,name,on}]` |
| `/sys/airplane/set` | `on` | כמו get. "מצב טיסה" = כיבוי כל המשדרים; בכיבוי חוזרים המשדרים שהיו דולקים (`airplane.restore`) |
| `/sys/printers/list` | — | `printers[{name,isDefault,offline,network,driver,port,status}]`, `windowsManagesDefault` |
| `/sys/printers/set-default` | `name` | כמו list. מכבה את LegacyDefaultPrinterMode=0 ("Windows ינהל") |
| `/sys/printers/test-page` | `name` | `ok` |
| `/sys/time/get` | — | `now`, `zone`, `zoneName`, `dst`, `clock24`, `autoSync`, `zones[{id,name}]` |
| `/sys/time/set` | `zone` / `clock24` / `dateTime` (yyyy-MM-ddTHH:mm) | כמו get. `dateTime` דורש מנהל → forbidden |
| `/sys/time/sync` | — | כמו get (`w32tm /resync`, דורש מנהל) |
| `/sys/keyboard/default-language/get` | — | `methods[{id,lang,name}]`, `current` (InputTip, ריק = לפי רשימת השפות) |
| `/sys/keyboard/default-language/set` | `id` (ריק = ביטול) | כמו get (`Set-WinDefaultInputMethodOverride`) |
| `/sys/update-check` | `force` | `current`, `latest`, `tag`, `newer`, `url`, `canInstall`, `notes` |
| `/sys/update-install` | — | `started`, `version`, `sha256`. חסום בנעילה |
| `/sys/power` | `action`: גם `hibernate`, `signout` | `ok` |
| `/sys/usb/eject` | `letter` | נוסף `poweredOff`. כשתפוס: הודעת busy עם שמות התוכנות המחזיקות |
| `/sys/keyboard/backlight/get` | — | נוסף `hint` (קיצור Fn לפי יצרן), `levels` ב-ASUS |

### נעילה — שדות חדשים ב-`/kiosk/status` וב-`/kiosk/settings`
`readOnly`, `clock`, `clockPosition` (bottom-left/bottom-right/top-left/top-right), `restScreen`, `fullscreenOtzaria`, `openSections` (מתוך `allSections`), `warnings` (`admin`).
בזמן נעילה, כל פעולת שינוי של הגדרה שלא ב-`openSections` מחזירה `locked` (423) — נאכף ב-`Server.Handle` לפי `Kiosk.SectionOf`.

## נוסף בגרסה 3.0.0 (פירוט: docs/HARDLOCK.md)
### צימוד — חובה מ-3.0.0
כל בקשה, חוץ מ-`GET /health` ו-`POST /pair/request`, חייבת כותרת `X-Otzaria-Token: <token>`. בלעדיה מתקבלת השגיאה `401` עם הקוד `unpaired`.

| נקודת קצה | גוף | תשובה |
|---|---|---|
| `/pair/request` | `{ client, name }` | `{ token }`. הבקשה ממתינה לאישור בחלון של הגשר (עד 2 דקות). שגיאות: `forbidden` (נדחה), `busy`, `too_many` |
| `/pair/list` | `{}` | `{ clients: [{ id, client, name, created, lastUsed }] }` |
| `/pair/revoke` | `{ id, password? }` | `{ clients }`. בזמן נעילה נדרשת סיסמה |

`/health` מחזיר גם את השדות `auth: "token"` ו-`paired` (האם המפתח שנשלח תקין).

### נעילה מוקשחת — /kiosk/hard/*
| נקודת קצה | גוף | תשובה |
|---|---|---|
| `/kiosk/hard/status` | `{}` | `{ installed, running, configured, active, blockUsbStorage, version, needsUpdate, sessions }` |
| `/kiosk/hard/install` | `{ password }` | סטטוס. מציג UAC; הקוד `cancelled` אם לא אושר. משמש גם לעדכון השירות |
| `/kiosk/hard/settings` | `{ password, blockUsbStorage }` | סטטוס |
| `/kiosk/hard/uninstall` | `{ password }` | סטטוס. מתקבל גם קוד שחזור |

`/kiosk/status` כולל גם את השדה `hard`, באותו מבנה כמו `/kiosk/hard/status`.

שדות בריאות אופציונליים ב-`hard` (גם ב-`/kiosk/hard/status`; בוליאנים/קודים בלבד, בלי נתיבים או SID; `null` = לא ידוע):

| שדה | משמעות |
|---|---|
| `enforcementIssues` | מערך קודים קצרים לכל חסימה שלא הוחלה: `unverified` (נכתבה ולא אומתה) או `error`. מערך ריק = הכול הוחל |
| `stateCorrupt` | קובץ המצב של השירות נראה פגום |
| `hasRecovery` | השירות מחזיק קוד שחזור |
| `configSealOk` | חתימת מפתחות האבטחה ב-config.json תקינה (מטמון קיים של `Kiosk.SecurityIntact`, בלי קריאת pipe נוספת; בלי שירות: true) |
| `updateHealth` | `{ state: none/waiting/healthy/rollback, ok, pending, rollback }` לפי `pending.json` ו-`health.ok` של UpdateRollback. `none` = אין עדכון תלוי (ok=true) |

## דיווחים (Reports.cs — בסיס מינימלי, 13.10.2026)
- `POST /diag/list` → `{enabled, items:[{id, sent, date, kind, text}]}` — ממתינים (outbox) ונשלחו (outbox/sent, 30 אחרונים).
- `POST /diag/delete {ids}` — מחיקת ממתינים לפני שליחה.
- `POST /diag/markSent {ids, details?}` — עם details: הארכיון שומר את הדיווח המאוחד כמו שנשלח (כולל היומן המקוצר) במקום הפריטים הבודדים.
- `POST /diag/markSent {ids}` — העברה לארכיון "נשלחו" (במקום `/diag/ack` שמוחק; התוסף נופל ל-ack בגשר ישן).
- `POST /diag/logTail` → `{text}` — 60 שורות אחרונות של bridge.log בלי שורות שנראות כסוד ובלי שם המשתמש.
- `POST /diag/note {text}` — תיאור ידני לתור.
- `POST /sys/offline {report?: bool}` → `{offline}` — התוסף מדווח את מצב המנותק של אוצריא (`app.getConnectivity`); נשמר ב-config (`otzariaOfflineMode`). בלי report: קריאה בלבד.
### הקשחת BIOS/UEFI מתוך תוכנה — /bios/*

ניסיון להגדיר סיסמת BIOS ולחסום אתחול מהתקן USB/חיצוני דרך ממשקי WMI ספציפיים ליצרן (ר' `bridge/BiosHardening.cs`
ו-`docs/SECURITY_NOTES.md` לפירוט מלא, כולל מגבלות אמיתיות וסיכון סיסמה שנשכחה). נתמך בפועל: Lenovo, HP (דגמים
עסקיים). Dell/Asus: זיהוי והסבר בלבד, בלי ניסיון הגדרה.

| נתיב | גוף | data |
|---|---|---|
| `/bios/status` | `{}` | `{ vendor: "lenovo"\|"hp"\|"dell"\|"asus"\|"other", manufacturer, model, supported: bool, wmiReachable?, wmiError?, usbBootSetting?: {name, currentValue, possibleValues} \| null, note?, reason? }` — לא דורש סיסמה, לא משנה כלום |
| `/bios/harden` | `{ password, setPassword: bool, biosPassword?, blockUsbBoot: bool }` | `{ vendor, manufacturer, model, steps: [{ id: "password"\|"usbBoot", attempted, success, settingName?, message }] }` — דורש סיסמת מנהל (או קוד שחזור) ו-Windows Hello כשהאימות הדו-שלבי פעיל; `unsupported` ביצרן שאינו Lenovo/HP |

כל שלב מדווח בכנות `success`/`message` גם כשהפעולה נכשלת (למשל הגדרה לא נמצאה בדגם הזה, או שכבר מוגדרת סיסמה אחרת) —
אין החזרת "הצלחה" ללא אישור בפועל מה-WMI.

## מיקום, משוב ו-PDF (20.10.2026, לא ארוז עדיין)

כל הנקודות POST עם `X-Otzaria-Bridge: 1` ו-`X-Otzaria-Token` (צימוד), כמו כל השאר.

| נתיב | גוף | תשובה |
|---|---|---|
| `/sys/location` (וגם `/location`) | `{ maxAgeSec?: 600, timeoutSec?: 15 }` | `{ lat, lon, accuracy, source: "gnss"\|"wifi"\|"cellular"\|"ip"\|"default"\|"unknown", time, altitude?, heading?, speed? }` (שלושת האחרונים רק עם GPS, אחרת null — מהשחרור הבא). מיקום כבוי או חסום → `location_off` (403) עם הסבר איך להפעיל; לא התקבל בזמן → `location_timeout` (504) |
| `/sys/location/address` | `{ lat, lon }` | #105, ניסיון בלבד (MapLocationFinder של Windows, לרוב דורש מפתח שירות): `{ found: true, source: "windows", town, district, region, country, street, number, postcode, display }` או `{ found: false, reason }` — אז ליפול ל-OpenStreetMap בתוסף. תמיד 200 |
| `/sys/location/settings` | `{}` | פותח את הגדרות המיקום של Windows (`ms-settings:privacy-location`) |
| `/feedback` | `{ text, plugin, version, repo?, title?, kind? }` | `{ queued: true }`. כינוי ל-`/sys/feedback`: נכנס לתור ונשלח למתווך `achat-relay` |
| `/pdf/handlers` | `{ force? }` | `{ defaultId, handlers: [{ id, name, kind, exists, isDefault, capabilities }] }`. מזהים: `windows-default`, `windows-chooser`, `sumatra`, `acrobat`/`acrobat-reader`, `foxit` |
| `/pdf/open` | `{ path, handlerId?, page? }` | `{ opened, handlerId, pageHonored }`. נתיב עובר `FxPolicy`; בנעילה רק אם `pdf` ב-`kiosk.allowOpenExtensions`, ותמיד בתוכנת ברירת המחדל |

הערה: בתוכנית "כיוון תפילה" נכתב `GET /location`. בגשר כל הבקשות (חוץ מ-`/health`) הן POST עם צימוד, ולכן המימוש הוא `POST /location`.

## 1.0.2 (20.10.2026)
| נקודת קצה | גוף | תשובה |
|---|---|---|
| `/pdf/assoc/status` | `{}` | `{ registered, isDefault, command }` |
| `/pdf/assoc/register` / `unregister` | `{}` | כמו status. נרשם אוטומטית בעלייה אלא אם `pdf.assoc=false` |
| `/pdf/assoc/open-settings` | `{}` | פותח `ms-settings:defaultapps` |
| `/pdf/inbox/list` | `{}` | `{ items: [{ id, path, name, size, at, source }] }` (תוקף 10 דק', אחרי FxPolicy) |
| `/pdf/inbox/ack` | `{ ids }` | `{ removed }` |
| `/pdf/engines` | `{}` | `{ engines: [{id,name,path,ops}], ops }` — ghostscript, qpdf, mutool, pdftk, poppler, libreoffice, word |
| `/pdf/op/begin` → `chunk {id,data}` → `run {id,op,params}` → `read {id,offset}` → `end {id}` | | `run` מקבל `engine`. פעולות: compress {level}, grayscale, pdfa, repair, protect, unlock, linearize, pdf2png (ZIP), totext, office2pdf {ext} |
| `/sys/compass` (וגם `/compass`) | `{}` | `{ available, headingTrue, headingMagnetic, accuracy, time }` — רק במכשיר עם חיישן מצפן |
שורת פקודה: `OtzariaBridge.exe --open-pdf "<path>"` (שם הקובץ בתור, פותח את אוצריא בתוסף הצופה).


## משימות רקע (#134–#135)
כל משימת "שגר ושכח" בגשר עוברת דרך `SafeTask.Run(...)` או `.Observe(where)` (bridge/SafeTask.cs): חריגה נרשמת ליומן ונצפית, ולא מגיעה ל-`UnobservedTaskException` כדיווח task-error. קוד חדש: לא להשאיר `Task.Run`/`ReadToEndAsync` בלי await — להשתמש ב-SafeTask.
