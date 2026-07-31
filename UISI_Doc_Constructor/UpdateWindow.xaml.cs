using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class UpdateWindow : Window
{
    private readonly AppUpdateService _updateService;
    private bool _isBusy;

    public UpdateWindow(AppUpdateService updateService)
    {
        _updateService = updateService;
        InitializeComponent();

        CurrentVersionText.Text = _updateService.CurrentVersion;
        ChannelText.Text = _updateService.Configuration.DisplayChannel;
        SourceBadgeText.Text = _updateService.Configuration.DisplaySource;
        _updateService.StatusChanged += UpdateService_StatusChanged;
        RenderStatus(_updateService.LastStatus);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_updateService.IsInstalled &&
            _updateService.LastStatus.State is AppUpdateState.Ready or AppUpdateState.Error)
        {
            await CheckForUpdatesAsync();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _updateService.StatusChanged -= UpdateService_StatusChanged;
    }

    private void UpdateService_StatusChanged(AppUpdateStatus status)
    {
        if (Dispatcher.CheckAccess())
        {
            RenderStatus(status);
            return;
        }

        Dispatcher.Invoke(() => RenderStatus(status));
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_updateService.LastStatus.State)
        {
            case AppUpdateState.Available:
                await DownloadUpdateAsync();
                break;

            case AppUpdateState.ReadyToInstall:
                ApplyUpdateAndRestart();
                break;

            default:
                await CheckForUpdatesAsync();
                break;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        RenderStatus(new AppUpdateStatus(
            AppUpdateState.Checking,
            "Проверяем GitHub Releases",
            "Это займёт всего несколько секунд."));

        var status = await _updateService.CheckForUpdatesAsync();
        SetBusy(false);
        RenderStatus(status);
    }

    private async Task DownloadUpdateAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;

        var progress = new Progress<int>(value =>
        {
            UpdateProgressBar.Value = value;
            ProgressValueText.Text = $"{value}%";
        });

        RenderStatus(new AppUpdateStatus(
            AppUpdateState.Downloading,
            "Загружаем обновление",
            "Можно оставить это окно открытым — установка начнётся только по вашей команде.",
            _updateService.LastStatus.AvailableVersion,
            _updateService.LastStatus.DownloadSize,
            _updateService.LastStatus.ReleaseNotes));

        var status = await _updateService.DownloadUpdateAsync(progress);
        SetBusy(false);
        RenderStatus(status);
    }

    private void ApplyUpdateAndRestart()
    {
        SetBusy(true);
        RenderStatus(new AppUpdateStatus(
            AppUpdateState.Applying,
            "Закрываем документы и обновляем программу",
            "UISI автоматически откроется снова через несколько секунд.",
            _updateService.LastStatus.AvailableVersion,
            _updateService.LastStatus.DownloadSize,
            _updateService.LastStatus.ReleaseNotes));

        try
        {
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.PrepareForUpdate();
            }

            _updateService.ApplyUpdateAndRestart();
        }
        catch (Exception exception)
        {
            SetBusy(false);
            RenderStatus(new AppUpdateStatus(
                AppUpdateState.Error,
                "Не удалось начать установку",
                exception.Message));
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        CheckAgainButton.IsEnabled = !isBusy && _updateService.IsInstalled;
        PrimaryActionButton.IsEnabled = !isBusy &&
                                        _updateService.LastStatus.State != AppUpdateState.DevelopmentBuild;
    }

    private void RenderStatus(AppUpdateStatus status)
    {
        StatusTitleText.Text = status.Title;
        StatusDescriptionText.Text = status.Description;
        AvailableVersionText.Text = status.AvailableVersion is null
            ? "Новых выпусков пока нет"
            : $"Версия {status.AvailableVersion}";
        PackageSizeText.Text = status.DownloadSize is > 0
            ? FormatFileSize(status.DownloadSize.Value)
            : "Будет определён";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(status.ReleaseNotes)
            ? "После проверки здесь появится краткое описание новой версии."
            : status.ReleaseNotes.Trim();

        UpdateProgressBar.IsIndeterminate = status.State is AppUpdateState.Checking;

        switch (status.State)
        {
            case AppUpdateState.DevelopmentBuild:
                SetStatusVisual(PackIconKind.CodeBraces, "РЕЖИМ РАЗРАБОТКИ", "—");
                SetPrimaryAction("Доступно после установки", PackIconKind.PackageVariantClosed, false);
                break;

            case AppUpdateState.Checking:
                SetStatusVisual(PackIconKind.CloudSearchOutline, "ПРОВЕРКА ОБНОВЛЕНИЙ", "ПОИСК");
                SetPrimaryAction("Проверяем...", PackIconKind.CloudSearchOutline, false);
                break;

            case AppUpdateState.UpToDate:
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 100;
                SetStatusVisual(PackIconKind.CheckDecagramOutline, "ВСЁ АКТУАЛЬНО", "100%");
                SetPrimaryAction("Проверить ещё раз", PackIconKind.Refresh, true);
                break;

            case AppUpdateState.Available:
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                SetStatusVisual(PackIconKind.CloudDownloadOutline, "НОВАЯ ВЕРСИЯ", "ГОТОВО");
                SetPrimaryAction("Загрузить обновление", PackIconKind.Download, true);
                break;

            case AppUpdateState.Downloading:
                SetStatusVisual(PackIconKind.ProgressDownload, "ЗАГРУЗКА", "0%");
                SetPrimaryAction("Загружаем...", PackIconKind.ProgressDownload, false);
                break;

            case AppUpdateState.ReadyToInstall:
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 100;
                SetStatusVisual(PackIconKind.PackageVariantClosedCheck, "ГОТОВО К УСТАНОВКЕ", "100%");
                SetPrimaryAction("Перезапустить и обновить", PackIconKind.Restart, true);
                break;

            case AppUpdateState.Applying:
                UpdateProgressBar.IsIndeterminate = true;
                SetStatusVisual(PackIconKind.Update, "УСТАНОВКА", "СТАРТ");
                SetPrimaryAction("Обновляем...", PackIconKind.Update, false);
                break;

            case AppUpdateState.Error:
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                SetStatusVisual(PackIconKind.AlertCircleOutline, "НУЖНО ВНИМАНИЕ", "ОШИБКА");
                SetPrimaryAction("Повторить проверку", PackIconKind.Refresh, _updateService.IsInstalled);
                break;

            default:
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 0;
                SetStatusVisual(PackIconKind.Update, "СИСТЕМА ОБНОВЛЕНИЙ", "ГОТОВО");
                SetPrimaryAction("Проверить обновления", PackIconKind.Refresh, _updateService.IsInstalled);
                break;
        }

        CheckAgainButton.IsEnabled = !_isBusy && _updateService.IsInstalled;
    }

    private void SetStatusVisual(PackIconKind icon, string pill, string progress)
    {
        StatusIcon.Kind = icon;
        StatusPillText.Text = pill;
        ProgressValueText.Text = progress;
        StatusIconBorder.Background = new SolidColorBrush(Color.FromArgb(47, 255, 255, 255));
    }

    private void SetPrimaryAction(string text, PackIconKind icon, bool isEnabled)
    {
        PrimaryActionText.Text = text;
        PrimaryActionIcon.Kind = icon;
        PrimaryActionButton.IsEnabled = !_isBusy && isEnabled;
    }

    private static string FormatFileSize(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1
            ? $"{megabytes:0.#} МБ"
            : $"{bytes / 1024d:0.#} КБ";
    }
}
