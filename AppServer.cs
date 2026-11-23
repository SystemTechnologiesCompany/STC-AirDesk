using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

internal sealed class AppServer : IDisposable
{
    private static readonly byte[] Empty = new byte[0];
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly MouseControl mouse;
    private readonly AirDeskUpdates updates = new AirDeskUpdates();
    private readonly Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private readonly string route = "/" + Guid.NewGuid().ToString("N") + "/";
    private readonly string host;
    private readonly string origin;
    private readonly Thread worker;
    private volatile bool pageServed;
    private readonly SemaphoreSlim connections = new SemaphoreSlim(8);
    private readonly AppSettings settings = new AppSettings(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Launcher.AppTitle),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Launcher.LegacyProductName));

    public AppServer() : this(null) { }

    public AppServer(FloatingKeyboard keyboard)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string filename in assembly.GetManifestResourceNames())
        {
            using (Stream source = assembly.GetManifestResourceStream(filename))
            using (MemoryStream bytes = new MemoryStream())
            {
                source.CopyTo(bytes);
                assets.Add(route + Uri.EscapeDataString(filename), bytes.ToArray());
            }
        }
        listener.Start(12);
        host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        origin = "http://" + host;
        mouse = new MouseControl(keyboard);
        worker = new Thread(Serve) { IsBackground = true };
        worker.Start();
    }

    public string Page { get { return origin + route + "index.html"; } }
    public bool PageServed { get { return pageServed; } }

    private void Serve()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }
            if (!connections.Wait(0))
            {
                client.Close();
                continue;
            }
            ThreadPool.QueueUserWorkItem(HandleClient, client);
        }
    }

    private void HandleClient(object state)
    {
        try
        {
            using (TcpClient client = (TcpClient)state)
            {
                client.ReceiveTimeout = 1000;
                client.SendTimeout = 1000;
                client.NoDelay = true;
                Handle(client.GetStream());
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        finally { connections.Release(); }
    }

    private void Handle(NetworkStream stream)
    {
        string[] request;
        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (BufferedStream input = new BufferedStream(stream, 4096))
        {
            request = ReadLine(input, 2048).Split(' ');
            int headerBytes = 0;
            while (true)
            {
                string line = ReadLine(input, 4096);
                if (line.Length == 0) break;
                headerBytes += line.Length;
                int colon = line.IndexOf(':');
                if (headerBytes > 16384 || colon < 1) throw new IOException("Invalid HTTP headers.");
                headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
            }
            string value;
            if (request.Length != 3 || !headers.TryGetValue("Host", out value) || value != host)
            {
                Reply(stream, "400 Bad Request", null, Empty);
                return;
            }
            byte[] asset;
            string path = request[1];
            if (request[0] == "GET" && assets.TryGetValue(path, out asset))
            {
                Reply(stream, "200 OK", AssetType(path), asset);
                if (path == route + "index.html") pageServed = true;
                return;
            }
            bool preferences = path == route + "settings";
            bool startup = path == route + "startup";
            bool download = path == route + "update-download";
            if (request[0] == "GET" && path == route + "update-version")
            {
                Reply(stream, "200 OK", "text/plain; charset=utf-8", Encoding.ASCII.GetBytes(AirDeskUpdates.InstalledVersion));
                return;
            }
            if (request[0] == "GET" && path == route + "updates")
            {
                using (MemoryStream json = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(AirDeskUpdates.UpdateStatus)).WriteObject(json, updates.Check());
                    Reply(stream, "200 OK", "application/json; charset=utf-8", json.ToArray());
                }
                return;
            }
            if (request[0] == "GET" && (preferences || startup))
            {
                ReplySettings(stream, startup, null);
                return;
            }
            if (request[0] != "POST" || (!preferences && !startup && !download && path != route + "mouse") ||
                !headers.TryGetValue("Origin", out value) || value != origin)
            {
                Reply(stream, "404 Not Found", null, Empty);
                return;
            }
            int length;
            if (headers.ContainsKey("Transfer-Encoding") ||
                !headers.TryGetValue("Content-Length", out value) || !Int32.TryParse(value, out length) ||
                length < 1 || length > (preferences ? AppSettings.MaxBytes : 64))
            {
                Reply(stream, "400 Bad Request", null, Empty);
                return;
            }
            byte[] body = new byte[length];
            for (int offset = 0; offset < length;)
            {
                int count = input.Read(body, offset, length - offset);
                if (count == 0) throw new IOException("Incomplete body.");
                offset += count;
            }
            if (download) ReplyDownload(stream, Encoding.ASCII.GetString(body));
            else if (preferences || startup) ReplySettings(stream, startup, Encoding.UTF8.GetString(body));
            else Reply(stream, mouse.Handle(Encoding.ASCII.GetString(body)), null, Empty);
        }
    }

    private void ReplyDownload(NetworkStream stream, string version)
    {
        AirDeskUpdates.DownloadOffer offer = updates.GetOffer(version);
        if (offer == null)
        {
            Reply(stream, "409 Conflict", null, Empty);
            return;
        }
        HttpWebResponse response;
        try { response = AirDeskUpdates.OpenDownload(offer); }
        catch (WebException error)
        {
            if (error.Response != null) error.Response.Dispose();
            Reply(stream, "502 Bad Gateway", null, Empty);
            return;
        }
        catch (IOException)
        {
            Reply(stream, "502 Bad Gateway", null, Empty);
            return;
        }
        using (response)
        using (Stream input = response.GetResponseStream())
        {
            ReplyHeaders(stream, "200 OK", "application/octet-stream", offer.Size);
            byte[] buffer = new byte[65536];
            for (long remaining = offer.Size; remaining > 0;)
            {
                int count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) break; // A short response will fail the browser's length and checksum checks.
                stream.Write(buffer, 0, count);
                remaining -= count;
            }
        }
    }

    private void ReplySettings(NetworkStream stream, bool startup, string body)
    {
        string status = "204 No Content";
        string result = null;
        try
        {
            if (body == null)
            {
                result = startup ? (AppSettings.StartupEnabled() ? "true" : "false") : settings.Read();
                status = "200 OK";
            }
            else if (startup)
            {
                if (body == "true" || body == "false") AppSettings.SetStartup(body == "true");
                else status = "400 Bad Request";
            }
            else if (!settings.Save(body)) status = "400 Bad Request";
        }
        catch (IOException) { status = "503 Service Unavailable"; }
        catch (UnauthorizedAccessException) { status = "503 Service Unavailable"; }
        catch (System.Security.SecurityException) { status = "503 Service Unavailable"; }
        Reply(stream, status, result == null ? null : "application/json; charset=utf-8",
            result == null ? Empty : Encoding.UTF8.GetBytes(result));
    }

    private static string ReadLine(Stream input, int limit)
    {
        StringBuilder line = new StringBuilder();
        for (int index = 0; index < limit; index++)
        {
            int value = input.ReadByte();
            if (value == -1) break;
            if (value == '\r' && input.ReadByte() == '\n') return line.ToString();
            if (value < 32 || value > 126) break;
            line.Append((char)value);
        }
        throw new IOException("Invalid HTTP request line.");
    }

    private static string AssetType(string path)
    {
        switch (Path.GetExtension(path))
        {
            case ".html": return "text/html; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".js": return "text/javascript; charset=utf-8";
            case ".svg": return "image/svg+xml";
            case ".png": return "image/png";
            case ".ico": return "image/x-icon";
            default: return "application/octet-stream";
        }
    }

    private static void Reply(Stream stream, string status, string type, byte[] body)
    {
        ReplyHeaders(stream, status, type, body.Length);
        if (body.Length > 0) stream.Write(body, 0, body.Length);
    }

    private static void ReplyHeaders(Stream stream, string status, string type, long length)
    {
        string headers = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; " +
            "script-src 'self' 'wasm-unsafe-eval' https://cdn.jsdelivr.net; " +
            "connect-src 'self' https://cdn.jsdelivr.net https://storage.googleapis.com\r\n" +
            "Content-Length: " + length + "\r\n" +
            (type == null ? "" : "Content-Type: " + type + "\r\n") + "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        listener.Stop();
        mouse.Dispose();
    }
}
