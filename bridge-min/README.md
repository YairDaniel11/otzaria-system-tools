# גשר אוצריא — מינימלי

גשר מקומי קטן ל**צופה PDF+OCR** בלבד. מחליף את "אחת שאלתי" כשצריך רק את הפונקציות שהצופה משתמש בהן.

## מה הוא עושה (בדיוק מה שהתוסף קורא לו)
| נקודת קצה | שימוש בצופה |
|---|---|
| `GET /health`, `/pair/request` | זיהוי הגשר וצימוד (חלון אישור במחשב) |
| `/oneocr/status`, `/oneocr/recognize` | OCR ישיר במנוע של כלי החיתוך + שכבת טקסט מדויקת (PDF חיפוש) |
| `/fs/roots`, `/fs/search`, `/fs/stat`, `/fs/read` | חיפוש קובץ במחשב ופתיחתו (כולל קבצים גדולים מ-50MB) |
| `/fs/pick-save-file`, `/fs/write-binary` | שמירת PDF עם שכבת טקסט |
| `/snip/status`, `/snip/dump`, `/snip/ocr` | תצוגת מצב בלבד (ראו "מגבלות") |

## בנייה
דרוש .NET 10 SDK. מריצים `build.bat` (נייד מלא, exe יחיד) או `build.bat light` (קטן, דורש .NET 10 Desktop Runtime מותקן).
בגיטהאב: ה-workflow מייצר שני קבצים (OtzariaBridge ו-OtzariaBridge-light).

## נייד
כל הקבצים נוצרים בתיקיית `data` שליד ה-exe: `clients.json` (גיבוב מפתחות הצימוד), `oneocr\` (קבצי המנוע שמועתקים מכלי החיתוך), `bridge.log`.
אם אין הרשאת כתיבה לתיקייה — נעשה שימוש ב-`%LOCALAPPDATA%\OtzariaBridgeMin`.

## הפעלה אוטומטית עם Windows
בהפעלה הראשונה הגשר מוסיף את עצמו להפעלה אוטומטית (ערך אחד ב-`HKCU\\...\\Run`, בלי הרשאות מנהל). אפשר לכבות/להדליק בתפריט המגש ("הפעלה אוטומטית עם Windows").
אם ה-exe הועבר לתיקייה אחרת — מפעילים אותו פעם אחת משם והנתיב ברישום מתעדכן לבד. לפני מחיקת ה-exe מומלץ לכבות את האפשרות.

## זיכרון
כשאין בקשות במשך 3 שניות: איסוף זבל מלא עם דחיסה והחזרת הזיכרון ל-Windows. מודל ה-OCR משוחרר אחרי 20 שניות בלי זיהוי (הטעינה הבאה לוקחת כשנייה-שתיים). הערכים בקבועים `IdleMs` (Mem, ב-Program.cs) ו-`IdleUnload` (Ocr.cs).

## הפעלה
מפעילים את `OtzariaBridge.exe` (אייקון במגש; לחיצה ימנית: ביטול צימודים / תיקיית נתונים / יציאה).
בפעם הראשונה שהצופה מתחבר יוצג חלון "בקשת גישה" — לאשר.
לאתחול עם Windows: קיצור דרך ל-exe בתיקיית `shell:startup`.

## מגבלות
* דורש Windows 11 עם "כלי החיתוך" (Snipping Tool) מותקן — משם מועתק מנוע ה-OCR.
* ללא גיבוי של אוטומציית חלון כלי החיתוך (`/snip/ocr`). אם המנוע הישיר לא זמין, ה-OCR של Windows ייכשל והצופה יציג שגיאה (אפשר לעבור ל-Tesseract שבתוסף).
* ללא משוב ל-GitHub (`/sys/feedback`) — המשוב ממשיך להישלח דרך אוצריא/דוא"ל.
* ללא נעילה, הקשחה, עדכונים והגדרות מערכת.

## הורדה (מהמאגר achat-shaalti)
- נייד מלא: https://github.com/Hadabadra/achat-shaalti/releases/download/bridge-min/OtzariaBridgeMin.exe
- קל (דורש .NET 10 Desktop Runtime): https://github.com/Hadabadra/achat-shaalti/releases/download/bridge-min/OtzariaBridgeMin-light.exe

המקור: arye000001-boop/-PDF (רישיון MIT). ה-workflow `bridge-min.yml` בונה ומעדכן את המהדורה `bridge-min` בכל דחיפה ל-main שנוגעת בתיקייה.
