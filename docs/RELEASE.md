# תהליך אריזה ושחרור — לקחים ומלכודות

## סטטוס 3.6.0 (13.10.2026) — שוחרר
- commit `4982dfd` ב-main וב-next, CI ירוק (ריצה 38399560181). המהדורה `v3.6.0` נוצרה ע"י ה-CI עם 4 קבצים. לא פורסם לחנות.
- הבנייה המקומית ובדיקת node --check: JS נקי; את הגשר (net8.0-windows) אפשר לבנות רק ב-CI.
- תוכן: סעיף "משוב בדיקה 13.10.2026" ב-ROADMAP — מומש אך **טרם נבדק ב-Windows**.
- 3.5.0: נפתר — המהדורה v3.5.0 קיימת עם 4 קבצים.

## מלכודות שנתגלו
1. **`check-prerelease.py` קרס ב-Windows CI** (UnicodeEncodeError, cp1252) כשהדפיס עברית. תוקן: הפלט מוגדר ל-UTF-8 בתחילת הסקריפט.
2. **התיאור הקצר ב-manifest מוגבל ל-150 תווים** (ולידציית החנות). לבדוק את האורך לפני bump.
   בדיקה: `python -c "import json;print(len(json.load(open('file-explorer/manifest.json',encoding='utf-8'))['description']))"`
3. **מסנן הנתיבים ב-build.yml** לא כולל את `tools/`. שינוי שנוגע רק ב-`tools/` לא מפעיל CI, וצריך להריץ ידנית (workflow_dispatch).
4. **המהדורה נוצרת אוטומטית מדחיפה ל-main** כשה-`<Version>` ב-csproj חדש. אם הריצה הראשונה שיצרה את המהדורה לא צירפה קבצים, ריצות הבאות לא מעדכנות אותה, כי שלב ההשלמה פועל רק על טיוטה.
   פתרון: למחוק את המהדורה (לא את התג) ולהריץ שוב את ה-workflow, או להעביר את המהדורה לטיוטה ולהריץ שוב.
5. **מגבלות בסשנים של Claude בענן:** דחיפת תגים ב-git מחזירה 403, יצירה, עריכה ומחיקה של מהדורות חסומות, `gh` עם GraphQL חסום (צריך `gh api` ב-REST), והעלאה של קבצים בינאריים חסומה.
   את שלב המהדורה צריך להשאיר ל-CI או לעשות ידנית.

## סדר מומלץ לשחרור הבא
1. `python tools/check-prerelease.py`
2. bump ב-`bridge/OtzariaBridge.csproj` וב-`*/manifest.json`, ובדיקת אורך התיאור הקצר (≤150).
3. סעיף חדש ב-CHANGELOG מתוך `git log --no-merges main..next`.
4. commit, ‏push ל-next, ‏fast-forward ל-main. ה-CI יוצר את התג ואת המהדורה בעצמו, ואין צורך לדחוף תג ידנית.
5. לוודא שהמהדורה נוצרה עם 4 הקבצים, ורק אז לשקול פרסום לחנות (`store=true`, ידני).

## שחרור חתום (הקשחה S4): מ-main בלבד, בלי טריגר מתגית

מה השתנה: `build.yml` רק בונה ובודק (`dotnet test`) על כל push לענף ועל כל `pull_request` (בלי סודות, בלי חתימה, בלי Release).
תגית `v*` כבר **לא** מפעילה כלום. שחרור וחתימה נמצאים רק ב-`release.yml`, שמופעל ידנית (`workflow_dispatch`) ורק מהענף main.
WHY: workflow שמופעל מתגית נלקח מה-commit של התגית, כלומר מי שיכול לדחוף תגית שולט בקוד ה-workflow (כולל בדיקת "ב-main" ושלב החתימה).

זרימה: (1) מיזוג PR ל-main אחרי ביקורת. (2) יוצרים תגית `vX.Y.Z` על commit שכבר ב-main (או מ-UI של GitHub). (3) Actions > "שחרור וחתימה" > Run workflow על main, קלט `tag=vX.Y.Z`.
הג'וב `verify` מוודא שהתגית קיימת ושה-commit שלה אב-קדמון של main; `build` בונה את אותו SHA; `release` (Environment `release`) חותם ומפרסם, אחרי בדיקה חוזרת שהתגית לא זזה.

### צעדים ידניים (בהגדרות GitHub, אי אפשר מהקוד)
1. Settings > Environments > `release`: Required reviewers (אתם); Deployment branches and tags = **Selected branches: main** בלבד (לא `v*`).
2. להעביר את הסוד `UPDATE_SIGNING_KEY` ל-**Environment secret** של `release` ולמחוק אותו מ-Repository secrets.
3. Settings > Rules > Rulesets: Tag ruleset על `v*` (Restrict creations/updates/deletions, ‏bypass רק לבעלים): תגית משוחררת לא ניתנת להזזה.
4. Branch ruleset על main: Require a pull request, Require status checks (הג'וב `build` מ-`build.yml`), חסימת force-push.
5. Settings > Actions > General: "Fork pull request workflows" = Require approval; Workflow permissions = Read.
6. (מומלץ) CODEOWNERS ל-`.github/workflows/*` ו-`tools/*`, כדי ששינוי בתהליך השחרור ידרוש אישור.

### ביטול מפתח (keyver)
ה-CI כותב `# keyver=N` ל-SHA256SUMS.txt (משתנה `KEY_VERSION` ב-build.yml), והגשר דוחה keyver חסר (=0) או נמוך מ-`UpdateApi.MinKeyVersion`.
אחרי דליפה: מפתח חדש + סוד חדש, העלאת `KEY_VERSION` ב-build.yml וגם `MinKeyVersion` ב-UpdateApi.cs לאותו N+1, ושחרור גשר מעבר.
גשר שלא עודכן ממשיך לסמוך על המפתח הישן (אין דרך להפעיל ביטול מרחוק): זו מגבלה מובנית של אימות מקומי.

### עדכון כשהשירות מותקן
`/sys/update-install` מחזיר עכשיו כשל ברור (409) כשהגשר רץ מ-Program Files כשירות, כי `--install` מעדכן רק את LOCALAPPDATA והשירות היה ממשיך להריץ את הגרסה הישנה.
העדכון בפועל: להריץ את קובץ ההתקנה כמנהל. הצעה לנתיב עדכון דרך השירות: op חדש `update-service` ב-pipe של השירות שמקבל נתיב+SHA-256 ומריץ `InstallTrust.CopyVerified` (כולל בדיקת מונוטוניות הגרסה).
