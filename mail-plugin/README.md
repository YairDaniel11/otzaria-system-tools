# דואר — תוסף לאוצריא (בענף next; לא מוטמע בגשר ולא מפורסם בחנות)

דואר אלקטרוני בתוך אוצריא, בממשק בסגנון Gmail בצבעי אוצריא: קבלה, שליחה ותזמון, טיוטות ואנשי קשר — **עובד גם בלי הגשר ובלי רשת**.

- התוסף מדבר רק עם הגשר "אחת שאלתי" (`127.0.0.1`), והגשר מתחבר לשרת ה-IMAP (`bridge/MailApi.cs`, MailKit).
- הסיסמה (סיסמת אפליקציה) נשמרת במחשב בלבד, מוצפנת ב-DPAPI של משתמש Windows, ולא חוזרת לתוסף.
- במצב נעילה הדואר חסום (אלא אם `kiosk.allowMail` = true).
- תמונות מרוחקות במייל מוסתרות עד לחיצה ("פיקסל מעקב"); HTML מוצג ב-iframe בלי סקריפטים.
- קבצים מצורפים נשמרים בתיקיית ההורדות ונפתחים בסייר הקבצים.
- **לא מותקן אוטומטית מהגשר** — מתקינים ידנית: אוצריא ← התקנת תוסף מקובץ ← `mail-plugin/dist/mail.otzplugin`.
- הגשר הרגיל (מ-next) כולל את `/mail/*`; אין גרסה נפרדת.

## בלי חיבור (1.0.0)
- דואר שמור ב-IndexedDB (`js/store.js`): רשימות, תוכן הודעות וקבצים מצורפים לפי ההגדרות (כמה הודעות לשמור בתיקייה, הורדה מראש, גודל מרבי לקובץ, מקום כולל).
- טיוטות נשמרות במחשב אוטומטית. "שליחה" בלי אחת שאלתי → תור מקומי (`outbox`), שנמסר לגשר כשהוא עולה.
- הגשר מחזיק תור יוצא על הדיסק (`%LOCALAPPDATA%\OtzariaBridge\mail-outbox`) ושולח בזמן שנקבע או כשהרשת חוזרת — גם כשהתוסף סגור; ניסיונות חוזרים עם השהיה.
- אנשי קשר (`js/contacts.js`): ייבוא vCard/CSV (ייצוא Google Contacts או Outlook), ייצוא vCard, השלמה אוטומטית בכתיבה. חשבון Google של לוח השנה באוצריא חסום לתוספים (`key-google-calendar-*`), לכן הייבוא מגוגל הוא מקובץ ייצוא.

## API בגשר
`/mail/account/get`, `/mail/account/set {email,password,host,port,user}`, `/mail/account/clear`, `/mail/folders`,
`/mail/list {folder,offset,limit,q}`, `/mail/read {folder,uid}`, `/mail/flag {folder,uid,seen?,flagged?}`, `/mail/attachment {folder,uid,index}`,
`/mail/attachment-data {folder,uid,index,max}`, `/mail/send {to,cc,bcc,subject,text,attachments[{name,type,data}],inReplyTo,references}`, `/mail/outbox/add` (עם `sendAt`), `/mail/outbox/list`, `/mail/outbox/cancel {id}`.

## להמשך (לא נעשה)
התראות על דואר חדש (מופע רקע), כמה חשבונות, OAuth במקום סיסמת אפליקציה, תצוגת שרשורים, עורך עשיר (HTML).
