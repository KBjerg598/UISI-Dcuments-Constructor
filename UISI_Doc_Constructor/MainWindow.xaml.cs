using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class MainWindow : Window
{
    private const string FieldDragFormat = "UISI.TemplateField";

    private readonly TemplateStorageService _storageService = new();
    private readonly OpenXmlDocumentService _documentService = new();
    private readonly WordTemplateEditorService _wordEditorService = new();
    private readonly ObservableCollection<DocumentTemplate> _templates = [];
    private readonly ObservableCollection<TemplateField> _fields = [];

    private DocumentTemplate? _currentTemplate;
    private string? _currentTemplatePath;
    private bool _isSelectingTemplate;
    private Point _fieldDragStartPoint;
    private TemplateField? _draggedField;

    public MainWindow()
    {
        InitializeComponent();

        TemplatesList.ItemsSource = _templates;
        FieldsList.ItemsSource = _fields;

        var templatesView = CollectionViewSource.GetDefaultView(_templates);
        templatesView.Filter = TemplateMatchesSearch;

        SetGreeting();
        SetActiveNavigation(OverviewNavButton);
        ReloadTemplateCatalog();
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ImportNavButton);

        var fileDialog = new OpenFileDialog
        {
            Title = "Импорт шаблона",
            Filter =
                "Все поддерживаемые шаблоны (*.docx;*.uisi-template)|*.docx;*.uisi-template|" +
                "Шаблоны UISI (*.uisi-template)|*.uisi-template|" +
                "Документы Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        string suggestedName;
        try
        {
            suggestedName = _storageService.GetSuggestedTemplateName(fileDialog.FileName);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return;
        }

        var nameDialog = new TemplateNameWindow(
            suggestedName,
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

    private void ExportTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocumentTemplate template })
        {
            ExportTemplate(template);
        }
    }

    private void ExportCurrentTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is not null)
        {
            ExportTemplate(_currentTemplate);
        }
    }

    private void OpenTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DocumentTemplate template })
        {
            return;
        }

        RunSafely(() =>
        {
            SetActiveNavigation(TemplatesNavButton);
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
            SetActiveNavigation(FillNavButton);
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

    private void RenameTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocumentTemplate template })
        {
            RenameTemplate(template);
        }
    }

    private void RenameCurrentTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is not null)
        {
            RenameTemplate(_currentTemplate);
        }
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

    private void FieldsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is retained for keyboard actions and drag-and-drop feedback.
    }

    private void RenameField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TemplateField field })
        {
            EditField(field);
            e.Handled = true;
        }
    }

    private void DeleteField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TemplateField field })
        {
            DeleteField(field);
            e.Handled = true;
        }
    }

    private void FieldsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FieldsList.SelectedItem is TemplateField field)
        {
            EditField(field);
        }
    }

    private void FieldsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FieldsList.SelectedItem is not TemplateField field)
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            EditField(field);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteField(field);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && e.Key == Key.Up)
        {
            MoveField(field, _fields.IndexOf(field) - 1);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && e.Key == Key.Down)
        {
            MoveField(field, _fields.IndexOf(field) + 1);
            e.Handled = true;
        }
    }

    private void FieldsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fieldDragStartPoint = e.GetPosition(FieldsList);
        _draggedField = null;

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var item = ItemsControl.ContainerFromElement(
            FieldsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.Content is TemplateField field)
        {
            _draggedField = field;
            FieldsList.SelectedItem = field;
        }
    }

    private void FieldsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedField is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(FieldsList);
        if (Math.Abs(currentPosition.X - _fieldDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _fieldDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(FieldDragFormat, _draggedField);
        try
        {
            DragDrop.DoDragDrop(FieldsList, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedField = null;
            ClearFieldDropIndicators();
        }
    }

    private void FieldsList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FieldDragFormat) ||
            e.Data.GetData(FieldDragFormat) is not TemplateField)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var targetItem = ItemsControl.ContainerFromElement(
            FieldsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var insertAfter = targetItem is not null &&
                          e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2;
        ShowFieldDropIndicator(targetItem, insertAfter);
    }

    private void FieldsList_DragLeave(object sender, DragEventArgs e)
    {
        if (!FieldsList.IsMouseOver)
        {
            ClearFieldDropIndicators();
        }
    }

    private void FieldsList_Drop(object sender, DragEventArgs e)
    {
        ClearFieldDropIndicators();

        if (e.Data.GetData(FieldDragFormat) is not TemplateField draggedField)
        {
            return;
        }

        var targetItem = ItemsControl.ContainerFromElement(
            FieldsList,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var targetIndex = _fields.Count;

        if (targetItem?.Content is TemplateField targetField)
        {
            targetIndex = _fields.IndexOf(targetField);
            if (e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2)
            {
                targetIndex++;
            }
        }

        var sourceIndex = _fields.IndexOf(draggedField);
        if (sourceIndex < 0)
        {
            return;
        }

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        MoveField(draggedField, targetIndex);
        e.Handled = true;
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingTemplate || TemplatesList.SelectedItem is not DocumentTemplate template)
        {
            return;
        }

        RunSafely(() =>
        {
            SetActiveNavigation(TemplatesNavButton);
            _wordEditorService.CloseDocument(saveChanges: true);
            SetCurrentTemplate(template);
            SetStatus($"Шаблон «{template.Name}» выбран для работы.");
        });
    }

    private void FillDocument_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(FillNavButton);

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
        SetActiveNavigation(OverviewNavButton);
        DashboardScrollViewer.ScrollToTop();
    }

    private void TemplatesNav_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(TemplatesNavButton);
        WorkspaceGrid.BringIntoView();
        TemplateSearchBox.Focus();
    }

    private void ShowHelp_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new HelpWindow
        {
            Owner = this
        };

        helpWindow.ShowDialog();
    }

    private void RenameTemplate(DocumentTemplate template)
    {
        SetActiveNavigation(TemplatesNavButton);

        var nameDialog = new TemplateNameWindow(
            template.Name,
            _templates
                .Where(item => item.Id != template.Id)
                .Select(item => item.Name),
            isRename: true)
        {
            Owner = this
        };

        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            var previousName = template.Name;
            var renamedTemplate = _storageService.Rename(
                template.Id,
                nameDialog.TemplateName);

            TemplateSearchBox.Clear();
            ReloadTemplateCatalog(renamedTemplate.Id);
            SetStatus($"Шаблон «{previousName}» переименован в «{renamedTemplate.Name}».");
        });
    }

    private void ExportTemplate(DocumentTemplate template)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "Экспорт шаблона UISI",
            Filter = "Шаблон UISI (*.uisi-template)|*.uisi-template",
            DefaultExt = TemplateStorageService.PackageExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = GetSafeExportFileName(template.Name)
        };

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        RunSafely(() =>
        {
            if (_currentTemplate?.Id == template.Id)
            {
                _wordEditorService.SaveDocument();
                PersistFieldOrder();
            }

            _storageService.Export(template.Id, saveDialog.FileName);
            SetStatus($"Шаблон «{template.Name}» экспортирован: {saveDialog.FileName}");
        });
    }

    private void EditField(TemplateField field)
    {
        if (!TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        var nameWindow = new FieldNameWindow(
            field.DisplayName,
            _fields
                .Where(item => !string.Equals(item.Tag, field.Tag, StringComparison.Ordinal))
                .Select(item => item.DisplayName))
        {
            Owner = this
        };

        if (nameWindow.ShowDialog() != true ||
            string.Equals(
                nameWindow.FieldName,
                field.DisplayName,
                StringComparison.CurrentCulture))
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            var renamedOccurrences = _documentService.RenameField(
                templatePath,
                field.Tag,
                nameWindow.FieldName);
            ReloadFields();
            SelectField(field.Tag);
            SetStatus(
                $"Поле «{field.DisplayName}» переименовано. Обновлено вхождений: {renamedOccurrences}.");
        });
    }

    private void DeleteField(TemplateField field)
    {
        if (!TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        var confirmationWindow = new ConfirmationWindow(
            "Удалить поле?",
            $"Поле «{field.DisplayName}» будет удалено из всех мест шаблона " +
            $"({field.OccurrencesText}). Сам текст в документе останется на месте.",
            "Удалить")
        {
            Owner = this
        };

        if (confirmationWindow.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _wordEditorService.CloseDocument(saveChanges: true);
            var removedOccurrences = _documentService.RemoveField(templatePath, field.Tag);
            ReloadFields();
            SetStatus(
                $"Поле «{field.DisplayName}» удалено. Удалено вхождений: {removedOccurrences}.");
        });
    }

    private void SetActiveNavigation(Button activeButton)
    {
        var regularStyle = (Style)FindResource("NavButtonStyle");
        var selectedStyle = (Style)FindResource("SelectedNavButtonStyle");

        foreach (var button in new[]
                 {
                     OverviewNavButton,
                     TemplatesNavButton,
                     ImportNavButton,
                     FillNavButton
                 })
        {
            button.Style = ReferenceEquals(button, activeButton)
                ? selectedStyle
                : regularStyle;
        }
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

    private void MoveField(TemplateField field, int targetIndex)
    {
        var currentIndex = _fields.IndexOf(field);
        if (currentIndex < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _fields.Count - 1);
        if (targetIndex == currentIndex)
        {
            return;
        }

        RunSafely(() =>
        {
            _fields.Move(currentIndex, targetIndex);
            FieldsList.SelectedItem = field;
            FieldsList.ScrollIntoView(field);
            PersistFieldOrder();
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

        UpdateDashboard();
    }

    private void SelectField(string tag)
    {
        var field = _fields.FirstOrDefault(item =>
            string.Equals(item.Tag, tag, StringComparison.Ordinal));
        if (field is null)
        {
            return;
        }

        FieldsList.SelectedItem = field;
        FieldsList.ScrollIntoView(field);
    }

    private void ShowFieldDropIndicator(ListBoxItem? targetItem, bool insertAfter)
    {
        ClearFieldDropIndicators();

        if (targetItem is null && _fields.Count > 0)
        {
            targetItem = FieldsList.ItemContainerGenerator.ContainerFromIndex(
                _fields.Count - 1) as ListBoxItem;
            insertAfter = true;
        }

        if (targetItem is null)
        {
            return;
        }

        targetItem.BorderBrush = (Brush)FindResource("PrimaryBrush");
        targetItem.BorderThickness = insertAfter
            ? new Thickness(0, 0, 0, 2)
            : new Thickness(0, 2, 0, 0);
    }

    private void ClearFieldDropIndicators()
    {
        for (var index = 0; index < FieldsList.Items.Count; index++)
        {
            if (FieldsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item)
            {
                continue;
            }

            item.ClearValue(Border.BorderBrushProperty);
            item.ClearValue(Border.BorderThicknessProperty);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static string GetSafeExportFileName(string templateName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(templateName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "UISI template";
        }

        return safeName + TemplateStorageService.PackageExtension;
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
