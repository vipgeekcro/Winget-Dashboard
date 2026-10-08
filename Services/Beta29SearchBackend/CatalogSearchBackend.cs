using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Beta29.SearchBackend.Compatibility;

namespace Beta29.SearchBackend;

/// <summary>One process-wide catalog, as in Beta 29's Search process. Not thread-safe.</summary>
public static class CatalogSearchBackend
{
    private static FastSearchEntry[] searchIndex = Array.Empty<FastSearchEntry>();
    public static IReadOnlyList<FastSearchEntry> Entries => Array.AsReadOnly(searchIndex);
    public static CatalogLoadTiming LastLoadTiming { get; private set; }
    public static SearchTiming LastSearchTiming { get; private set; }
    public static IReadOnlyList<string> LastLoadErrors { get; private set; } = Array.Empty<string>();

    // Native Windows powershell.exe hosts normally have no >=4.5 AppDomain target.
    // The oracle records the actual Framework compatibility flag. Set this only
    // when matching a host that opts into Framework 4.5 sorting. It changes fully
    // equal-key permutations, never the three-key comparison or the score.
    public static bool ReferenceHostTargetsFramework45 { get; set; }

    /// <summary>START: read the existing Catalog/catalog.json; then prepare all entries and the index.</summary>
    public static void LoadCatalog(string catalogPath)
    {
        RuntimeCompatibility.RequireWindowsNls();
        if (!File.Exists(catalogPath)) throw new FileNotFoundException("Catalog/catalog.json was not found.", catalogPath);
        var total = Stopwatch.StartNew();
        var read = Stopwatch.StartNew();
        // StreamReader uses the same UTF-8 replacement fallback and BOM detection
        // as Get-Content -Raw -Encoding UTF8; line endings are not rewritten.
        string raw;
        using (var reader = new StreamReader(catalogPath, new UTF8Encoding(false, false), true))
            raw = reader.ReadToEnd();
        read.Stop();
        var parse = Stopwatch.StartNew();
        object catalog = CatalogObject.Materialize(FrameworkJsonReader.BasicDeserialize(raw, 102));
        object packages = CatalogObject.GetMember(catalog, "packages");
        if (catalog == null || packages == null) throw new InvalidDataException("The catalog does not have the expected structure.");
        parse.Stop();
        raw = null;

        var build = Stopwatch.StartNew();
        int packageCount;
        PrepareCatalog(packages, out packageCount);
        build.Stop();
        total.Stop();
        LastLoadTiming = new CatalogLoadTiming(read.ElapsedMilliseconds, parse.ElapsedMilliseconds,
            build.ElapsedMilliseconds, total.ElapsedMilliseconds, packageCount);
        if (searchIndex.Length == 0) throw new InvalidDataException("The catalog contains no searchable entries.");
    }

    /// <summary>BuildEntry for each non-null package, in original order, followed by PrepareIndex.</summary>
    private static void PrepareCatalog(object packages, out int packageCount)
    {
        var entries = new List<FastSearchEntry>();
        var errors = new List<string>();
        packageCount = 0;
        foreach (object package in CatalogObject.Enumerate(packages))
        {
            packageCount++;
            if (package == null) continue;
            try
            {
                entries.Add(FastSearchEngine.BuildEntry(package,
                    CatalogObject.GetMember(package, "name"),
                    CatalogObject.GetMember(package, "id"),
                    CatalogObject.GetMember(package, "version"),
                    CatalogObject.GetMember(package, "publisher"),
                    CatalogObject.GetMember(package, "shortDescription"),
                    CatalogObject.GetMember(package, "moniker"),
                    CatalogObject.GetMember(package, "description"),
                    CatalogObject.GetMember(package, "tags")));
            }
            catch (ArgumentException exception)
            {
                // In the original worker a failing static method invocation is
                // a non-terminating PowerShell error; subsequent packages still load.
                errors.Add("Package " + (packageCount - 1) + ": " + exception.Message);
            }
        }
        FastSearchEngine.PrepareIndex(entries);
        searchIndex = entries.ToArray();
        LastLoadErrors = errors.AsReadOnly();
    }

    /// <summary>END: exact counterpart of Search-Catalog, including final sorting and First.</summary>
    public static List<FastScoredEntry> SearchCatalog(string query, int maxResults = 100)
    {
        RuntimeCompatibility.RequireWindowsNls();
        var total = Stopwatch.StartNew();
        var prep = Stopwatch.StartNew();
        string q = FastSearchEngine.NormalizeText(query);
        // Beta 29 returns before changing LastSearchTiming for an empty normalized query.
        if (string.IsNullOrEmpty(q)) return new List<FastScoredEntry>();
        string[] words = GetQueryWords(q);
        prep.Stop();

        var score = Stopwatch.StartNew();
        List<FastScoredEntry> ranked = FastSearchEngine.ScoreAll(searchIndex, q, words);
        score.Stop();
        var sort = Stopwatch.StartNew();
        // Select-Object validates First after query preparation/scoring in the original.
        if (maxResults < 0) throw new ArgumentOutOfRangeException(nameof(maxResults));
        var matrix = new List<FastScoredEntry>();
        foreach (FastScoredEntry entry in ranked) matrix.Add(entry);
        var backing = new FastScoredEntry[matrix.Capacity];
        matrix.CopyTo(backing);
        FrameworkSort<FastScoredEntry>.Sort(backing, matrix.Count, ReferenceHostTargetsFramework45,
            new FinalComparer(CultureInfo.CurrentCulture));
        int count = Math.Min(maxResults, matrix.Count);
        var final = new List<FastScoredEntry>();
        for (int i = 0; i < count; i++) final.Add(backing[i]);
        sort.Stop();
        total.Stop();
        LastSearchTiming = new SearchTiming(prep.ElapsedMilliseconds, score.ElapsedMilliseconds,
            sort.ElapsedMilliseconds, total.ElapsedMilliseconds, searchIndex.Length, ranked.Count, final.Count);
        return final;
    }

    public static string ConvertToSearchText(object value) => FastSearchEngine.NormalizeText(value);

    private static string[] GetQueryWords(string normalizedText)
    {
        var words = new List<string>();
        foreach (string word in normalizedText.Split(' '))
            if (word.Length > 0 && !words.Contains(word)) words.Add(word);
        return words.ToArray(); // ScoreAll intentionally ignores this argument in Beta 29.
    }

    private sealed class FinalComparer : IComparer<FastScoredEntry>
    {
        private readonly CompareInfo comparison;
        internal FinalComparer(CultureInfo culture) { comparison = culture.CompareInfo; }
        public int Compare(FastScoredEntry left, FastScoredEntry right)
        {
            int score = right.Score.CompareTo(left.Score);
            if (score != 0) return score;
            int name = comparison.Compare(left.Entry.Name, right.Entry.Name, CompareOptions.IgnoreCase);
            return name != 0 ? name : comparison.Compare(left.Entry.Id, right.Entry.Id, CompareOptions.IgnoreCase);
        }
    }
}

public sealed record CatalogLoadTiming(long ReadMs, long JsonMs, long BuildMs, long TotalMs, int PackageCount);
public sealed record SearchTiming(long QueryPrepMs, long ScoringMs, long SortMs, long TotalMs,
    int Packages, int PositiveMatches, int Returned);
