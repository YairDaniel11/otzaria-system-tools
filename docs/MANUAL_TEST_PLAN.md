# תוכנית בדיקה ידנית להקשחה (מחשב ייעודי, לפני פריסה)

**מצב המסמך: תוכנית בלבד. אף אחת מהבדיקות כאן טרם בוצעה.** כל תיבת סימון ריקה. הקוד נבנה ונבדק ב-xunit, אבל חלקים רבים (ר' פרק 4) מסומנים בקוד כ"לא נבדק מול Windows חי".
משלים את הפרק "פריסה בטוחה" ב-`SECURITY_NOTES.md` ואת `BOOT_HARDENING.md`, `HARDLOCK.md`, `RELEASE.md`. מדיניות: `HARDENING_POLICY.md`.

כללי ברזל:
- רק על VM או מחשב חילוף. **לא על המחשב הראשי.** ההקשחה משנה SafeBoot, WinRE, bcdedit ו-Policies, ויכולה לנעול החוצה.
- מריצים לפי הסדר. לא עוברים לשלב הבא לפני שהקודם עבר או שהכשל הובן.
- בכל כשל: שומרים את `%ProgramData%\OtzariaBridge\service.log` (מנהל) ואת מה שהמסך הראה, ומשחזרים snapshot לפני המשך.
- נתונים ששימושיים: שירות `OtzariaBridgeService`, קובץ `%ProgramFiles%\OtzariaBridge\OtzariaBridge.exe`, יציאות הגשר 47917-47921 (`GET /health`), תיקיית מצב `%ProgramData%\OtzariaBridge`.

---

## 0. הכנה ומסלול חירום (לפני כל דבר אחר)

### 0.1 סביבה
- [ ] VM (Hyper-V/VMware/VirtualBox) עם Windows 11 אמיתי ומעודכן, או מחשב חילוף. עדיף שני חשבונות: **מנהל** (`admin1`) ו**משתמש רגיל** (`student`). בנוסף חשבון מנהל שני `admin2` שלא משתמשים בו לבדיקות, רק לחירום.
- [ ] Word (או Office) מותקן, אוצריא מותקנת, דפדפן. אם בודקים BitLocker/Secure Boot: VM עם TPM וירטואלי ו-UEFI.
- [ ] **Snapshot/נקודת שחזור "נקי"** לפני כל התקנה (ב-VM: checkpoint תקין, כבוי ללא מצב זיכרון). במחשב פיזי: תמונת דיסק (Macrium/ווינדוס) ולא רק נקודת שחזור, כי ההקשחה נוגעת באתחול.
- [ ] צילום מצב רישום לפני: `reg export HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot safeboot-before.reg`, `bcdedit /enum all > bcd-before.txt`, `reagentc /info > winre-before.txt`, `sc qc OtzariaBridgeService` (צפוי: לא קיים).
- [ ] לשמור את הקבצים האלה מחוץ למחשב הנבדק.

### 0.2 מסלול חירום: להכין ולבדוק עכשיו
1. **קוד שחזור:** נוצר בהגדרה הראשונה של הסיסמה (`/kiosk/set-password`, 12 תווים, מוצג פעם אחת). לצלם/לרשום מחוץ למחשב. אם לא נשמר: אין דרך להפיק אותו מחדש בלי ביטול הנעילה.
2. **cmd מורם עם `--uninstall`:** לפתוח "שורת פקודה כמנהל" (`admin1` או `admin2`) ולהכין את הפקודות, בלי להריץ:
   - `"%ProgramFiles%\OtzariaBridge\OtzariaBridge.exe" --unlock` (בלי ערך: מבקש קוד בהקלדה מוסתרת; או `--unlock -` עם stdin)
   - `"%ProgramFiles%\OtzariaBridge\OtzariaBridge.exe" --uninstall` (מבקש סיסמה מוסתרת)
   - הערה: `--unlock CODE` ו-`--password X` בשורת הפקודה עדיין עובדים, אבל נחשפים לתהליכים אחרים. לשימוש חירום בלבד.
3. **`sc.exe delete`:** `sc stop OtzariaBridgeService` ואז `sc config OtzariaBridgeService start= disabled`, ובמקרה קיצון `sc delete OtzariaBridgeService`. אחרי מחיקה ידנית: `reg delete HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\OtzariaBridgeService /f` וגם `...\Network\OtzariaBridgeService`, `bcdedit /set recoveryenabled Yes`, `reagentc /enable`, ואם נשארו מדיניות: `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System /v DisableTaskMgr /f` (וכן `DisableRegistryTools`).
4. **אתחול מדיסק חיצוני אם WinRE כבוי:** ההקשחה מריצה `reagentc /disable`. להכין USB התקנה של Windows (Media Creation Tool) ולוודא ש-BIOS/VM מאפשר אתחול ממנו (ב-VM: ISO מחובר). משם: Repair > Command Prompt, ומה שצריך (`reg load` לקוץ SYSTEM ומחיקת מפתח השירות, או `sc` לא עובד אופליין, לכן `reg load HKLM\TMP D:\Windows\System32\config\SYSTEM` ועריכת `ControlSet001\Services\OtzariaBridgeService\Start` ל-4). אם הוקשח BIOS (חסימת USB boot) צריך את סיסמת ה-BIOS: **במחשב ייעודי לא מפעילים הקשחת BIOS בסבב הראשון.**
5. חשבון מנהל שני וגישה מרחוק/קונסולת VM כדי שאפשר להיכנס גם כשהמשתמש הראשי תקוע.

### 0.3 בדיקת מסלול החירום (לפני ההתקנה, ושוב אחרי ההתקנה בשלב 1)
- [ ] הרצת USB/ISO אתחול ל-Repair Command Prompt עובדת, והדיסק נגיש (אם BitLocker פעיל: מפתח השחזור ביד).
- [ ] הפקודות מ-0.2 נכתבות ונטענות בלי שגיאת תחביר. (ההרצה האמיתית רק אחרי ההתקנה.)
- [ ] ה-snapshot משוחזר בהצלחה: משחזרים פעם אחת בכוונה ובודקים שהמכונה עולה.

אם נתקעים: אין מסלול חירום עובד = **לא ממשיכים**.

---

## 1. סדר הבדיקות (מהבטוח לסיכוני)

פורמט לכל בדיקה: צעדים, צפוי, אם נתקעים. אחרי כל שלב: snapshot חדש ("אחרי שלב N").

### 1. התקנה (בלי נעילה פעילה)
צעדים:
1. כ-`student` להריץ את `OtzariaBridge.exe` מתיקיית הורדות. צפוי: התקנה עצמית ל-`%LOCALAPPDATA%\OtzariaBridge`, קיצור "קיוסק אוצריא", אייקון במגש.
2. `curl http://127.0.0.1:47917/health` (או הפורט הפנוי הראשון עד 47921).
3. כל התוספים נטענים באוצריא ללא שגיאה.

צפוי: אין נעילה, אין שירות. שולחן עבודה רגיל. מחיקת ההתקנה (`--uninstall`, סיסמה ריקה אם לא הוגדרה) עובדת ומחזירה למצב נקי.
נתקעים: להסיר עם `--uninstall`; אם נכשל: לשחזר snapshot.

### 2. שירות (התקנת הרכיב המוקשח, נעילה עדיין כבויה)
צעדים:
1. מהתוסף "הגדרות מערכת" > נעילה > נעילה מוקשחת > התקנה (או `/kiosk/hard/install`). לאשר UAC.
2. `sc query OtzariaBridgeService` (RUNNING), `sc qc OtzariaBridgeService` (LocalSystem, אוטומטי).
3. `icacls %ProgramData%\OtzariaBridge` כ-`student` ניסיון קריאה: Access Denied. `icacls "%ProgramFiles%\OtzariaBridge\OtzariaBridge.exe"` ללא כתיבה למשתמש רגיל.
4. קיום `%ProgramData%\OtzariaBridge\net` (תיקיית חילוץ): כתיבה רק ל-SYSTEM/Admins. הגשר עולה (`/health`) ואין שגיאות ב-`service.log`. (פרק 4, פריט תיקיית חילוץ.)
5. אתחול מחדש: השירות והגשר עולים לבד.

6. **Boot hardening מופעל כבר בשלב הזה (מכוון, החלטת S1: "כל עוד השירות Configured", גם כשהנעילה כבויה).** אחרי ההתקנה: `reagentc /info` = Disabled, `bcdedit /enum` מראה `recoveryenabled No` + `bootstatuspolicy IgnoreAllFailures`, ויש `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\OtzariaBridgeService` (וגם `Network`). כלומר המחשב כבר ללא WinRE גם בלי נעילה. זו לא תקלה.
7. אימות pipe כמשתמש לא-מורם: `/kiosk/hard/status` מחזיר תשובה (לא 503), ובלוג הגשר אין "שרת ה-pipe אינו תהליך השירות המותקן". האימות הוא מול ה-pid ש-SCM מדווח לשירות (`sc queryex OtzariaBridgeService` > PID), לא מול נתיב/סשן.
8. קיצור הדרך `קיוסק אוצריא.lnk` נוצר (גם כש-ACP של Windows אינו עברית).

שחזור (לבטל את Boot hardening): הדרך המומלצת `--uninstall` (בסיסמה/קוד שחזור), שמחזיר הכול ממקור. ידנית, ממנהל מורם: `bcdedit /set {default} recoveryenabled Yes`, `reagentc /enable`, ומחיקת `reg delete HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\OtzariaBridgeService /f` וכן `...\SafeBoot\Network\OtzariaBridgeService`. (השירות עלול לאכוף מחדש כל עוד הוא Configured; לכן קודם `--uninstall`, או עצירה והשבתה `sc config OtzariaBridgeService start= disabled`.)

צפוי: ללא נעילה, המחשב נראה רגיל (חוץ מהשינויי האתחול שלעיל). ב-`service.log` רשומות install/הכנה, בלי חריגות.
נתקעים: `service.log` (כמנהל). אם הגשר לא עולה כלל: `--uninstall`, ואז snapshot.

### 3. pipe והתחברות
צעדים:
1. כ-`student`: `/kiosk/hard/status` מחזיר `installed/running` נכון.
2. `\\.\pipe\OtzariaBridgeService` קיים. (FirstPipeInstance, פרק 4.)
3. תהליך מתחזה: כ-`student` להריץ סקריפט (PowerShell) שיוצר `NamedPipeServerStream("OtzariaBridgeService")` לפני עליית השירות (עוצרים שירות, יוצרים pipe, מפעילים שירות). צפוי: השירות לא נופל בשקט; ב-`service.log` "יצירת המופע הראשון נכשלה". אין הרצה שבה הגשר מדבר עם ה-pipe המתחזה (בדיקה: ללא בקשת סיסמה אליו).
4. הגדרת סיסמה לראשונה: חייבת מנהל מורם; כמשתמש רגיל נדחית (`forbidden`).
5. הגדרת סיסמה ורישום קוד שחזור.

צפוי: סטטוס אמין; pipe חסום להתחזות.
נתקעים: להפסיק את התהליך המתחזה, `sc stop` ו-`sc start`. אם ה-pipe תפוס: אתחול.

### 4. נעילה בסיסית
צעדים:
1. הפעלת נעילה (`/kiosk/enable`) עם סיסמה. צפוי: מסך מלא/מצב קיוסק, אוצריא בחזית, שולחן עבודה חסום, משימות מוסתרת.
2. מנהל משימות (`Ctrl+Shift+Esc`), `regedit`, `mmc` כ-`student`: נחסמים ונהרגים (GuardTools, פרק 4). כ-מנהל מורם: לא נהרגים.
3. `taskkill /f /im OtzariaBridge.exe` כ-`student`: Access Denied, או חוזר תוך שניות (EnsureBridgeInSessions).
4. העתק של `OtzariaBridge.exe` מתיקיית משתמש, הרצה כשנעילה פעילה (גשר זר, פרק 4): נהרג, והגשר האמיתי ממשיך. ב-`service.log` "נהרג גשר שלא הופעל ע"י השירות".
5. פתיחה זמנית (`/kiosk/temp-unlock`) וסיום מוקדם: הנעילה חוזרת.
6. כיבוי בסיסמה. כיבוי בקוד שחזור. קוד שגוי נדחה; 5 שגויים = השהיה של דקה.
7. החזרת השעון (שינוי שעון ידני > 2 דקות): הפתיחה הזמנית מתבטלת והנעילה חוזרת (fail-closed).

נתקעים: `--unlock` מ-cmd מורם (0.2). אם גם זה נכשל: `sc stop` ואז `--unlock`; לאחר מכן Safe Mode/ISO.

### 5. hook מקלדת
צעדים (כשנעילה פעילה, אוצריא בחזית):
- [ ] `Win`, `Win+כל מקש`, `Alt+Tab`, `Alt+Esc`, `Alt+Space`, `Ctrl+Esc`, `Ctrl+Shift+Esc`: נבלעים.
- [ ] `Alt+F11` ו-`Alt+F8` ב-Word: נבלעים. `Alt+F4` באוצריא מותר רק בתוכנה מותרת.
- [ ] `Ctrl+Alt+O` (חזרה לאוצריא) עובד; `AltGr+O` בפריסה אירופית **לא** מפעיל.
- [ ] watchdog: אחרי עומס CPU (או הקפאת התהליך 10 שניות בכלי) ה-hook חוזר תוך 3-6 שניות (Windows מסירה hook איטי בשקט).
- [ ] עכבר: לחיצה ימנית וכד' לפי מה שהוגדר.

נתקעים: אם ה-hook "בולע" מקשים בלי הצדקה (נעילת הקלדה): קוד שחזור מ-cmd מורם, או כיבוי מהשירות. רושמים איזה צירוף.

### 6. דיאלוגי פתיחה/שמירה
זיהוי לפי class `#32770`, AutomationId `1148` (שם הקובץ), `1000`/`1001` (פרק 4). בכל בדיקה נרשם גם אם נראה הבזק של תוכן.
- [ ] Word: קובץ > פתח > בחירת `.docx` מותר: נפתח.
- [ ] בחירת `.exe`, `.bat`, `.ps1`, `.json`, `.lnk` (גם בהקלדת השם): הדיאלוג נסגר.
- [ ] קובץ בלי סיומת (תוכן docx ששמו `report`): נבדק לפי תוכן/נחסם לפי המדיניות.
- [ ] הקלדה איטית של `report.docx` (הדיביונס ~0.4 שניות): אין סגירה באמצע הקלדה.
- [ ] שם ארוך 8.3, `report.docx:stream` (ADS), נתיב UNC, נתיב `ProgramData`, `Program Files`: חסומים.
- [ ] FxPolicy locked: כל דיאלוג Open/Save נסגר מיד. restricted: תיקיות מחוץ לרשימה נחסמות כשהנתיב מלא בתיבת השם.
- [ ] בחירה מרובה (`"a.docx" "b.exe"`).
- [ ] דיאלוג מותאם (לא comdlg32) בתוכנה כלשהי: מתועד כחור ידוע, לא כשל בדיקה.
- [ ] הדיאלוג בעברית RTL ובלוקליזציה אחרת (אם אפשר): AutomationId אותו דבר?

נתקעים: אם דיאלוג חוקי נסגר תמיד: להפעיל `FxPolicy=open` או לכבות נעילה, לרשום את גרסת Windows ושם הדיאלוג ובדוח.

### 7. Word / VBA
- [ ] Word נפתח מרשימת "תוכנות מותרות" (`/kiosk/programs/launch`), ולא ממקום אחר.
- [ ] `Alt+F11`, `Alt+F8`, סרגל "מפתחים" ופתיחת מאקרו מקובץ `.docm`: נחסמים או הקובץ נחסם בדיאלוג.
- [ ] חלון Word ב-`ApplicationFrameWindow`/ClickToRun לא נהרג בטעות על ידי SweepProcesses.
- [ ] תהליכי-בן של Word (`ai.exe`, `OfficeClickToRun`, `msoia`) לא נהרגים ללא סיבה (selfTree). מקרה בעייתי: `splwow64`, עוזרי הדפסה.
- [ ] מגבלה ידועה: מאקרו שכבר רץ במסמך מהימן לא נתפס (DialogGuard #10). לרשום שזה צפוי.

### 8. צילום מסך
(שמות מחלקות החלון בפרק 4)
- [ ] `Win+Shift+S` / כלי החיתוך מופיעים רק כשהצילום מאושר (עד 120 שניות). צפוי: הצילום נעשה ונשמר בלוח, שכבת הצילום לא נסגרת כל 2 שניות ("רענון" בלי צילום).
- [ ] ב-Win11 "חדש": אחרי הלחיצה אין חלון חסום, שורת המשימות מוסתרת בזמן הצילום, ואחריו חוזרת למצב הנעילה.
- [ ] בזמן החלון המאושר: `Win+R` (class `#32770`), Explorer (`CabinetWClass`) ו-`ApplicationFrameWindow` זר **נסגרים**.
- [ ] אחרי 120 שניות: כלי החיתוך נסגר.
- רושמים את שמות מחלקות החלון שנצפו בפועל (Spy++/Inspect) ומשווים ל-`IsSnipOverlayClass`.

### 9. תהליכים בלי חלון (SweepProcesses)
- [ ] Baseline: מה שרץ כשהשומר עלה לא נהרג (גם אם לא מהימן). לבדוק: הפעלת נעילה כשיש תהליך משתמש רגיל ברקע, צפוי: נשאר.
- [ ] תהליך חדש בלי חלון (`powershell -w hidden -c "Start-Sleep 600"`, `cmd /c ping -t localhost`, `.exe` מתיקיית Downloads): נהרג, ב-`service.log` / יומן הגשר `process sweep: ... נהרג`.
- [ ] תהליכי מערכת חוקיים לא נהרגים: `RuntimeBroker`, `ctfmon`, `SecurityHealthSystray`, `OneDrive`, עדכוני Windows, מדפסת, `Teams`/`Zoom` אם מותר.
- [ ] תהליך של תוכנה מותרת (ברשימה לבנה) בלי חלון: לא נהרג. תהליך מורם/מוגן (אין גישה לנתיב): לא נהרג ולא נתקע.
- [ ] משתמש שני מחובר (session אחר): לא משפיע.
- [ ] CPU/זיכרון: הסריקה כל כמה שניות לא מעמיסה (מדידה בדקה).

נתקעים: אם נהרג תהליך נחוץ: לרשום שם+נתיב, לכבות נעילה, ולהוסיף לרשימה הלבנה (או לתקן בקוד) לפני פריסה.

### 10. Safe Mode ו-WinRE (**מסוכן; רק אחרי snapshot**)
הערה: Boot hardening (WinRE כבוי, `recoveryenabled No`, SafeBoot) פעיל מרגע שהשירות Configured (שלב 2), **גם כשהנעילה כבויה**, לפי החלטת S1. "סגירת נעילה" לבדה לא מחזירה אותם; רק `--uninstall` (או שחזור ידני: `bcdedit /set {default} recoveryenabled Yes`, `reagentc /enable`, מחיקת מפתחות SafeBoot של OtzariaBridgeService).
- [ ] עם השירות מותקן (נעילה פעילה או לא): קיים `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\OtzariaBridgeService` וגם `Network`.
- [ ] `reagentc /info`: Disabled. `bcdedit /enum`: `recoveryenabled No`, `bootstatuspolicy IgnoreAllFailures`, `bootmenupolicy Standard`. הקובץ `%ProgramData%\OtzariaBridge\boothardening.json` קיים עם ערכים מקוריים.
- [ ] `msconfig` > Boot > Safe boot > Minimal, אתחול. צפוי: השירות רץ ואוכף. `--unlock` מ-cmd מורם עובד גם בתוך Safe Mode.
- [ ] Safe Mode עם רשת.
- [ ] סגירת נעילה בלבד: הרכיב עדיין Configured, ולכן `bcdedit`/`reagentc`/SafeBoot נשארים מוקשחים (מכוון, S1). חזרה למקור (רק מה שהשתנה, `boothardening.json` מתעדכן) מתבצעת בהסרה.
- [ ] הסרה מלאה (`--uninstall`): אין יותר רישום SafeBoot, WinRE חזר.
- [ ] 3 כשלי אתחול מכוונים (כיבוי כוח באמצע עליית Windows): צפוי שאין "תיקון אוטומטי" וש-Windows עולה; לוודא שיש ISO.
- [ ] המצב "recoveryenabled No": אם Windows לא עולה, ליפול ל-0.2 סעיף 4.

נתקעים: Safe Mode שהנעילה חוסמת: להיכנס לחשבון מנהל, `--unlock`. קוד אבוד: `sc config OtzariaBridgeService start= disabled`, אתחול רגיל, `--uninstall`, מחיקת `%ProgramData%\OtzariaBridge`.

### 11. Sign out / Logon / החלפת משתמש
- [ ] התנתקות והתחברות מחדש כ-`student`: הגשר עולה בתוך כ-3-6 שניות, והנעילה חלה לפני שרואים שולחן עבודה לזמן ארוך.
- [ ] כניסה כ-`admin1` בזמן נעילה: הגשר רץ גם שם (מנהל מורם נפטר מ-GuardTools).
- [ ] החלפת משתמש מהירה (Fast User Switching): שני סשנים, גשר בכל אחד.
- [ ] נעילת מסך (`Win+L`) ופתיחה, שינה/המשך, אתחול מחדש: הנעילה נשארת.
- [ ] כניסה רחוקה (RDP) אם רלוונטי: סשן נוסף, לא נתקע.
- [ ] Windows Hello (אם 2FA פעיל): תהליך העזר `--hello-helper` מציג חלון בסשן המשתמש; ביטול = סירוב; מחשב בלי ביומטריה: מסלול קוד שחזור. רשום את התוצאה (פרק 4, Hello-helper).
- [ ] `ReadSecret` בקונסול: ראו בדיקה 14.

### 12. עדכון
- [ ] עדכון מגרסה ישנה כשהשירות מותקן: `/sys/update-install` מחזיר 409 ברור ולא הצלחה מדומה. העדכון בפועל: הרצת קובץ ההתקנה כמנהל (UAC).
- [ ] אחרי עדכון: גרסת `ServiceExe` החדשה, השירות עולה, הנעילה נשמרת, הסיסמה/הקוד לא אבדו.
- [ ] ‏`SHA256SUMS.txt.sig` חסר, או sums ששונה בבית אחד, או `keyver` נמוך מ-`MinKeyVersion`: "העדכון בוטל".
- [ ] `icacls %LOCALAPPDATA%\OtzariaBridge\update`: בלי ירושה, רק משתמש נוכחי/SYSTEM/Administrators.
- [ ] שדרוג לאחור (גרסה נמוכה יותר) נדחה (מונוטוניות).

### 13. DACL של הגשר
- [ ] כ-`student` עם Process Explorer/`Get-Process`/קוד קצר ב-C#: `OpenProcess(PROCESS_TERMINATE)`, `PROCESS_VM_WRITE`, `PROCESS_CREATE_THREAD` על הגשר: נדחה. `PROCESS_QUERY_LIMITED_INFORMATION`: מותר.
- [ ] גם חוטים: `OpenThread(THREAD_SUSPEND_RESUME/TERMINATE)` נדחה (ThreadGuardTick כל ~200ms; חלון קצר אחרי הפעלה הוא ידוע).
- [ ] הגשר שנוצר על ידי שירות (`--from-service`) מול גשר שעלה מ-Run של המשתמש: הראשון מוגן מרגע היצירה; השני נהרג ומוחלף.
- [ ] `TokenDefaultDacl`: **כבוי בכוונה** (`UseTokenDefaultDacl=false`). לא מפעילים בבדיקה הזו; רק אם רוצים לבדוק בנפרד (פרק 4).
- [ ] תהליכי-בן (אוצריא, Word) נשארים ניתנים לסגירה על ידי המשתמש בדרך רגילה (לא נתקעים בלתי-סגירים).
- [ ] תיקיות וקבצים: `%ProgramData%\OtzariaBridge` ו-`...\net` ללא כתיבה למשתמש; קישור (junction) שנזרע בתיקיה נמחק על ידי השירות.

### 14. ReadSecret בקונסול
- [ ] cmd רגיל: `OtzariaBridge.exe --unlock` מבקש קוד בלי הד; הקלדה, Enter. עובד גם בעברית/הדבקה.
- [ ] `echo CODE | OtzariaBridge.exe --unlock -` (stdin).
- [ ] PowerShell, Windows Terminal, ו-cmd מורם מ-UAC (חלון חדש): ההקלדה מוסתרת, ולא נשאר ECHO כבוי אחרי יציאה (הסרת הד שוחזרה).
- [ ] הקונסול מתחבר (`AttachConsole(-1)`) ומדפיס תוצאה גם כשהקובץ הוא אפליקציית GUI.
- [ ] `--uninstall` מבקש סיסמה באותו אופן.
נתקעים: אם אין הד ואין קלט: להשתמש ב-stdin (`-`), או בצורה המוכרת `--unlock CODE` (חשיפה זמנית, משנים קוד אחר כך).

---

## 2. ניקוי בין שלבים
אחרי כל שלב שנכשל או הושלם: `--unlock`, `--uninstall`, בדיקה שאין שירות (`sc query`), אין מפתחות SafeBoot, `bcdedit`/`reagentc` תואמים ל-snapshot הנקי, ואז משחזרים snapshot אם משהו לא תואם.

## 3. סיכום תוצאות (למלא בזמן הבדיקה)
| שלב | עבר/נכשל | תאריך | הערות/גרסת Windows |
|---|---|---|---|
| 0 חירום | | | |
| 1 התקנה | | | |
| 2 שירות | | | |
| 3 pipe | | | |
| 4 נעילה | | | |
| 5 hook | | | |
| 6 דיאלוגים | | | |
| 7 Word/VBA | | | |
| 8 צילום | | | |
| 9 תהליכים | | | |
| 10 Safe/WinRE | | | |
| 11 Logon | | | |
| 12 עדכון | | | |
| 13 DACL | | | |
| 14 ReadSecret | | | |

---

## 4. ריכוז סיכוני false-positive ופריטים ש"דורשים Windows חי"
מקורות: הערות בקוד (`KioskShell.cs`, `DialogGuard.cs`, `HardLock.cs`, `ServiceHardening.cs`) ודוחות הסוכנים. כולם **לא אומתו על Windows אמיתי** והם המקומות הסבירים לסגירה שגויה או נעילה תקועה.

| פריט | איפה | סיכון | מה לבדוק (שלב) | סימן כשל |
|---|---|---|---|---|
| שמות מחלקות חלון בצילום (`ScreenClip*`, `Windows.UI.Core.CoreWindow`, `Microsoft.UI.Content.DesktopChildSiteBridge`, `Windows.UI.Composition.DesktopWindowContentBridge`, `Windows.UI.Input.InputSite.WindowClass`) | `KioskShell.IsSnipOverlayClass` | רשימה לא אומתה; מחלקה חסרה = שכבת הצילום נסגרת ונראה כרענון בלי צילום; מחלקה מיותרת = פתח עוקף | 8 | הצילום "מהבהב" ונעלם |
| AutomationId `1148` (שם הקובץ) ו-`1000`/`1001` (רשימה/breadcrumb), `1` (אישור) | `DialogGuard.cs` | זיהוי דיאלוג וקריאת תיבת השם; שינוי בין גרסאות Windows = כשל זיהוי (נכשל סגור: סוגר) או לא נתפס | 6 | דיאלוג חוקי נסגר תמיד, או `.exe` נבחר |
| `FirstPipeInstance` | `HardLock.cs` ~1190 | התחזות על ה-pipe; אם משהו תופס את השם מוקדם (אנטי-וירוס/שירות קודם שלא נעצר) השירות לא עונה | 3 | `service.log`: "יצירת המופע הראשון נכשלה", הגשר נשאר בלי שירות |
| תיקיית חילוץ `%ProgramData%\OtzariaBridge\net` (`DOTNET_BUNDLE_EXTRACT_BASE_DIR` ב-Environment של השירות) | `HardLock.PrepareBundleDir/MarkBundleReady` | אם הגשר לא טוען את ה-DLL מהתיקיה (הרשאות/AV) הוא לא עולה בכלל | 2 | `/health` לא עונה אחרי התקנה; "תיקיית חילוץ מוגנת לא פעילה" |
| הריגת גשר זר (נתיב ≠ `ServiceExe`) | `EnsureBridgeInSessions`, `BridgeProcs.Untrusted/Impostors` | הרג גשר לגיטימי אחרי עדכון/נתיב 8.3/Run של המשתמש; לולאת הריגה-הפעלה | 4, 12 | `service.log` חוזר "נהרג גשר" כל שנייה |
| `TokenDefaultDacl` (כבוי: `UseTokenDefaultDacl=false`) | `ServiceHardening.ProcessDacl` | אם יופעל: תהליכי-בן בלתי-ניתנים לסגירה, אובייקטי סנכרון | 13 | לא להפעיל בלי בדיקה נפרדת; נשאר חלון קצר לחוטים |
| Hello-helper (`--hello-helper`) | `Program.cs`, `HardLock.cs` ~1255-1565 | חלון Hello לא מופיע (סשן, פוקוס); תהליך עזר רץ בטוקן המשתמש (חלון הזרקה קצר) | 11 | הפעולה "תקועה" עד timeout; קוד שחזור פוטר |
| GuardTools (הריגת taskmgr/regedit/mmc למשתמש רגיל) | `HardLock.GuardTools`, `ToolGuard` | הורג כלי של מנהל מורם אם זיהוי הרמה נכשל; משתמש בסשן אחר | 4 | מנהל מורם לא מצליח לפתוח regedit בזמן נעילה |
| SweepProcesses baseline | `KioskShell.SweepProcesses` | baseline נתפס בעליית השומר: אם עלה מאוחר, תהליכים זרים נכנסים ל"מותר"; אם תוכנה לגיטימית עולה אחרי: נהרגת | 9 | תוכנה מותרת נעלמת; תהליך חשוד נשאר |
| ReadSecret בקונסול (`CONIN$`, `ReadConsoleW`, ללא ECHO) | `CliSecret.ReadSecret` | מסלול החירום עצמו; כשל = קוד חירום לא נקלט | 14 | אין הד ואין קבלת קלט; קונסול נשאר בלי הד |
| RepairPolicyKeyAcl | `HardLock.RepairPolicyKeyAcl` (הערה: "לא נבדק בחי") | משתמש שם Deny ל-SYSTEM על `Policies\System`; התיקון עלול לשבור ACL; הגיבוי הוא GuardTools | 4 | `service.log`: "policy ... נכשל"/"לא אומת", מדיניות לא נאכפת |
| `KioskShell` רשימת Close/Kill וה-explorer (`ExplorerShellClasses`, `NeverKill`) | `KioskShell.cs` | סגירת חלונות מעטפת מותרים או explorer; שורת משימות/תפריט התחל | 4, 8 | שולחן עבודה ריק/שחור |
| מגבלות ידועות נוספות בקוד | `DialogGuard` 1-10, `BiosHardening` (Dell/Lenovo לא נבדקו), `/bios/harden` | חסימת BIOS ללא גישה לסיסמה | לא בסבב הראשון | |

כללים להכרעה: כל כשל בפריט מהטבלה = חוסם פריסה עד תיקון או החלטה מפורשת. פריט שהוא "חור ידוע" (למשל דיאלוג מותאם, מאקרו VBA) מתועד ואינו כשל בדיקה.

---

## 5. צ'קליסט פריסה (רק אחרי שכל הסעיפים לעיל עברו)

### 5.1 לפני הפריסה
- [ ] טבלת התוצאות (פרק 3) מלאה, בלי "נכשל" פתוח; ממצאים תועדו ונסגרו (קוד/תיעוד).
- [ ] `dotnet build bridge\OtzariaBridge.csproj -c Release` ו-`dotnet test tests\OtzariaBridge.Tests` ירוקים על ה-commit שמשוחרר.
- [ ] מסלול חירום נבדק **שוב** על הבנייה הסופית (לא רק על בנייה קודמת).
- [ ] קוד שחזור ו-סיסמת מנהל נשמרים מחוץ למחשב היעד; ISO/USB התקנה זמין; אם הוקשח BIOS: הסיסמה שמורה ונבדקה.
- [ ] BitLocker עם PIN, Secure Boot ו-TPM פעילים (`BOOT_HARDENING.md`); הקשחת BIOS רק אחרי שהכול עבד בלי BIOS.
- [ ] פריסה למחשב אחד קודם, לפי תקופת ניסיון (יום-יומיים), ורק אז לשאר.

### 5.2 צעדי GitHub ידניים (מתוך `docs/RELEASE.md`)
1. Settings > Environments > `release`: Required reviewers (אתם); Deployment branches and tags = Selected branches: `main` בלבד (לא `v*`).
2. להעביר את הסוד `UPDATE_SIGNING_KEY` ל-Environment secret של `release` ולמחוק אותו מ-Repository secrets.
3. Settings > Rules > Rulesets: Tag ruleset על `v*` (Restrict creations/updates/deletions, bypass לבעלים בלבד).
4. Branch ruleset על `main`: Require a pull request, Require status checks (ג'וב `build` מ-`build.yml`), חסימת force-push.
5. Settings > Actions > General: "Fork pull request workflows" = Require approval; Workflow permissions = Read.
6. (מומלץ) CODEOWNERS ל-`.github/workflows/*` ול-`tools/*`.

### 5.3 שחרור
- [ ] מיזוג ה-PR ל-`main` אחרי ביקורת.
- [ ] `python tools/check-prerelease.py`; bump ב-`bridge/OtzariaBridge.csproj` וב-`*/manifest.json`; תיאור קצר ב-manifest ≤150 תווים; סעיף CHANGELOG. (גרסת יעד לפי תוכנית השחרור, למשל 1.0.0.)
- [ ] יצירת תגית `vX.Y.Z` על commit שכבר ב-`main` (או מ-UI של GitHub). אין דחיפת תגית מסשן ענן (403).
- [ ] Actions > "שחרור וחתימה" > Run workflow על `main` עם `tag=vX.Y.Z`; אישור ה-reviewer ב-Environment `release`.
- [ ] לוודא שה-Release נוצר עם כל הקבצים, כולל `SHA256SUMS.txt` ו-`SHA256SUMS.txt.sig`, ושה-`keyver` בקובץ הוא הנכון.
- [ ] בדיקת עדכון מקצה לקצה על ה-VM מגרסה ישנה לחדשה (שלב 12).
- [ ] פרסום לחנות (`store=true`) רק ידנית ולבסוף.

### 5.4 אחרי הפריסה
- [ ] ניטור `service.log` ביומיים הראשונים; דיווח משתמשים על "נעילה תקועה".
- [ ] מסלול החירום זמין למנהל המקומי (מסמך מודפס עם הפקודות מ-0.2).
