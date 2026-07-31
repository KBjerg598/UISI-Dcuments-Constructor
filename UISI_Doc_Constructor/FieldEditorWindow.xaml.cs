using System.Windows;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor;

public partial class FieldEditorWindow : Window
{
    private readonly HashSet<string> _existingNames;

    public FieldEditorWindow(IEnumerable<TemplateField> existingFields)
    {
        InitializeComponent();
        var fields = existingFields.ToList();
        _existingNames = fields
            .Select(field => field.DisplayName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        ExistingFieldsComboBox.ItemsSource = fields;

        Loaded += (_, _) => FieldNameTextBox.Focus();
    }

    public string FieldDisplayName { get; private set; } = string.Empty;

    public string? ExistingFieldTag { get; private set; }

    private void FieldMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var useExisting = ExistingFieldRadioButton.IsChecked == true;
        FieldNameTextBox.IsEnabled = !useExisting;
        ExistingFieldsComboBox.IsEnabled = useExisting;
        ValidationText.Text = string.Empty;
    }

    private void FieldNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void ExistingFieldsComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (ExistingFieldRadioButton.IsChecked == true)
        {
            if (ExistingFieldsComboBox.SelectedItem is not TemplateField selectedField)
            {
                ShowValidationMessage("Выберите поле, которое нужно повторить.");
                return;
            }

            FieldDisplayName = selectedField.DisplayName;
            ExistingFieldTag = selectedField.Tag;
        }
        else
        {
            var displayName = FieldNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                ShowValidationMessage("Введите название нового поля.");
                return;
            }

            if (_existingNames.Contains(displayName))
            {
                ShowValidationMessage(
                    "Поле с таким названием уже существует. Выберите «Повторить существующее поле». ");
                return;
            }

            FieldDisplayName = displayName;
            ExistingFieldTag = null;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidationMessage(string message)
    {
        ValidationText.Text = message.TrimEnd();
    }
}
