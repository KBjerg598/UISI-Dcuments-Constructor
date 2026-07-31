using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class MainWindow : Window
{
    private readonly TemplateStorageService _storageService = new();
    private readonly OpenXmlDocumentService _documentService = new();
    private readonly WordTemplateEditorService _wordEditorService = new();
    private readonly ObservableCollection<DocumentTemplate> _templates = [];
    private readonly ObservableCollection<TemplateField> _fields = [];

    private DocumentTemplate? _currentTemplate;
    private string? _currentTemplatePath;
    private bool _isSelectingTemplate;

    public MainWindow()
    {
        InitializeComponent();

        TemplatesList.ItemsSource = _templates;
        FieldsList.ItemsSource = _fields;

        var templatesView = CollectionViewSource.GetDefaultView(_templates);
        templatesView.Filter = TemplateMatchesSearch;

        SetGreeting();
        ReloadTemplateCatalog();
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Выберите документ Word",
            Filter = "Документы Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        var nameDialog = new TemplateNameWindow(
            Path.GetFileNameWithoutExtension(fileDialog.FileName),
            _templates.Select(template => template.Name))
        {
            Owner = this
        };

        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);

            var importedTemplate = _storageService.Import(
                fileDialog.FileName,
                nameDialog.TemplateName);

            TemplateSearchBox.Clear();
            ReloadTemplateCatalog(importedTemplate.Id);
            SetStatus(
                $"Шаблон «{importedTemplate.Name}» импортирован и сохранён в библиотеке программы.");
        });
    }

    private void OpenTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DocumentTemplate template })
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            SetCurrentTemplate(template);
            SetStatus($"Шаблон «{template.Name}» выбран для работы.");
        });
    }

    private void FillTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DocumentTemplate template })
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            SetCurrentTemplate(template);
        });

        if (_currentTemplate?.Id == template.Id)
        {
            FillDocument_Click(sender, e);
        }
    }

    private void OpenInWord_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.OpenDocument(templatePath);
            SetStatus("Шаблон открыт в Word. Выделите текст и нажмите «Создать поле».");
        });
    }

    private void CreateField_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        if (!_wordEditorService.IsDocumentOpen(templatePath))
        {
            ShowError("Сначала откройте текущий шаблон в Microsoft Word.");
            return;
        }

        var fieldWindow = new FieldEditorWindow(_fields)
        {
            Owner = this
        };

        if (fieldWindow.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            var createdField = _wordEditorService.CreateFieldFromSelection(
                fieldWindow.FieldDisplayName,
                fieldWindow.ExistingFieldTag);

            UpdateFieldListAfterCreation(createdField);
            PersistFieldOrder();
            SetStatus($"Поле «{createdField.DisplayName}» создано, шаблон сохранён.");
        });
    }

    private void RefreshFields_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTemplate(out _))
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.SaveDocument();
            ReloadFields();
            SetStatus("Список и порядок полей обновлены.");
        });
    }

    private void MoveFieldUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedField(-1);
    }

    private void MoveFieldDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedField(1);
    }

    private void FieldsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMoveButtons();
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingTemplate || TemplatesList.SelectedItem is not DocumentTemplate template)
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            SetCurrentTemplate(template);
            SetStatus($"Шаблон «{template.Name}» выбран для работы.");
        });
    }

    private void FillDocument_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            ReloadFields();

            if (_fields.Count == 0)
            {
                throw new InvalidOperationException(
                    "В шаблоне ещё нет полей. Откройте его в Word и создайте хотя бы одно поле.");
            }

            var fillWindow = new DocumentFillWindow(
                templatePath,
                _fields,
                _documentService)
            {
                Owner = this
            };

            if (fillWindow.ShowDialog() == true && fillWindow.ResultPath is not null)
            {
                SetStatus($"Готовый документ сохранён: {fillWindow.ResultPath}");
            }
        });
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e)
    {
        DashboardScrollViewer.ScrollToTop();
    }

    private void TemplatesNav_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceGrid.BringIntoView();
        TemplateSearchBox.Focus();
    }

    private void ShowHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "1. Импортируйте документ DOCX и дайте шаблону понятное название.\n\n" +
            "2. Откройте шаблон в Word, выделите текст и создайте из него поле. " +
            "Одно поле можно повторять в нескольких местах документа.\n\n" +
            "3. Расположите поля в удобном порядке и нажмите «Заполнить документ».",
            "Как создать документ",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void TemplateSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_templates).Refresh();
        UpdateTemplatesVisibility();
    }

    private bool TemplateMatchesSearch(object item)
    {
        if (item is not DocumentTemplate template)
        {
            return false;
        }

        var query = TemplateSearchBox.Text.Trim();
        return query.Length == 0 || template.Name.Contains(
            query,
            StringComparison.CurrentCultureIgnoreCase);
    }

    private void ReloadTemplateCatalog(Guid? selectedTemplateId = null)
    {
        var templates = _storageService.GetTemplates();
        var currentTemplateId = selectedTemplateId ?? _currentTemplate?.Id;

        _templates.Clear();
        foreach (var template in templates)
        {
            _templates.Add(template);
        }

        CollectionViewSource.GetDefaultView(_templates).Refresh();

        var templateToSelect = currentTemplateId is null
            ? _templates.FirstOrDefault()
            : _templates.FirstOrDefault(template => template.Id == currentTemplateId)
              ?? _templates.FirstOrDefault();

        if (templateToSelect is not null)
        {
            SetCurrentTemplate(templateToSelect);
        }
        else
        {
            ClearCurrentTemplate();
        }

        UpdateDashboard();
    }

    private void SetCurrentTemplate(DocumentTemplate template)
    {
        var templatePath = _storageService.GetTemplatePath(template);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException(
                $"Файл шаблона «{template.Name}» не найден.",
                templatePath);
        }

        _currentTemplate = template;
        _currentTemplatePath = Path.GetFullPath(templatePath);

        _isSelectingTemplate = true;
        try
        {
            TemplatesList.SelectedItem = _templates.FirstOrDefault(item => item.Id == template.Id);
            TemplatesList.ScrollIntoView(TemplatesList.SelectedItem);
        }
        finally
        {
            _isSelectingTemplate = false;
        }

        TemplateNameText.Text = template.Name;
        TemplatePathText.Text = _currentTemplatePath;
        CurrentTemplateEmptyPanel.Visibility = Visibility.Collapsed;
        CurrentTemplatePanel.Visibility = Visibility.Visible;

        OpenWordButton.IsEnabled = true;
        CreateFieldButton.IsEnabled = true;
        RefreshFieldsButton.IsEnabled = true;

        ReloadFields();
    }

    private void ClearCurrentTemplate()
    {
        _currentTemplate = null;
        _currentTemplatePath = null;
        _fields.Clear();

        CurrentTemplateEmptyPanel.Visibility = Visibility.Visible;
        CurrentTemplatePanel.Visibility = Visibility.Collapsed;
        OpenWordButton.IsEnabled = false;
        CreateFieldButton.IsEnabled = false;
        RefreshFieldsButton.IsEnabled = false;
        FillDocumentButton.IsEnabled = false;
    }

    private void ReloadFields()
    {
        if (_currentTemplatePath is null || _currentTemplate is null)
        {
            return;
        }

        var fields = _documentService.ReadFields(_currentTemplatePath);
        var orderIndexes = _currentTemplate.FieldOrder
            .Select((tag, index) => new { tag, index })
            .GroupBy(item => item.tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);

        var orderedFields = fields
            .Select((field, documentIndex) => new
            {
                Field = field,
                DocumentIndex = documentIndex,
                SavedIndex = orderIndexes.TryGetValue(field.Tag, out var savedIndex)
                    ? savedIndex
                    : int.MaxValue
            })
            .OrderBy(item => item.SavedIndex)
            .ThenBy(item => item.DocumentIndex)
            .Select(item => item.Field)
            .ToList();

        _fields.Clear();
        foreach (var field in orderedFields)
        {
            _fields.Add(field);
        }

        PersistFieldOrder();
        UpdateFieldsSummary();
    }

    private void UpdateFieldListAfterCreation(TemplateField createdField)
    {
        var existingField = _fields.FirstOrDefault(field => field.Tag == createdField.Tag);
        if (existingField is null)
        {
            _fields.Add(createdField);
            FieldsList.SelectedItem = createdField;
        }
        else
        {
            var index = _fields.IndexOf(existingField);
            var updatedField = new TemplateField
            {
                Tag = existingField.Tag,
                DisplayName = existingField.DisplayName,
                Occurrences = existingField.Occurrences + 1
            };

            _fields[index] = updatedField;
            FieldsList.SelectedItem = updatedField;
        }

        UpdateFieldsSummary();
    }

    private void MoveSelectedField(int direction)
    {
        if (FieldsList.SelectedItem is not TemplateField selectedField)
        {
            return;
        }

        var currentIndex = _fields.IndexOf(selectedField);
        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= _fields.Count)
        {
            return;
        }

        RunSafely(() =>
        {
            _fields.Move(currentIndex, targetIndex);
            FieldsList.SelectedItem = selectedField;
            FieldsList.ScrollIntoView(selectedField);
            PersistFieldOrder();
            UpdateMoveButtons();
            SetStatus("Порядок полей сохранён.");
        });
    }

    private void PersistFieldOrder()
    {
        if (_currentTemplate is null)
        {
            return;
        }

        _storageService.SaveFieldOrder(
            _currentTemplate.Id,
            _fields.Select(field => field.Tag));

        CollectionViewSource.GetDefaultView(_templates).Refresh();
    }

    private void UpdateFieldsSummary()
    {
        FieldsSummaryText.Text = _fields.Count == 0
            ? "Поля пока не созданы"
            : $"Создано полей: {_fields.Count}";

        FieldsCountMetricText.Text = _fields.Count.ToString();
        FillDocumentButton.IsEnabled = _currentTemplatePath is not null && _fields.Count > 0;

        var isReady = _fields.Count > 0;
        CurrentProgressBar.Value = isReady ? 100 : 40;
        CurrentProgressText.Text = isReady ? "100%" : "40%";
        CurrentTemplateHintText.Text = isReady
            ? "Шаблон готов: можно заполнить поля и создать документ."
            : "Откройте шаблон в Word и добавьте поля.";

        UpdateMoveButtons();
        UpdateDashboard();
    }

    private void UpdateMoveButtons()
    {
        var selectedIndex = FieldsList.SelectedIndex;
        MoveFieldUpButton.IsEnabled = selectedIndex > 0;
        MoveFieldDownButton.IsEnabled =
            selectedIndex >= 0 && selectedIndex < _fields.Count - 1;
    }

    private void UpdateDashboard()
    {
        var templateCount = _templates.Count;
        TemplatesCountBadgeText.Text = templateCount.ToString();
        TemplatesCountMetricText.Text = templateCount.ToString();
        FieldsCountMetricText.Text = _fields.Count.ToString();

        var latestUpdate = _templates
            .OrderByDescending(template => template.UpdatedAtUtc)
            .FirstOrDefault()
            ?.UpdatedAtUtc
            .ToLocalTime();

        LastUpdatedMetricText.Text = latestUpdate is null
            ? "—"
            : FormatCompactDate(latestUpdate.Value);

        UpdateTemplatesVisibility();
    }

    private void UpdateTemplatesVisibility()
    {
        var view = CollectionViewSource.GetDefaultView(_templates);
        var hasVisibleTemplates = view.Cast<object>().Any();
        EmptyTemplatesPanel.Visibility = hasVisibleTemplates
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_templates.Count > 0 && !hasVisibleTemplates)
        {
            EmptyTemplatesTitleText.Text = "Шаблоны не найдены";
            EmptyTemplatesDescriptionText.Text =
                "Попробуйте изменить запрос или очистить строку поиска.";
        }
        else
        {
            EmptyTemplatesTitleText.Text = "Добавьте первый шаблон";
            EmptyTemplatesDescriptionText.Text =
                "Импортируйте документ DOCX, назовите его — и он останется в библиотеке программы.";
        }
    }

    private void SetGreeting()
    {
        GreetingText.Text = DateTime.Now.Hour switch
        {
            < 6 => "Доброй ночи!",
            < 12 => "Доброе утро!",
            < 18 => "Добрый день!",
            _ => "Добрый вечер!"
        };
    }

    private static string FormatCompactDate(DateTime date)
    {
        if (date.Date == DateTime.Today)
        {
            return "Сегодня";
        }

        if (date.Date == DateTime.Today.AddDays(-1))
        {
            return "Вчера";
        }

        return date.ToString("dd.MM.yyyy");
    }

    private bool TryGetCurrentTemplate(out string templatePath)
    {
        if (_currentTemplatePath is not null && File.Exists(_currentTemplatePath))
        {
            templatePath = _currentTemplatePath;
            return true;
        }

        templatePath = string.Empty;
        ShowError("Сначала импортируйте или выберите сохранённый шаблон.");
        return false;
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Конструктор документов",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        SetStatus(message);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _wordEditorService.Dispose();
    }
}
