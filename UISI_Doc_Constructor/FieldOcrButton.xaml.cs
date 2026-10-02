using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class FieldOcrButton : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(FieldOcrButton), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty FieldNameProperty = DependencyProperty.Register(nameof(FieldName), typeof(string),
        typeof(FieldOcrButton), new PropertyMetadata(string.Empty, FieldNameChanged));
    private static bool _capturing;

    public FieldOcrButton() => InitializeComponent();
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? FieldName { get => (string?)GetValue(FieldNameProperty); set => SetValue(FieldNameProperty, value); }

    private static void FieldNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (FieldOcrButton)sender;
        if (control.CaptureButton is not null)
            AutomationProperties.SetName(control.CaptureButton, $"Распознать с экрана для поля «{e.NewValue}»");
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (_capturing || owner is null) return;
        _capturing = true;
        CaptureButton.IsEnabled = false;
        try
        {
            var image = await ScreenCaptureService.SelectAsync(owner);
            if (image is null) return;
            var dialog = new OcrReviewWindow(image, FieldName ?? "Поле", Text ?? string.Empty) { Owner = owner };
            if (dialog.ShowDialog() == true)
            {
                SetCurrentValue(TextProperty, dialog.ResultText);
                GetBindingExpression(TextProperty)?.UpdateSource();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, "Не удалось распознать текст. " + exception.Message,
                "Распознавание с экрана", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
            owner.Activate();
        }
    }
}
