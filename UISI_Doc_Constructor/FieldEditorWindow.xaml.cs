using System.Windows;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor;

public partial class FieldEditorWindow : Window
{
    public FieldEditorWindow(IEnumerable<TemplateField> existingFields)
    {
        InitializeComponent();
        ExistingFieldsComboBox.ItemsSource = existingFields.ToList();
        FieldNameTextBox.Focus();
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
        MessageBox.Show(
            this,
            message,
            "Создание поля",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
