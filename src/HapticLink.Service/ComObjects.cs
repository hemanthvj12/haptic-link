namespace HapticLink.Service;

/// <summary>
/// Creates Windows COM objects without a [ComImport] class type. The sound device enumerator is one shared object per
/// process: .NET wraps it once and reuses that wrapper, typed as whichever class created it first, so two [ComImport]
/// classes for it clash ("Unable to cast 'MMDeviceEnumerator' to 'MMDeviceEnumerator'", seen in PeripheralHub). Created
/// this way the wrapper is a plain COM object, cast only to interfaces.
/// </summary>
internal static class ComObjects
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    /// <summary>The undocumented policy client behind Windows' sound settings (default device, endpoint visibility).</summary>
    public static readonly Guid PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    public static object Create(Guid clsid) => Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
}
