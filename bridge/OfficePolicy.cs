using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// מדיניות Office בשירות כשהנעילה נאכפת: VBAWarnings=4 (מאקרו מושבת בלי התראה) ו-AccessVBOM=0 (אין גישה למודל האובייקטים של VBA)
/// תחת HKLM\Software\Policies\Microsoft\Office\16.0\&lt;App&gt;\Security. WHY (T1): מסמך Word/Excel שנפתח בנעילה יכול להריץ מאקרו ולצאת מהמסגרת
/// (cmd/powershell/חלונות); מדיניות HKLM נאכפת על כל המשתמשים ואינה ניתנת לשינוי בידי משתמש רגיל (ההגדרה בממשק של Office נחסמת).
/// הערכים המקוריים נשמרים במצב השירות ומוחזרים בשחרור. כתיבה מאומתת (קריאה חוזרת). לא נבדק בחי מול Office מותקן.
/// </summary>
internal static class OfficePolicy
{
    internal static readonly string[] Apps = { "Word", "Excel", "PowerPoint" };
    internal static readonly (string name, int value)[] Values = { ("VBAWarnings", 4), ("AccessVBOM", 0) };
    internal const int Absent = -1; // הערך המקורי לא היה קיים

    internal static string SubKey(string app) => @"Software\Policies\Microsoft\Office\16.0\" + app + @"\Security";

    /// <summary>גישה לרישום (ניתנת להחלפה בבדיקות). Get: null = הערך לא קיים</summary>
    internal interface IStore
    {
        int? Get(string app, string name);
        void Set(string app, string name, int value);
        void Delete(string app, string name);
    }

    internal sealed class RegistryStore : IStore
    {
        public int? Get(string app, string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(SubKey(app), false);
            return k?.GetValue(name) is int v ? v : null;
        }
        public void Set(string app, string name, int value)
        {
            using var k = Registry.LocalMachine.CreateSubKey(SubKey(app));
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        public void Delete(string app, string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(SubKey(app), true);
            k?.DeleteValue(name, false);
        }
    }

    /// <summary>
    /// מיישם (active) או מחזיר (!active) את המדיניות. original = המקורות השמורים ("App|Name" → ערך או Absent); מתעדכן במקום.
    /// מחזיר true אם original השתנה (השירות צריך לשמור). שגיאות לא נבלעות כאן: הקורא רושם ביומן; fail-closed = ממשיכים לנסות בטיק הבא
    /// </summary>
    internal static bool Apply(IStore store, bool active, JsonObject original, List<string> issues = null)
    {
        bool changed = false;
        foreach (string app in Apps)
            foreach (var (name, want) in Values)
            {
                string key = app + "|" + name;
                try
                {
                    int? cur = store.Get(app, name);
                    if (active)
                    {
                        if (!original.ContainsKey(key)) { original[key] = cur ?? Absent; changed = true; } // המקור נשמר פעם אחת, לפני הכתיבה הראשונה
                        if (cur != want) store.Set(app, name, want);
                        if (store.Get(app, name) != want) issues?.Add(key + " לא אומת");
                    }
                    else if (original.ContainsKey(key))
                    {
                        int orig = original[key]?.GetValue<int>() ?? Absent;
                        // מחזירים רק אם הערך עדיין שלנו; ערך שמנהל שינה בינתיים לא נדרס
                        if (cur == want)
                        {
                            if (orig == Absent) store.Delete(app, name); else store.Set(app, name, orig);
                        }
                        original.Remove(key);
                        changed = true;
                    }
                }
                catch (Exception ex) { issues?.Add(key + ": " + ex.Message); }
            }
        return changed;
    }
}
