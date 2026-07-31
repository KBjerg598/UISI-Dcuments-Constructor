using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace UISI_Doc_Constructor.Services;

public static class OfficeInteropAssemblyResolver
{
    private const string OfficeAssemblyName = "office";
    private const string MicrosoftOfficePublicKeyToken = "71e9bce111e9429c";

    private static readonly object InitializationLock = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (InitializationLock)
        {
            if (_initialized)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += ResolveOfficeAssembly;
            _initialized = true;
        }
    }

    public static bool IsWordInstalled()
    {
        try
        {
            return Type.GetTypeFromProgID("Word.Application", throwOnError: false) is not null;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static Assembly? ResolveOfficeAssembly(
        AssemblyLoadContext context,
        AssemblyName requestedAssembly)
    {
        if (!string.Equals(
                requestedAssembly.Name,
                OfficeAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var candidatePath in EnumerateCandidatePaths().Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!IsTrustedOfficeAssembly(candidatePath, requestedAssembly))
            {
                continue;
            }

            try
            {
                return context.LoadFromAssemblyPath(Path.GetFullPath(candidatePath));
            }
            catch (Exception exception) when (
                exception is FileLoadException or
                FileNotFoundException or
                BadImageFormatException)
            {
                // Проверяем следующий известный путь Office.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "office.dll");

        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var path in EnumerateGacCandidates(
                     Path.Combine(windowsPath, "assembly", "GAC_MSIL", OfficeAssemblyName)))
        {
            yield return path;
        }

        foreach (var path in EnumerateGacCandidates(
                     Path.Combine(
                         windowsPath,
                         "Microsoft.NET",
                         "assembly",
                         "GAC_MSIL",
                         OfficeAssemblyName)))
        {
            yield return path;
        }

        foreach (var installationPath in ReadOfficeInstallationPaths())
        {
            foreach (var path in BuildOfficeFolderCandidates(installationPath))
            {
                yield return path;
            }
        }

        var programFilesFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var programFiles in programFilesFolders.Where(
                     path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (var path in BuildOfficeFolderCandidates(
                         Path.Combine(programFiles, "Microsoft Office")))
            {
                yield return path;
            }

            yield return Path.Combine(
                programFiles,
                "Microsoft Visual Studio",
                "Shared",
                "Visual Studio Tools for Office",
                "PIA",
                "Office15",
                "office.dll");
        }
    }

    private static IEnumerable<string> EnumerateGacCandidates(string gacOfficePath)
    {
        if (!Directory.Exists(gacOfficePath))
        {
            yield break;
        }

        IEnumerable<string> versionFolders;
        try
        {
            versionFolders = Directory.EnumerateDirectories(gacOfficePath).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var versionFolder in versionFolders)
        {
            yield return Path.Combine(versionFolder, "office.dll");
            yield return Path.Combine(versionFolder, "OFFICE.DLL");
        }
    }

    private static IEnumerable<string> BuildOfficeFolderCandidates(string officePath)
    {
        yield return Path.Combine(officePath, "office.dll");
        yield return Path.Combine(officePath, "root", "Office16", "office.dll");
        yield return Path.Combine(officePath, "Office16", "office.dll");
        yield return Path.Combine(officePath, "root", "Office15", "office.dll");
        yield return Path.Combine(officePath, "Office15", "office.dll");
    }

    private static IEnumerable<string> ReadOfficeInstallationPaths()
    {
        var registryKeys = new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Office\ClickToRun\Configuration",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\Configuration"
        };

        foreach (var registryKey in registryKeys)
        {
            string? installationPath = null;

            try
            {
                installationPath = Registry.GetValue(
                    registryKey,
                    "InstallationPath",
                    null) as string;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                // Остальные стандартные пути всё ещё будут проверены.
            }

            if (!string.IsNullOrWhiteSpace(installationPath))
            {
                yield return installationPath;
            }
        }
    }

    private static bool IsTrustedOfficeAssembly(
        string candidatePath,
        AssemblyName requestedAssembly)
    {
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        try
        {
            var candidateAssembly = AssemblyName.GetAssemblyName(candidatePath);
            var publicKeyToken = candidateAssembly.GetPublicKeyToken();
            var publicKeyTokenText = publicKeyToken is null
                ? string.Empty
                : Convert.ToHexString(publicKeyToken).ToLowerInvariant();

            return string.Equals(
                       candidateAssembly.Name,
                       OfficeAssemblyName,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       publicKeyTokenText,
                       MicrosoftOfficePublicKeyToken,
                       StringComparison.OrdinalIgnoreCase) &&
                   IsCompatibleVersion(candidateAssembly.Version, requestedAssembly.Version);
        }
        catch (Exception exception) when (
            exception is FileLoadException or
            FileNotFoundException or
            BadImageFormatException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCompatibleVersion(Version? candidate, Version? requested)
    {
        if (candidate is null || requested is null)
        {
            return true;
        }

        return candidate.Major == requested.Major && candidate >= requested;
    }
}
