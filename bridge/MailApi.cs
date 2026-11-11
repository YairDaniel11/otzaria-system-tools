using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace OtzariaBridge;

/// <summary>
/// קבלת דואר (IMAP, קריאה בלבד) עבור התוסף "דואר" (ענף mail-plugin, 11.2026).
/// הגשר מתחבר לשרת הדואר; התוסף מדבר רק עם הגשר (127.0.0.1), ולכן אינו צריך הרשאת רשת לאינטרנט.
/// הסיסמה (לרוב "סיסמת אפליקציה") נשמרת מוצפנת ב-DPAPI של המשתמש (mail.account.pw), לעולם לא חוזרת לתוסף.
/// במצב נעילה הדואר חסום, אלא אם המנהל הפעיל kiosk.allowMail.
/// </summary>
internal static class MailApi
{
    private const string Cfg = "mail.account";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ImapClient _client;

    public static void Register()
    {
        Server.Route("/mail/account/get", _ => AccountInfo());
        Server.Route("/mail/account/set", SetAccount);
        Server.Route("/mail/account/clear", _ => { Disconnect(); Config.Remove(Cfg); Config.Remove(Cfg + ".pw"); Config.Save(); return new { cleared = true }; });
        Server.Route("/mail/folders", b => Run(Folders));
        Server.Route("/mail/list", b => Run(c => List(c, b)));
        Server.Route("/mail/read", b => Run(c => Read(c, b)));
        Server.Route("/mail/flag", b => Run(c => Flag(c, b)));
        Server.Route("/mail/attachment", b => Run(c => SaveAttachment(c, b)));
    }

    private static void Guard()
    {
        if (Kiosk.IsActive && !Config.Get("kiosk.allowMail", false))
            throw BridgeException.Locked("הדואר חסום במצב נעילה (המנהל יכול לאפשר אותו בהגדרות הנעילה)");
    }

    private static object AccountInfo()
    {
        var a = Config.Raw(Cfg) as System.Text.Json.Nodes.JsonObject;
        if (a == null) return new { configured = false };
        return new
        {
            configured = true,
            email = a["email"]?.ToString() ?? "",
            host = a["host"]?.ToString() ?? "",
            port = (int?)a["port"] ?? 993,
            user = a["user"]?.ToString() ?? "",
            locked = Kiosk.IsActive && !Config.Get("kiosk.allowMail", false)
        };
    }

    private static async Task<object> SetAccount(JsonElement b)
    {
        Guard();
        string email = b.Req("email").Trim(), host = b.Req("host").Trim(), user = b.Str("user", email).Trim(), pw = b.Req("password");
        int port = (int)Math.Clamp(b.Long("port", 993), 1, 65535);
        if (user.Length == 0) user = email;
        // בדיקת חיבור לפני שמירה — כך המשתמש מקבל מיד "סיסמה שגויה" ולא חשבון שבור
        using (var test = new ImapClient())
        {
            test.Timeout = 20000;
            try
            {
                await test.ConnectAsync(host, port, port == 143 ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect);
                await test.AuthenticateAsync(user, pw);
                await test.DisconnectAsync(true);
            }
            catch (MailKit.Security.AuthenticationException)
            {
                throw new BridgeException("auth_failed", "שם המשתמש או הסיסמה שגויים. ב-Gmail וב-Outlook יש להשתמש ב\"סיסמת אפליקציה\" ולא בסיסמה הרגילה.", 401);
            }
            catch (Exception ex) when (ex is not BridgeException)
            {
                throw new BridgeException("connect_failed", "לא ניתן להתחבר לשרת הדואר (" + host + ":" + port + "): " + ex.Message, 502);
            }
        }
        Disconnect();
        Config.Set(Cfg, new System.Text.Json.Nodes.JsonObject { ["email"] = email, ["host"] = host, ["port"] = port, ["user"] = user });
        Config.Set(Cfg + ".pw", Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(pw))));
        Config.Save();
        return AccountInfo();
    }

    private static async Task<object> Run(Func<ImapClient, Task<object>> f)
    {
        Guard();
        await Gate.WaitAsync();
        try
        {
            var c = await Client();
            try { return await f(c); }
            catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ImapProtocolException)
            {
                Disconnect();                       // חיבור שנפל: ניסיון אחד נוסף בחיבור חדש
                return await f(await Client());
            }
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            throw new BridgeException("mail_error", "שגיאת דואר: " + ex.Message, 502);
        }
        finally { Gate.Release(); }
    }

    private static async Task<ImapClient> Client()
    {
        if (_client is { IsConnected: true, IsAuthenticated: true }) return _client;
        Disconnect();
        var a = Config.Raw(Cfg) as System.Text.Json.Nodes.JsonObject;
        string enc = Config.Get(Cfg + ".pw", "");
        if (a == null || enc.Length == 0) throw new BridgeException("no_account", "עדיין לא הוגדר חשבון דואר", 409);
        string host = a["host"]?.ToString() ?? "", user = a["user"]?.ToString() ?? "";
        int port = (int?)a["port"] ?? 993;
        string pw;
        try { pw = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(enc))); }
        catch { throw new BridgeException("no_account", "לא ניתן לפענח את סיסמת הדואר (משתמש Windows אחר?). יש להגדיר את החשבון מחדש", 409); }
        var c = new ImapClient { Timeout = 30000 };
        try
        {
            await c.ConnectAsync(host, port, port == 143 ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect);
            await c.AuthenticateAsync(user, pw);
        }
        catch (MailKit.Security.AuthenticationException)
        {
            c.Dispose();
            throw new BridgeException("auth_failed", "השרת דחה את הסיסמה. ייתכן שסיסמת האפליקציה בוטלה — יש להגדיר את החשבון מחדש", 401);
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            c.Dispose();
            throw new BridgeException("offline", "אין חיבור לשרת הדואר: " + ex.Message, 503);
        }
        _client = c;
        return c;
    }

    private static void Disconnect()
    {
        try { _client?.Disconnect(true); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    private static async Task<IMailFolder> Folder(ImapClient c, string name, FolderAccess access)
    {
        IMailFolder f = string.IsNullOrEmpty(name) || name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? c.Inbox : await c.GetFolderAsync(name);
        if (!f.IsOpen || (access == FolderAccess.ReadWrite && f.Access != FolderAccess.ReadWrite)) await f.OpenAsync(access);
        return f;
    }

    private static string Role(IMailFolder f)
    {
        var a = f.Attributes;
        if ((a & FolderAttributes.Inbox) != 0 || f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return "inbox";
        if ((a & FolderAttributes.Sent) != 0) return "sent";
        if ((a & FolderAttributes.Drafts) != 0) return "drafts";
        if ((a & FolderAttributes.Junk) != 0) return "junk";
        if ((a & FolderAttributes.Trash) != 0) return "trash";
        if ((a & FolderAttributes.Flagged) != 0) return "starred";
        if ((a & FolderAttributes.All) != 0) return "all";
        if ((a & FolderAttributes.Important) != 0) return "important";
        return "";
    }

    private static async Task<object> Folders(ImapClient c)
    {
        var list = new List<object>();
        var ns = c.PersonalNamespaces.Count > 0 ? c.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var all = await c.GetFoldersAsync(ns, StatusItems.Unread);
        if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
        {
            await c.Inbox.StatusAsync(StatusItems.Unread);
            all = new[] { c.Inbox }.Concat(all).ToList();
        }
        foreach (var f in all)
        {
            if ((f.Attributes & (FolderAttributes.NonExistent | FolderAttributes.NoSelect)) != 0) continue;
            list.Add(new { id = f.FullName, name = f.Name, role = Role(f), unread = f.Unread });
        }
        return new { folders = list };
    }

    private static string Addr(InternetAddressList l) =>
        string.Join(", ", l.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private static async Task<object> List(ImapClient c, JsonElement b)
    {
        var f = await Folder(c, b.Str("folder", "INBOX"), FolderAccess.ReadOnly);
        int limit = (int)Math.Clamp(b.Long("limit", 50), 1, 200);
        int offset = (int)Math.Max(0, b.Long("offset", 0));
        string q = (b.Str("q", "") ?? "").Trim();
        IList<UniqueId> uids;
        if (q.Length > 0)
            uids = await f.SearchAsync(SearchQuery.SubjectContains(q).Or(SearchQuery.FromContains(q)).Or(SearchQuery.BodyContains(q)));
        else
            uids = await f.SearchAsync(SearchQuery.All);
        var ordered = uids.OrderByDescending(u => u.Id).ToList();
        var page = ordered.Skip(offset).Take(limit).ToList();
        var items = new List<object>();
        if (page.Count > 0)
        {
            var sums = await f.FetchAsync(page, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                                               | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText);
            foreach (var s in sums.OrderByDescending(x => x.UniqueId.Id))
            {
                var e = s.Envelope;
                items.Add(new
                {
                    uid = s.UniqueId.Id,
                    from = e?.From != null ? Addr(e.From) : "",
                    subject = e?.Subject ?? "",
                    date = e?.Date?.ToString("o") ?? "",
                    seen = s.Flags.HasValue && s.Flags.Value.HasFlag(MessageFlags.Seen),
                    flagged = s.Flags.HasValue && s.Flags.Value.HasFlag(MessageFlags.Flagged),
                    attach = s.Attachments?.Any() == true,
                    preview = (s.PreviewText ?? "").Trim()
                });
            }
        }
        return new { total = ordered.Count, offset, items };
    }

    private static async Task<object> Read(ImapClient c, JsonElement b)
    {
        var f = await Folder(c, b.Str("folder", "INBOX"), FolderAccess.ReadWrite);
        var uid = new UniqueId((uint)b.Long("uid", 0));
        var m = await f.GetMessageAsync(uid);
        try { await f.AddFlagsAsync(uid, MessageFlags.Seen, true); } catch { }
        int i = 0;
        var att = m.Attachments.Select(a => new
        {
            index = i++,
            name = a is MimePart p ? (p.FileName ?? "קובץ") : (a.ContentType?.Name ?? "הודעה מצורפת"),
            type = a.ContentType?.MimeType ?? ""
        }).ToList();
        return new
        {
            uid = uid.Id,
            from = Addr(m.From),
            fromAddress = m.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            to = Addr(m.To),
            cc = Addr(m.Cc),
            subject = m.Subject ?? "",
            date = m.Date.ToString("o"),
            html = m.HtmlBody ?? "",
            text = m.TextBody ?? "",
            attachments = att
        };
    }

    private static async Task<object> Flag(ImapClient c, JsonElement b)
    {
        var f = await Folder(c, b.Str("folder", "INBOX"), FolderAccess.ReadWrite);
        var uid = new UniqueId((uint)b.Long("uid", 0));
        if (b.Has("seen")) { if (b.Bool("seen")) await f.AddFlagsAsync(uid, MessageFlags.Seen, true); else await f.RemoveFlagsAsync(uid, MessageFlags.Seen, true); }
        if (b.Has("flagged")) { if (b.Bool("flagged")) await f.AddFlagsAsync(uid, MessageFlags.Flagged, true); else await f.RemoveFlagsAsync(uid, MessageFlags.Flagged, true); }
        return new { ok = true };
    }

    /// <summary>שומר קובץ מצורף בתיקיית ההורדות (שם בטוח, בלי דריסה) ומחזיר את הנתיב — הסייר/הצופה פותחים אותו משם</summary>
    private static async Task<object> SaveAttachment(ImapClient c, JsonElement b)
    {
        var f = await Folder(c, b.Str("folder", "INBOX"), FolderAccess.ReadOnly);
        var m = await f.GetMessageAsync(new UniqueId((uint)b.Long("uid", 0)));
        int idx = (int)b.Long("index", -1);
        var a = m.Attachments.ElementAtOrDefault(idx) ?? throw BridgeException.NotFound("הקובץ המצורף לא נמצא");
        string name = a is MimePart p ? (p.FileName ?? "attachment") : "message.eml";
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        for (int n = 1; File.Exists(path); n++)
            path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));
        await using (var fs = File.Create(path))
        {
            if (a is MimePart part) await part.Content.DecodeToAsync(fs);
            else if (a is MessagePart mp) await mp.Message.WriteToAsync(fs);
        }
        return new { path };
    }

    // ---- DPAPI (CurrentUser), כמו ב-PipeMac ----
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int cb; public IntPtr pb; }
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob i, string d, IntPtr e, IntPtr r, IntPtr p, int f, ref Blob o);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob i, IntPtr d, IntPtr e, IntPtr r, IntPtr p, int f, ref Blob o);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private static byte[] Dp(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var i = new Blob { cb = data.Length, pb = h.AddrOfPinnedObject() };
            var o = new Blob();
            bool ok = protect ? CryptProtectData(ref i, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o)
                              : CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var r = new byte[o.cb]; Marshal.Copy(o.pb, r, 0, o.cb); LocalFree(o.pb);
            return r;
        }
        finally { h.Free(); }
    }
    private static byte[] Protect(byte[] d) => Dp(d, true);
    private static byte[] Unprotect(byte[] d) => Dp(d, false);
}
