using System.Windows;

namespace UISI_Doc_Constructor;

public partial class TemplateNameWindow : Window
{
    private readonly HashSet<string> _existingNames;

    public TemplateNameWindow(string suggestedName, IEnumerable<string> existingNames)
    {
        InitializeComponent();

        _existingNames = existingNames.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        TemplateNameTextBox.Text = suggestedName;
    }

    public string TemplateName { get; private set; } = string.Empty;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TemplateNameTextBox.Focus();
        TemplateNameTextBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var templateName = TemplateNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(templateName))
        {
            ShowValidationMessage("Введите название шаблона.");
            return;
        }

        if (_existingNames.Contains(templateName))
        {
            ShowValidationMessage(
                $"Шаблон с названием «{templateName}» уже существует. Укажите другое название.");
            return;
        }

        TemplateName = templateName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidationMessage(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Название шаблона",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
