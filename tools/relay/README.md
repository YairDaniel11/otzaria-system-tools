# מתווך דיווחי תקלות (#65)

הגשר שולח דיווחי תקלות (אחרי הסתרת שם משתמש ושמות קבצים) לכתובת שבהגדרה `diag.relayUrl`. המתווך פותח issue בריפו עם התווית `auto-report`. הטוקן נשמר רק במתווך. בגשר אין טוקן.

## הקמה (פעם אחת, כ-10 דקות, חינם)
1. **טוקן GitHub:** ב-github.com → Settings → Developer settings → Fine-grained tokens → Generate.
   - Resource owner: **הארגון `achat-shaalti`** (לא חשבון אישי). אם הארגון דורש אישור לטוקנים, מנהל הארגון צריך לאשר את הטוקן ב-Organization → Settings → Personal access tokens.
   - Repository access: Only select repositories, רק `achat-shaalti/achat-shaalti` (ובנוסף ריפואים מהרשימה ב-GH_REPOS, ראו למטה).
   - Permissions → Issues: **Read and write** בלבד (Metadata: Read-only נוסף אוטומטית). לא לסמן שום הרשאה נוספת.
   - תוקף: שנה. להעתיק את הטוקן.
2. **תווית:** בריפו → Issues → Labels → New label בשם `auto-report`.
3. **Cloudflare:** להירשם ב-dash.cloudflare.com (חינם).
   - Workers & Pages → Create → Worker → שם `achat-relay` → Deploy.
   - Edit code: למחוק הכול, להדביק את `worker.js`, ואז Deploy.
4. **משתנים** (Worker → Settings → Variables and Secrets):
   - `GH_REPO` (Text) = `achat-shaalti/achat-shaalti`
   - `GH_TOKEN` (**Secret**) = הטוקן משלב 1
5. להעתיק את כתובת ה-Worker (`https://achat-relay.<שם>.workers.dev`) ולמסור אותה ל-Claude. היא תוכנס כברירת מחדל ל-`diag.relayUrl` בגשר.
   עד אז אפשר להוסיף ידנית ל-`%LOCALAPPDATA%\OtzariaBridge\config.json`: `"diag.relayUrl": "https://..."`.

## בדיקה
```
curl -X POST https://achat-relay.<שם>.workers.dev -H "content-type: application/json" \
  -d '{"title":"test","body":"test","fingerprint":"0123456789abcdef","version":"3.9.0"}'
```
אמור לחזור `ok` ולהיפתח issue. שליחה חוזרת תחזיר `duplicate`.

## הגנות
- 5 לשעה לכל IP (זיכרון ה-Worker), ו-30 ליום בסך הכול (לפי issues עם auto-report). אין צורך ב-KV.
- כפילות: issue פתוח עם אותו fingerprint חוסם דיווח חוזר. אם חיפוש ה-GitHub נכשל המתווך מחזיר 503 (נכשל סגור), והגשר ינסה שוב מאוחר יותר.
- דיווח אחד ליממה לכל fingerprint: בזיכרון ה-Worker כברירת מחדל, או ב-KV אם מגדירים binding אופציונלי בשם `DEDUPE` (לא חובה).
- גוף עד 8000 תווים, ו-`@` מנוטרל כדי שלא יהיו תיוגים.
- משתמש יכול לכבות את הדיווח האוטומטי (`diag.autoReport`). זה מכבה גם את השליחה לכאן.

## #70 — משוב ידני וריפואים נוספים
- המתווך מקבל גם `kind: "manual"` (משוב משתמש, תווית `user-feedback`) ו-`repo` אופציונלי.
- משתנה סביבה חדש `GH_REPOS` — רשימה סגורה מופרדת בפסיקים, למשל:
  `achat-shaalti/achat-shaalti,achat-shaalti/pdf-viewer,achat-shaalti/pdf-ocr-library,achat-shaalti/madaei-hatanach,achat-shaalti/otzaria`
- הטוקן `GH_TOKEN` צריך הרשאת Issues: write לכל הריפואים ברשימה.
- אחרי עדכון הקוד: `wrangler deploy` מחדש.
- מחוברים: סייר והגדרות (דרך הגשר), צופה PDF (דרך הגשר, ריפו pdf-viewer), כיוון תפילה 2.1.2 (ישירות, ריפו otzaria). עינים למקרא שולח לגיט' דרך ממסר Apps Script משלו.

## פריסה מחדש אחרי מעבר הריפו לארגון (למנהל Cloudflare)
הסימפטום: הדיווחים לא פותחים issue. הסיבה: `GH_REPO`/`GH_REPOS` או הטוקן עדיין מצביעים על הבעלים הישן. בלי השלבים האלה המתווך לא יעבוד.
1. **טוקן חדש:** github.com → Settings → Developer settings → Fine-grained tokens → Generate. Resource owner = הארגון `achat-shaalti`. Repository access = רק הריפואים שברשימת `GH_REPOS`. Permissions → Issues = Read and write, ושום דבר אחר. תוקף עד שנה. אם הארגון דורש אישור, לאשר ב-Organization settings.
2. **תוויות:** לוודא שבכל ריפו קיימות התוויות `auto-report` ו-`user-feedback`.
3. **משתנים ב-Cloudflare** (Workers & Pages → `achat-relay` → Settings → Variables and Secrets):
   - `GH_REPO` = `achat-shaalti/achat-shaalti`
   - `GH_REPOS` = `achat-shaalti/achat-shaalti` ובתוספת כל ריפו נוסף שמחובר, בפורמט `בעלים/שם` לפי הבעלים הנוכחי שלו בפועל. ריפו שעדיין תחת חשבון אישי נשאר עם שם החשבון.
   - `GH_TOKEN` (**Secret**) = הטוקן החדש. להחליף את הישן.
4. **קוד:** להדביק את `worker.js` העדכני (Edit code → Deploy), או `wrangler deploy` אם משתמשים ב-wrangler.
5. לבטל (Revoke) את הטוקן הישן.

### בדיקה אחרי הפריסה
```
curl -i -X POST https://achat-relay.<שם>.workers.dev -H "content-type: application/json"   -d '{"title":"test","body":"test","fingerprint":"0123456789abcdef","version":"3.9.0"}'
```
- `201 ok` וה-issue נפתח בריפו בארגון: תקין.
- שליחה חוזרת: `409 duplicate`.
- `400 repo not allowed`: `GH_REPO`/`GH_REPOS` לא תואמים. `502 github 4xx`: הטוקן חסר הרשאה או לא אושר בארגון. `503`: חיפוש GitHub נכשל (נכשל סגור, הגשר ינסה שוב).
אחרי הבדיקה לסגור את ה-issue של הבדיקה.

### התנהגות שנשמרה (#379)
נכשל סגור (503) כשחיפוש הכפילויות נכשל, דיווח אחד ליממה לכל fingerprint (KV אופציונלי `DEDUPE` או זיכרון), 5 לשעה לכל IP, 30 ליום. ההפניות (redirect) לא מבוצעות והצלחה רק ב-201.
