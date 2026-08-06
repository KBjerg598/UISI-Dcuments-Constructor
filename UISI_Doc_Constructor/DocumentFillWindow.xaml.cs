using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class DocumentFillWindow : Window
{
    private readonly DocumentTemplate _template;
    private readonly string _templatePath;
    private readonly OpenXmlDocumentService _documentService;
    private readonly SavedFillingStorageService _savedFillingStorageService;
    private readonly SavedFillingMatcher _savedFillingMatcher = new();

    public DocumentFillWindow(
        DocumentTemplate template,
        string templatePath,
        IEnumerable<TemplateField> fields,
        OpenXmlDocumentService documentService,
        SavedFillingStorageService savedFillingStorageService)
    {
        InitializeComponent();

        _template = template;
        _templatePath = templatePath;
        _documentService = documentService;
        _savedFillingStorageService = savedFillingStorageService;
        Fields = new ObservableCollection<FieldValueItem>(
            fields.Select(field => new FieldValueItem
            {
                Tag = field.Tag,
                DisplayName = field.DisplayName
            }));

        DataContext = this;
    }

    public ObservableCollection<FieldValueItem> Fields { get; }

    public string? ResultPath { get; private set; }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var emptyField = Fields.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Value));
        if (emptyField is not null)
        {
            MessageBox.Show(
                this,
                $"Заполните поле «{emptyField.DisplayName}».",
                "Заполнение документа",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var templateName = Path.GetFileNameWithoutExtension(_templatePath);
        var dialog = new SaveFileDialog
        {
            Title = "Сохраните готовый документ",
            Filter = "Документы Word (*.docx)|*.docx",
            FileName = $"{templateName}_готовый_{DateTime.Now:yyyy-MM-dd}.docx",
            AddExtension = true,
            DefaultExt = ".docx",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (string.Equals(
                Path.GetFullPath(dialog.FileName),
                Path.GetFullPath(_templatePath),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Готовый документ нельзя сохранить поверх рабочего шаблона. Выберите другое имя.",
                "Заполнение документа",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var values = Fields.ToDictionary(field => field.Tag, field => field.Value);
            var replacements = _documentService.GenerateDocument(
                _templatePath,
                dialog.FileName,
                values);

            if (replacements == 0)
            {
                throw new InvalidDataException("В документе не найдено ни одного поля для заполнения.");
            }

            string savedDataMessage;
            try
            {
                var saveResult = _savedFillingStorageService.SaveAfterDocumentCreated(
                    _template,
                    Fields,
                    dialog.FileName);
                savedDataMessage = saveResult.WasCreated
                    ? $"Данные заполнения сохранены как «{saveResult.Filling.Name}»."
                    : $"Такой набор данных уже существует. Обновлено заполнение «{saveResult.Filling.Name}».";
            }
            catch (Exception exception)
            {
                savedDataMessage = $"Документ создан, но данные заполнения сохранить не удалось: {exception.Message}";
            }

            ResultPath = dialog.FileName;

            MessageBox.Show(
                this,
                $"Документ успешно создан.\n\nЗаменено полей: {replacements}\n{savedDataMessage}",
                "Готово",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Не удалось создать документ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void LoadSavedFilling_Click(object sender, RoutedEventArgs e)
    {
        var libraryWindow = new SavedFillingsWindow(
            _savedFillingStorageService,
            _template.Id,
            _template.Name)
        {
            Owner = this
        };
        if (libraryWindow.ShowDialog() != true || libraryWindow.SelectedFilling is null)
        {
            return;
        }

        try
        {
            var filling = libraryWindow.SelectedFilling;
            var targetFields = Fields.Select(field => new TemplateField
            {
                Tag = field.Tag,
                DisplayName = field.DisplayName,
                Occurrences = 1
            }).ToList();
            var matchResult = _savedFillingMatcher.Match(filling, _template.Id, targetFields);
            IReadOnlyList<SavedFillingFieldMatch> manualMatches = [];

            if (matchResult.UnmatchedSourceFields.Count > 0)
            {
                var mappingWindow = new SavedFillingMappingWindow(
                    filling,
                    matchResult,
                    _savedFillingMatcher)
                {
                    Owner = this
                };
                if (mappingWindow.ShowDialog() != true)
                {
                    return;
                }

                manualMatches = mappingWindow.ManualMatches;
            }

            var allMatches = matchResult.AutomaticMatches
                .Concat(manualMatches)
                .ToList();
            foreach (var match in allMatches)
            {
                var target = Fields.FirstOrDefault(field =>
                    string.Equals(field.Tag, match.Target.Tag, StringComparison.Ordinal));
                if (target is not null)
                {
                    target.Value = match.Source.Value;
                }
            }

            if (manualMatches.Count > 0)
            {
                _savedFillingStorageService.RememberMappings(
                    filling.Id,
                    _template.Id,
                    manualMatches.Select(match => new SavedFillingManualMapping
                    {
                        TargetTemplateId = _template.Id,
                        SourceFieldTag = match.Source.Tag,
                        SourceFieldName = match.Source.DisplayName,
                        TargetFieldTag = match.Target.Tag,
                        TargetFieldName = match.Target.DisplayName
                    }));
            }

            _savedFillingStorageService.MarkUsed(filling.Id);
            var ignoredCount = matchResult.UnmatchedSourceFields.Count - manualMatches.Count;
            SavedDataStatusText.Text =
                $"Загружено «{filling.Name}»: {allMatches.Count} полей" +
                (ignoredCount > 0 ? $", пропущено {ignoredCount}" : string.Empty);
            SavedDataStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Не удалось загрузить заполнение",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
