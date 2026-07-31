using System.IO;
using System.Text.Json;

namespace UISI_Doc_Constructor.Services;

public sealed record UpdateConfiguration(
    string Source,
    string Channel,
    bool IncludePrereleases,
    string? AccessToken)
{
    private const string DefaultGithubRepository =
        "https://github.com/KBjerg598/UISI-Dcuments-Constructor";

    public bool IsGithub =>
        Uri.TryCreate(Source, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalSource => !Uri.TryCreate(Source, UriKind.Absolute, out var uri) || uri.IsFile;

    public string DisplaySource => IsGithub
        ? "GitHub Releases"
        : IsLocalSource
            ? "Локальная тестовая папка"
            : "Сервер обновлений";

    public string DisplayChannel => string.Equals(Channel, "stable", StringComparison.OrdinalIgnoreCase)
        ? "Стабильный"
        : "Тестовый";

    public static UpdateConfiguration Load()
    {
        var fileSettings = LoadBundledSettings();
        var source = GetEnvironmentValue("UISI_UPDATE_SOURCE")
                     ?? fileSettings?.Source
                     ?? DefaultGithubRepository;
        var channel = GetEnvironmentValue("UISI_UPDATE_CHANNEL")
                      ?? fileSettings?.Channel
                      ?? "test";
        var includePrereleases = fileSettings?.IncludePrereleases
                                 ?? !string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase);

        if (bool.TryParse(GetEnvironmentValue("UISI_UPDATE_PRERELEASES"), out var configuredPrereleases))
        {
            includePrereleases = configuredPrereleases;
        }

        return new UpdateConfiguration(
            NormalizeSource(source),
            NormalizeChannel(channel),
            includePrereleases,
            GetEnvironmentValue("UISI_GITHUB_TOKEN"));
    }

    private static BundledUpdateSettings? LoadBundledSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "update-config.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(settingsPath);
            return JsonSerializer.Deserialize<BundledUpdateSettings>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizeSource(string source)
    {
        var trimmed = source.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return trimmed;
        }

        return Path.GetFullPath(trimmed, AppContext.BaseDirectory);
    }

    private static string NormalizeChannel(string channel)
    {
        var normalized = channel.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "test" : normalized;
    }

    private static string? GetEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class BundledUpdateSettings
    {
        public string? Source { get; init; }

        public string? Channel { get; init; }

        public bool? IncludePrereleases { get; init; }
    }
}
