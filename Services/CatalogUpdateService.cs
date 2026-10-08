using System;
using System.IO;
using System.Threading.Tasks;

namespace WingetDashboard.Services;

internal sealed class CatalogUpdateService
{
    private readonly CatalogService _catalogService;
    private readonly CatalogBuilderService _catalogBuilderService;

    public CatalogUpdateService(CatalogService catalogService, CatalogBuilderService catalogBuilderService)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _catalogBuilderService = catalogBuilderService ?? throw new ArgumentNullException(nameof(catalogBuilderService));
    }

    public async Task UpdateCatalogAsync()
    {
        string backupPath = _catalogService.CatalogPath + ".update-backup";
        bool hadExistingCatalog = File.Exists(_catalogService.CatalogPath);

        if (File.Exists(backupPath))
            File.Delete(backupPath);
        if (hadExistingCatalog)
            File.Copy(_catalogService.CatalogPath, backupPath);

        try
        {
            await _catalogBuilderService.BuildCatalogAsync(_catalogService.CatalogPath);

            CatalogStatus finalStatus = await _catalogService.GetStatusAsync();
            if (finalStatus != CatalogStatus.Available)
                throw new InvalidOperationException("The updated catalog was created, but validation failed.");

            await _catalogService.LoadAndPrepareAsync(forceReload: true);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch
        {
            if (hadExistingCatalog && File.Exists(backupPath))
            {
                File.Copy(backupPath, _catalogService.CatalogPath, overwrite: true);
                File.Delete(backupPath);
                await _catalogService.LoadAndPrepareAsync(forceReload: true);
            }
            throw;
        }
    }
}
