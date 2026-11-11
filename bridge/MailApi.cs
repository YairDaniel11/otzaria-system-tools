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
        Server.Route("/mail/attachment-data", b => Run(c => AttachmentData(c, b)));
        // שליחה ותור יוצא (11.2026): התוסף מעביר הודעות לגשר; הגשר שולח מיד, בזמן מתוזמן, או כשתחזור הרשת — גם כשהתוסף סגור
        Server.Route("/mail/send", b => { Guard(); return QueueAdd(b, immediate: true); });
        Server.Route("/mail/outbox/add", b => { Guard(); return QueueAdd(b, immediate: false); });
        Server.Route("/mail/outbox/list", _ => QueueList());
        Server.Route("/mail/outbox/cancel", b => QueueCancel(b.Req("id")));
        _outboxTimer = new System.Threading.Timer(_ => { try { FlushOutbox().Wait(); } catch { } }, null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60));
        try { System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) _ = Task.Run(FlushOutbox); }; } catch { }
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
            name = a["name"]?.ToString() ?? "",
            smtpHost = a["smtpHost"]?.ToString() ?? "",
            smtpPort = (int?)a["smtpPort"] ?? 465,
            locked = Kiosk.IsActive && !Config.Get("kiosk.allowMail", false)
        };
    }

    private static async Task<object> SetAccount(JsonElement b)
    {
        Guard();
        string email = b.Req("email").Trim(), host = b.Req("host").Trim(), user = b.Str("user", email).Trim(), pw = b.Str("password", "");
        int port = (int)Math.Clamp(b.Long("port", 993), 1, 65535);
        string smtpHost = (b.Str("smtpHost", "") ?? "").Trim();
        int smtpPort = (int)Math.Clamp(b.Long("smtpPort", 0), 0, 65535);
        if (smtpHost.Length == 0) (smtpHost, smtpPort) = GuessSmtp(host, smtpPort);
        if (smtpPort == 0) smtpPort = 465;
        // עדכון פרטים בלי להקליד שוב סיסמה: נשארת הסיסמה השמורה
        if (pw.Length == 0)
        {
            string enc0 = Config.Get(Cfg + ".pw", "");
            if (enc0.Length == 0) throw BridgeException.BadRequest("חסרה סיסמה");
            try { pw = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(enc0))); } catch { throw BridgeException.BadRequest("יש להקליד את הסיסמה מחדש"); }
        }
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
        Config.Set(Cfg, new System.Text.Json.Nodes.JsonObject { ["email"] = email, ["host"] = host, ["port"] = port, ["user"] = user, ["smtpHost"] = smtpHost, ["smtpPort"] = smtpPort, ["name"] = b.Str("name", "") });
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
            messageId = m.MessageId ?? "",
            references = string.Join(" ", m.References.Select(r => "<" + r + ">")),
            replyTo = m.ReplyTo.Mailboxes.FirstOrDefault()?.Address ?? "",
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

    /// <summary>תוכן קובץ מצורף כ-base64 — לשמירה במטמון המקומי של התוסף (קריאה בלי חיבור). max = תקרה בבתים</summary>
    private static async Task<object> AttachmentData(ImapClient c, JsonElement b)
    {
        var f = await Folder(c, b.Str("folder", "INBOX"), FolderAccess.ReadOnly);
        var m = await f.GetMessageAsync(new UniqueId((uint)b.Long("uid", 0)));
        var a = m.Attachments.ElementAtOrDefault((int)b.Long("index", -1)) ?? throw BridgeException.NotFound("הקובץ המצורף לא נמצא");
        long max = Math.Clamp(b.Long("max", 10L << 20), 1, 40L << 20);
        using var ms = new MemoryStream();
        if (a is MimePart part) await part.Content.DecodeToAsync(ms); else if (a is MessagePart mp) await mp.Message.WriteToAsync(ms);
        if (ms.Length > max) return new { tooBig = true, size = ms.Length };
        return new { name = a is MimePart p ? (p.FileName ?? "attachment") : "message.eml", type = a.ContentType?.MimeType ?? "application/octet-stream", size = ms.Length, data = Convert.ToBase64String(ms.ToArray()) };
    }

    // ================= שליחה ותור יוצא =================
    private static System.Threading.Timer _outboxTimer;
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static string OutboxDir => Path.Combine(Config.Dir, "mail-outbox");

    private static (string, int) GuessSmtp(string imapHost, int port)
    {
        string h = imapHost.ToLowerInvariant();
        if (h.Contains("gmail")) return ("smtp.gmail.com", port == 0 ? 465 : port);
        if (h.Contains("office365") || h.Contains("outlook") || h.Contains("hotmail")) return ("smtp.office365.com", port == 0 ? 587 : port);
        if (h.Contains("yahoo")) return ("smtp.mail.yahoo.com", port == 0 ? 465 : port);
        if (h.Contains("me.com") || h.Contains("icloud")) return ("smtp.mail.me.com", port == 0 ? 587 : port);
        return (h.StartsWith("imap.") ? "smtp." + h[5..] : h, port == 0 ? 465 : port);
    }

    private static object QueueAdd(JsonElement b, bool immediate)
    {
        if (Config.Raw(Cfg) == null) throw new BridgeException("no_account", "עדיין לא הוגדר חשבון דואר", 409);
        if (b.Str("to", "").Trim().Length == 0) throw BridgeException.BadRequest("חסר נמען");
        string id = b.Str("id", "");
        if (id.Length == 0 || id.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')) id = Guid.NewGuid().ToString("N");
        var node = System.Text.Json.Nodes.JsonNode.Parse(b.GetRawText())!.AsObject();
        node["id"] = id;
        node["queuedAt"] = DateTimeOffset.Now.ToString("o");
        if (immediate) node.Remove("sendAt");
        node["attempts"] = 0;
        Directory.CreateDirectory(OutboxDir);
        File.WriteAllText(Path.Combine(OutboxDir, id + ".json"), node.ToJsonString());
        _ = Task.Run(FlushOutbox);
        return new { id, queued = true };
    }

    private static object QueueList()
    {
        var list = new List<object>();
        if (Directory.Exists(OutboxDir))
            foreach (var f in Directory.GetFiles(OutboxDir, "*.json"))
            {
                try
                {
                    var n = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                    list.Add(new { id = n["id"]?.ToString(), to = n["to"]?.ToString(), subject = n["subject"]?.ToString(), sendAt = n["sendAt"]?.ToString() ?? "", attempts = (int?)n["attempts"] ?? 0, lastError = n["lastError"]?.ToString() ?? "" });
                }
                catch { }
            }
        var sent = Config.Raw("mail.sentLog") as System.Text.Json.Nodes.JsonArray;
        return new { items = list, sent = sent?.Select(x => x?.ToString()).Take(50).ToArray() ?? Array.Empty<string>() };
    }

    private static object QueueCancel(string id)
    {
        if (id.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')) throw BridgeException.BadRequest("מזהה לא תקין");
        string p = Path.Combine(OutboxDir, id + ".json");
        bool had = File.Exists(p);
        if (had) File.Delete(p);
        return new { cancelled = had };
    }

    private static async Task FlushOutbox()
    {
        if (!Directory.Exists(OutboxDir)) return;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
        if (Kiosk.IsActive && !Config.Get("kiosk.allowMail", false)) return;
        if (!await SendGate.WaitAsync(0)) return;
        try
        {
            foreach (var f in Directory.GetFiles(OutboxDir, "*.json"))
            {
                System.Text.Json.Nodes.JsonObject n;
                try { n = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject(); } catch { continue; }
                if (DateTimeOffset.TryParse(n["sendAt"]?.ToString(), out var at) && at > DateTimeOffset.Now) continue;
                int attempts = (int?)n["attempts"] ?? 0;
                // השהיה הולכת וגדלה בין ניסיונות כושלים (עד שעה)
                if (attempts > 0 && DateTimeOffset.TryParse(n["lastTry"]?.ToString(), out var last)
                    && DateTimeOffset.Now - last < TimeSpan.FromMinutes(Math.Min(60, attempts * attempts))) continue;
                try
                {
                    await SendNow(n);
                    File.Delete(f);
                    var log = Config.Raw("mail.sentLog") as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                    log.Insert(0, n["id"]?.ToString());
                    while (log.Count > 100) log.RemoveAt(log.Count - 1);
                    Config.Set("mail.sentLog", log); Config.Save();
                }
                catch (Exception ex)
                {
                    n["attempts"] = attempts + 1; n["lastTry"] = DateTimeOffset.Now.ToString("o"); n["lastError"] = ex.Message;
                    try { File.WriteAllText(f, n.ToJsonString()); } catch { }
                    Config.Log("mail outbox: " + ex.Message);
                }
            }
        }
        finally { SendGate.Release(); }
    }

    private static InternetAddressList Parse(string s)
    {
        var l = new InternetAddressList();
        foreach (var part in (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MailboxAddress.TryParse(part, out var mb)) l.Add(mb);
            else throw BridgeException.BadRequest("כתובת לא תקינה: " + part);
        return l;
    }

    private static async Task SendNow(System.Text.Json.Nodes.JsonObject n)
    {
        var a = Config.Raw(Cfg) as System.Text.Json.Nodes.JsonObject ?? throw new InvalidOperationException("אין חשבון דואר");
        string pw = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(Config.Get(Cfg + ".pw", ""))));
        string email = a["email"]?.ToString() ?? "", user = a["user"]?.ToString() ?? email, name = a["name"]?.ToString() ?? "";
        var (sh, sp0) = GuessSmtp(a["host"]?.ToString() ?? "", 0);
        string smtpHost = a["smtpHost"]?.ToString() is { Length: > 0 } h ? h : sh;
        int smtpPort = (int?)a["smtpPort"] is int sp and > 0 ? sp : sp0;

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(name, email));
        msg.To.AddRange(Parse(n["to"]?.ToString()));
        msg.Cc.AddRange(Parse(n["cc"]?.ToString()));
        msg.Bcc.AddRange(Parse(n["bcc"]?.ToString()));
        msg.Subject = n["subject"]?.ToString() ?? "";
        if (n["inReplyTo"]?.ToString() is { Length: > 0 } irt) msg.InReplyTo = irt.Trim('<', '>');
        if (n["references"]?.ToString() is { Length: > 0 } refs)
            foreach (var r in refs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) msg.References.Add(r.Trim('<', '>'));
        var body = new BodyBuilder { TextBody = n["text"]?.ToString() ?? "" };
        if (n["html"]?.ToString() is { Length: > 0 } html) body.HtmlBody = html;
        if (n["attachments"] is System.Text.Json.Nodes.JsonArray atts)
            foreach (var x in atts.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var bytes = Convert.FromBase64String(x["data"]?.ToString() ?? "");
                body.Attachments.Add(x["name"]?.ToString() ?? "file", bytes, ContentType.Parse(x["type"]?.ToString() is { Length: > 0 } t ? t : "application/octet-stream"));
            }
        msg.Body = body.ToMessageBody();

        using (var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 60000 })
        {
            await smtp.ConnectAsync(smtpHost, smtpPort, smtpPort == 465 ? MailKit.Security.SecureSocketOptions.SslOnConnect : MailKit.Security.SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(user, pw);
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);
        }
        // Gmail שומר בעצמו ב"דואר יוצא"; בשאר הספקים — מוסיפים עותק לתיקיית Sent (לא קריטי אם נכשל)
        if (!smtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase))
        {
            await Gate.WaitAsync();
            try
            {
                var c = await Client();
                IMailFolder sent = null;
                try { sent = c.GetFolder(SpecialFolder.Sent); } catch { }
                if (sent != null) await sent.AppendAsync(msg, MessageFlags.Seen);
            }
            catch { }
            finally { Gate.Release(); }
        }
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
