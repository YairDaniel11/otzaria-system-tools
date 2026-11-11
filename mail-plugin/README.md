# דואר — תוסף לאוצריא (ענף `mail-plugin` בלבד)

קבלת דואר אלקטרוני בתוך אוצריא, בממשק בסגנון Gmail בצבעי אוצריא. **קריאה בלבד** (בלי שליחה).

- התוסף מדבר רק עם הגשר "אחת שאלתי" (`127.0.0.1`), והגשר מתחבר לשרת ה-IMAP (`bridge/MailApi.cs`, MailKit).
- הסיסמה (סיסמת אפליקציה) נשמרת במחשב בלבד, מוצפנת ב-DPAPI של משתמש Windows, ולא חוזרת לתוסף.
- במצב נעילה הדואר חסום (אלא אם `kiosk.allowMail` = true).
- תמונות מרוחקות במייל מוסתרות עד לחיצה ("פיקסל מעקב"); HTML מוצג ב-iframe בלי סקריפטים.
- קבצים מצורפים נשמרים בתיקיית ההורדות ונפתחים בסייר הקבצים.
- **לא מותקן אוטומטית מהגשר** — מתקינים ידנית: אוצריא ← התקנת תוסף מקובץ ← `mail-plugin/dist/mail.otzplugin`.
- דורש גשר שנבנה מהענף הזה (יש בו `/mail/*`); גשר רגיל יציג "הגרסה המותקנת אינה תומכת בדואר".

## API בגשר
`/mail/account/get`, `/mail/account/set {email,password,host,port,user}`, `/mail/account/clear`, `/mail/folders`,
`/mail/list {folder,offset,limit,q}`, `/mail/read {folder,uid}`, `/mail/flag {folder,uid,seen?,flagged?}`, `/mail/attachment {folder,uid,index}`.

## להמשך (לא נעשה)
שליחה/תשובה (SMTP), התראות על דואר חדש (מופע רקע), כמה חשבונות, OAuth במקום סיסמת אפליקציה, תצוגת שרשורים.
