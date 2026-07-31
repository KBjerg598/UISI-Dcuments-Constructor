using System.Windows;
using System.Windows.Controls;

namespace UISI_Doc_Constructor;

public partial class FieldNameWindow : Window
{
    private readonly HashSet<string> _reservedNames;

    public FieldNameWindow(string currentName, IEnumerable<string> reservedNames)
    {
        InitializeComponent();

        _reservedNames = reservedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        FieldNameTextBox.Text = currentName;

        Loaded += (_, _) =>
        {
            FieldNameTextBox.Focus();
            FieldNameTextBox.SelectAll();
        };
    }

    public string FieldName { get; private set; } = string.Empty;

    private void FieldNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fieldName = FieldNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            ShowValidation("Введите название поля.");
            return;
        }

        if (_reservedNames.Contains(fieldName))
        {
            ShowValidation(
                "Поле с таким названием уже существует. Для повторения используйте существующее поле.");
            return;
        }

        FieldName = fieldName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        FieldNameTextBox.Focus();
    }
}
