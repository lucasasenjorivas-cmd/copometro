using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public class Player
{
    public string id, name, emoji, ig, photo;
    public int photoV;
    public Dictionary<string, int> drinks = new Dictionary<string, int>();
    public List<long> times = new List<long>();
    public long joined;
}

public class Reto
{
    public string id, from, to, kind, status;
    public int range, fromPick, toPick;
    public bool match, paid;
    public long created;
}

public class Party
{
    public string code, name, host, music;
    public long created, finishedAt;
    public bool finished;
    public int v;
    public List<Player> players = new List<Player>();
    public List<Reto> retos = new List<Reto>();
}

public class ApiError : Exception
{
    public int Status;
    public ApiError(int status, string msg) : base(msg) { Status = status; }
}

public static class Program
{
    static readonly JavaScriptSerializer J = new JavaScriptSerializer();
    static readonly object L = new object();
    static readonly Random Rnd = new Random();
    static Dictionary<string, Party> Parties = new Dictionary<string, Party>();
    static string Dir, DataFile;
    static readonly string[] Kinds = { "cana", "copa", "chupito", "vino", "agua", "vomito" };
    static bool Alc(string k) { return k == "cana" || k == "copa" || k == "chupito" || k == "vino"; }

    // Persistencia opcional en Upstash Redis (REST). Sin variables de entorno solo se guarda en data.json
    static string RUrl, RTok, PersistErr = "";
    static int PersistOk = 0;
    static readonly HashSet<string> Dirty = new HashSet<string>();

    static string Redis(string path, string body)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(RUrl.TrimEnd('/') + (path == "" ? "" : "/" + path));
        req.Method = "POST";
        req.Headers["Authorization"] = "Bearer " + RTok;
        req.ContentType = "application/json";
        req.Timeout = 15000;
        byte[] b = Encoding.UTF8.GetBytes(body);
        req.ContentLength = b.Length;
        using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    static void LoadFromRedis()
    {
        string cursor = "0";
        int loaded = 0;
        do
        {
            string res = Redis("", J.Serialize(new object[] { "SCAN", cursor, "MATCH", "copometro:p:*", "COUNT", "200" }));
            Dictionary<string, object> d = (Dictionary<string, object>)J.DeserializeObject(res);
            object[] arr = (object[])d["result"];
            cursor = Convert.ToString(arr[0]);
            object[] keys = (object[])arr[1];
            if (keys.Length > 0)
            {
                List<object> cmds = new List<object>();
                foreach (object k in keys) cmds.Add(new object[] { "GET", Convert.ToString(k) });
                string r2 = Redis("pipeline", J.Serialize(cmds));
                object[] items = (object[])J.DeserializeObject(r2);
                foreach (object it in items)
                {
                    Dictionary<string, object> di = (Dictionary<string, object>)it;
                    if (di.ContainsKey("result") && di["result"] != null)
                    {
                        Party p = J.Deserialize<Party>(Convert.ToString(di["result"]));
                        if (p != null && p.code != null) { Parties[p.code] = p; loaded++; }
                    }
                }
            }
        } while (cursor != "0");
        Console.WriteLine("Redis: " + loaded + " fiestas cargadas");
    }

    static void Flusher()
    {
        while (true)
        {
            Thread.Sleep(1500);
            List<string> codes = null;
            lock (L) { if (Dirty.Count > 0) { codes = new List<string>(Dirty); Dirty.Clear(); } }
            if (codes == null) continue;
            try
            {
                List<object> cmds = new List<object>();
                lock (L)
                {
                    foreach (string c in codes)
                    {
                        Party p;
                        if (Parties.TryGetValue(c, out p)) cmds.Add(new object[] { "SET", "copometro:p:" + c, J.Serialize(p), "EX", "2592000" });
                    }
                }
                if (cmds.Count > 0) Redis("pipeline", J.Serialize(cmds));
                PersistOk++; PersistErr = "";
            }
            catch (Exception ex)
            {
                PersistErr = ex.Message;
                Console.WriteLine("Redis ERROR: " + ex.Message);
                lock (L) { foreach (string c in codes) Dirty.Add(c); }
            }
        }
    }

    public static void Main(string[] args)
    {
        int port = 8080;
        string envPort = Environment.GetEnvironmentVariable("PORT");
        if (!string.IsNullOrEmpty(envPort)) int.TryParse(envPort, out port);
        if (args.Length > 0) int.TryParse(args[0], out port);
        J.MaxJsonLength = 20 * 1024 * 1024;
        Dir = AppDomain.CurrentDomain.BaseDirectory;
        DataFile = Path.Combine(Dir, "data.json");
        try
        {
            if (File.Exists(DataFile))
                Parties = J.Deserialize<Dictionary<string, Party>>(File.ReadAllText(DataFile, Encoding.UTF8));
        }
        catch { Parties = new Dictionary<string, Party>(); }

        RUrl = Environment.GetEnvironmentVariable("UPSTASH_REDIS_REST_URL");
        RTok = Environment.GetEnvironmentVariable("UPSTASH_REDIS_REST_TOKEN");
        if (string.IsNullOrEmpty(RUrl) || string.IsNullOrEmpty(RTok)) { RUrl = null; Console.WriteLine("Sin base de datos externa: solo data.json"); }
        else
        {
            try { LoadFromRedis(); }
            catch (Exception ex) { PersistErr = ex.Message; Console.WriteLine("Redis ERROR al cargar: " + ex.Message); }
            Thread ft = new Thread(Flusher);
            ft.IsBackground = true;
            ft.Start();
        }

        TcpListener l = new TcpListener(IPAddress.Any, port);
        l.Start();
        Console.WriteLine("Copometro listo en http://localhost:" + port + "  (" + Parties.Count + " fiestas guardadas)");
        while (true)
        {
            TcpClient c = l.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(Handle, c);
        }
    }

    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }

    static void Save()
    {
        try
        {
            string tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, J.Serialize(Parties), Encoding.UTF8);
            if (File.Exists(DataFile)) File.Delete(DataFile);
            File.Move(tmp, DataFile);
        }
        catch { }
    }

    static void Handle(object o)
    {
        TcpClient c = (TcpClient)o;
        try
        {
            c.ReceiveTimeout = 8000;
            c.SendTimeout = 8000;
            using (NetworkStream s = c.GetStream()) Process(s);
        }
        catch { }
        finally { try { c.Close(); } catch { } }
    }

    static int FindEnd(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i - 3;
        return -1;
    }

    static void Process(NetworkStream s)
    {
        byte[] buf = new byte[65536];
        int len = 0, hend = -1;
        while (hend < 0)
        {
            int n = s.Read(buf, len, buf.Length - len);
            if (n <= 0) return;
            len += n;
            hend = FindEnd(buf, len);
            if (hend < 0 && len == buf.Length) return;
        }
        string head = Encoding.ASCII.GetString(buf, 0, hend);
        string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
        string[] rl = lines[0].Split(' ');
        if (rl.Length < 2) return;
        string method = rl[0].ToUpperInvariant();
        string target = rl[1];
        int cl = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(lines[i].Substring(15).Trim(), out cl);
        }
        int bodyStart = hend + 4;
        if (cl > 30000) { Write(s, 413, "text/plain", Encoding.UTF8.GetBytes("Demasiado grande")); return; }
        while (len - bodyStart < cl)
        {
            int n = s.Read(buf, len, buf.Length - len);
            if (n <= 0) return;
            len += n;
        }
        string body = cl > 0 ? Encoding.UTF8.GetString(buf, bodyStart, cl) : "";

        string path = target, qs = "";
        int qi = target.IndexOf('?');
        if (qi >= 0) { path = target.Substring(0, qi); qs = target.Substring(qi + 1); }
        Dictionary<string, string> q = new Dictionary<string, string>();
        foreach (string kv in qs.Split('&'))
        {
            if (kv.Length == 0) continue;
            int e = kv.IndexOf('=');
            string k = e < 0 ? kv : kv.Substring(0, e);
            string v = e < 0 ? "" : kv.Substring(e + 1);
            try { q[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v); } catch { }
        }

        if (path.StartsWith("/photo/"))
        {
            string[] pp = path.Substring(7).Split('/');
            byte[] img = null;
            if (pp.Length == 2)
            {
                lock (L)
                {
                    Party party;
                    if (Parties.TryGetValue(pp[0].ToUpperInvariant(), out party))
                    {
                        Player pl = Find(party, pp[1]);
                        if (pl != null && pl.photo != null)
                        {
                            try { img = Convert.FromBase64String(pl.photo.Substring(pl.photo.IndexOf(',') + 1)); } catch { img = null; }
                        }
                    }
                }
            }
            if (img != null) Write(s, 200, "image/jpeg", img, "public, max-age=86400");
            else Write(s, 404, "text/plain", new byte[0]);
            return;
        }

        if (path.StartsWith("/api/"))
        {
            try
            {
                object res = Api(method, path.Substring(5).Trim('/').Split('/'), q, body);
                Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(J.Serialize(res)));
            }
            catch (ApiError ex)
            {
                Dictionary<string, object> er = new Dictionary<string, object>();
                er["error"] = ex.Message;
                Write(s, ex.Status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(J.Serialize(er)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex);
                Write(s, 500, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"Error interno\"}"));
            }
            return;
        }
        if (path == "/health") { Write(s, 200, "text/plain", Encoding.UTF8.GetBytes("ok")); return; }
        if (path == "/favicon.ico") { Write(s, 204, "text/plain", new byte[0]); return; }
        string file = Path.Combine(Dir, "index.html");
        if (File.Exists(file)) Write(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(file));
        else Write(s, 404, "text/plain", Encoding.UTF8.GetBytes("Falta index.html"));
    }

    static void Write(NetworkStream s, int status, string type, byte[] body)
    {
        Write(s, status, type, body, "no-store");
    }

    static void Write(NetworkStream s, int status, string type, byte[] body, string cache)
    {
        string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : status == 413 ? "Payload Too Large" : "Error";
        string h = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                   "\r\nCache-Control: " + cache + "\r\nConnection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(h);
        s.Write(hb, 0, hb.Length);
        if (body.Length > 0) s.Write(body, 0, body.Length);
        s.Flush();
    }

    // ---------- helpers ----------
    static string Str(Dictionary<string, object> d, string k)
    {
        object v;
        if (d != null && d.TryGetValue(k, out v) && v != null) return Convert.ToString(v);
        return "";
    }
    static int Int(Dictionary<string, object> d, string k)
    {
        try { return Convert.ToInt32(Str(d, k)); } catch { return 0; }
    }
    static string Clean(string s, int max)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char ch in s) if (!char.IsControl(ch)) sb.Append(ch);
        string r = sb.ToString().Trim();
        if (r.Length > max)
        {
            r = r.Substring(0, max);
            if (char.IsHighSurrogate(r[r.Length - 1])) r = r.Substring(0, r.Length - 1);
        }
        return r;
    }
    static string Ig(string s)
    {
        s = s.Trim().TrimStart('@');
        return Regex.Replace(s, "[^A-Za-z0-9._]", "").Length > 30 ? Regex.Replace(s, "[^A-Za-z0-9._]", "").Substring(0, 30) : Regex.Replace(s, "[^A-Za-z0-9._]", "");
    }
    static string Pid(Dictionary<string, object> b)
    {
        string p = Str(b, "pid");
        if (!Regex.IsMatch(p, "^[a-z0-9]{6,24}$")) throw new ApiError(400, "Identificador no valido");
        return p;
    }
    static Party Get(string code)
    {
        code = (code ?? "").ToUpperInvariant();
        Party p;
        if (!Parties.TryGetValue(code, out p)) throw new ApiError(404, "No existe esa fiesta");
        return p;
    }
    static string NewCode()
    {
        const string a = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        for (int t = 0; t < 200; t++)
        {
            char[] c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = a[Rnd.Next(a.Length)];
            string s = new string(c);
            if (!Parties.ContainsKey(s)) return s;
        }
        throw new ApiError(500, "No hay codigos libres");
    }
    static Player Find(Party p, string id)
    {
        foreach (Player x in p.players) if (x.id == id) return x;
        return null;
    }
    static Player Upsert(Party p, Dictionary<string, object> b, string pid)
    {
        if (Str(b, "adult").ToLowerInvariant() != "true") throw new ApiError(400, "Tienes que confirmar que eres mayor de 18 anos");
        string name = Clean(Str(b, "name"), 18);
        if (name == "") throw new ApiError(400, "Falta tu nombre");
        string emoji = Clean(Str(b, "emoji"), 4);
        if (emoji == "") emoji = "\U0001F57A";
        Player pl = Find(p, pid);
        if (pl == null)
        {
            if (p.players.Count >= 60) throw new ApiError(400, "La fiesta esta llena");
            pl = new Player(); pl.id = pid; pl.joined = Now();
            p.players.Add(pl);
        }
        pl.name = name; pl.emoji = emoji; pl.ig = Ig(Str(b, "ig"));
        string ph = Str(b, "photo");
        if (ph != "")
        {
            if (ph.Length > 24000 || !Regex.IsMatch(ph, "^data:image/jpeg;base64,[A-Za-z0-9+/=]+$")) throw new ApiError(400, "Foto no valida");
            pl.photo = ph; pl.photoV++;
        }
        else if (Str(b, "removePhoto").ToLowerInvariant() == "true" && pl.photo != null) { pl.photo = null; pl.photoV++; }
        return pl;
    }
    static object View(Party p, string pid)
    {
        List<object> rs = new List<object>();
        foreach (Reto r in p.retos)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = r.id; d["from"] = r.from; d["to"] = r.to; d["kind"] = r.kind; d["status"] = r.status;
            d["range"] = r.range; d["created"] = r.created;
            if (r.status == "resolved")
            {
                d["fromPick"] = r.fromPick; d["toPick"] = r.toPick; d["match"] = r.match; d["paid"] = r.paid;
            }
            else if (r.from == pid) d["fromPick"] = r.fromPick;
            rs.Add(d);
        }
        Dictionary<string, object> v = new Dictionary<string, object>();
        List<object> ps = new List<object>();
        foreach (Player pl in p.players)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = pl.id; d["name"] = pl.name; d["emoji"] = pl.emoji; d["ig"] = pl.ig; d["drinks"] = pl.drinks; d["joined"] = pl.joined; d["ph"] = pl.photo != null ? pl.photoV : 0;
            if (p.finished) d["times"] = pl.times;
            ps.Add(d);
        }
        v["code"] = p.code; v["name"] = p.name; v["v"] = p.v; v["players"] = ps; v["retos"] = rs;
        v["finished"] = p.finished; v["finishedAt"] = p.finishedAt; v["created"] = p.created; v["host"] = p.host ?? ""; v["music"] = p.music ?? "";
        return v;
    }
    static void Bump(Party p) { p.v++; Dirty.Add(p.code); Save(); }

    // ---------- API ----------
    static object Api(string method, string[] seg, Dictionary<string, string> q, string body)
    {
        Dictionary<string, object> b = null;
        if (method == "POST" && body.Length > 0)
        {
            try { b = J.DeserializeObject(body) as Dictionary<string, object>; } catch { }
            if (b == null) throw new ApiError(400, "Cuerpo no valido");
        }

        if (seg[0] == "status")
        {
            Dictionary<string, object> st = new Dictionary<string, object>();
            st["persist"] = RUrl != null; st["flushes"] = PersistOk; st["error"] = PersistErr;
            lock (L) { st["parties"] = Parties.Count; }
            return st;
        }

        lock (L)
        {
            if (seg[0] == "create" && method == "POST")
            {
                string pid = Pid(b);
                if (Parties.Count > 3000) throw new ApiError(400, "Demasiadas fiestas");
                Party p = new Party();
                p.code = NewCode(); p.created = Now(); p.host = pid;
                p.name = Clean(Str(b, "party"), 28);
                Player me = Upsert(p, b, pid);
                if (p.name == "") p.name = "Fiesta de " + me.name;
                Parties[p.code] = p;
                Bump(p);
                return View(p, pid);
            }
            if (seg[0] == "p" && seg.Length >= 2)
            {
                Party p = Get(seg[1]);
                if (seg.Length == 2 && method == "GET")
                {
                    string pid = q.ContainsKey("pid") ? q["pid"] : "";
                    int have;
                    if (q.ContainsKey("v") && int.TryParse(q["v"], out have) && have == p.v)
                    {
                        Dictionary<string, object> same = new Dictionary<string, object>();
                        same["same"] = true; same["v"] = p.v;
                        return same;
                    }
                    return View(p, pid);
                }
                if (seg.Length == 3 && method == "POST")
                {
                    string pid = Pid(b);
                    string act = seg[2];
                    if (act == "join")
                    {
                        Upsert(p, b, pid); Bump(p); return View(p, pid);
                    }
                    Player me = Find(p, pid);
                    if (me == null) throw new ApiError(400, "Primero tienes que unirte a la fiesta");

                    if (act == "music")
                    {
                        string url = Str(b, "url").Trim();
                        if (url != "")
                        {
                            Uri uu;
                            if (url.Length > 300 || !Uri.TryCreate(url, UriKind.Absolute, out uu) || uu.Scheme != "https") throw new ApiError(400, "El enlace tiene que empezar por https://");
                            string h = uu.Host.ToLowerInvariant();
                            string[] ok = { "open.spotify.com", "spotify.link", "spotify.app.link", "music.youtube.com", "www.youtube.com", "youtube.com", "youtu.be", "music.apple.com", "soundcloud.com", "on.soundcloud.com", "www.deezer.com", "deezer.com", "link.deezer.com", "tidal.com", "listen.tidal.com" };
                            if (Array.IndexOf(ok, h) < 0) throw new ApiError(400, "Pon un enlace de Spotify, YouTube, Apple Music, SoundCloud, Deezer o Tidal");
                        }
                        p.music = url == "" ? null : url;
                        Bump(p);
                        return View(p, pid);
                    }
                    if (act == "finish" || act == "reopen")
                    {
                        if (!string.IsNullOrEmpty(p.host) && p.host != pid) throw new ApiError(400, "Solo quien creo la fiesta puede hacerlo");
                        if (act == "finish" && !p.finished) { p.finished = true; p.finishedAt = Now(); Bump(p); }
                        if (act == "reopen" && p.finished) { p.finished = false; p.finishedAt = 0; Bump(p); }
                        return View(p, pid);
                    }
                    if (act == "drink")
                    {
                        if (p.finished) throw new ApiError(400, "La fiesta ya ha terminado");
                        string k = Str(b, "k");
                        if (Array.IndexOf(Kinds, k) < 0) throw new ApiError(400, "Bebida no valida");
                        int delta = Int(b, "delta") >= 0 ? 1 : -1;
                        int cur; me.drinks.TryGetValue(k, out cur);
                        int nv = Math.Max(0, cur + delta);
                        if (nv != cur)
                        {
                            me.drinks[k] = nv;
                            if (Alc(k))
                            {
                                if (delta > 0) { me.times.Add(Now()); if (me.times.Count > 400) me.times.RemoveAt(0); }
                                else if (me.times.Count > 0) me.times.RemoveAt(me.times.Count - 1);
                            }
                            Bump(p);
                        }
                        return View(p, pid);
                    }
                    if (act == "reto")
                    {
                        if (p.finished) throw new ApiError(400, "La fiesta ya ha terminado");
                        Player to = Find(p, Str(b, "to"));
                        string kind = Str(b, "kind");
                        int range = Int(b, "range"), pick = Int(b, "pick");
                        if (to == null || to.id == pid) throw new ApiError(400, "Elige a otra persona");
                        if (kind != "trago" && kind != "chupito") throw new ApiError(400, "Tipo no valido");
                        if (range != 3 && range != 5 && range != 10) throw new ApiError(400, "Rango no valido");
                        if (pick < 1 || pick > range) throw new ApiError(400, "Tu numero no es valido");
                        Reto r = new Reto();
                        r.id = Guid.NewGuid().ToString("N").Substring(0, 8);
                        r.from = pid; r.to = to.id; r.kind = kind; r.range = range; r.fromPick = pick;
                        r.status = "pending"; r.created = Now();
                        p.retos.Insert(0, r);
                        if (p.retos.Count > 150) p.retos.RemoveRange(150, p.retos.Count - 150);
                        Bump(p); return View(p, pid);
                    }
                    if (act == "answer" || act == "pay")
                    {
                        Reto r = null;
                        string rid = Str(b, "id");
                        foreach (Reto x in p.retos) if (x.id == rid) r = x;
                        if (r == null || r.to != pid) throw new ApiError(400, "Ese reto no es tuyo");
                        if (act == "answer")
                        {
                            int pick = Int(b, "pick");
                            if (r.status != "pending") return View(p, pid);
                            if (pick < 1 || pick > r.range) throw new ApiError(400, "Numero no valido");
                            r.toPick = pick; r.status = "resolved"; r.match = pick == r.fromPick;
                            Bump(p);
                        }
                        else
                        {
                            if (r.status == "resolved" && r.match && !r.paid)
                            {
                                r.paid = true;
                                string k = r.kind == "chupito" ? "chupito" : "copa";
                                int cur; me.drinks.TryGetValue(k, out cur);
                                me.drinks[k] = cur + 1;
                                Bump(p);
                            }
                        }
                        return View(p, pid);
                    }
                }
            }
        }
        throw new ApiError(404, "No encontrado");
    }
}
