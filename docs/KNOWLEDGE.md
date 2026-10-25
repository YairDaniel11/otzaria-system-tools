# ידע מצטבר — כלי מערכת לאוצריא

קובץ ידע קבוע: עובדות שנמצאו בעבודה ואין צורך לחפש אותן שוב. מעדכנים בכל סשן שמגלה משהו חדש.
מצב השלבים ב-[WORKPLAN.md](WORKPLAN.md), מצב כללי ב-[STATUS.md](STATUS.md), חוזה הגשר ב-[BRIDGE_API.md](BRIDGE_API.md).

## אוצריא — SDK וחנות
- **הרשאות:** החנות דוחה תוסף עם הרשאה חסרה. כל קריאה ל-`Otzaria.call` צריכה הרשאה מתאימה במניפסט.
- **תגית "מראה תואם":** ניתנת רק עם 0 הערות עיצוב. צבעים רק כטוקנים ב-`:root`, גדלים ב-em.
- **`app.run_on_startup`:** הוסר בגרסה 0.9.98, ולכן לא משתמשים בו.
- **כפתור בסרגל הקורא:**
  - `contributes.startup.toolbarItems[{id,type:"button",title,icon,openPlugin}]`, עם ההרשאות `reader.toolbar` ו-`app.startup_contributions`.
  - האירוע: `Otzaria.on('reader.toolbar_item_clicked', fn)`.
  - בלי `contexts` הכפתור מופיע בכל קורא. ה-context הידוע הוא `reader-pdf`.
- **קריאות שאומתו:** `storage.get`, `storage.set`, `notifications.showInApp`, `library.getBookContent`, `reader.addContextMenuItem`, `app.open_url`, `network.fetchStream`.
- **אין API ידוע לשלושה דברים:**
  - פתיחת קובץ מקומי באוצריא;
  - פתיחת תוסף אחר עם פרמטר (`plugin.openSelf({param})` פותח רק את התוסף עצמו);
  - התקנה שקטה של תוסף (ההתקנה רושמת הרשאות ב-DB של אוצריא). המשתמש מתקין קובץ `.otzplugin` מתוך אוצריא.
- **תקשורת עם תוכנה מקומית:** דרך `network.fetchStream` עם ההרשאה `network.localhost`, ובמניפסט `network.allowlist: ["127.0.0.1","localhost"]`.
- **מקור מלא לתאימות:** `docs/תאימות-SDK-אוצריא.md` בענף main.

- **ולידטור החנות (`Otzaria/otzaria-plugin-validator@v1`):** `toolTab.iconName` חייב להיות שם FluentUI בלי קידומת (`folder_24_regular`, לא `fluent:...`). `font-family: inherit` או גופן מקודד = "הערת עיצוב" — רק `var(--font-*)`. פרסום ראשון של תוסף מחייב צילום מסך. פרטי החנות בסודות הריפו (`OTZARIA_USER`/`OTZARIA_PASSWORD`).
- **`app.openUrl`** (לא `app.open_url`) הוא שם הקריאה; `app.open_url` הוא שם ההרשאה.

## הגשר (bridge/)
- **טכנולוגיה:**
  - C#/.NET 8 עם TFM ‏`net8.0-windows10.0.19041.0`;
  - Kestrel עם `FrameworkReference Microsoft.AspNetCore.App`;
  - WinForms למגש;
  - `System.Management` ל-WMI.
- **קבצים:**

  | קובץ | תפקיד |
  |---|---|
  | `Program.cs` | ארגומנטים, Config, הרצת תהליכים |
  | `Server.cs` | מעטפת תשובה, מיפוי שגיאות, עזרי JSON |
  | `FsApi.cs` | נקודות הקצה של `/fs` |
  | `SysApi.cs` | Wi-Fi, שמע ונקודות הקצה של `/sys` |
  | `Kiosk.cs` | נעילה |
  | `Installer.cs` | התקנה והסרה |
  | `Tray.cs` | אייקון במגש |

- **ארגומנטים:**
  - `--install`;
  - `--background`;
  - `--portable` (הרצה בלי התקנה);
  - `--uninstall [--password X]`;
  - `--unlock <קוד שחזור>`.
- **קומפילציה מקומית בלי Windows:**
  1. להתקין .NET 8 SDK עם `dotnet-install.sh --channel 8.0`.
  2. להריץ `dotnet build -c Release -r win-x64`.
  3. `EnableWindowsTargeting` כבר מוגדר בקובץ csproj.

  כך נמנעים סבבי CI.
- **CI:**
  - `.github/workflows/bridge.yml` בענף הזה בלבד, על `windows-latest`.
  - אורז את `system-settings/` ואת `file-explorer/` לקובצי `.otzplugin` בתוך `bridge/plugins/`. התיקייה ב-gitignore, ומשם הקבצים מוטמעים כמשאב.
  - artifact נוצר בכל דחיפה. Release מתויג `vX.Y.Z` (לא `bridge-v*`).
- **גודל ה-exe:** כ-89MB, עם `EnableCompressionInSingleFile`. אי אפשר לבצע trimming, כי הגשר משתמש ב-WinForms.
- **שורת hit בחיפוש:** `{type:"hit", entryType:"dir"|"file", name, path, size, modified}`. ב-`bridge.js` הקוד כבר קורא את `entryType`.
- **Wi-Fi דרך netsh:**
  - מריצים `chcp 65001` כדי שהפלט יגיע ב-UTF-8.
  - הפענוח לא תלוי בשפת המערכת: שורות `SSID n :`, אחוז האות לפי `NN%`, וסוג האימות בשורה השנייה של כל גוש. ערכי האימות (Open, WPA2...) באנגלית בכל שפה.
- **שמע:** Core Audio COM. החלפת ההתקן המוגדר כברירת מחדל נעשית דרך הממשק הלא מתועד `IPolicyConfig`. אם נכשל, מחזירים `unsupported`.
- **נעילה:**
  - נעילה רכה: הגשר סוגר את explorer שוב ושוב כל 2 שניות, מתקין hook מקלדת על החוט של המגש, ומחזיר את אוצריא לחזית.
  - נעילה קפדנית: מחליפה את ה-Shell ב-HKCU לגשר עצמו עם `--background`, והגשר מפעיל את אוצריא. כבויה כברירת מחדל, ונדרש `strict:true` מפורש.
  - קוד השחזור (12 תווים) מוצג פעם אחת בלבד, בהגדרת הסיסמה הראשונה.
- **לא נבדק עדיין על Windows אמיתי:** Wi-Fi, בלוטות׳, `IPolicyConfig`, hook המקלדת, קיצור הדרך בשולחן העבודה.

## כללי עבודה (לכל התוספים)
- **קומיטים:** author ו-committer רק `Claude <noreply@anthropic.com>`.
- **דרך העבודה:**
  - בלי סוכנים, בלי צילומי מסך, בלי playwright;
  - `grep -n` ואחריו `sed` לטווח הדרוש, במקום לקרוא קבצים שלמים;
  - עד 2 סבבי תיקון CI.
- **הערות JS:** בלי גרש ASCII בודד.
- **פרסום:** לא מפרסמים לחנות בלי אישור.
- **שמירת ידע:** פרטים חשובים נשמרים בזיכרון של Claude. הידע הטכני נשמר כאן, בגיט.

## CI והוצאה (נלמד ב-1.7.0)
- **הוצאה אוטומטית:** דחיפה ל-main עם שינוי ב-`bridge/`, `system-settings/`, `file-explorer/` או `CHANGELOG.md` בונה ויוצרת Release מהסעיף העליון ב-CHANGELOG. אם התג כבר קיים, מתעדכנות רק ההערות, בלי הקבצים. כדי לרענן קבצים צריך למחוק את ה-Release ואת התג ולדחוף שוב.
- **ולידטור החנות:** תיאור קצר במניפסט עד 150 תווים, אחרת הבדיקה נכשלת. הולידציה נכשלת אחרי שה-Release כבר נוצר.
- **שגיאות CI:** יומני העבודות מופנים לדומיין שאינו נגיש מהסביבה. את השגיאות קוראים מ-`GET /repos/.../check-runs/{job_id}/annotations`.
- **קומפילציה מקומית (עובד מ-12.10.2026):** ה-SDK של אובונטו (apt) חסר WindowsDesktop. מתקינים את ה-SDK הרשמי: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir /opt/dn8`, יוצרים קבצים ריקים `bridge/plugins/{system-settings,file-explorer}.otzplugin`, ומריצים `/opt/dn8/dotnet build -c Release -r win-x64` בתיקייה `bridge`. NuGet נגיש. כך נחסכים סבבי CI.
- **תאורת מקלדת:** ל-Windows אין API אחיד לתאורה רגילה. `Windows.Devices.Lights.LampArray` (מקלדות HID LampArray) מתקמפל ועובד כ-API, אך לא נבדק מול חומרה.


## נלמד ב-2.0.0
- **בודק העדכונים:** ההוצאות מתויגות `vX.Y.Z` (לא `bridge-v*`). הבאג הישן סינן הכול ולכן תמיד החזיר "מעודכן".
- **"מסך שחור" (#8):** המעטפת ב-`HKCU\...\Winlogon\Shell` גוברת גם במצב בטוח ולא מושבתת ב"אפליקציות אתחול". לכן `Kiosk.BootGuard` רץ ראשון בכל הפעלה ומנקה כשאין נעילה מאושרת.
- **עדכון עצמי:** הורדה ל-`%TEMP%\OtzariaBridge-update`, אימות מול `SHA256SUMS.txt` שבהוצאה, ואז `OtzariaBridge.exe --install --quiet` (המתקין סוגר את הגשר הישן, מעתיק ומפעיל מחדש).
- **חלונות WinForms בנעילה** (`Overlay.cs`): שעון צף עם `WS_EX_NOACTIVATE` כדי לא לגנוב פוקוס; מסך מנוחה במקום שחזור מיידי של אוצריא ממוזערת. השומר מדלג כל עוד מסך המנוחה גלוי.
- **תאריך עברי:** ב-C# `CultureInfo("he-IL")` עם `HebrewCalendar`; ב-JS `Intl.DateTimeFormat('he-IL-u-ca-hebrew')`.
- **תוכנות תורה:** נתיבים ידועים ב-`Programs.KnownList`; אם לא נמצאו — חיפוש לפי שם קובץ בכוננים (עומק 4, עד 8 שניות, מטמון 10 דקות).
- **מיזוג בקשות מיזוג:** מיזוג ישיר דרך ה-API (`PUT /pulls/N/merge`) עובד; בהתנגשות מיישמים ידנית.

## נלמד ב-3.0.0
- **צימוד:** אין צורך שהתוסף יקרא סוד מקובץ. הגשר נותן מפתח אחרי אישור בחלון, והתוסף שומר אותו ב-`storage.set` (מפתח `bridgeToken`). כל בקשה שולחת `X-Otzaria-Token`; `unpaired` (401) = לבקש צימוד ולנסות שוב. `/health` פתוח ומחזיר `auth:"token"` ו-`paired`.
- **רכיב השירות:** אותו exe עם `--service` (ServiceBase מ-`System.ServiceProcess.ServiceController`) ו-`--install-service` (מורץ עם `Verb=runas`). צינור עם `NamedPipeServerStreamAcl.Create`. הפעלת הגשר בחשבון משתמש: `WTSEnumerateSessions` → `WTSQueryUserToken` → `CreateProcessAsUser` על `winsta0\default`. קובץ השירות חייב לשבת ב-Program Files (אסור להריץ כ-SYSTEM קובץ שהמשתמש יכול להחליף), ולכן עדכון השירות דורש UAC.
- **בלי שירות מותקן** לא ממתינים לצינור: `HardLock.ServiceStatus` בודק קודם את רשימת השירותים (מטמון של 30 שניות).
- פירוט מלא: `docs/HARDLOCK.md`.


## הבאה לחזית, סרגל תוכנות וחירום (נלמד ב-3.2.0)
- `SetForegroundWindow` מתהליך רקע נכשל בשקט. `Kiosk.BringToFront` משתמש ב-`AttachThreadInput` + Alt שקטה + TOPMOST זמני. תמיד להשתמש בו, לא ב-`SetForegroundWindow` ישירות.
- `Programs.Activate(entry)` פותח או מחזיר לחזית (בלי עותק שני); `Programs.ExistingWindow` מחזיר חלון גלוי או מוקטן של תוכנה מהרשימה.
- `SideBar` (Extras.cs) ו-`RestScreen` (Overlay.cs) רצים על חוט המגש בלבד (`Tray.Invoke`).
- קיצור חירום Ctrl+Alt+Shift+F12 נבדק ב-hook המקלדת ופותח `EmergencyForm`, שדורש את קוד השחזור. אין קיצור בלי אימות, בכוונה.
- יומן השינויים מוטמע ב-exe (`CHANGELOG.md` כמשאב), ולכן כל שינוי בו משנה את ה-exe.
- חיפוש תוכן ב-`FsApi.ContentSnippet` מחזיר קטע לשורת התוצאה (שדה `snippet` ב-hit); הסייר פותח את הקובץ עם `find` להדגשה (`FX.highlightIn`).

## גרסת התוספים חייבת לעלות תמיד (לקח מ-3.7.0)
ב-3.7.0 ה-CI חישב גרסת תוספים 0.x מספירת commits (0.58.0) — נמוכה מ-3.6.0 המותקן, ואוצריא סירבה להתקין (שדרוג לאחור).
מ-3.7.1 שלב "גרסת התוספים" ב-build.yml לוקח את `<Version>` מ-bridge/OtzariaBridge.csproj. אסור לחזור למספור עצמאי/נמוך.
לפני כל שחרור: לוודא ש-manifest.json של שני התוספים בקובץ ה-.otzplugin גבוה מהגרסה הקודמת.

## לוח השנה של אוצריא — איך אירוע calendar.event מוצג (נבדק בקוד אוצריא, 15.10.2026, #69)
מקור: `lib/plugins/adapters/plugin_calendar_adapter.dart` (ענף dev).
- נקראים רק `title`, `startsAt`, `source`, `importance`. **השעה ו-`endsAt` אינם מוצגים** — האירוע נקבע ליום (`baseGregorianDate`) בלבד. לכן השעות צריכות להיות בתוך הכותרת.
- `DateTime.tryParse` ממיר מחרוזת עם היסט (`+03:00`) ל-UTC — 00:30 מקומי נופל ביום הקודם. לכן שולחים זמן מקומי בלי היסט (`2026-10-15T00:30:00`).
- התיאור = `source (importance)`. ערך שאינו `high/medium/low` נראה מוזר — לא שולחים importance.
- `publishedData.upsert/remove` מרעננים את לוח השנה מיד. הפרסום רץ רק כשדף "הגדרות מערכת" פתוח (אין מנוע רקע); רשומות נשמרות עד שמסירים אותן.

## קיצורי מקלדת של התוספים (#69)
- מוצהרים במניפסט: `contributes.startup.shortcuts` (`id`, `label`, `key`, `command`) — דורש `app.startup_contributions` + `app.shortcuts`. אוצריא מעירה את התוסף ושולחת `app.command` עם `{ command, shortcutId }`; המשתמש משנה בהגדרות ← קיצורי מקשים. `plugin.openSelf` (`navigation.write`) פותח את דף התוסף, `param` מגיע ב-`plugin.page_opened`.
- מקשים תפוסים באוצריא: ר' `lib/shortcuts/shortcut_validator.dart` (למשל ctrl+shift+e = לוח שנה). לכן ctrl+shift+alt+…
- שם קריאת התראת מערכת: `notifications.sendSystem` (לא `notifications.system`, שזה שם ההרשאה).

## i18n
- `node tools/i18n-missing.js <plugin>` מדפיס מחרוזות עבריות בלי תרגום. מה שנשאר מכוון: נתוני הדמה ב-js/bridge.js, אותיות גימטריה (מטופלות בקוד), מילות חיפוש, ונוסח הקדשה.
- i18n.js מתרגם מחרוזת מורכבת לפי חלקים (מילים שלמות, מהארוך לקצר) רק אם לא נשאר עברית.

## מופע רקע של הגדרות מערכת (#69)
- `contributes.background.entrypoint = background.html` (בלי ממשק) + `startup.activationEvents: ["app.startup", "calendar.city_changed"]` + הרשאת `app.run_on_startup` (כבויה עד שהמשתמש מאשר בבאנר).
- background.js: רק אם יש `bridgeToken` ב-storage (בלי חלון צימוד מהרקע — `SS.bridge.noPair`), ורק אם הגשר עונה. מריץ `SS.sync.zmanim()` ו-`SS.sync.lockCalendar()` (js/sync.js, משותף לדף) ואז `plugin.backgroundDone`.

- **#8 רשת ביטחון למעטפת (15.10.2026):** בנעילה קפדנית ה-Shell ב-HKCU מפנה ל-`%LOCALAPPDATA%\OtzariaBridge\shell-guard.vbs` (wscript //B), שמריץ את הגשר וממתין לו; אם הקובץ חסר, ההפעלה נכשלה או יציאה עם קוד ≠0 (קריסה) — מופעל explorer. בלי wscript: הפניה ישירה כמו קודם. BootGuard (מצב בטוח/נעילה לא מאושרת) נשאר. טרם נבדק ב-Windows.
