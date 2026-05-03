using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UtpStream.Internal;

/// <summary>
/// Resolves <c>utp</c> across the layouts we ship in:
/// <list type="bullet">
///   <item>NuGet consumer: file is delivered under <c>runtimes/&lt;rid&gt;/native/</c>
///         and the default resolver finds it.</item>
///   <item>Project reference (dev/tests): the file is copied to
///         <c>bin/.../runtimes/&lt;rid&gt;/native/</c> but the default
///         resolver doesn't probe there — we do it manually.</item>
///   <item><c>dotnet publish -r &lt;rid&gt;</c>: file is flattened next to
///         the assembly; default resolver covers it.</item>
/// </list>
/// </summary>
internal static class NativeLibraryResolver
{
    // CA2255: a module initializer is exactly what we want here — the
    // resolver must be installed before any P/Invoke into libutp, and we
    // don't want consumers to call an init method.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibUtp).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibUtp.Library) return 0;

        string fileName = NativeFileName();
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(asmDir)) return 0;

        // Probe RIDs in order of specificity. SDK-reported RID may be
        // distro-specific (e.g. "ubuntu.24.04-x64") while we ship under
        // the portable RID ("linux-x64"), so we always also try that.
        foreach (var rid in CandidateRids())
        {
            var path = Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h))
                return h;
        }
        return 0;
    }

    private static IEnumerable<string> CandidateRids()
    {
        yield return RuntimeInformation.RuntimeIdentifier;
        yield return PortableRid();
    }

    private static string PortableRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
                  : OperatingSystem.IsMacOS() ? "osx"
                  : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private static string NativeFileName()
    {
        if (OperatingSystem.IsWindows()) return "utp.dll";
        if (OperatingSystem.IsMacOS()) return "libutp.dylib";
        return "libutp.so";
    }
}
