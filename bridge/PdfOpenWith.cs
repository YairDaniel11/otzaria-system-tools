using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// כל התוכנות ש-Windows מציע ב"פתח באמצעות" לסוג קובץ (כולל אפליקציות Store),
/// דרך SHAssocEnumHandlers — אותו מקור שתפריט Windows עצמו משתמש בו.
/// </summary>
internal static class PdfOpenWith
{
    internal sealed record Item(string Id, string Name, IAssocHandler Handler);

    /// <summary>ממשקי המעטפת של Windows צריכים חוט STA; שרת הגשר רץ על חוטי MTA</summary>
    public static T Sta<T>(Func<T> f)
    {
        T r = default!; Exception err = null;
        var t = new System.Threading.Thread(() => { try { r = f(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(System.Threading.ApartmentState.STA); t.IsBackground = true; t.Start();
        if (!t.Join(15000)) throw new TimeoutException("assoc"); if (err != null) throw err; return r;
    }

    public static List<Item> Enumerate(string ext)
    {
        var list = new List<Item>();
        try
        {
            if (SHAssocEnumHandlers(ext, ASSOC_FILTER.RECOMMENDED, out var en) != 0 || en == null) return list;
            var arr = new IAssocHandler[1];
            while (en.Next(1, arr, out uint got) == 0 && got == 1)
            {
                var h = arr[0];
                try
                {
                    h.GetUIName(out string name); h.GetName(out string path);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new("assoc:" + (path ?? name).ToLowerInvariant(), name, h));
                }
                catch { }
            }
        }
        catch (Exception ex) { Config.Log("assoc enum: " + ex.Message); }
        return list;
    }

    /// <summary>פתיחת קובץ בתוכנה שנבחרה מהרשימה</summary>
    public static bool Open(string ext, string id, string file)
    {
        // הגנה בעומק (WHY): handler שרירותי ממערכת = הרצת תוכנה לא מוכרת; בנעילה/מצב מוגבל רק ברירת המחדל דרך הרצף המאומת
        if (Kiosk.IsActive || FxPolicy.IsRestricted) return false;
        foreach (var it in Enumerate(ext))
        {
            if (it.Id != id) continue;
            SHParseDisplayName(file, IntPtr.Zero, out IntPtr pidl, 0, out _);
            if (pidl == IntPtr.Zero) return false;
            try
            {
                SHCreateItemFromIDList(pidl, typeof(IShellItem).GUID, out IShellItem item);
                item.BindToHandler(IntPtr.Zero, BHID_DataObject, typeof(IDataObject_).GUID, out object dobj);
                return it.Handler.Invoke(dobj) == 0;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        return false;
    }

    private enum ASSOC_FILTER { NONE = 0, RECOMMENDED = 1 }
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHAssocEnumHandlers(string ext, ASSOC_FILTER f, out IEnumAssocHandlers e);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumAssocHandlers { [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IAssocHandler[] rg, out uint fetched); }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        void MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string desc);
        [PreserveSig] int Invoke([MarshalAs(UnmanagedType.Interface)] object dataObject);
        void CreateInvoker(object dataObject, out object invoker);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem parent); void GetDisplayName(uint sigdn, out IntPtr name); void GetAttributes(uint mask, out uint attrs); void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("0000010e-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataObject_ { }
}
