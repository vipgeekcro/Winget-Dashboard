using System;
using System.IO;
using System.Text.Json;
using Beta29.SearchBackend;
using System.Threading;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

public enum CatalogStatus
{
    Available,
    Missing,
    Invalid
}

/// <summary>
/// Central catalog service. Besides validating catalog.json, it owns the one prepared
/// in-memory catalog/index used for the lifetime of the application.
/// </summary>
public sealed class CatalogService
{
    private const int MinimumPackageCount = 1000;
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    private readonly object _loadLock = new();
    private int _loadGeneration;
    private Task? _loadTask;

    public string CatalogPath { get; }
    public CatalogLoadState LoadState { get; private set; } = CatalogLoadState.NotLoaded;

    public CatalogService(string? applicationDirectory = null)
    {
        CatalogPath = applicationDirectory is null
            ? AppPaths.CatalogPath
            : Path.Combine(applicationDirectory, "Catalog", "catalog.json");
    }

    public Task<CatalogStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetStatus(cancellationToken), cancellationToken);

    /// <summary>
    /// Starts the Beta 29 catalog preparation pipeline once. Concurrent callers receive
    /// the same task instead of causing another file read/index build. Forced reloads
    /// are queued; only the latest requested load determines this service's state.
    /// </summary>
    public Task LoadAndPrepareAsync(bool forceReload = false, CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            if (!forceReload && _loadTask is not null)
                return _loadTask;

            LoadState = CatalogLoadState.Loading;
            Task? previousLoad = _loadTask;
            int generation = ++_loadGeneration;
            _loadTask = Task.Run(() => LoadAndPrepareAsync(previousLoad, generation, cancellationToken));
            return _loadTask;
        }
    }

    public void RemoveInvalidCatalog()
    {
        if (File.Exists(CatalogPath))
            File.Delete(CatalogPath);
    }

    private async Task LoadAndPrepareAsync(Task? previousLoad, int generation, CancellationToken cancellationToken)
    {
        // A failed previous request must not prevent an explicitly requested reload.
        if (previousLoad is not null)
        {
            try { await previousLoad.ConfigureAwait(false); } catch { }
        }

        try
        {
            // The backend owns a process-wide index, so serialize preparation across
            // service instances as well as across forced reloads of this instance.
            await PreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CatalogSearchBackend.LoadCatalog(CatalogPath);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                PreparationGate.Release();
            }

            lock (_loadLock)
            {
                if (generation == _loadGeneration)
                    LoadState = CatalogLoadState.Ready;
            }
        }
        catch
        {
            lock (_loadLock)
            {
                if (generation == _loadGeneration)
                    LoadState = CatalogLoadState.Failed;
            }
            throw;
        }
    }

    private CatalogStatus GetStatus(CancellationToken cancellationToken)
    {
        if (!File.Exists(CatalogPath))
            return CatalogStatus.Missing;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using FileStream stream = new(
                CatalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            using JsonDocument parsed = JsonDocument.Parse(stream);
            JsonElement root = parsed.RootElement;

            if (!root.TryGetProperty("metadata", out JsonElement metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("packages", out JsonElement packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                return CatalogStatus.Invalid;
            }

            int packageCount = packages.GetArrayLength();
            if (packageCount < MinimumPackageCount)
                return CatalogStatus.Invalid;

            if (!metadata.TryGetProperty("packageCount", out JsonElement metadataCount) ||
                metadataCount.ValueKind != JsonValueKind.Number ||
                !metadataCount.TryGetInt32(out int declaredCount) ||
                declaredCount != packageCount)
            {
                return CatalogStatus.Invalid;
            }

            int checkedPackages = 0;
            foreach (JsonElement package in packages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedPackages++ >= 100)
                    break;

                if (IsMissingOrWhitespace(package, "id") ||
                    IsMissingOrWhitespace(package, "name") ||
                    IsMissingOrWhitespace(package, "version"))
                {
                    return CatalogStatus.Invalid;
                }
            }

            return CatalogStatus.Available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CatalogStatus.Invalid;
        }
    }

    private static bool IsMissingOrWhitespace(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(property.GetString());
    }
}
