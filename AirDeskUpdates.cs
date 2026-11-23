using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed class AirDeskUpdates
{
    public const string InstalledVersion = "1.0.0";
    private const string Repository = "SystemTechnologiesCompany/STC-AirDesk";
    private const string AssetName = "STC-AirDesk.exe";
    private const long MaxDownloadBytes = 150 * 1024 * 1024;
    private volatile DownloadOffer currentOffer;

    [DataContract]
    private sealed class Release
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "body")] public string Notes { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public Asset[] Assets { get; set; }
    }

    [DataContract]
    private sealed class Asset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "digest")] public string Digest { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    [DataContract]
    public sealed class UpdateStatus
    {
        [DataMember] public string installed = InstalledVersion;
        [DataMember] public string state;
        [DataMember(EmitDefaultValue = false)] public string version;
        [DataMember(EmitDefaultValue = false)] public string notes;
        [DataMember(EmitDefaultValue = false)] public string sha256;
        [DataMember(EmitDefaultValue = false)] public string fileName;
        [DataMember(EmitDefaultValue = false)] public long size;
    }

    public sealed class DownloadOffer
    {
        public readonly string Version;
        public readonly long Size;
        public readonly Uri Url;

        public DownloadOffer(string version, long size, Uri url)
        {
            Version = version;
            Size = size;
            Url = url;
        }
    }

    public UpdateStatus Check()
    {
        currentOffer = null;
        Uri endpoint = new Uri("https://api.github.com/repos/" + Repository + "/releases/latest");
        try
        {
            using (HttpWebResponse response = (HttpWebResponse)CreateRequest(endpoint).GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream body = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK) return State("error");
                byte[] buffer = new byte[8192];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (body.Length + count > 1024 * 1024) return State("error");
                    body.Write(buffer, 0, count);
                }
                body.Position = 0;
                return ReadRelease(body);
            }
        }
        catch (WebException error)
        {
            using (HttpWebResponse response = error.Response as HttpWebResponse)
                return State(response != null && response.StatusCode == HttpStatusCode.NotFound ? "unavailable" : "error");
        }
        catch (IOException) { return State("error"); }
        catch (SerializationException) { return State("error"); }
        catch (XmlException) { return State("error"); }
    }

    private UpdateStatus ReadRelease(Stream source)
    {
        Release release = new DataContractJsonSerializer(typeof(Release),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 8192 }).ReadObject(source) as Release;
        Version version;
        if (release == null || release.Draft || release.Prerelease ||
            String.IsNullOrEmpty(release.Tag) || !Regex.IsMatch(release.Tag, @"\A[vV]?[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !Version.TryParse(release.Tag.TrimStart('v', 'V'), out version))
            return State("unavailable");
        if (version <= new Version(InstalledVersion)) return State("current");

        UpdateStatus result = State("missing-download");
        result.version = version.ToString(3);
        result.notes = release.Notes ?? "";
        if (result.notes.Length > 6000) result.notes = result.notes.Substring(0, 6000) + "…";
        string path = "/" + Repository + "/releases/download/" + release.Tag + "/" + AssetName;
        foreach (Asset asset in release.Assets ?? new Asset[0])
        {
            Uri url;
            if (asset == null || asset.Name != AssetName || asset.Size < 1 || asset.Size > MaxDownloadBytes ||
                asset.Digest == null || !Regex.IsMatch(asset.Digest, @"\Asha256:[a-fA-F0-9]{64}\z") ||
                !Uri.TryCreate(asset.Url, UriKind.Absolute, out url) || !SecureUrl(url) ||
                url.Host != "github.com" || url.AbsolutePath != path || url.Query.Length != 0)
                continue;
            currentOffer = new DownloadOffer(result.version, asset.Size, url);
            result.state = "available";
            result.sha256 = asset.Digest.Substring(7).ToLowerInvariant();
            result.fileName = AssetName;
            result.size = asset.Size;
            break;
        }
        return result;
    }

    public DownloadOffer GetOffer(string version)
    {
        DownloadOffer available = currentOffer;
        return available != null && available.Version == version ? available : null;
    }

    public static HttpWebResponse OpenDownload(DownloadOffer offer)
    {
        Uri url = offer.Url;
        for (int hop = 0; hop < 5; hop++)
        {
            HttpWebResponse response = (HttpWebResponse)CreateRequest(url).GetResponse();
            if (response.StatusCode == HttpStatusCode.OK &&
                (response.ContentLength < 0 || response.ContentLength == offer.Size))
                return response;

            string location = response.Headers[HttpResponseHeader.Location];
            int status = (int)response.StatusCode;
            response.Dispose();
            Uri next;
            if (status < 300 || status >= 400 || !Uri.TryCreate(url, location, out next) ||
                !SecureUrl(next) || (next.Host != "release-assets.githubusercontent.com" &&
                                    next.Host != "objects.githubusercontent.com"))
                break;
            url = next;
        }
        throw new IOException("The release download was unavailable.");
    }

    private static bool SecureUrl(Uri url)
    {
        return url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
    }

    private static HttpWebRequest CreateRequest(Uri url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.AllowAutoRedirect = false;
        request.Timeout = 10000;
        request.ReadWriteTimeout = 12000;
        request.UserAgent = "STC-AirDesk/" + InstalledVersion;
        if (url.Host == "api.github.com") request.Accept = "application/vnd.github+json";
        return request;
    }

    private static UpdateStatus State(string state)
    {
        return new UpdateStatus { state = state };
    }
}
