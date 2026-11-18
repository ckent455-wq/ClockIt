using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class ClockItUpdater
{
    private const string Repository = "ckent455-wq/ClockIt";
    private const string LatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string ExecutableName = "ClockIt.exe";
    private const int MaximumDownloadSize = 25 * 1024 * 1024;

    internal sealed class Update
    {
        public Version Version;
        public Uri Download;
        public string Hash;
        public string InstalledHash;
        public int Size;

        public string VersionName { get { return "v" + Version.ToString(3); } }
    }

    public static async Task<Update> CheckAsync(string installedPath, Version installedVersion)
    {
        string installedHash = HashFile(installedPath);
        using (var client = NewClient())
        {
            string json = await client.GetStringAsync(LatestRelease).ConfigureAwait(false);
            return ReadUpdate(json, installedHash, installedVersion);
        }
    }

    private static Update ReadUpdate(string json, string installedHash, Version installedVersion)
    {
        var release = new JavaScriptSerializer().Deserialize<Release>(json);
        Version available;
        if (release == null || release.draft || release.prerelease ||
            !Regex.IsMatch(release.tag_name ?? "", @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !Version.TryParse(release.tag_name.Substring(1) + ".0", out available))
            throw new IOException("GitHub did not return a stable ClockIt release.");

        if (available < installedVersion) return null;
        string expectedUrl = "https://github.com/" + Repository + "/releases/download/" + release.tag_name + "/" + ExecutableName;
        Asset asset = (release.assets ?? new Asset[0]).SingleOrDefault(item => item.name == ExecutableName);
        if (asset == null || asset.state != "uploaded" || asset.browser_download_url != expectedUrl ||
            asset.size <= 0 || asset.size > MaximumDownloadSize ||
            !Regex.IsMatch(asset.digest ?? "", @"\Asha256:[0-9a-fA-F]{64}\z"))
            throw new IOException("The latest release has no verifiable ClockIt executable.");

        string hash = asset.digest.Substring("sha256:".Length).ToLowerInvariant();
        if (hash == installedHash) return null;
        return new Update { Version = available, Download = new Uri(expectedUrl), Hash = hash, InstalledHash = installedHash, Size = asset.size };
    }

    public static async Task InstallAsync(Update update, string installedPath)
    {
        string stage = installedPath + "." + Guid.NewGuid().ToString("N") + ".update";
        bool handedOff = false;
        try
        {
            byte[] content;
            using (var client = NewClient())
                content = await DownloadAsync(client, update.Download).ConfigureAwait(false);
            using (var sha256 = SHA256.Create())
            {
                if (content.Length != update.Size || Hex(sha256.ComputeHash(content)) != update.Hash)
                    throw new IOException("The download did not match GitHub's checksum. Try checking for updates again.");
            }

            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }

            using (var process = Process.GetCurrentProcess())
            using (var worker = Process.Start(CreateInstaller(stage, installedPath, update.Hash, update.InstalledHash, process.Id)))
            {
                if (worker == null) throw new IOException("Windows could not start the ClockIt updater.");
                handedOff = true;
            }
        }
        finally
        {
            if (!handedOff && File.Exists(stage)) File.Delete(stage);
        }
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, Uri url)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            using (HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false))
            {
                int status = (int)response.StatusCode;
                if (status < 300 || status >= 400)
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }

                if (response.Headers.Location == null ||
                    !Uri.TryCreate(url, response.Headers.Location, out url) || url.Scheme != Uri.UriSchemeHttps)
                    throw new IOException("GitHub returned an invalid download redirect.");
            }
        }
        throw new IOException("GitHub returned too many download redirects.");
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, SslProtocols = SslProtocols.Tls12 })
        {
            Timeout = TimeSpan.FromSeconds(25),
            MaxResponseContentBufferSize = MaximumDownloadSize
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClockIt-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return client;
    }

    private static string HashFile(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha256 = SHA256.Create())
            return Hex(sha256.ComputeHash(stream));
    }

    private static string Hex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private static ProcessStartInfo CreateInstaller(string stage, string installedPath, string hash, string installedHash, int parentId)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell,
            "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(InstallScript)))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(installedPath)
        };
        start.EnvironmentVariables["CLOCKIT_UPDATE_STAGE"] = stage;
        start.EnvironmentVariables["CLOCKIT_UPDATE_TARGET"] = installedPath;
        start.EnvironmentVariables["CLOCKIT_UPDATE_HASH"] = hash;
        start.EnvironmentVariables["CLOCKIT_UPDATE_OLD_HASH"] = installedHash;
        start.EnvironmentVariables["CLOCKIT_UPDATE_PARENT"] = parentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }

    private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
$stage = $env:CLOCKIT_UPDATE_STAGE
$target = $env:CLOCKIT_UPDATE_TARGET
$backup = $stage + '.backup'
$parent = Get-Process -Id ([int]$env:CLOCKIT_UPDATE_PARENT) -ErrorAction SilentlyContinue
$parentExited = !$parent
try {
    if ($parent) { $parentExited = $parent.WaitForExit(60000) }
    if (!$parentExited) { throw 'ClockIt did not close in time.' }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $stage).Hash -ne $env:CLOCKIT_UPDATE_HASH) {
        throw 'The staged update failed its checksum.'
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash -ne $env:CLOCKIT_UPDATE_OLD_HASH) {
        throw 'The installed program changed while the update was being prepared.'
    }
    [IO.File]::Replace($stage, $target, $backup)
    try {
        Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target))
    } catch {
        [IO.File]::Replace($backup, $target, $null)
        throw
    }
    Remove-Item -LiteralPath $backup -ErrorAction SilentlyContinue
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show(('Could not install the ClockIt update. ' + $_.Exception.Message), 'ClockIt!')
    if ($parentExited -and (Test-Path -LiteralPath $target)) {
        Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target))
    }
} finally {
    Remove-Item -LiteralPath $stage -ErrorAction SilentlyContinue
}
";

    private sealed class Release
    {
        public string tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public Asset[] assets { get; set; }
    }

    private sealed class Asset
    {
        public string name { get; set; }
        public string state { get; set; }
        public string browser_download_url { get; set; }
        public string digest { get; set; }
        public int size { get; set; }
    }
}
