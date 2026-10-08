using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

/// <summary>
/// Native C# port of the proven CatalogBuilder.ps1 behavior.
/// This service intentionally preserves the existing catalog schema,
/// manifest selection rules, simple YAML parsing rules and validation.
/// It does not implement startup prompting; it only builds a catalog when called.
/// </summary>
public sealed class CatalogBuilderService
{
    private const string RepositoryUrl = "https://codeload.github.com/microsoft/winget-pkgs/zip/refs/heads/master";
    private const string UserAgent = "WingetWinFormsCatalog/1.0";
    private const int MinimumPackageCount = 1000;

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex ManifestLineRegex = new(@"^([A-Za-z][A-Za-z0-9]*):(?:\s*(.*))?$", RegexOptions.Compiled);
    private static readonly Regex TagLineRegex = new(@"^\s+-\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex InlineCommentRegex = new(@"\s+#.*$", RegexOptions.Compiled);
    private static readonly Regex VersionPartRegex = new(@"\d+|[^\d]+", RegexOptions.Compiled);
    private static readonly Regex YamlExtensionRegex = new(@"\.ya?ml$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallerManifestRegex = new(@"\.installer\.ya?ml$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocaleManifestRegex = new(@"\.locale\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] LocaleFields =
    [
        "PackageIdentifier", "PackageVersion", "PackageLocale", "Publisher", "PublisherUrl",
        "PublisherSupportUrl", "PrivacyUrl", "Author", "PackageName", "PackageUrl", "License",
        "LicenseUrl", "Copyright", "CopyrightUrl", "ShortDescription", "Description", "Moniker",
        "Tags", "PurchaseUrl", "ReleaseNotesUrl", "ManifestType"
    ];

    public async Task BuildCatalogAsync(string catalogPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
            throw new ArgumentException("Catalog path must not be empty.", nameof(catalogPath));

        string fullCatalogPath = Path.GetFullPath(catalogPath);
        string catalogDirectory = Path.GetDirectoryName(fullCatalogPath)
            ?? throw new InvalidOperationException("The catalog directory could not be determined.");
        string temporaryCatalog = Path.Combine(catalogDirectory, "catalog.tmp.json");
        string repositoryZip = Path.Combine(Path.GetTempPath(), $"winget-pkgs-{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(catalogDirectory);
            DeleteIfExists(temporaryCatalog);

            await BuildFromOfficialManifestsAsync(repositoryZip, temporaryCatalog, cancellationToken).ConfigureAwait(false);
            ValidateTemporaryCatalog(temporaryCatalog);

            // Preserve the startup builder's safety rule while also supporting an explicit
            // Catalog-page update. Build and validate completely before touching catalog.json.
            // File.Move(..., overwrite: true) replaces the old catalog only after the new file
            // is ready, so a failed build leaves the existing catalog untouched.
            File.Move(temporaryCatalog, fullCatalogPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryCatalog);
            throw;
        }
        finally
        {
            TryDelete(repositoryZip);
        }
    }

    private static async Task BuildFromOfficialManifestsAsync(
        string repositoryZip,
        string destination,
        CancellationToken cancellationToken)
    {
        await DownloadRepositoryAsync(repositoryZip, cancellationToken).ConfigureAwait(false);

        using ZipArchive zip = ZipFile.OpenRead(repositoryZip);

        // Same path index used by the PowerShell builder to avoid searching the whole ZIP per package.
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
            entries[entry.FullName] = entry;

        // Version manifests determine Package ID, latest version and DefaultLocale.
        Dictionary<string, SelectedVersion> latest = new(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Length <= 0)
                continue;
            if (!entry.FullName.Contains("/manifests/", StringComparison.OrdinalIgnoreCase) || !YamlExtensionRegex.IsMatch(entry.FullName))
                continue;
            if (InstallerManifestRegex.IsMatch(entry.FullName) || LocaleManifestRegex.IsMatch(entry.FullName))
                continue;

            try
            {
                Dictionary<string, object?> manifest = ReadManifestFields(
                    ReadZipText(entry),
                    ["PackageIdentifier", "PackageVersion", "DefaultLocale", "ManifestType"]);

                if (!StringValue(manifest, "ManifestType").Equals("version", StringComparison.OrdinalIgnoreCase))
                    continue;

                string id = StringValue(manifest, "PackageIdentifier");
                string version = StringValue(manifest, "PackageVersion");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                    continue;

                if (!latest.TryGetValue(id, out SelectedVersion? selected) ||
                    ComparePackageVersion(version, selected.Version) > 0)
                {
                    int slash = entry.FullName.LastIndexOf('/');
                    string directory = slash >= 0 ? entry.FullName[..(slash + 1)] : string.Empty;
                    latest[id] = new SelectedVersion(version, StringValue(manifest, "DefaultLocale"), directory);
                }
            }
            catch
            {
                // The reference builder deliberately skips malformed/unreadable manifests.
            }
        }

        if (latest.Count < MinimumPackageCount)
            throw new InvalidOperationException($"Too few packages were found in the official manifests: {latest.Count}");

        List<CatalogPackage> packages = new(latest.Count);

        // PowerShell used: $latest.Keys | Sort-Object. Its default string sorting is culture-aware.
        // CurrentCultureIgnoreCase preserves the culture-aware, case-insensitive ID ordering intent.
        foreach (string id in latest.Keys.OrderBy(static value => value, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SelectedVersion selected = latest[id];
            ZipArchiveEntry? localeEntry = null;

            // Multi-file manifest: first try the exact defaultLocale filename.
            if (!string.IsNullOrWhiteSpace(selected.DefaultLocale))
            {
                string candidate = selected.Directory + id + ".locale." + selected.DefaultLocale + ".yaml";
                entries.TryGetValue(candidate, out localeEntry);
            }

            // Safe fallback: find ManifestType: defaultLocale only inside the selected version directory.
            if (localeEntry is null)
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(selected.Directory, StringComparison.Ordinal))
                        continue;

                    string relative = entry.FullName[selected.Directory.Length..];
                    if (relative.Contains('/') || !YamlExtensionRegex.IsMatch(relative) || InstallerManifestRegex.IsMatch(relative))
                        continue;

                    try
                    {
                        Dictionary<string, object?> probe = ReadManifestFields(ReadZipText(entry), ["ManifestType"]);
                        if (StringValue(probe, "ManifestType").Equals("defaultLocale", StringComparison.OrdinalIgnoreCase))
                        {
                            localeEntry = entry;
                            break;
                        }
                    }
                    catch
                    {
                        // Match the reference builder: ignore this candidate and continue.
                    }
                }
            }

            if (localeEntry is null)
                continue;

            try
            {
                Dictionary<string, object?> manifest = ReadManifestFields(ReadZipText(localeEntry), LocaleFields);
                if (!StringValue(manifest, "ManifestType").Equals("defaultLocale", StringComparison.OrdinalIgnoreCase))
                    continue;

                packages.Add(new CatalogPackage
                {
                    Id = id,
                    Name = NullableStringValue(manifest, "PackageName"),
                    Version = selected.Version,
                    Publisher = NullableStringValue(manifest, "Publisher"),
                    ShortDescription = NullableStringValue(manifest, "ShortDescription"),
                    Description = NullableStringValue(manifest, "Description"),
                    Moniker = NullableStringValue(manifest, "Moniker"),
                    Tags = StringArrayValue(manifest, "Tags"),
                    License = NullableStringValue(manifest, "License"),
                    LicenseUrl = NullableStringValue(manifest, "LicenseUrl"),
                    Homepage = NullableStringValue(manifest, "PackageUrl"),
                    PackageUrl = NullableStringValue(manifest, "PackageUrl"),
                    PublisherUrl = NullableStringValue(manifest, "PublisherUrl"),
                    PublisherSupportUrl = NullableStringValue(manifest, "PublisherSupportUrl"),
                    PrivacyUrl = NullableStringValue(manifest, "PrivacyUrl"),
                    Author = NullableStringValue(manifest, "Author"),
                    Copyright = NullableStringValue(manifest, "Copyright"),
                    CopyrightUrl = NullableStringValue(manifest, "CopyrightUrl"),
                    PurchaseUrl = NullableStringValue(manifest, "PurchaseUrl"),
                    ReleaseNotesUrl = NullableStringValue(manifest, "ReleaseNotesUrl"),
                    Locale = NullableStringValue(manifest, "PackageLocale")
                });
            }
            catch
            {
                // Match the reference builder: one bad package must not abort the complete catalog.
            }
        }

        if (packages.Count < MinimumPackageCount)
            throw new InvalidOperationException($"The generated catalog contains too few real packages: {packages.Count}");

        CatalogDocument catalog = new()
        {
            Metadata = new CatalogMetadata
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Source = "microsoft/winget-pkgs",
                SourceBranch = "master",
                PackageCount = packages.Count
            },
            Packages = packages
        };

        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        string json = JsonSerializer.Serialize(catalog, jsonOptions);
        await File.WriteAllTextAsync(destination, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadRepositoryAsync(string destination, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, RepositoryUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadZipText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, object?> ReadManifestFields(string text, IEnumerable<string> fields)
    {
        HashSet<string> wanted = new(fields, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);
        string[] lines = Regex.Split(text, "\\r?\\n");

        for (int i = 0; i < lines.Length; i++)
        {
            Match match = ManifestLineRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            string key = match.Groups[1].Value;
            if (!wanted.Contains(key))
                continue;

            string raw = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;

            if (key == "Tags")
            {
                List<string> tags = [];
                int j = i + 1;
                while (j < lines.Length)
                {
                    Match tagMatch = TagLineRegex.Match(lines[j]);
                    if (!tagMatch.Success)
                        break;

                    string? tag = ConvertFromSimpleYamlScalar(tagMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                    j++;
                }

                result[key] = tags.ToArray();
                i = j - 1;
                continue;
            }

            string trimmed = raw.Trim();
            if (trimmed is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            {
                List<string> parts = [];
                int j = i + 1;
                while (j < lines.Length)
                {
                    if (lines[j].Length > 0 && !char.IsWhiteSpace(lines[j][0]))
                        break;

                    Match indented = Regex.Match(lines[j], @"^\s+(.*)$");
                    parts.Add(indented.Success ? indented.Groups[1].Value : string.Empty);
                    j++;
                }

                result[key] = trimmed.StartsWith('>')
                    ? string.Join(" ", parts).Trim()
                    : string.Join("\n", parts).Trim();
                i = j - 1;
                continue;
            }

            result[key] = ConvertFromSimpleYamlScalar(raw);
        }

        return result;
    }

    private static string? ConvertFromSimpleYamlScalar(string? value)
    {
        if (value is null)
            return null;

        string v = value.Trim();
        if (v.Length == 0 || v == "~" || v == "null")
            return null;

        if (v.Length >= 2 && v.StartsWith('\'') && v.EndsWith('\''))
            return v[1..^1].Replace("''", "'", StringComparison.Ordinal);

        if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
        {
            string inner = v[1..^1];
            return inner.Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        v = InlineCommentRegex.Replace(v, string.Empty);
        return v.Trim();
    }

    internal static int ComparePackageVersion(string a, string b)
    {
        if (a == b)
            return 0;

        string[] aa = VersionPartRegex.Matches(a).Select(static m => m.Value).ToArray();
        string[] bb = VersionPartRegex.Matches(b).Select(static m => m.Value).ToArray();
        int count = Math.Max(aa.Length, bb.Length);

        for (int i = 0; i < count; i++)
        {
            if (i >= aa.Length) return -1;
            if (i >= bb.Length) return 1;

            string x = aa[i];
            string y = bb[i];
            bool xIsNumber = long.TryParse(x, out long xn);
            bool yIsNumber = long.TryParse(y, out long yn);

            if (xIsNumber && yIsNumber)
            {
                if (xn < yn) return -1;
                if (xn > yn) return 1;
            }
            else
            {
                int comparison = string.Compare(x, y, ignoreCase: true, CultureInfo.InvariantCulture);
                if (comparison < 0) return -1;
                if (comparison > 0) return 1;
            }
        }

        return 0;
    }

    private static void ValidateTemporaryCatalog(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("The temporary catalog was not created.");

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        JsonElement root = parsed.RootElement;

        if (!root.TryGetProperty("metadata", out JsonElement metadata) ||
            !root.TryGetProperty("packages", out JsonElement packages) ||
            packages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The temporary catalog does not have the expected structure.");
        }

        int packageCount = packages.GetArrayLength();
        if (packageCount < MinimumPackageCount)
            throw new InvalidOperationException("The temporary catalog does not contain enough real packages.");

        if (!metadata.TryGetProperty("packageCount", out JsonElement metadataCount) ||
            metadataCount.GetInt32() != packageCount)
        {
            throw new InvalidOperationException("The package count in metadata does not match the catalog contents.");
        }

        int checkedPackages = 0;
        foreach (JsonElement package in packages.EnumerateArray())
        {
            if (checkedPackages++ >= 100)
                break;

            if (IsMissingOrWhitespace(package, "id") ||
                IsMissingOrWhitespace(package, "name") ||
                IsMissingOrWhitespace(package, "version"))
            {
                throw new InvalidOperationException("The catalog contains a package without a required ID, name or version.");
            }
        }
    }

    private static bool IsMissingOrWhitespace(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            return true;
        return string.IsNullOrWhiteSpace(property.GetString());
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

    private static string? NullableStringValue(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out object? value) ? value as string : null;

    private static string[] StringArrayValue(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out object? value) && value is string[] strings ? strings : [];

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try { DeleteIfExists(path); }
        catch { }
    }

    private sealed record SelectedVersion(string Version, string DefaultLocale, string Directory);

    private sealed class CatalogDocument
    {
        public required CatalogMetadata Metadata { get; init; }
        public required List<CatalogPackage> Packages { get; init; }
    }

    private sealed class CatalogMetadata
    {
        public int SchemaVersion { get; init; }
        public required string GeneratedAtUtc { get; init; }
        public required string Source { get; init; }
        public required string SourceBranch { get; init; }
        public int PackageCount { get; init; }
    }

    private sealed class CatalogPackage
    {
        public required string Id { get; init; }
        public string? Name { get; init; }
        public required string Version { get; init; }
        public string? Publisher { get; init; }
        public string? ShortDescription { get; init; }
        public string? Description { get; init; }
        public string? Moniker { get; init; }
        public required string[] Tags { get; init; }
        public string? License { get; init; }
        public string? LicenseUrl { get; init; }
        public string? Homepage { get; init; }
        public string? PackageUrl { get; init; }
        public string? PublisherUrl { get; init; }
        public string? PublisherSupportUrl { get; init; }
        public string? PrivacyUrl { get; init; }
        public string? Author { get; init; }
        public string? Copyright { get; init; }
        public string? CopyrightUrl { get; init; }
        public string? PurchaseUrl { get; init; }
        public string? ReleaseNotesUrl { get; init; }
        public string? Locale { get; init; }
    }
}
