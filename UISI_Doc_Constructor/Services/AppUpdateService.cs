using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace UISI_Doc_Constructor.Services;

public enum AppUpdateState
{
    Ready,
    DevelopmentBuild,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Applying,
    Error
}

public sealed record AppUpdateStatus(
    AppUpdateState State,
    string Title,
    string Description,
    string? AvailableVersion = null,
    long? DownloadSize = null,
    string? ReleaseNotes = null);

public sealed class AppUpdateService
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly UpdateManager _manager;
    private UpdateInfo? _availableUpdate;

    public AppUpdateService()
    {
        Configuration = UpdateConfiguration.Load();

        var options = new UpdateOptions
        {
            ExplicitChannel = Configuration.Channel
        };

        _manager = Configuration.IsGithub
            ? new UpdateManager(
                new GithubSource(
                    Configuration.Source,
                    Configuration.AccessToken,
                    Configuration.IncludePrereleases),
                options)
            : new UpdateManager(Configuration.Source, options);

        LastStatus = CreateInitialStatus();
    }

    public UpdateConfiguration Configuration { get; }

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ?? GetAssemblyVersion();

    public bool IsInstalled => _manager.IsInstalled;

    public AppUpdateStatus LastStatus { get; private set; }

    public event Action<AppUpdateStatus>? StatusChanged;

    public bool ShouldCheckAutomatically()
    {
        try
        {
            var path = GetLastCheckPath();
            if (!File.Exists(path))
            {
                return true;
            }

            var value = File.ReadAllText(path).Trim();
            return !DateTimeOffset.TryParse(value, out var lastCheck) ||
                   DateTimeOffset.UtcNow - lastCheck.ToUniversalTime() >= AutomaticCheckInterval;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void RecordAutomaticCheck()
    {
        try
        {
            var path = GetLastCheckPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // A failed timestamp write must never block the application.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed timestamp write must never block the application.
        }
    }

    public async Task<AppUpdateStatus> CheckForUpdatesAsync()
    {
        if (!_manager.IsInstalled)
        {
            return SetStatus(new AppUpdateStatus(
                AppUpdateState.DevelopmentBuild,
                "Запущена версия для разработки",
                "Проверка обновлений станет доступна после установки программы через UISI Setup.exe."));
        }

        await _operationLock.WaitAsync();
        try
        {
            var pendingRelease = _manager.UpdatePendingRestart;
            if (pendingRelease is not null)
            {
                return SetStatus(new AppUpdateStatus(
                    AppUpdateState.ReadyToInstall,
                    $"Версия {pendingRelease.Version} готова",
                    "Обновление уже загружено. Перезапустите программу, чтобы завершить установку.",
                    pendingRelease.Version.ToString(),
                    pendingRelease.Size,
                    pendingRelease.NotesMarkdown));
            }

            SetStatus(new AppUpdateStatus(
                AppUpdateState.Checking,
                "Проверяем GitHub Releases",
                "Ищем новую версию в выбранном канале обновлений."));

            _availableUpdate = await _manager.CheckForUpdatesAsync();
            if (_availableUpdate is null)
            {
                return SetStatus(new AppUpdateStatus(
                    AppUpdateState.UpToDate,
                    "Установлена актуальная версия",
                    $"Версия {CurrentVersion} — новее обновлений в канале или совпадает с ними."));
            }

            var release = _availableUpdate.TargetFullRelease;
            return SetStatus(new AppUpdateStatus(
                AppUpdateState.Available,
                $"Доступна версия {release.Version}",
                "Можно загрузить обновление сейчас и установить его после безопасного перезапуска.",
                release.Version.ToString(),
                release.Size,
                release.NotesMarkdown));
        }
        catch (Exception exception)
        {
            return SetStatus(new AppUpdateStatus(
                AppUpdateState.Error,
                "Не удалось проверить обновления",
                GetFriendlyError(exception)));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<AppUpdateStatus> DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_availableUpdate is null)
        {
            return await CheckForUpdatesAsync();
        }

        await _operationLock.WaitAsync();
        try
        {
            var release = _availableUpdate.TargetFullRelease;
            SetStatus(new AppUpdateStatus(
                AppUpdateState.Downloading,
                $"Загружаем версию {release.Version}",
                "Программу можно продолжать использовать, пока пакет загружается.",
                release.Version.ToString(),
                release.Size,
                release.NotesMarkdown));

            await _manager.DownloadUpdatesAsync(
                _availableUpdate,
                value => progress?.Report(value));

            return SetStatus(new AppUpdateStatus(
                AppUpdateState.ReadyToInstall,
                $"Версия {release.Version} готова",
                "Все файлы проверены. Перезапустите программу, чтобы завершить обновление.",
                release.Version.ToString(),
                release.Size,
                release.NotesMarkdown));
        }
        catch (Exception exception)
        {
            return SetStatus(new AppUpdateStatus(
                AppUpdateState.Error,
                "Не удалось загрузить обновление",
                GetFriendlyError(exception)));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
        var release = _manager.UpdatePendingRestart ?? _availableUpdate?.TargetFullRelease;
        if (release is null)
        {
            throw new InvalidOperationException("Сначала загрузите обновление.");
        }

        SetStatus(new AppUpdateStatus(
            AppUpdateState.Applying,
            "Устанавливаем обновление",
            "Программа сейчас перезапустится.",
            release.Version.ToString(),
            release.Size,
            release.NotesMarkdown));

        _manager.ApplyUpdatesAndRestart(release);
    }

    private AppUpdateStatus CreateInitialStatus()
    {
        if (!_manager.IsInstalled)
        {
            return new AppUpdateStatus(
                AppUpdateState.DevelopmentBuild,
                "Запущена версия для разработки",
                "Установщик и обновления готовы к тестированию после сборки релиза.");
        }

        var pendingRelease = _manager.UpdatePendingRestart;
        return pendingRelease is null
            ? new AppUpdateStatus(
                AppUpdateState.Ready,
                "Готово к проверке",
                "Нажмите кнопку, чтобы проверить новые выпуски на GitHub.")
            : new AppUpdateStatus(
                AppUpdateState.ReadyToInstall,
                $"Версия {pendingRelease.Version} готова",
                "Перезапустите программу, чтобы завершить обновление.",
                pendingRelease.Version.ToString(),
                pendingRelease.Size,
                pendingRelease.NotesMarkdown);
    }

    private AppUpdateStatus SetStatus(AppUpdateStatus status)
    {
        LastStatus = status;
        StatusChanged?.Invoke(status);
        return status;
    }

    private static string GetLastCheckPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UISI Doc Constructor",
            "Updater",
            "last-check.utc");

    private static string GetAssemblyVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null)
        {
            return "неизвестна";
        }

        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private static string GetFriendlyError(Exception exception)
    {
        var message = exception.GetBaseException().Message.Trim();
        if (message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub не открыл список выпусков. Проверьте, что репозиторий обновлений публичный и в нём создан Release.";
        }

        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub временно ограничил число запросов. Подождите несколько минут и повторите проверку.";
        }

        return "Проверьте подключение к интернету и повторите попытку. Техническая причина: " + message;
    }
}
