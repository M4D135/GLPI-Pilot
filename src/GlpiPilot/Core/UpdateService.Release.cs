using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record OfficialUpdate(string Version, string Tag, string Commit, string Edition, string ArchiveName, string ArchiveUrl,
    string ArchiveSha256, long Bytes, DateTimeOffset PublishedAt, string ReleasePage);

/// <summary>Explicit requests to the single public project repository. No ticket data, browser cookies or credentials.</summary>
public sealed class UpdateReleaseClient : IDisposable
{
    public const string ReleasesPage = "https://github.com/M4D135/GLPI-Pilot/releases";
    private const string ApiRoot = "https://api.github.com/repos/M4D135/GLPI-Pilot/";
    private const string AssetPath = "/M4D135/GLPI-Pilot/releases/download/";
    private readonly HttpClient _http;
    public UpdateReleaseClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, true) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GLPI-Pilot-Updater/1.0");
    }
    public async Task<OfficialUpdate> FindLatestAsync(string edition, CancellationToken ct = default)
    {
        if (edition is not ("PROD" or "DEV")) throw new InvalidDataException("Choisissez l’édition PROD ou DEV.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(90)); ct = budget.Token;
        using var release = JsonDocument.Parse(await ReadSmallAsync(new Uri(ApiRoot + "releases/latest"), true, 2 * 1024 * 1024, ct), new JsonDocumentOptions { MaxDepth = 20 });
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("La publication reçue n’est pas une version stable publiée.");
        var tag = root.GetProperty("tag_name").GetString() ?? ""; var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(version, out var parsed) || parsed.Build < 0 || tag.Length > 45 || tag.Any(ch => !char.IsAsciiDigit(ch) && ch is not ('.' or 'v'))) throw new InvalidDataException("L’étiquette de version officielle est invalide.");
        var name = $"GLPI-Pilot_{version}_{edition}_Windows-x64.zip"; var assets = root.GetProperty("assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100) throw new InvalidDataException("Inventaire de publication invalide.");
        var matching = assets.EnumerateArray().Where(item => item.GetProperty("name").GetString() == name).ToArray(); var sums = assets.EnumerateArray().Where(item => item.GetProperty("name").GetString() == "SHA256SUMS.txt").ToArray();
        if (matching.Length != 1 || sums.Length != 1) throw new InvalidDataException("Cette publication ne propose pas le pack demandé et son inventaire SHA256SUMS.txt.");
        var size = matching[0].GetProperty("size").GetInt64(); if (size is <= 0 or > UpdateService.MaximumArchiveBytes) throw new InvalidDataException("Le pack publié dépasse la taille autorisée.");
        Uri Asset(JsonElement value)
        {
            var uri = new Uri(value.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute);
            if (uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.StartsWith(AssetPath + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal)) throw new InvalidDataException("La publication contient une adresse de téléchargement extérieure au dépôt officiel.");
            return uri;
        }
        var archive = Asset(matching[0]); var hashes = UpdateService.ParseHashes(Encoding.UTF8.GetString(await ReadSmallAsync(Asset(sums[0]), false, 1024 * 1024, ct)));
        if (!hashes.TryGetValue(name, out var expected)) throw new InvalidDataException("L’empreinte du pack demandé manque à la publication officielle.");
        using var reference = JsonDocument.Parse(await ReadSmallAsync(new Uri(ApiRoot + "git/ref/tags/" + Uri.EscapeDataString(tag)), true, 32768, ct), new JsonDocumentOptions { MaxDepth = 12 });
        var target = reference.RootElement.GetProperty("object"); var sha = target.GetProperty("sha").GetString() ?? ""; var kind = target.GetProperty("type").GetString();
        for (var depth = 0; kind == "tag" && depth < 4; depth++)
        {
            if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("Référence d’étiquette officielle invalide.");
            using var annotated = JsonDocument.Parse(await ReadSmallAsync(new Uri(ApiRoot + "git/tags/" + sha), true, 262144, ct), new JsonDocumentOptions { MaxDepth = 12 });
            target = annotated.RootElement.GetProperty("object"); sha = target.GetProperty("sha").GetString() ?? ""; kind = target.GetProperty("type").GetString();
        }
        if (kind != "commit" || sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("Le commit associé à l’étiquette officielle est invalide.");
        var published = root.GetProperty("published_at").GetDateTimeOffset();
        return new(version, tag, sha, edition, name, archive.AbsoluteUri, expected, size, published, ReleasesPage + "/tag/" + Uri.EscapeDataString(tag));
    }
    public async Task<string> DownloadAsync(OfficialUpdate release, string downloadsRoot, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!UpdateService.IsHash(release.ArchiveSha256) || release.Bytes is <= 0 or > UpdateService.MaximumArchiveBytes || release.ArchiveName != $"GLPI-Pilot_{release.Version}_{release.Edition}_Windows-x64.zip") throw new InvalidDataException("La publication à télécharger est invalide.");
        var uri = new Uri(release.ArchiveUrl, UriKind.Absolute); ValidateUrl(uri, false, initial: true);
        if (uri.AbsolutePath != AssetPath + Uri.EscapeDataString(release.Tag) + "/" + release.ArchiveName) throw new InvalidDataException("L’adresse du pack ne correspond pas à la publication vérifiée.");
        UpdateService.EnsureNoLinks(downloadsRoot); Directory.CreateDirectory(downloadsRoot); var path = Path.Combine(Path.GetFullPath(downloadsRoot), Guid.NewGuid().ToString("N") + ".zip");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromMinutes(10)); ct = budget.Token;
        try
        {
            using var response = await RequestAsync(uri, false, ct); await using var input = await response.Content.ReadAsStreamAsync(ct); await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long length = 0, reported = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if ((length += count) > release.Bytes || length > UpdateService.MaximumArchiveBytes) throw new InvalidDataException("Le téléchargement dépasse la taille annoncée.");
                hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct); if (length - reported >= 1024 * 1024 || length == release.Bytes) { progress?.Report(length); reported = length; }
            }
            if (length != release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Le pack téléchargé est incomplet ou son empreinte diffère de la publication officielle.");
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    private async Task<byte[]> ReadSmallAsync(Uri uri, bool api, int maximum, CancellationToken ct)
    {
        using var response = await RequestAsync(uri, api, ct); if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("La réponse officielle dépasse la taille autorisée.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > maximum) throw new InvalidDataException("La réponse officielle dépasse la taille autorisée."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private async Task<HttpResponseMessage> RequestAsync(Uri uri, bool api, CancellationToken ct)
    {
        ValidateUrl(uri, api, initial: true);
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            ct.ThrowIfCancellationRequested(); using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (api) { request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10"); }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (api || location == null || redirect == 4) throw new InvalidDataException("Redirection inattendue du service officiel.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); ValidateUrl(uri, false, initial: false); continue;
            }
            if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new InvalidDataException("GitHub n’a pas fourni la publication demandée (HTTP " + status + "). Vous pouvez utiliser un ZIP local vérifié."); }
            return response;
        }
        throw new InvalidDataException("Trop de redirections de téléchargement.");
    }
    private static void ValidateUrl(Uri uri, bool api, bool initial)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort) throw new InvalidDataException("Adresse de mise à jour non autorisée.");
        if (api)
        {
            if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsoluteUri.StartsWith(ApiRoot, StringComparison.Ordinal) || uri.Query.Length > 0) throw new InvalidDataException("L’API doit appartenir au dépôt GLPI Pilot officiel.");
        }
        else if (initial)
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith(AssetPath, StringComparison.Ordinal) || uri.Query.Length > 0) throw new InvalidDataException("Le téléchargement doit partir du dépôt GLPI Pilot officiel.");
        }
        else if (!uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith(AssetPath, StringComparison.Ordinal))) throw new InvalidDataException("La redirection du pack vise un hôte non autorisé.");
    }
    public void Dispose() => _http.Dispose();
}
