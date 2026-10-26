# X3: הקשחת עדכון (rollback, נעילת handle)

- **Rollback** (`bridge/UpdateRollback.cs`): `UpdateApi.Install` מגבה את ה-exe המותקן ל-`%LOCALAPPDATA%\OtzariaBridge\rollback\` ורושם `pending.json` (SHA-256 של הגיבוי והחדש) אחרי שכל בדיקות החתימה/הורדה עברו. המתקין (`Installer.Install`) ממתין עד 90 שניות ל-`health.ok` שהגשר החדש כותב (hash של עצמו) אחרי ש-`/health` שלו ענה. בלי סימן: הרג הגשר החדש, שחזור הגיבוי רק אם ה-hash שלו שווה ל-PrevSha הנרשם, הפעלה מחדש וקוד יציאה 4.
- **HandleLock** (`bridge/HandleLock.cs`): בזמן `Kiosk.IsActive` ה-exe הרץ נפתח בלי FILE_SHARE_DELETE/WRITE ותיקיית הנתונים בלי FILE_SHARE_DELETE (חוסם שינוי שם/מחיקה, גם ממנהל). משתחרר ב-Install/Uninstall/Update ובסנכרון (5ש') כשהנעילה כבויה.
- **מגבלות:** למידע על מגבלות או פרצות פתוחות בתוכנה - פנו למפתח יאיר דניאל.
