using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OtzariaBridge;

/// <summary>
/// נקודות הקצה /oneocr/* — זיהוי טקסט ישיר במנוע ה-OCR של כלי החיתוך (oneocr.dll), בלי לפתוח את כלי החיתוך.
/// הגשר לא כולל ולא מפיץ את קבצי המנוע: הם מועתקים (פעם אחת) מההתקנה של כלי החיתוך שכבר קיימת
/// במחשב של המשתמש אל %LOCALAPPDATA%\OtzariaBridge\oneocr.
/// אפשר גם להניח שם ידנית את שלושת הקבצים (oneocr.dll, oneocr.onemodel, onnxruntime.dll).
/// ה-API של המנוע אינו מתועד ואינו נתמך על ידי Microsoft, ולכן עדכון של כלי החיתוך עלול לשבור אותו;
/// הלקוח חוזר אז אוטומטית לשיטת כלי החיתוך הישנה (/snip/ocr).
/// </summary>
public static class OneOcrApi
{
    private const string Pfn = "Microsoft.ScreenSketch_8wekyb3d8bbwe";
    private static readonly string[] Files = { "onnxruntime.dll", "oneocr.dll", "oneocr.onemodel" };
    private const string ModelKey = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    private const int MinSide = 50, MaxSide = 10000, MaxLines = 1000;
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(30);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IntPtr _lib;
    private static long _init, _pipeline, _opts;
    private static System.Threading.Timer _idle;

    public static void Register()
    {
        Server.Route("/oneocr/status", _ => Status());
        Server.Route("/oneocr/recognize", Recognize);
    }

    // ── מבני המנוע ────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct ImageStruct { public int Type, Width, Height, Reserved; public long Step; public IntPtr Data; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Out(out long a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_SetDelay(long a, byte b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_CreatePipeline(IntPtr model, IntPtr key, long init, out long pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_SetMax(long a, long b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Run(long pipe, ref ImageStruct img, long opts, out long res);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Float(long a, out float f);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Count(long a, out long n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Item(long a, long i, out long h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long D_Ptr(long a, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_Release(long a);

    private static D_Out CreateInitOptions, CreateProcessOptions;
    private static D_SetDelay SetDelayLoad;
    private static D_CreatePipeline CreatePipeline;
    private static D_SetMax SetMaxLines;
    private static D_Run RunPipeline;
    private static D_Float GetAngle, GetWordConfidence;
    private static D_Count GetLineCount, GetLineWordCount;
    private static D_Item GetLine, GetWord;
    private static D_Ptr GetLineContent, GetLineBox, GetWordContent, GetWordBox;
    private static D_Release ReleaseResult, ReleaseInit, ReleasePipeline, ReleaseOpts;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private static bool _boosted, _boostOk;
    /// <summary>הגשר הוא אפליקציית מגש בלי חלון, ו-Windows 11 עלול להכניס אותה ל"מצב יעילות" (EcoQoS) ולהאט חישוב כבד פי כמה — מכבים את זה לתהליך.</summary>
    private static void Boost()
    {
        if (_boosted) return;
        _boosted = true;
        try
        {
            var s = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = 0 }; // EXECUTION_SPEED: ביצועים מלאים
            _boostOk = SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, Marshal.SizeOf<PowerThrottling>());
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint len);

    // ── איתור והעתקת הקבצים ────────────────────────────────────────────
    private static string DataDir => Path.Combine(Config.Dir, "oneocr");

    private static string FindSourceDir()
    {
        var roots = new List<string>();
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser("", Pfn)) { try { roots.Add(p.InstalledPath); } catch { } }
        }
        catch { /* ננסה סריקה */ }
        try
        {
            string wa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            roots.AddRange(Directory.GetDirectories(wa, "Microsoft.ScreenSketch_*_x64__8wekyb3d8bbwe"));
        }
        catch { /* אין הרשאת רשימה */ }
        foreach (var r in roots.Distinct().OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string sub = Path.Combine(r, "SnippingTool");
            if (Files.All(f => File.Exists(Path.Combine(sub, f)))) return sub;
        }
        return null;
    }

    private static string Prepare()
    {
        string dir = DataDir;
        string stamp = Path.Combine(dir, "source.txt");
        bool have = Files.All(f => File.Exists(Path.Combine(dir, f)));
        if (_lib != IntPtr.Zero && have) return dir; // כבר טעון
        string src = FindSourceDir();
        if (src == null)
        {
            if (have) return dir;
            throw BridgeException.Unsupported("לא נמצאו קבצי המנוע של כלי החיתוך (Snipping Tool). ודאו שהוא מותקן ב-Windows 11, או הניחו את הקבצים ב-" + dir);
        }
        string last = File.Exists(stamp) ? File.ReadAllText(stamp) : "";
        if (!have || !string.Equals(last, src, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var f in Files) File.Copy(Path.Combine(src, f), Path.Combine(dir, f), true);
                File.WriteAllText(stamp, src);
            }
            catch (Exception ex)
            {
                throw BridgeException.Unsupported("העתקת קבצי המנוע נכשלה (" + ex.Message + "). נסו להריץ את הגשר כמנהל, או להעתיק ידנית את הקבצים אל " + dir);
            }
        }
        return dir;
    }

    private static T Fn<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    private static void Check(long code, string what)
    {
        if (code != 0) throw new BridgeException("ocr_failed", "מנוע הזיהוי נכשל: " + what + " (קוד " + code + ")", 502);
    }

    private static void EnsureEngine()
    {
        if (_pipeline != 0) return;
        string dir = Prepare();
        try
        {
            if (_lib == IntPtr.Zero)
            {
                NativeLibrary.Load(Path.Combine(dir, "onnxruntime.dll")); // נטען קודם כדי ש-oneocr.dll ימצא אותו
                var lib = NativeLibrary.Load(Path.Combine(dir, "oneocr.dll"));
                _lib = lib;
                CreateInitOptions = Fn<D_Out>("CreateOcrInitOptions");
                SetDelayLoad = Fn<D_SetDelay>("OcrInitOptionsSetUseModelDelayLoad");
                CreatePipeline = Fn<D_CreatePipeline>("CreateOcrPipeline");
                CreateProcessOptions = Fn<D_Out>("CreateOcrProcessOptions");
                SetMaxLines = Fn<D_SetMax>("OcrProcessOptionsSetMaxRecognitionLineCount");
                RunPipeline = Fn<D_Run>("RunOcrPipeline");
                GetAngle = Fn<D_Float>("GetImageAngle");
                GetLineCount = Fn<D_Count>("GetOcrLineCount");
                GetLine = Fn<D_Item>("GetOcrLine");
                GetLineContent = Fn<D_Ptr>("GetOcrLineContent");
                GetLineBox = Fn<D_Ptr>("GetOcrLineBoundingBox");
                GetLineWordCount = Fn<D_Count>("GetOcrLineWordCount");
                GetWord = Fn<D_Item>("GetOcrWord");
                GetWordContent = Fn<D_Ptr>("GetOcrWordContent");
                GetWordBox = Fn<D_Ptr>("GetOcrWordBoundingBox");
                GetWordConfidence = Fn<D_Float>("GetOcrWordConfidence");
                ReleaseResult = Fn<D_Release>("ReleaseOcrResult");
                ReleaseInit = Fn<D_Release>("ReleaseOcrInitOptions");
                ReleasePipeline = Fn<D_Release>("ReleaseOcrPipeline");
                ReleaseOpts = Fn<D_Release>("ReleaseOcrProcessOptions");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is BadImageFormatException || ex is EntryPointNotFoundException)
        {
            throw BridgeException.Unsupported("טעינת מנוע הזיהוי נכשלה (" + ex.Message + "). ייתכן שגרסת כלי החיתוך אינה תואמת.");
        }

        Check(CreateInitOptions(out _init), "CreateOcrInitOptions");
        Check(SetDelayLoad(_init, 0), "SetUseModelDelayLoad");

        // נתיב קצר (ASCII) — המנוע מקבל מחרוזת ANSI, ושם משתמש בעברית היה שובר אותה
        string model = Path.Combine(dir, "oneocr.onemodel");
        var sb = new StringBuilder(520);
        if (GetShortPathNameW(model, sb, (uint)sb.Capacity) > 0) model = sb.ToString();
        IntPtr pModel = Marshal.StringToHGlobalAnsi(model), pKey = Marshal.StringToHGlobalAnsi(ModelKey);
        try { Check(CreatePipeline(pModel, pKey, _init, out _pipeline), "CreateOcrPipeline"); }
        finally { Marshal.FreeHGlobal(pModel); Marshal.FreeHGlobal(pKey); }

        Check(CreateProcessOptions(out _opts), "CreateOcrProcessOptions");
        Check(SetMaxLines(_opts, MaxLines), "SetMaxRecognitionLineCount");
    }

    private static void Unload()
    {
        if (!Gate.Wait(0)) return;
        try
        {
            if (_pipeline != 0) { ReleaseOpts(_opts); ReleasePipeline(_pipeline); ReleaseInit(_init); _opts = _pipeline = _init = 0; }
        }
        catch { }
        finally { Gate.Release(); }
    }

    private static async Task Warm()
    {
        if (!await Gate.WaitAsync(0)) return;
        try { EnsureEngine(); } catch { }
        finally { Gate.Release(); }
        ArmIdle();
    }

    private static void ArmIdle()
    {
        _idle?.Dispose();
        _idle = new System.Threading.Timer(_ => Unload(), null, IdleUnload, Timeout.InfiniteTimeSpan);
    }

    // ── נקודות קצה ─────────────────────────────────────────────────────
    private static object Status()
    {
        string err = null; bool ok = false;
        try { Prepare(); ok = true; } catch (Exception ex) { err = ex.Message; }
        if (ok && _pipeline == 0) _ = Task.Run(Warm); // טוען את המנוע ברקע, כדי שהעמוד הראשון לא ימתין לטעינה
        return new { available = ok, loaded = _pipeline != 0, dataDir = DataDir, error = err, engine = "oneocr" };
    }

    private static byte[] DecodeImage(string s)
    {
        int comma = s.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) s = s.Substring(comma + 7);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw BridgeException.BadRequest("השדה image אינו base64 תקין"); }
    }

    /// <summary>גוף: { image: base64 או data URL (PNG/JPEG/BMP) } →
    /// { text, angle, width, height, engine, lines: [{ text, box:[x1,y1..x4,y4], x, y, w, h, words: [{ text, box, x, y, w, h, conf }] }] }
    /// קואורדינטות בפיקסלים של התמונה; box = ארבע פינות (ימין-שמאל/עליון-תחתון לפי הסיבוב), x/y/w/h = מלבן חוסם.</summary>
    private static async Task<object> Recognize(JsonElement b)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] bytes = DecodeImage(b.Req("image"));
        if (bytes.Length == 0) throw BridgeException.BadRequest("התמונה ריקה");

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        BitmapDecoder decoder;
        try { decoder = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception) { throw BridgeException.BadRequest("פורמט התמונה אינו נתמך"); }
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        if (w > MaxSide || h > MaxSide)
            throw BridgeException.BadRequest("התמונה גדולה מדי (מקסימום " + MaxSide + " פיקסלים בכל צד)");

        var pd = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] px = pd.DetachPixelData();
        long decodeMs = sw.ElapsedMilliseconds;

        await Gate.WaitAsync();
        try { return await Task.Run(() => RunNative(px, w, h, decodeMs)); }
        finally { Gate.Release(); ArmIdle(); }
    }

    private static object RunNative(byte[] px, int w, int h, long decodeMs)
    {
        Boost();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureEngine();
        long loadMs = sw.ElapsedMilliseconds;

        // המנוע דורש לפחות 50 פיקסלים בכל צד: מרפדים בלבן (הקואורדינטות נשארות כמו במקור)
        int pw = Math.Max(w, MinSide), ph = Math.Max(h, MinSide);
        if (pw != w || ph != h)
        {
            var padded = new byte[pw * ph * 4];
            Array.Fill(padded, (byte)255);
            for (int y = 0; y < h; y++) System.Buffer.BlockCopy(px, y * w * 4, padded, y * pw * 4, w * 4);
            px = padded;
        }

        var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var img = new ImageStruct { Type = 3, Width = pw, Height = ph, Step = (long)pw * 4, Data = pin.AddrOfPinnedObject() };
            sw.Restart();
            Check(RunPipeline(_pipeline, ref img, _opts, out long res), "RunOcrPipeline");
            long runMs = sw.ElapsedMilliseconds;
            try { return Parse(res, w, h, new { decode = decodeMs, load = loadMs, ocr = runMs, ort = OrtMb(), boost = _boostOk }); }
            finally { ReleaseResult(res); }
        }
        finally { pin.Free(); }
    }

    private static double OrtMb()
    {
        try { return Math.Round(new FileInfo(Path.Combine(DataDir, "onnxruntime.dll")).Length / 1048576.0, 1); }
        catch { return 0; }
    }

    private static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(p) ?? "");

    private static float[] Box(D_Ptr fn, long handle)
    {
        if (fn(handle, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        var f = new float[8];
        Marshal.Copy(p, f, 0, 8);
        return f;
    }

    private static object Rect(float[] b, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (b == null) return null;
        double x0 = Math.Min(Math.Min(b[0], b[2]), Math.Min(b[4], b[6]));
        double x1 = Math.Max(Math.Max(b[0], b[2]), Math.Max(b[4], b[6]));
        double y0 = Math.Min(Math.Min(b[1], b[3]), Math.Min(b[5], b[7]));
        double y1 = Math.Max(Math.Max(b[1], b[3]), Math.Max(b[5], b[7]));
        x = Math.Round(x0, 1); y = Math.Round(y0, 1); w = Math.Round(x1 - x0, 1); h = Math.Round(y1 - y0, 1);
        return null;
    }

    private static object Parse(long res, int width, int height, object ms)
    {
        Check(GetLineCount(res, out long n), "GetOcrLineCount");
        var lines = new List<object>();
        var all = new StringBuilder();
        for (long i = 0; i < n; i++)
        {
            if (GetLine(res, i, out long line) != 0 || line == 0) continue;
            string text = GetLineContent(line, out IntPtr cp) == 0 ? Utf8(cp) : "";
            var lbox = Box(GetLineBox, line);
            Rect(lbox, out double lx, out double ly, out double lw, out double lh);

            var words = new List<object>();
            if (GetLineWordCount(line, out long wc) == 0)
            {
                for (long k = 0; k < wc; k++)
                {
                    if (GetWord(line, k, out long word) != 0 || word == 0) continue;
                    string wt = GetWordContent(word, out IntPtr wp) == 0 ? Utf8(wp) : "";
                    var wbox = Box(GetWordBox, word);
                    Rect(wbox, out double x, out double y, out double ww, out double wh);
                    float conf = GetWordConfidence(word, out float c) == 0 ? c : 0f;
                    words.Add(new { text = wt, box = wbox, x, y, w = ww, h = wh, conf });
                }
            }
            if (text.Length > 0) { if (all.Length > 0) all.Append('\n'); all.Append(text); }
            lines.Add(new { text, box = lbox, x = lx, y = ly, w = lw, h = lh, words });
        }
        float? angle = GetAngle(res, out float a) == 0 ? a : null;
        return new { text = all.ToString(), angle, width, height, engine = "oneocr", ms, lines };
    }
}
