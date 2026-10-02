using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace UISI_Doc_Constructor;

public partial class FieldCopyButton : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FieldCopyButton),
        new PropertyMetadata(string.Empty, ContentChanged));

    public static readonly DependencyProperty FieldNameProperty = DependencyProperty.Register(
        nameof(FieldName), typeof(string), typeof(FieldCopyButton),
        new PropertyMetadata(string.Empty, ContentChanged));

    private readonly DispatcherTimer _feedbackTimer;
    private bool _copyInProgress;

    public FieldCopyButton()
    {
        InitializeComponent();
        _feedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _feedbackTimer.Tick += (_, _) => ResetFeedback();
        Unloaded += (_, _) => ResetFeedback();
        ResetFeedback();
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? FieldName
    {
        get => (string?)GetValue(FieldNameProperty);
        set => SetValue(FieldNameProperty, value);
    }

    private static void ContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FieldCopyButton control && control.CopyButton is not null)
        {
            control.ResetFeedback();
        }
    }

    private void ResetFeedback()
    {
        _feedbackTimer?.Stop();
        CopyIcon.Kind = PackIconKind.ContentCopy;
        CopyIcon.Foreground = (Brush)FindResource("MutedTextBrush");
        CopyButton.ToolTip = string.IsNullOrEmpty(Text)
            ? "В поле пока нет текста"
            : "Скопировать текст поля";
        AutomationProperties.SetName(CopyButton, string.IsNullOrWhiteSpace(FieldName)
            ? "Скопировать текст поля"
            : $"Скопировать поле «{FieldName}»");
        UpdateAvailability();
    }

    private void UpdateAvailability() =>
        CopyButton.IsEnabled = !_copyInProgress && !string.IsNullOrEmpty(Text);

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = Text;
        if (_copyInProgress || string.IsNullOrEmpty(text))
        {
            return;
        }

        _feedbackTimer.Stop();
        _copyInProgress = true;
        UpdateAvailability();
        try
        {
            // Clipboard ownership can briefly be held by another application.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    break;
                }
                catch (ExternalException) when (attempt < 3)
                {
                    await Task.Delay(80);
                }
            }

            CopyIcon.Kind = PackIconKind.Check;
            CopyIcon.Foreground = (Brush)FindResource("SuccessBrush");
            CopyButton.ToolTip = "Текст скопирован";
        }
        catch (ExternalException)
        {
            CopyIcon.Kind = PackIconKind.AlertCircleOutline;
            CopyIcon.Foreground = (Brush)FindResource("WarningBrush");
            CopyButton.ToolTip = "Буфер обмена занят. Нажмите ещё раз.";
        }
        finally
        {
            _copyInProgress = false;
            UpdateAvailability();
            if (IsLoaded)
            {
                _feedbackTimer.Start();
            }
        }
    }
}
