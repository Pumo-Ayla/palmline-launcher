using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

[DataContract]
internal sealed class ReleaseAsset
{
    [DataMember] public string name { get; set; }
    [DataMember] public string digest { get; set; }
    [DataMember] public string browser_download_url { get; set; }
    [DataMember] public long size { get; set; }
    internal Version Version;
    internal Version MinimumLauncher;
    internal int NormalUpdates, CriticalUpdates;
    internal bool HasHistory;
}

[DataContract]
internal sealed class GitHubRelease
{
    [DataMember] public string tag_name { get; set; }
    [DataMember] public string body { get; set; }
    [DataMember] public bool draft { get; set; }
    [DataMember] public bool prerelease { get; set; }
    [DataMember] public ReleaseAsset[] assets { get; set; }
}

internal static class ReleasePackage
{
    internal static readonly string Endpoint = "https://api.github.com/repos/" +
        UpdateIdentity.Repository + "/releases/latest";
    internal const long MaxDownload = 2L * 1024 * 1024 * 1024;

    internal static HttpClient Client()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Palmline-Launcher-Updater/1");
        return client;
    }

    internal static Version ReadVersion(string tag)
    {
        var match = Regex.Match(tag ?? "", @"^(?:launcher[ ._-]+)?v?(\d+\.\d+\.\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Version version;
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version)) return null;
        return new Version(version.Major, version.Minor, version.Build, 0);
    }

    internal static ReleaseAsset Check(HttpClient client, Version installed)
    {
        GitHubRelease release;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
        using (var response = client.GetAsync(Endpoint, timeout.Token).GetAwaiter().GetResult()) {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using (var json = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                release = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(json);
        }
        var version = ReadVersion(release.tag_name);
        if (release.draft || release.prerelease || version == null || version <= installed) return null;
        string pattern = @"^Launcher[ ._-]+v?" + Regex.Escape(version.ToString(3)) + @"\.zip$";
        var asset = (release.assets ?? new ReleaseAsset[0]).SingleOrDefault(a =>
            Regex.IsMatch(a.name ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (asset == null) return null;
        Uri uri;
        if (asset.size <= 0 || asset.size > MaxDownload ||
            !Regex.IsMatch(asset.digest ?? "", @"^sha256:[a-fA-F0-9]{64}$") ||
            !Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out uri) ||
            uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort ||
            !uri.AbsolutePath.StartsWith("/" + UpdateIdentity.Repository + "/releases/download/" +
                Uri.EscapeDataString(release.tag_name) + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid release asset metadata.");

        asset.Version = version;
        var floor = Regex.Match(release.body ?? "", @"(?im)^[ \t]*<!--[ \t]*min-launcher-version:[ \t]*(\d+\.\d+\.\d+)[ \t]*-->[ \t]*\r?$");
        if (floor.Success) {
            var minimum = ReadVersion(floor.Groups[1].Value);
            if (minimum != null && minimum <= version) asset.MinimumLauncher = minimum;
        }
        ReadHistory(release.body ?? "", installed, asset);
        return asset;
    }

    static void ReadHistory(string notes, Version installed, ReleaseAsset asset)
    {
        const string marker = "<!-- sr-update-history-v1";
        int start = notes.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) {
            // Releases before the cumulative format only provided a minimum version.
            if (asset.Version >= new Version(1, 0, 3, 0))
                throw new InvalidDataException("Release notes are missing the cumulative update history.");
            return;
        }
        start += marker.Length;
        int end = notes.IndexOf("-->", start, StringComparison.Ordinal);
        if (end < 0 || notes.IndexOf(marker, start, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Invalid update history block.");
        Version previous = null;
        foreach (var raw in notes.Substring(start, end - start).Split('\n')) {
            var row = raw.Trim();
            if (row.Length == 0) continue;
            var fields = Regex.Match(row, @"^(\d+\.\d+\.\d+) (normal|critical)$");
            var version = fields.Success ? ReadVersion(fields.Groups[1].Value) : null;
            if (version == null || (previous != null && version <= previous) || version > asset.Version)
                throw new InvalidDataException("Invalid or unordered update history: " + row);
            previous = version;
            bool critical = fields.Groups[2].Value == "critical";
            if (critical && (asset.MinimumLauncher == null || version > asset.MinimumLauncher))
                asset.MinimumLauncher = version;
            if (version > installed) {
                if (critical) asset.CriticalUpdates++;
                else asset.NormalUpdates++;
            }
        }
        if (previous != asset.Version) throw new InvalidDataException("Update history does not cover the published version.");
        asset.HasHistory = true;
    }

    // Commit the verified archive first and its ready marker last.
    internal static void Download(HttpClient client, string state, ReleaseAsset asset, Action<int> progress)
    {
        int reported = -1;
        string part = Path.Combine(state, "download.part");
        try {
            long received = 0;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            using (var response = client.GetAsync(asset.browser_download_url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult())
            using (var hash = SHA256.Create()) {
                response.EnsureSuccessStatusCode();
                using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (timeout.Token.Register(source.Dispose))
                using (var output = File.Create(part)) {
                    var buffer = new byte[131072];
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) != 0) {
                        received += count;
                        if (received > asset.size) throw new InvalidDataException("Download exceeded its declared size.");
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                        output.Write(buffer, 0, count);
                        int percent = (int)Math.Min(99, received * 100 / asset.size);
                        if (percent != reported) { reported = percent; progress(percent); }
                    }
                    hash.TransformFinalBlock(buffer, 0, 0);
                    output.Flush(true);
                }
                string digest = "sha256:" + BitConverter.ToString(hash.Hash).Replace("-", "");
                if (received != asset.size || !String.Equals(asset.digest, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update size or SHA-256 mismatch.");
            }
            string marker = Path.Combine(state, "ready");
            File.WriteAllLines(marker + ".tmp", new[] { asset.Version.ToString(), asset.digest.Substring(7) });
            File.Delete(marker);
            string archive = Path.Combine(state, "package.zip");
            if (File.Exists(archive)) File.Replace(part, archive, null);
            else File.Move(part, archive);
            File.Move(marker + ".tmp", marker);
            progress(100);
        } finally { File.Delete(part); }
    }
}
