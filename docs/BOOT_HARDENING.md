# הקשחת אתחול ומערכת

## מה נאכף אוטומטית (השירות, בזמן נעילה פעילה)

מימוש: `bridge\BootHardening.cs`, נקרא מ-`HardLock.Tick` (שורה מסומנת `BOOTHARDENING`).

| פעולה | מטרה |
|---|---|
| רישום השירות ב-`HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal` ו-`Network` | הנעילה ממשיכה לעבוד גם ב-Safe Mode |
| `reagentc /disable` | אין WinRE (Command Prompt/איפוס מתפריט התאוששות) |
| `bcdedit recoveryenabled No` | אין כניסה אוטומטית ל-WinRE אחרי כשלי אתחול |
| `bcdedit bootstatuspolicy IgnoreAllFailures` | אין מסך "תיקון אוטומטי" |
| `bcdedit bootmenupolicy Standard` | ללא תפריט F8 הישן (דרכו נכנסים ל-Safe Mode) |

כל שינוי נרשם (הערך המקורי) ב-`%ProgramData%\OtzariaBridge\boothardening.json`, ובשחרור הנעילה מוחזר רק מה שאנחנו שינינו.
מה שהיה כבר בערך הרצוי לפני כן לא נרשם ולא מוחזר. האכיפה נבדקת מחדש אחת לכ-5 דקות. כשל בשלב מדווח כ"הקשחת אתחול לא הושלמה".

## מה מדווח ב-`/bios/status` (שדה `boot`)

BitLocker (כבוי / TPM בלבד / עם PIN), TPM (קיים/פעיל), Secure Boot, ומצב האכיפה האחרון. מצב שלא ניתן לקרוא מסומן "לא ידוע", לא "תקין".

## מה דורש פעולה ידנית

- **BitLocker עם PIN לפני אתחול.** אי אפשר לקבוע PIN בלי אינטראקציה. הפעלה: `manage-bde -protectors -add C: -TPMAndPIN` (או Group Policy "Require additional authentication at startup"). בלי PIN, תוקף עם גישה פיזית יכול לעקוף את הנעילה ולקרוא את הדיסק מחוץ ל-Windows.
- **סיסמת BIOS.** ב-Dell/Asus/יצרנים אחרים ידנית. ב-Lenovo/HP אפשר דרך `/bios/harden`, ואז מומלץ לאמת ב-BIOS.
- **Secure Boot ו-TPM** שכבויים: הפעלה בתפריט ה-BIOS.
- **Dell:** אתחול USB וסדר/תפריט אתחול ידניים. UEFI Network Stack ו-Legacy ROM דרך cctk (לא נבדק על Dell אמיתי).

## סדר הפעולות ב-`/bios/harden`

קודם כל החסימות (`blockUsbBoot`, `blockNetworkBoot`, `blockLegacy`, `lockBootOrder`, `lockBootMenu`; או `all`), ורק בסוף הסיסמה (`setPassword`, מינימום 12 תווים).
סיבה: אחרי שנקבעה סיסמה, כתיבת הגדרות בלי להזין אותה נכשלת ב-Access Denied. אם כבר מוגדרת סיסמה, מזינים אותה ב-`currentBiosPassword`.
התשובה כוללת `complete` ו-`summary`; כשל בשלב מופיע כ"הקשחה לא הושלמה" עם רשימת השלבים שנכשלו.

## המלצת פריסה

1. BitLocker + PIN, Secure Boot ו-TPM פעילים לפני ההתקנה.
2. הקשחת BIOS: `all` + סיסמה (אחסנו אותה בנפרד, אין דרך לשחזר סיסמת BIOS שאבדה).
3. התקנת הנעילה המוקשחת (שירות), והפעלת נעילה. ב-`/bios/status` לוודא שאין ממצאים אדומים.
4. בדיקת חירום: אתחול ל-Safe Mode ווידוא שקוד השחזור פותח את הנעילה.

## סיכונים

- ב-Safe Mode השירות רץ ואוכף: קוד השחזור/`--unlock` הם מסלול החירום. בלי השירות (למשל הוסר) הרישום ב-SafeBoot נשאר עד ניקוי (`reg delete` על שני המפתחות).
- `recoveryenabled No` + WinRE כבוי: אם Windows נכשל באתחול, אין תיקון אוטומטי; נדרש מדיה התקנה (שחסומה ב-BIOS אם הוקשח). לכן יש לוודא גיבוי/גישה לסיסמת BIOS לפני הפריסה.
