using Microsoft.Management.Deployment;
using WindowsPackageManager.Interop;

namespace WingetDashboard.Interop;

internal enum InstallElevationMode
{
    Normal,
    Elevated,
    Prohibited,
    Unknown
}

internal sealed record InstallElevationDecision(
    InstallElevationMode Mode,
    string Reason,
    string? ElevationRequirement,
    string? Scope);

internal static class WingetNativeMetadataService
{
    internal static InstallElevationDecision GetDecision(string packageId)
    {
        try
        {
            WindowsPackageManagerStandardFactory factory;
            PackageManager manager;
            try
            {
                factory = new WindowsPackageManagerStandardFactory();
                manager = factory.CreatePackageManager();
            }
            catch
            {
                // Match UniGetUI's second activation attempt for newer WinGet registrations.
                factory = new WindowsPackageManagerStandardFactory(allowLowerTrustRegistration: true);
                manager = factory.CreatePackageManager();
            }
            PackageCatalogReference catalog = manager.GetPackageCatalogByName("winget");
            if (catalog is null)
                return Unknown("WinGet native API did not return the winget catalog.");

            catalog.AcceptSourceAgreements = true;
            ConnectResult connection = catalog.Connect();
            if (connection.Status != ConnectResultStatus.Ok)
                return Unknown($"WinGet native catalog connection failed: {connection.Status}.");

            FindPackagesOptions findOptions = factory.CreateFindPackagesOptions();
            PackageMatchFilter filter = factory.CreatePackageMatchFilter();
            filter.Field = PackageMatchField.Id;
            filter.Value = packageId;
            filter.Option = PackageFieldMatchOption.Equals;
            findOptions.Filters.Add(filter);
            findOptions.ResultLimit = 1;

            FindPackagesResult result = connection.PackageCatalog.FindPackages(findOptions);
            if (result.Matches is null || result.Matches.Count == 0)
                return Unknown("WinGet native API could not find the exact package ID.");

            CatalogPackage package = result.Matches[0].CatalogPackage;
            PackageVersionInfo version = package.DefaultInstallVersion;
            if (version is null)
                return Unknown("WinGet native API returned no default install version.");

            InstallOptions options = factory.CreateInstallOptions();
            PackageInstallerInfo? installer = version.GetApplicableInstaller(options);
            if (installer is null)
                return Unknown("WinGet native API could not select an applicable installer.");

            string elevation = installer.ElevationRequirement.ToString();
            string scope = installer.Scope.ToString();

            if (installer.ElevationRequirement == ElevationRequirement.ElevationProhibited)
                return new(InstallElevationMode.Prohibited,
                    "The applicable WinGet installer explicitly prohibits elevation.", elevation, scope);

            if (installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf)
                return new(InstallElevationMode.Elevated,
                    $"The applicable WinGet installer reports {installer.ElevationRequirement}.", elevation, scope);

            if (installer.Scope == PackageInstallerScope.System)
                return new(InstallElevationMode.Elevated,
                    "The applicable WinGet installer has system scope.", elevation, scope);

            return new(InstallElevationMode.Normal,
                "The applicable WinGet installer does not require elevation.", elevation, scope);
        }
        catch (Exception ex)
        {
            return Unknown($"WinGet native metadata lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static InstallElevationDecision Unknown(string reason) =>
        new(InstallElevationMode.Unknown, reason, null, null);
}
