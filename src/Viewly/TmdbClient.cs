using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Viewly;

internal sealed class TmdbClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _imageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _metadataPath;
    private readonly string _coverPath;

    public TmdbClient(string dataRoot, string token)
    {
        _metadataPath = Path.Combine(dataRoot, "Metadata");
        _coverPath = Path.Combine(dataRoot, "Covers");
        Directory.CreateDirectory(_metadataPath);
        Directory.CreateDirectory(_coverPath);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Viewly/0.1 (portable media library)");
    }

    public async Task<TmdbMatch?> LookupAsync(string relativePath, string filenameTitle, CancellationToken cancellationToken)
    {
        var key = GetKey(relativePath);
        var metadataFile = Path.Combine(_metadataPath, key + ".json");
        if (File.Exists(metadataFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<TmdbMatch>(await File.ReadAllTextAsync(metadataFile, cancellationToken), JsonOptions);
                if (cached is not null)
                    return cached with { CoverPath = cached.CoverPath is null ? null : Path.Combine(_coverPath, Path.GetFileName(cached.CoverPath)) };
            }
            catch { }
        }

        var query = MakeQuery(filenameTitle);
        if (query.Length == 0) return null;
        var isSeries = LooksLikeEpisode(filenameTitle);
        var route = isSeries ? "search/tv" : "search/movie";
        var year = FindYear(filenameTitle);
        var url = $"{route}?query={Uri.EscapeDataString(query)}&include_adult=false" +
                  (year is null ? "" : isSeries ? $"&first_air_date_year={year}" : $"&year={year}");
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = document.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;
        var first = results[0];
        var displayName = GetString(first, isSeries ? "name" : "title") ?? query;
        var overview = GetString(first, "overview") ?? "";
        var poster = GetString(first, "poster_path");
        var release = GetString(first, isSeries ? "first_air_date" : "release_date");
        var id = first.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        string? localCover = null;
        if (!string.IsNullOrWhiteSpace(poster))
        {
            var coverFile = Path.Combine(_coverPath, key + ".jpg");
            if (!File.Exists(coverFile))
            {
                try
                {
                    using var coverResponse = await _imageHttp.GetAsync(new Uri($"https://image.tmdb.org/t/p/w500{poster}"), cancellationToken);
                    if (coverResponse.IsSuccessStatusCode)
                    {
                        var bytes = await coverResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                        await File.WriteAllBytesAsync(coverFile, bytes, cancellationToken);
                    }
                }
                catch { }
            }
            if (File.Exists(coverFile)) localCover = coverFile;
        }

        var match = new TmdbMatch(displayName, overview, release, id, localCover, isSeries);
        var tempFile = metadataFile + ".tmp";
        var portableMatch = match with { CoverPath = localCover is null ? null : Path.GetFileName(localCover) };
        await File.WriteAllTextAsync(tempFile, JsonSerializer.Serialize(portableMatch, JsonOptions), cancellationToken);
        File.Move(tempFile, metadataFile, true);
        return match;
    }

    private static string GetKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool LooksLikeEpisode(string title) => Regex.IsMatch(title, @"(?i)(?:S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3})");
    private static int? FindYear(string title)
    {
        var match = Regex.Match(title, @"(?<!\d)((?:19|20)\d{2})(?!\d)");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }
    private static string MakeQuery(string title)
    {
        var clean = Regex.Replace(title, @"(?i)(?:[ ._-]+S\d{1,2}E\d{1,3}|[ ._-]+\d{1,2}x\d{1,3}).*$", "");
        clean = Regex.Replace(clean, @"[._]+", " ");
        clean = Regex.Replace(clean, @"(?i)\b(19|20)\d{2}\b", " ");
        clean = Regex.Replace(clean, @"(?i)\b(1080p|720p|2160p|4k|bluray|blu-ray|web-dl|webrip|hdtv|x264|x265|hevc|dvdrip)\b.*$", " ");
        return Regex.Replace(clean, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        _http.Dispose();
        _imageHttp.Dispose();
    }
}

internal sealed record TmdbMatch(string Title, string Overview, string? ReleaseDate, int TmdbId, string? CoverPath, bool IsSeries)
{
    public string Year => DateTime.TryParse(ReleaseDate, out var date) ? date.Year.ToString() : "";
}

internal sealed record ViewlySettings(string TmdbReadAccessToken);
