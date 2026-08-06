using System.Windows;

namespace UISI_Doc_Constructor;

public partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(
        string heading,
        string description,
        string confirmLabel,
        string? context = null)
    {
        InitializeComponent();
        Title = heading;
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        ConfirmButton.Content = confirmLabel;
        if (!string.IsNullOrWhiteSpace(context))
        {
            ContextText.Text = context;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
