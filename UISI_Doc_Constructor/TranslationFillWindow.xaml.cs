using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class TranslationFillWindow : Window
{
    private readonly TranslationPackageService _packageService;
    private readonly TranslationPackageSession _session;

    public TranslationFillWindow(
        TranslationPackageService packageService,
        TranslationPackageSession session)
    {
        InitializeComponent();
        _packageService = packageService;
        _session = session;
        Fields = new ObservableCollection<TranslationFieldValueItem>(
            packageService.CreateTargetItems(session));
        TemplateDescription =
            $"Шаблон «{session.Manifest.TemplateName}» · редакция {session.Manifest.Revision}";
        TargetCountText = Fields.Count switch
        {
            1 => "1 поле EN",
            >= 2 and <= 4 => $"{Fields.Count} поля EN",
            _ => $"{Fields.Count} полей EN"
        };
        DataContext = this;
    }

    public ObservableCollection<TranslationFieldValueItem> Fields { get; }

    public string TemplateDescription { get; }

    public string TargetCountText { get; }

    public string SourceEditingDescription => _session.Manifest.FormatVersion >= 2
        ? "Украинский текст можно исправить слева. Правки сохранятся в пакете и итоговом DOCX; все рамки будут удалены."
        : "В старом пакете украинский текст доступен только для копирования. Для его исправления создайте новый пакет из существующего шаблона.";

    public string SourceEditingHelpText => _session.Manifest.FormatVersion >= 2
        ? "Исправьте украинский оригинал или выделите текст и скопируйте его сочетанием Ctrl+C."
        : "Выделите текст и скопируйте его сочетанием Ctrl+C. Для редактирования нужен новый пакет из существующего шаблона.";

    public string? ResultPath { get; private set; }

    private void SavePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохраните заполненный пакет перевода",
            Filter = TranslationPackageService.PackageSaveDialogFilter,
            DefaultExt = TranslationPackageService.PackageExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = CreateTranslatedPackageName()
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _packageService.SavePackage(_session, Fields, dialog.FileName);
            StatusText.Text = $"Пакет сохранён: {dialog.FileName}";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message, "Не удалось сохранить пакет");
        }
    }

    private void GenerateFinal_Click(object sender, RoutedEventArgs e)
    {
        var emptyField = Fields.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Value));
        if (emptyField is not null)
        {
            ShowError(
                $"Заполните английское поле «{emptyField.DisplayName}».",
                "Заполнение перевода");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохраните окончательный двуязычный документ",
            Filter = "Документы Word (*.docx)|*.docx",
            DefaultExt = ".docx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{CreateSafeFileName(_session.Manifest.TemplateName)}_переведено_{DateTime.Now:yyyy-MM-dd}.docx"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var replacements = _packageService.GenerateFinalDocument(
                _session,
                Fields,
                dialog.FileName);
            string packageMessage;
            try
            {
                _packageService.SavePackage(_session, Fields, _session.SourcePath);
                packageMessage = "Значения также сохранены в исходном пакете перевода.";
            }
            catch (Exception exception)
            {
                packageMessage = $"Пакет обновить не удалось: {exception.Message}";
            }

            ResultPath = dialog.FileName;
            MessageBox.Show(
                this,
                $"Окончательный документ создан.\n\nЗаполнено вхождений полей: {replacements}\n{packageMessage}",
                "Перевод завершён",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message, "Не удалось создать документ");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private string CreateTranslatedPackageName()
    {
        var sourceName = Path.GetFileNameWithoutExtension(_session.SourcePath);
        return $"{CreateSafeFileName(sourceName)}_переведено{TranslationPackageService.PackageExtension}";
    }

    private static string CreateSafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(safeName) ? "Документ" : safeName;
    }

    private void ShowError(string message, string title) =>
        MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
