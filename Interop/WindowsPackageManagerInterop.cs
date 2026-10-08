using Microsoft.Management.Deployment;
using System.Runtime.InteropServices;
using WinRT;

namespace WindowsPackageManager.Interop;

internal static class WindowsPackageManagerClasses
{
    internal static (Guid Clsid, Guid Iid) Get<T>()
    {
        if (typeof(T) == typeof(PackageManager))
            return (new("C53A4F16-787E-42A4-B304-29EFFB4BF597"), typeof(IPackageManager).GUID);
        if (typeof(T) == typeof(FindPackagesOptions))
            return (new("572DED96-9C60-4526-8F92-EE7D91D38C1A"), typeof(IFindPackagesOptions).GUID);
        if (typeof(T) == typeof(InstallOptions))
            return (new("1095F097-EB96-453B-B4E6-1613637F3B14"), typeof(IInstallOptions).GUID);
        if (typeof(T) == typeof(PackageMatchFilter))
            return (new("D02C9DAF-99DC-429C-B503-4E504E4AB000"), typeof(IPackageMatchFilter).GUID);
        throw new InvalidOperationException($"Unsupported WinGet COM type: {typeof(T).FullName}");
    }
}

internal sealed class WindowsPackageManagerStandardFactory
{
    private readonly bool _allowLowerTrustRegistration;
    internal WindowsPackageManagerStandardFactory(bool allowLowerTrustRegistration = false) => _allowLowerTrustRegistration = allowLowerTrustRegistration;
    internal PackageManager CreatePackageManager() => CreateInstance<PackageManager>();
    internal FindPackagesOptions CreateFindPackagesOptions() => CreateInstance<FindPackagesOptions>();
    internal InstallOptions CreateInstallOptions() => CreateInstance<InstallOptions>();
    internal PackageMatchFilter CreatePackageMatchFilter() => CreateInstance<PackageMatchFilter>();

    private T CreateInstance<T>()
    {
        (Guid clsid, Guid iid) = WindowsPackageManagerClasses.Get<T>();
        uint context = 0x4; // CLSCTX_LOCAL_SERVER
        if (_allowLowerTrustRegistration) context |= 0x04000000; // CLSCTX_ALLOW_LOWER_TRUST_REGISTRATION
        int hr = CoCreateInstance(in clsid, IntPtr.Zero, context, in iid, out IntPtr instance);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { return MarshalGeneric<T>.FromAbi(instance); }
        finally { if (instance != IntPtr.Zero) Marshal.Release(instance); }
    }

    [DllImport("api-ms-win-core-com-l1-1-0.dll", EntryPoint = "CoCreateInstance", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);
}
