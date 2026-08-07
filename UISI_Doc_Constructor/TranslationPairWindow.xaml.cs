using System.Windows;
using System.Windows.Threading;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor;

public partial class TranslationPairWindow : Window
{
    private readonly Action _activateWord;
    private readonly Func<string, TemplateField> _createTargetField;

    public TranslationPairWindow(
        string sourceFieldName,
        string suggestedTargetFieldName,
        Action activateWord,
        Func<string, TemplateField> createTargetField)
    {
        InitializeComponent();
        _activateWord = activateWord;
        _createTargetField = createTargetField;
        SourceFieldNameText.Text = sourceFieldName;
        TargetFieldNameTextBox.Text = suggestedTargetFieldName;
        PairModeComboBox.ItemsSource = new TranslationPairModeOption[]
        {
            new(TranslationPairMode.Translation, "Переводит переводчик"),
            new(TranslationPairMode.SameValue, "Одинаковое значение в обеих версиях")
        };
        PairModeComboBox.SelectedIndex = 0;
    }

    public TemplateField? CreatedTargetField { get; private set; }

    public TranslationPairMode SelectedPairMode =>
        PairModeComboBox.SelectedValue is TranslationPairMode mode
            ? mode
            : TranslationPairMode.Translation;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 16, workArea.Right - ActualWidth - 24);
        Top = Math.Max(workArea.Top + 16, workArea.Bottom - ActualHeight - 34);

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(ShowWord));
    }

    private void ShowWord_Click(object sender, RoutedEventArgs e) => ShowWord();

    private void ShowWord()
    {
        try
        {
            _activateWord();
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var targetFieldName = TargetFieldNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetFieldName))
        {
            ValidationText.Text = "Введите название английского поля.";
            return;
        }

        try
        {
            CreatedTargetField = _createTargetField(targetFieldName);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void TargetFieldNameTextBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
