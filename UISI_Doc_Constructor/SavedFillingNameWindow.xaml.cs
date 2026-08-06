using System.Windows;
using System.Windows.Controls;

namespace UISI_Doc_Constructor;

public partial class SavedFillingNameWindow : Window
{
    private readonly HashSet<string> _reservedNames;

    public SavedFillingNameWindow(string currentName, IEnumerable<string> reservedNames)
    {
        InitializeComponent();

        _reservedNames = reservedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        SavedNameTextBox.Text = currentName;

        Loaded += (_, _) =>
        {
            SavedNameTextBox.Focus();
            SavedNameTextBox.SelectAll();
        };
    }

    public string SavedName { get; private set; } = string.Empty;

    private void SavedNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = SavedNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = "Введите название сохранённого заполнения.";
            SavedNameTextBox.Focus();
            return;
        }

        if (_reservedNames.Contains(name))
        {
            ValidationText.Text = "Заполнение с таким названием уже существует.";
            SavedNameTextBox.Focus();
            return;
        }

        SavedName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
