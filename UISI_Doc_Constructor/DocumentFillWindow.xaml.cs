using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class DocumentFillWindow : Window
{
    private readonly string _templatePath;
    private readonly OpenXmlDocumentService _documentService;

    public DocumentFillWindow(
        string templatePath,
        IEnumerable<TemplateField> fields,
        OpenXmlDocumentService documentService)
    {
        InitializeComponent();

        _templatePath = templatePath;
        _documentService = documentService;
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

            ResultPath = dialog.FileName;

            MessageBox.Show(
                this,
                $"Документ успешно создан.\n\nЗаменено полей: {replacements}",
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
