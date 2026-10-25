# מפרט: שילוב תוכנות PDF בגשר (הצעה)

> **20.10.2026 (1.0.2):** גם שלבים ג (PdfOps.cs, Ghostscript של PDF24 / qpdf) וד (PdfAssoc.cs, "פתח באמצעות" + inbox) מומשו. **קודם לכן:** שלבים א ו-ב מומשו ב-`bridge/PdfApi.cs` (`/pdf/handlers`, `/pdf/open` עם `sumatra -page N` ו-`acrobat /A page=N`). עדיין לא נבדקו ב-Windows ולא נארזו. שלבים ג-ה ממתינים לתשובות לשאלות הפתוחות (סעיף 9; הצעות תשובה שם).

> סטטוס: **הצעה, לא ממומש.** נכתב ב-19.10.2026 מול גרסה 1.0.0, על סמך `docs/BRIDGE_API.md` ועל סמך קריאה חלקית של `bridge/` (`Programs.cs`, `FsApi.cs`, `Installer.cs`).
> רכיבים שלא נבדקו בקוד מסומנים **[לאמת]**.
> הצד השני של התוכנית (התוסף "ספריית PDF+OCR") מתועד במאגר שלו: `docs/PDF-HANDLER-PLAN.md` ב-[pdf-viewer](https://github.com/e0548433917-gif/pdf-viewer). שני המסמכים חייבים להישאר עקביים, ושינוי בחוזה כאן מחייב עדכון שם.

## 1. מטרה

1. שהגשר יוכל לשמש כמטפל `.pdf` ב-Windows, כך שפתיחת PDF מהסייר תגיע לתוסף "ספריית PDF+OCR" באוצריא.
2. שהתוסף יוכל לדעת אילו תוכנות PDF מותקנות, ולפתוח קובץ באחת מהן ("פתח ב…").
3. שהתוסף יוכל להשתמש ביכולות של כלי PDF מותקנים (חיבור, חלוקה, סיבוב, חילוץ עמודים), בלי תלות בתוכנה מסוימת.

## 2. החלטה: גשר אחד, לא גשר נפרד לצופה

ההמלצה: להוסיף לגשר הקיים מודול `PdfApi.cs`, ולא ליצור גשר שני.

- **חיבור כפול:** שני תהליכים על אותו טווח פורטים (47917–47921) יתנגשו. גשר שני דורש טווח משלו, צימוד נפרד, אייקון מגש נוסף ובדיקת `/health` נוספת בכל תוסף.
- **נעילה:** כל ההגנות נמצאות בגשר הקיים: מצב נעילה, `PathPolicy`/`FxPolicy`, רשימת התוכנות המותרות, חלון החסד לחזית (`Programs.GrantForegroundGrace`). גשר נפרד לא יראה אותן, ויצטרך לשכפל או לעקוף אותן. זה פותח פרצה בדיוק במחשבי הלימוד הנעולים.
- **משטח תקיפה:** כל תהליך נוסף שמקשיב ל-localhost ומפעיל תוכנות מוסיף סיכון.
- **התקנה:** משתמש אחד, קובץ התקנה אחד (Inno), עדכון אחד.

מתי כן לשקול גשר נפרד: אם בעלי הגשר מחליטים שהם לא רוצים קוד רישום/הפעלת תוכנות חיצוניות במאגר שלהם. במקרה כזה צריך עוזר קטן בטווח פורטים נפרד (למשל 47931–47935), עם צימוד משלו, ועם החלטה מפורשת שהוא לא פועל במצב נעילה. זו חלופה גרועה יותר ולכן לא הדרך המומלצת.

## 3. עקרונות אבטחה (חובה)

- **פתיחה לפי `id` בלבד.** אין הפעלה של נתיב `exe` שהתוסף שולח. התוסף מקבל רשימת `id`-ים מהגשר, ושולח בחזרה `id`. זה אותו עיקרון כמו `/kiosk/programs/launch`.
- **כל נתיב קובץ** עובר את `PathPolicy`/`FxPolicy` הקיימים, כמו כל נתיב ב-`/fs/*`. גם נתיבי פלט של פעולות.
- **רישום ברישום Windows: `HKCU` בלבד.** בלי הרשאות מנהל. אותו עיקרון כמו `HKCU Run` הקיים.
- **אין שרשור פקודות.** ארגומנטים לכלים חיצוניים נבנים כרשימה (`ProcessStartInfo.ArgumentList`), לא כמחרוזת מחוברת. שם קובץ הוא תמיד ארגומנט, לא חלק מפקודה.
- **כותרות:** כל הבקשות כפופות לכללים הקיימים (`X-Otzaria-Bridge`, `X-Otzaria-Token`, דחיית `Origin`).
- **מצב נעילה:** `/pdf/open`, `/pdf/op` ו-`/pdf/assoc/*` מחזירים `locked` כשהנעילה פעילה, אלא אם הסיומת `pdf` ברשימת `kiosk.allowOpenExtensions`. ל-`handlers` (קריאה בלבד) אין חסימה. **[לאמת]** מול `Kiosk.cs` אם יש לחסום גם אותו.
- **רשימת מתאמים סגורה** בקוד. אי אפשר להוסיף "מתאם" מההגדרות או מהתוסף.

## 4. נקודות קצה חדשות (`/pdf/*`)

רישום באותה שיטה כמו `FsApi.cs`: `Server.Route("/pdf/handlers", Handlers);` וכו'. כל הגופים JSON, והתשובה במעטפת `{ok, data}` הקיימת.

| נתיב | גוף | data |
|---|---|---|
| `/pdf/handlers` | `{ ext?: ".pdf", force?: false }` | `{ defaultId: string\|null, handlers: [{ id, name, kind: "viewer"\|"editor"\|"tool", exists, isDefault, capabilities: [...] }] }` |
| `/pdf/open` | `{ path, handlerId?, page? }` | `{ opened: true, handlerId, pageHonored: bool }`. בלי `handlerId`: ברירת המחדל של Windows. `page` מכובד רק למתאם עם `openAtPage` |
| `/pdf/op` | `{ op, handlerId?, inputs: [path], outPath, params? }` | `{ outPath, engine, pages?: n }` |
| `/pdf/assoc/status` | `{}` | `{ registered: bool, isDefault: bool, command }` |
| `/pdf/assoc/register` | `{}` | `{ registered: true }`. רושם את הגשר כאפשרות פתיחה ל-`.pdf` (`HKCU\Software\Classes`, `Applications`, `OpenWithProgids`) |
| `/pdf/assoc/unregister` | `{}` | `{ registered: false }` |
| `/pdf/assoc/open-settings` | `{}` | `{ opened: true }`. פותח את מסך "אפליקציות ברירת מחדל" של Windows (`ms-settings:defaultapps`) |
| `/pdf/inbox/list` | `{}` | `{ items: [{ id, path, at (ISO UTC), source }] }` |
| `/pdf/inbox/ack` | `{ ids: [id] }` | `{ removed: n }` |

### 4.1 יכולות (`capabilities`)

ערכים אפשריים, וכל מתאם מצהיר רק על מה שהוא באמת תומך בו:
`open`, `openAtPage`, `print`, `merge`, `split`, `rotate`, `extractPages`.

### 4.2 פעולות (`/pdf/op`)

| `op` | `params` | הערה |
|---|---|---|
| `merge` | — | `inputs` לפי הסדר, `outPath` חדש |
| `split` | `{ ranges: "1-5,8,10-12" }` | יוצר קובץ אחד לכל טווח. `outPath` = תיקיית יעד |
| `rotate` | `{ pages: "1-3", degrees: 90\|180\|270 }` | |
| `extractPages` | `{ pages: "2-4" }` | |

- `outPath` חייב להיות חדש. אם קיים: `exists`, ואין דריסה שקטה.
- ללא `handlerId`, הגשר בוחר את המנוע הראשון הזמין לפי הסדר: כלי שורת פקודה → Acrobat.
- אם אין מנוע זמין: `unsupported`, והתוסף חוזר למנוע המובנה שלו (`pdf-lib`).

### 4.3 תור פתיחה (`inbox`)

התוסף לא יכול לקבל קריאות, הוא רק יוזם. לכן כשהמשתמש לוחץ פעמיים על PDF:
1. Windows מפעיל `OtzariaBridge.exe --open-pdf "<path>"`.
2. אם הגשר כבר רץ: העברה למופע הפעיל **[לאמת]** אם קיים מנגנון מופע יחיד (`PipeMac.cs`?), אחרת להוסיף.
3. המופע הפעיל בודק את הנתיב מול המדיניות, מוסיף ל-`inbox`, ומפעיל את אוצריא (`kiosk.otzariaPath` / סכמת `otzaria://`, ראו `Installer.OtzariaProtocolRegistered`) אם היא סגורה.
4. התוסף שולף ב-`/pdf/inbox/list` ומאשר ב-`/pdf/inbox/ack`.

הנתיבים בתור נשמרים בזיכרון בלבד ונמחקים אחרי `ack` או 10 דקות.

## 5. מתאמים

רשימה סגורה בקוד. הסעיפים שמסומנים **[לאמת]** הם פרמטרים שנכתבו מהזיכרון, ויש לבדוק מול הגרסה המותקנת לפני מימוש.

| מזהה | סוג | זיהוי | יכולות | פרמטרים עיקריים |
|---|---|---|---|---|
| `windows-default` | viewer | תמיד קיים | `open` | `ShellExecute` עם `open` |
| `windows-chooser` | viewer | תמיד קיים | `open` | חלון "פתח באמצעות" (כמו `/fs/open openWith`) |
| `sumatra` | viewer | נתיב התקנה / `App Paths` | `open`, `openAtPage`, `print` | `-page N` **[לאמת]** |
| `acrobat-reader` / `acrobat` | viewer/editor | `App Paths`, `Uninstall` | `open`, `openAtPage`, `print` (Pro: גם `merge`, `split`, ... דרך COM) | `/A "page=N"` **[לאמת]** |
| `foxit` | viewer | `Uninstall` | `open`, `print` | **[לאמת]** |
| `qpdf` | tool | `PATH` / נתיב מוגדר | `merge`, `split`, `rotate`, `extractPages` | `qpdf --empty --pages a.pdf b.pdf -- out.pdf`, `qpdf in.pdf --pages . 1-5 -- out.pdf`, `qpdf in.pdf --rotate=+90:1-3 -- out.pdf` **[לאמת]** |
| `mutool` | tool | `PATH` | `merge`, `extractPages` | `mutool merge -o out.pdf a.pdf b.pdf`, `mutool clean -g in.pdf out.pdf pages` **[לאמת]** |
| `pdftk` | tool | `PATH` | `merge`, `split`, `rotate` | `pdftk a.pdf b.pdf cat output out.pdf` **[לאמת]** |
| `acrobat-com` | editor | מופע COM `AcroExch.*` | `merge`, `split`, `extractPages` | `AcroExch.PDDoc.InsertPages`, `DeletePages` **[לאמת]**. רק עם Acrobat Pro |

**רישוי לפני צירוף:** הגשר ברישוי PolyForm Noncommercial (`docs/LICENSING.md`). הפעלת כלי שהמשתמש התקין בעצמו לא מצריכה צירוף. אם רוצים לצרף כלי עם הגשר: `qpdf` ברישיון Apache-2.0 **[לאמת]**, ואילו MuPDF (`mutool`) ו-`pdftk` ברישיונות copyleft חזקים, ולכן אין לצרף אותם בלי בדיקה משפטית.

### 5.1 זיהוי

- `HKLM`/`HKCU` `Software\Microsoft\Windows\CurrentVersion\Uninstall` (אותה שיטה שבה `Programs.DetectAuto` מזהה תוכנות תורניות. כדאי להרחיב אותה ולא לכתוב סורק שני).
- `App Paths` לפי שם קובץ ה-`exe`.
- `.pdf` → `OpenWithProgids` ו-`SHAssocEnumHandlers` לרשימת הטיפול המוכרת של Windows. **[לאמת]** אם `SHAssocEnumHandlers` זמין מ-C# בלי עטיפה.
- כלי שורת פקודה: חיפוש ב-`PATH` ובתיקיות התקנה מוכרות.
- התוצאה נשמרת במטמון כמה דקות, ורענון מפורש בקריאה עם `force`. (`force: true` ב-`/pdf/handlers`)

## 6. קודי שגיאה

קודים קיימים מספיקים: `bad_request`, `not_found`, `forbidden`, `exists`, `locked`, `unsupported`, `internal`, `fx_outside_policy`. נוספים:
- `no_handler`: `handlerId` לא ברשימה.
- `op_failed`: הכלי החיצוני החזיר שגיאה. ההודעה כוללת את הקוד ושורת שגיאה מקוצרת, בלי נתיבים מלאים של משתמשים אחרים.

## 7. שלבי מימוש מוצעים

| שלב | תוכן | תלות |
|---|---|---|
| א | `/pdf/handlers` (זיהוי) + `/pdf/open` עם `windows-default`/`windows-chooser` | אין |
| ב | מתאמי צופים (`sumatra`, `acrobat-reader`) + `openAtPage` | א |
| ג | `/pdf/op` עם `qpdf` | א |
| ד | `/pdf/assoc/*` + `--open-pdf` + `inbox` | א, ובדיקת מופע יחיד |
| ה | `acrobat-com`, `mutool`, `pdftk` | ג |

שלב א אפשרי גם בלי כל שאר החלקים, והתוסף יכול כבר להשתמש בו.

## 8. בדיקות ב-Windows

- רישום כמטפל: מופיע ב"פתח באמצעות", ולא משנה את ברירת המחדל לבד.
- `--open-pdf` כשהגשר רץ וכשהוא לא רץ; כשאוצריא סגורה; כשהנתיב מחוץ למדיניות.
- זיהוי לא יוצר חלון ולא חוסם את חוט ה-UI (סריקת הכוננים בעבר חסמה את ה-hook).
- מצב נעילה: כל הנקודות החוסמות מחזירות `locked`.
- שם קובץ עם רווחים, עברית, גרשיים וסוגריים, ונתיב ארוך.
- פלט `qpdf` לתיקייה שאינה מותרת: `fx_outside_policy`.

## 9. שאלות פתוחות

1. האם יש מנגנון מופע יחיד בגשר (`PipeMac.cs`)? אם לא, צריך אחד לפני שלב ד.
2. האם לחסום `/pdf/handlers` במצב נעילה?
3. האם להרחיב את `kiosk.allowOpenExtensions` או להגדיר רשימה נפרדת ל-PDF?
4. האם בעלי המאגר מוכנים לקוד רישום ל-`HKCU\Software\Classes` (הוא כבר רושם `Run` ו-`otzaria://` נקרא)?
5. צירוף `qpdf` או הסתמכות על התקנה של המשתמש.


### הצעות תשובה (20.10.2026)
1. מופע יחיד: יש mutex (`Local\OtzariaBridge` ב-`Program.cs`) שעוצר עותק שני, ויש pipe רק לנעילה המוקשחת (`HardLock.cs`). לשלב ד: pipe קטן חדש בקובץ נפרד, שהעותק השני שולח בו את הנתיב ויוצא.
2. `/pdf/handlers` בנעילה: לא לחסום (קריאה בלבד). `/pdf/open` בנעילה פותח רק בברירת המחדל וכשהסיומת מותרת (כך מומש).
3. להרחיב את `kiosk.allowOpenExtensions`, לא רשימה נפרדת.
4. רישום ב-`HKCU\Software\Classes`: יש תקדים (HKCU Run, `LockScreenImage.cs`). רק בלחיצת משתמש, וביטול שמחזיר את המצב הקודם.
5. qpdf: לא לצרף בשלב ראשון. התוסף עושה חיבור/חלוקה/סיבוב ב-pdf-lib; הגשר ישתמש ב-qpdf רק אם מותקן.
