using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class MainWindow : Window
{
    private readonly TemplateStorageService _storageService = new();
    private readonly OpenXmlDocumentService _documentService = new();
    private readonly WordTemplateEditorService _wordEditorService = new();
    private readonly ObservableCollection<TemplateField> _fields = [];

    private string? _currentTemplatePath;

    public MainWindow()
    {
        InitializeComponent();
        FieldsList.ItemsSource = _fields;
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите документ Word",
            Filter = "Документы Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            var importedPath = _storageService.Import(dialog.FileName);
            SetCurrentTemplate(importedPath);
            SetStatus("Документ импортирован. Работать будем с отдельной копией.");
        });
    }

    private void OpenStoredTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Откройте рабочую копию шаблона",
            InitialDirectory = _storageService.TemplatesPath,
            Filter = "Документы Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);

            var selectedPath = IsInsideTemplateStorage(dialog.FileName)
                ? dialog.FileName
                : _storageService.Import(dialog.FileName);

            SetCurrentTemplate(selectedPath);
            SetStatus("Рабочий шаблон открыт.");
        });
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
            SetStatus($"Поле «{createdField.DisplayName}» создано и шаблон сохранён.");
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
            SetStatus("Список полей обновлён.");
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

    private void SetCurrentTemplate(string templatePath)
    {
        _currentTemplatePath = Path.GetFullPath(templatePath);
        TemplateNameText.Text = Path.GetFileNameWithoutExtension(_currentTemplatePath);
        TemplatePathText.Text = _currentTemplatePath;

        OpenWordButton.IsEnabled = true;
        CreateFieldButton.IsEnabled = true;
        RefreshFieldsButton.IsEnabled = true;

        ReloadFields();
    }

    private void ReloadFields()
    {
        if (_currentTemplatePath is null)
        {
            return;
        }

        var fields = _documentService.ReadFields(_currentTemplatePath);
        _fields.Clear();

        foreach (var field in fields)
        {
            _fields.Add(field);
        }

        UpdateFieldsSummary();
    }

    private void UpdateFieldListAfterCreation(TemplateField createdField)
    {
        var existingField = _fields.FirstOrDefault(field => field.Tag == createdField.Tag);
        if (existingField is null)
        {
            _fields.Add(createdField);
        }
        else
        {
            var index = _fields.IndexOf(existingField);
            _fields[index] = new TemplateField
            {
                Tag = existingField.Tag,
                DisplayName = existingField.DisplayName,
                Occurrences = existingField.Occurrences + 1
            };
        }

        UpdateFieldsSummary();
    }

    private void UpdateFieldsSummary()
    {
        FieldsSummaryText.Text = _fields.Count == 0
            ? "Поля пока не созданы"
            : $"Создано полей: {_fields.Count}";

        FillDocumentButton.IsEnabled = _currentTemplatePath is not null && _fields.Count > 0;
    }

    private bool TryGetCurrentTemplate(out string templatePath)
    {
        if (_currentTemplatePath is not null && File.Exists(_currentTemplatePath))
        {
            templatePath = _currentTemplatePath;
            return true;
        }

        templatePath = string.Empty;
        ShowError("Сначала импортируйте или откройте рабочую копию шаблона.");
        return false;
    }

    private bool IsInsideTemplateStorage(string path)
    {
        var storageRoot = Path.GetFullPath(_storageService.TemplatesPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        return fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase);
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
