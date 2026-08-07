using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class MainWindow : Window
{
    private const string FieldDragFormat = "UISI.TemplateField";
    private const double FieldAutoScrollZone = 46;

    private readonly TemplateStorageService _storageService = new();
    private readonly SavedFillingStorageService _savedFillingStorageService = new();
    private readonly OpenXmlDocumentService _documentService = new();
    private readonly TranslationPackageService _translationPackageService;
    private readonly WordTemplateEditorService _wordEditorService = new();
    private readonly AppUpdateService _updateService = new();
    private readonly DispatcherTimer _fieldAutoScrollTimer;
    private readonly DispatcherTimer _translationToastTimer;
    private readonly ObservableCollection<DocumentTemplate> _templates = [];
    private readonly ObservableCollection<TemplateField> _fields = [];

    private DocumentTemplate? _currentTemplate;
    private string? _currentTemplatePath;
    private bool _isSelectingTemplate;
    private Point _fieldDragStartPoint;
    private Point _fieldDragPointerPosition;
    private TemplateField? _draggedField;
    private double _fieldAutoScrollDelta;
    private bool _startupPackageHandled;
    private int _translationToastVersion;

    public MainWindow()
    {
        InitializeComponent();

        _translationPackageService = new TranslationPackageService(_documentService);

        _fieldAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };
        _fieldAutoScrollTimer.Tick += FieldAutoScrollTimer_Tick;

        _translationToastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2600)
        };
        _translationToastTimer.Tick += TranslationToastTimer_Tick;

        TemplatesList.ItemsSource = _templates;
        FieldsList.ItemsSource = _fields;

        var templatesView = CollectionViewSource.GetDefaultView(_templates);
        templatesView.Filter = TemplateMatchesSearch;

        _updateService.StatusChanged += UpdateUpdateBadge;

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
        bool suggestedTranslationMode;
        try
        {
            suggestedName = _storageService.GetSuggestedTemplateName(fileDialog.FileName);
            suggestedTranslationMode = _storageService.GetSuggestedTranslationMode(
                fileDialog.FileName);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return;
        }

        var nameDialog = new TemplateNameWindow(
            suggestedName,
            _templates.Select(template => template.Name),
            initialHasTranslation: suggestedTranslationMode)
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
                nameDialog.TemplateName,
                nameDialog.HasTranslation);

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

    private void DeleteTemplateFromCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocumentTemplate template })
        {
            DeleteTemplate(template);
        }
    }

    private void ExportCurrentTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is not null)
        {
            ExportTemplate(_currentTemplate);
        }
    }

    private void DeleteCurrentTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is not null)
        {
            DeleteTemplate(_currentTemplate);
        }
    }

    private void SelectTemplateFromCard_Click(object sender, RoutedEventArgs e)
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

    private void ToggleTranslationMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTemplate is null)
        {
            return;
        }

        RunSafely(() =>
        {
            var isEnabled = !_currentTemplate.HasTranslation;
            _storageService.SetTranslationMode(_currentTemplate.Id, isEnabled);
            UpdateTranslationModeControls();
            ReloadFields();
            var message = isEnabled
                ? $"Перевод для шаблона «{_currentTemplate.Name}» включён."
                : $"Перевод для шаблона «{_currentTemplate.Name}» выключен.";
            SetStatus(message);
            ShowTranslationModeToast(isEnabled);
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

        var fieldWindow = new FieldEditorWindow(
            _fields,
            allowTranslationPair: _currentTemplate?.HasTranslation == true)
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

            if (!fieldWindow.CreateTranslationPair)
            {
                SetStatus($"Поле «{createdField.DisplayName}» создано, шаблон сохранён.");
                return;
            }

            if (!TryCreateTranslationPair(
                    createdField,
                    out var targetField,
                    out var pairMode))
            {
                SetStatus(
                    $"Украинское поле «{createdField.DisplayName}» создано без английской пары.");
                return;
            }

            CompleteTranslationPair(createdField, targetField!, pairMode);
        });
    }

    private void AddTranslationPair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TemplateField sourceField } ||
            !TryGetCurrentTemplate(out var templatePath))
        {
            return;
        }

        RunSafely(() =>
        {
            if (_currentTemplate?.HasTranslation != true)
            {
                throw new InvalidOperationException(
                    "Сначала включите для шаблона режим украинского и английского перевода.");
            }

            if (!_wordEditorService.IsDocumentOpen(templatePath))
            {
                _wordEditorService.OpenDocument(templatePath);
            }

            if (TryCreateTranslationPair(
                    sourceField,
                    out var targetField,
                    out var pairMode))
            {
                CompleteTranslationPair(sourceField, targetField!, pairMode);
            }
        });

        e.Handled = true;
    }

    private bool TryCreateTranslationPair(
        TemplateField sourceField,
        out TemplateField? targetField,
        out TranslationPairMode pairMode)
    {
        var pairWindow = new TranslationPairWindow(
            sourceField.DisplayName,
            CreateUniqueEnglishFieldName(sourceField.DisplayName),
            _wordEditorService.ActivateDocument,
            targetName =>
            {
                if (_fields.Any(field => string.Equals(
                        field.DisplayName,
                        targetName,
                        StringComparison.CurrentCultureIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "Поле с таким названием уже существует. Укажите другое название английского поля.");
                }

                return _wordEditorService.CreateFieldFromSelection(targetName);
            })
        {
            Owner = this
        };

        var created = pairWindow.ShowDialog() == true &&
                      pairWindow.CreatedTargetField is not null;
        targetField = pairWindow.CreatedTargetField;
        pairMode = pairWindow.SelectedPairMode;
        return created;
    }

    private void CompleteTranslationPair(
        TemplateField sourceField,
        TemplateField targetField,
        TranslationPairMode pairMode)
    {
        UpdateFieldListAfterCreation(targetField);
        PersistFieldOrder();
        var currentTemplate = _currentTemplate
            ?? throw new InvalidOperationException("Текущий шаблон не выбран.");
        _storageService.SaveTranslationPair(
            currentTemplate.Id,
            sourceField.Tag,
            targetField.Tag,
            pairMode);
        ApplyTranslationMetadataToCurrentFields();
        SelectField(targetField.Tag);
        Activate();
        SetStatus(pairMode == TranslationPairMode.SameValue
            ? $"Создана связанная пара «{sourceField.DisplayName}»: одно значение заполнит обе стороны."
            : $"Создана пара: «{sourceField.DisplayName}» → «{targetField.DisplayName}».");
    }

    private string CreateUniqueEnglishFieldName(string sourceFieldName)
    {
        var baseName = $"{sourceFieldName} — EN";
        var name = baseName;
        var suffix = 2;
        while (_fields.Any(field => string.Equals(
                   field.DisplayName,
                   name,
                   StringComparison.CurrentCultureIgnoreCase)))
        {
            name = $"{baseName} {suffix++}";
        }

        return name;
    }

    private void UpdateTranslationModeControls()
    {
        var hasTemplate = _currentTemplate is not null;
        var isEnabled = _currentTemplate?.HasTranslation == true;
        TranslationModeButton.IsEnabled = hasTemplate;
        TranslationModeButton.ToolTip = isEnabled
            ? "Выключить режим украинского и английского перевода"
            : "Включить режим украинского и английского перевода";
        AutomationProperties.SetName(
            TranslationModeButton,
            isEnabled ? "Выключить режим перевода" : "Включить режим перевода");
        TranslationModeButton.Foreground = (Brush)FindResource(
            isEnabled ? "SuccessBrush" : "PrimaryBrush");
    }

    private void ShowTranslationModeToast(bool isEnabled)
    {
        _translationToastTimer.Stop();
        _translationToastVersion++;

        TranslationToastText.Text = isEnabled
            ? "Перевод у шаблона включён"
            : "Перевод у шаблона выключен";
        TranslationToastDetails.Text = isEnabled
            ? "Можно создавать и связывать украинские и английские поля."
            : "Сохранённые пары полей не удалены и будут доступны после повторного включения.";

        var accentBrush = (Brush)FindResource(
            isEnabled ? "SuccessBrush" : "WarningBrush");
        TranslationToastIcon.Foreground = accentBrush;
        TranslationToastIconBackground.Background = (Brush)FindResource(
            isEnabled ? "SuccessSoftBrush" : "WarningSoftBrush");
        TranslationModeToast.BorderBrush = accentBrush;

        TranslationModeToast.BeginAnimation(OpacityProperty, null);
        TranslationModeToast.Visibility = Visibility.Visible;
        TranslationModeToast.Opacity = 0;
        TranslationModeToast.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        _translationToastTimer.Start();
    }

    private void TranslationToastTimer_Tick(object? sender, EventArgs e)
    {
        _translationToastTimer.Stop();
        var toastVersion = _translationToastVersion;
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            if (toastVersion == _translationToastVersion)
            {
                TranslationModeToast.Visibility = Visibility.Collapsed;
            }
        };
        TranslationModeToast.BeginAnimation(OpacityProperty, animation);
    }

    private void RefreshFields_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTemplate(out _))
        {
            return;
        }

        RunSafely(() =>
        {
            var saveResult = _wordEditorService.SaveDocument();
            ReloadFields();
            SetStatus(saveResult == WordDocumentSaveResult.Disconnected
                ? "Word был закрыт. Поля обновлены по последней сохранённой версии шаблона."
                : "Список и порядок полей обновлены.");
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
            StopFieldAutoScroll();
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

        UpdateFieldAutoScroll(e.GetPosition(FieldsList));

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
            StopFieldAutoScroll();
            ClearFieldDropIndicators();
        }
    }

    private void FieldsList_Drop(object sender, DragEventArgs e)
    {
        StopFieldAutoScroll();
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

            var template = _currentTemplate
                ?? throw new InvalidOperationException("Выберите шаблон для заполнения.");
            var fillWindow = new DocumentFillWindow(
                template,
                templatePath,
                _fields,
                _documentService,
                _savedFillingStorageService,
                _storageService,
                _translationPackageService)
            {
                Owner = this
            };

            if (fillWindow.ShowDialog() == true && fillWindow.ResultPath is not null)
            {
                SetStatus($"Готовый документ сохранён: {fillWindow.ResultPath}");
            }
        });
    }

    private void OpenTranslation_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(TranslationNavButton);

        var fileDialog = new OpenFileDialog
        {
            Title = "Откройте пакет перевода",
            Filter = TranslationPackageService.PackageOpenDialogFilter,
            DefaultExt = TranslationPackageService.PackageExtension,
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        OpenTranslationPackage(fileDialog.FileName);
    }

    private void OpenTranslationPackage(string packagePath)
    {
        RunSafely(() =>
        {
            using var session = _translationPackageService.OpenPackage(packagePath);
            var translationWindow = new TranslationFillWindow(
                _translationPackageService,
                session)
            {
                Owner = this
            };

            if (translationWindow.ShowDialog() == true &&
                translationWindow.ResultPath is not null)
            {
                SetStatus($"Перевод завершён: {translationWindow.ResultPath}");
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_startupPackageHandled)
        {
            _startupPackageHandled = true;
            var packagePath = Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(path =>
                    string.Equals(
                        Path.GetExtension(path),
                        TranslationPackageService.PackageExtension,
                        StringComparison.OrdinalIgnoreCase));
            if (packagePath is not null)
            {
                SetActiveNavigation(TranslationNavButton);
                OpenTranslationPackage(packagePath);
            }
        }

        if (!_updateService.IsInstalled || !_updateService.ShouldCheckAutomatically())
        {
            return;
        }

        try
        {
            await _updateService.CheckForUpdatesAsync();
        }
        finally
        {
            _updateService.RecordAutomaticCheck();
        }
    }

    private void ShowUpdates_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(UpdatesNavButton);
        var updateWindow = new UpdateWindow(_updateService)
        {
            Owner = this
        };

        updateWindow.ShowDialog();
        UpdateUpdateBadge(_updateService.LastStatus);
    }

    internal void PrepareForUpdate()
    {
        _wordEditorService.CloseDocument(saveChanges: true);
    }

    private void UpdateUpdateBadge(AppUpdateStatus status)
    {
        var hasUpdate = status.State is AppUpdateState.Available or AppUpdateState.ReadyToInstall;
        UpdateAvailableBadge.Visibility = hasUpdate
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateAvailableBadgeText.Text = status.State == AppUpdateState.ReadyToInstall
            ? "100%"
            : "NEW";
        UpdatesNavButton.ToolTip = status.Title;
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
            var saveResult = WordDocumentSaveResult.NoOpenDocument;
            if (_currentTemplate?.Id == template.Id)
            {
                saveResult = _wordEditorService.SaveDocument();
                PersistFieldOrder();
            }

            _storageService.Export(template.Id, saveDialog.FileName);
            SetStatus(saveResult == WordDocumentSaveResult.Disconnected
                ? $"Word был закрыт. Экспортирована последняя сохранённая версия шаблона «{template.Name}»: {saveDialog.FileName}"
                : $"Шаблон «{template.Name}» экспортирован: {saveDialog.FileName}");
        });
    }

    private void DeleteTemplate(DocumentTemplate template)
    {
        SetActiveNavigation(TemplatesNavButton);

        var confirmationWindow = new ConfirmationWindow(
            "Удалить шаблон?",
            $"Шаблон «{template.Name}» исчезнет из библиотеки программы. " +
            "Готовые документы, ранее созданные по этому шаблону, не изменятся.",
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
            var deletedCurrentTemplate = _currentTemplate?.Id == template.Id;
            if (deletedCurrentTemplate)
            {
                _wordEditorService.CloseDocument(saveChanges: true);
            }

            var deletedTemplate = _storageService.Delete(template.Id);
            TemplateSearchBox.Clear();
            ReloadTemplateCatalog(deletedCurrentTemplate ? null : _currentTemplate?.Id);
            SetStatus($"Шаблон «{deletedTemplate.Name}» удалён из библиотеки.");
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
                     FillNavButton,
                     TranslationNavButton,
                     UpdatesNavButton
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
        UpdateTranslationModeControls();

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
        TranslationModeButton.IsEnabled = false;
        FillDocumentButton.IsEnabled = false;
    }

    private void ReloadFields()
    {
        if (_currentTemplatePath is null || _currentTemplate is null)
        {
            return;
        }

        IReadOnlyList<TemplateField> fields;
        try
        {
            fields = _documentService.ReadFields(_currentTemplatePath);
        }
        catch (Exception exception) when (
            exception is IOException or FileFormatException or UnauthorizedAccessException)
        {
            UpdateFieldsSummary();
            SetStatus(
                $"Не удалось прочитать шаблон «{_currentTemplate.Name}». " +
                "Показывается последний полученный список полей; повторите обновление после сохранения в Word.");
            return;
        }

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

        orderedFields = ApplyTranslationMetadata(orderedFields);

        _fields.Clear();
        foreach (var field in orderedFields)
        {
            _fields.Add(field);
        }

        PersistFieldOrder();
        var migratedPairs = _storageService.MigrateLegacyTranslationPairs(
            _currentTemplate.Id,
            _fields);
        if (migratedPairs > 0)
        {
            ApplyTranslationMetadataToCurrentFields();
        }
        else
        {
            UpdateFieldsSummary();
        }
    }

    private List<TemplateField> ApplyTranslationMetadata(
        IEnumerable<TemplateField> fields)
    {
        if (_currentTemplate is null)
        {
            return fields.ToList();
        }

        var translationSettingsByTag = _currentTemplate.TranslationFields
            .ToDictionary(item => item.Tag, StringComparer.Ordinal);
        var pairedSourceTags = _currentTemplate.TranslationFields
            .Where(item =>
                item.Role == TranslationFieldRole.Target &&
                item.SourceFieldTag is not null)
            .Select(item => item.SourceFieldTag!)
            .ToHashSet(StringComparer.Ordinal);

        return fields.Select(field =>
        {
            translationSettingsByTag.TryGetValue(field.Tag, out var settings);
            return new TemplateField
            {
                Tag = field.Tag,
                DisplayName = field.DisplayName,
                Occurrences = field.Occurrences,
                TranslationRole = settings?.Role ?? TranslationFieldRole.Unassigned,
                SourceFieldTag = settings?.SourceFieldTag,
                PairId = settings?.PairId,
                PairMode = settings?.PairMode ?? TranslationPairMode.Translation,
                CanAddTranslationPair = _currentTemplate.HasTranslation &&
                                        settings?.Role != TranslationFieldRole.Target &&
                                        settings?.Role != TranslationFieldRole.Shared &&
                                        !pairedSourceTags.Contains(field.Tag)
            };
        }).ToList();
    }

    private void ApplyTranslationMetadataToCurrentFields()
    {
        var selectedTag = (FieldsList.SelectedItem as TemplateField)?.Tag;
        var updatedFields = ApplyTranslationMetadata(_fields);
        _fields.Clear();
        foreach (var field in updatedFields)
        {
            _fields.Add(field);
        }

        if (selectedTag is not null)
        {
            SelectField(selectedTag);
        }

        UpdateFieldsSummary();
    }

    private void UpdateFieldListAfterCreation(TemplateField createdField)
    {
        var existingField = _fields.FirstOrDefault(field => field.Tag == createdField.Tag);
        if (existingField is null)
        {
            var addedField = new TemplateField
            {
                Tag = createdField.Tag,
                DisplayName = createdField.DisplayName,
                Occurrences = createdField.Occurrences,
                TranslationRole = createdField.TranslationRole,
                SourceFieldTag = createdField.SourceFieldTag,
                PairId = createdField.PairId,
                PairMode = createdField.PairMode,
                CanAddTranslationPair = _currentTemplate?.HasTranslation == true &&
                                        createdField.TranslationRole != TranslationFieldRole.Target &&
                                        createdField.TranslationRole != TranslationFieldRole.Shared
            };
            _fields.Add(addedField);
            FieldsList.SelectedItem = addedField;
        }
        else
        {
            var index = _fields.IndexOf(existingField);
            var updatedField = new TemplateField
            {
                Tag = existingField.Tag,
                DisplayName = existingField.DisplayName,
                Occurrences = existingField.Occurrences + 1,
                TranslationRole = existingField.TranslationRole,
                SourceFieldTag = existingField.SourceFieldTag,
                PairId = existingField.PairId,
                PairMode = existingField.PairMode,
                CanAddTranslationPair = existingField.CanAddTranslationPair
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

    private void UpdateFieldAutoScroll(Point pointerPosition)
    {
        _fieldDragPointerPosition = pointerPosition;
        var viewportHeight = FieldsList.ActualHeight;
        if (viewportHeight <= 0)
        {
            StopFieldAutoScroll();
            return;
        }

        var scrollZone = Math.Min(FieldAutoScrollZone, viewportHeight / 3);
        if (pointerPosition.Y < scrollZone)
        {
            var intensity = Math.Clamp((scrollZone - pointerPosition.Y) / scrollZone, 0, 1);
            _fieldAutoScrollDelta = -(4 + 12 * intensity);
        }
        else if (pointerPosition.Y > viewportHeight - scrollZone)
        {
            var intensity = Math.Clamp(
                (pointerPosition.Y - (viewportHeight - scrollZone)) / scrollZone,
                0,
                1);
            _fieldAutoScrollDelta = 4 + 12 * intensity;
        }
        else
        {
            StopFieldAutoScroll();
            return;
        }

        if (!_fieldAutoScrollTimer.IsEnabled)
        {
            _fieldAutoScrollTimer.Start();
        }
    }

    private void FieldAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(FieldsList);
        if (scrollViewer is null)
        {
            StopFieldAutoScroll();
            return;
        }

        var targetOffset = Math.Clamp(
            scrollViewer.VerticalOffset + _fieldAutoScrollDelta,
            0,
            scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(targetOffset);

        var hitElement = FieldsList.InputHitTest(_fieldDragPointerPosition) as DependencyObject;
        var targetItem = ItemsControl.ContainerFromElement(FieldsList, hitElement) as ListBoxItem;
        var insertAfter = targetItem is not null &&
                          _fieldDragPointerPosition.Y -
                          targetItem.TranslatePoint(new Point(), FieldsList).Y >
                          targetItem.ActualHeight / 2;
        ShowFieldDropIndicator(targetItem, insertAfter);
    }

    private void StopFieldAutoScroll()
    {
        _fieldAutoScrollTimer.Stop();
        _fieldAutoScrollDelta = 0;
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

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
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
        StopFieldAutoScroll();
        _translationToastTimer.Stop();
        _updateService.StatusChanged -= UpdateUpdateBadge;
        _wordEditorService.Dispose();
    }
}
