using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace UISI_Doc_Constructor;

public partial class OcrReviewWindow : Window
{
    private readonly BitmapSource _image;
    private readonly string _existingText;
    private readonly List<Rect> _masks = [];
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<OcrWord> _words = [];
    private Point? _maskStart;
    private Rect _draftMask = Rect.Empty;
    private bool _busy;
    private bool _closing;
    private bool _updatingResult;
    private bool _manuallyEdited;

    public OcrReviewWindow(BitmapSource image, string fieldName, string existingText)
    {
        InitializeComponent();
        _image = image;
        _existingText = existingText;
        FieldLabel.Text = "Поле: " + fieldName;
        FieldLabel.ToolTip = fieldName;
        PreviewImage.Source = image;
        PictureSurface.Width = image.PixelWidth;
        PictureSurface.Height = image.PixelHeight;
        AppendButton.Visibility = string.IsNullOrEmpty(existingText) ? Visibility.Collapsed : Visibility.Visible;
        ReplaceButton.Content = string.IsNullOrEmpty(existingText) ? "Вставить в поле" : "Заменить текст";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += async (_, _) => { FitImage(); await RecognizeAsync(); };
        Closed += (_, _) =>
        {
            _closing = true;
            _cancellation.Cancel();
            if (!_busy) _cancellation.Dispose();
        };
    }

    public string ResultText { get; private set; } = string.Empty;

    private async Task RecognizeAsync()
    {
        if (_busy || _closing) return;
        _busy = true;
        StatusText.Text = "Распознаём текст на этом компьютере…";
        SetBusyState();
        try
        {
            var language = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
            var words = await new LocalOcrService().RecognizeAsync(_image, _masks.ToArray(), language, _cancellation.Token);
            if (_closing) return;
            _words = words;
            _manuallyEdited = false;
            UpdateResult();
            RenderOverlay();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing)
                StatusText.Text = "Не удалось распознать: " + exception.Message + " Можно повторить попытку или ввести текст справа вручную.";
        }
        finally
        {
            _busy = false;
            if (!_closing) SetBusyState();
            else _cancellation.Dispose();
        }
    }

    private async void Recognize_Click(object sender, RoutedEventArgs e)
    {
        if (_manuallyEdited && MessageBox.Show(this, "Повторное распознавание заменит ваши правки текста. Продолжить?",
                "Распознать заново", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RecognizeAsync();
    }

    private void SetBusyState()
    {
        RecognizeButton.IsEnabled = !_busy;
        LanguageBox.IsEnabled = !_busy;
        ImageTools.IsEnabled = !_busy;
        OverlayCanvas.IsEnabled = !_busy;
        ResultBox.IsReadOnly = _busy;
        BusyBar.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        UndoMaskButton.IsEnabled = !_busy && _masks.Count > 0;
        UpdateInsertButtons();
    }

    private void UpdateResult()
    {
        _updatingResult = true;
        ResultBox.Text = OcrTextComposer.Compose(_words);
        _updatingResult = false;
        var included = _words.Count(word => word.Included);
        var uncertain = _words.Count(word => word.Included && word.Confidence < 65);
        StatusText.Text = _words.Count == 0
            ? "Текст не найден. Попробуйте увеличить исходный текст на экране, выбрать язык или ввести его вручную."
            : $"Слов: {included} из {_words.Count}." + (uncertain > 0 ? $" Требуют особой проверки: {uncertain}." : " Проверьте результат перед вставкой.");
    }

    private void RenderOverlay()
    {
        if (OverlayCanvas is null) return;
        OverlayCanvas.Children.Clear();
        foreach (var word in _words)
        {
            var button = new Button
            {
                Width = Math.Max(2, word.Bounds.Width), Height = Math.Max(2, word.Bounds.Height),
                Style = (Style)FindResource("OcrWordButtonStyle"),
                Background = new SolidColorBrush(word.Included ? Color.FromArgb(32, 101, 89, 232) : Color.FromArgb(145, 240, 91, 105)),
                BorderBrush = new SolidColorBrush(word.Included ? Color.FromRgb(101, 89, 232) : Color.FromRgb(212, 51, 72)),
                IsHitTestVisible = MaskTool.IsChecked != true,
                ToolTip = $"{word.Text} · уверенность {word.Confidence:0}%\nНажмите, чтобы {(word.Included ? "исключить" : "вернуть")} слово"
            };
            AutomationProperties.SetName(button, $"{(word.Included ? "Исключить" : "Вернуть")} слово «{word.Text}»");
            button.Click += (_, _) => ToggleWord(word);
            Canvas.SetLeft(button, word.Bounds.X);
            Canvas.SetTop(button, word.Bounds.Y);
            OverlayCanvas.Children.Add(button);
        }
        foreach (var mask in _masks) DrawMask(mask);
        if (!_draftMask.IsEmpty) DrawMask(_draftMask);
        OverlayCanvas.Cursor = MaskTool.IsChecked == true ? Cursors.Cross : Cursors.Arrow;
    }

    private void ToggleWord(OcrWord word)
    {
        if (_busy) return;
        if (_manuallyEdited && MessageBox.Show(this, "Изменение выбора слов соберёт текст заново и заменит ваши ручные правки. Продолжить?",
                "Выбор слов", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _manuallyEdited = false;
        word.Included = !word.Included;
        UpdateResult();
        RenderOverlay();
    }

    private void DrawMask(Rect rect)
    {
        var shape = new Rectangle { Width = rect.Width, Height = rect.Height,
            Fill = new SolidColorBrush(Color.FromArgb(210, 23, 24, 43)), Stroke = Brushes.White, StrokeThickness = 1,
            IsHitTestVisible = false };
        Canvas.SetLeft(shape, rect.Left);
        Canvas.SetTop(shape, rect.Top);
        OverlayCanvas.Children.Add(shape);
    }

    private void Mask_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || MaskTool.IsChecked != true) return;
        if (_manuallyEdited && MessageBox.Show(this, "После замазывания текст нужно распознать заново. Ручные правки будут сброшены. Продолжить?",
                "Замазать область", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _maskStart = e.GetPosition(OverlayCanvas);
        OverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Mask_MouseMove(object sender, MouseEventArgs e)
    {
        if (_maskStart is null) return;
        var point = e.GetPosition(OverlayCanvas);
        point.X = Math.Clamp(point.X, 0, _image.PixelWidth);
        point.Y = Math.Clamp(point.Y, 0, _image.PixelHeight);
        _draftMask = new Rect(_maskStart.Value, point);
        RenderOverlay();
    }

    private void Mask_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_maskStart is null) return;
        Mask_MouseMove(sender, e);
        _maskStart = null;
        OverlayCanvas.ReleaseMouseCapture();
        if (!_draftMask.IsEmpty && _draftMask.Width >= 2 && _draftMask.Height >= 2)
        {
            _masks.Add(_draftMask);
            StatusText.Text = "Область замазана. Нажмите «Распознать заново», чтобы исключить её из текста.";
            // Existing recognition must not remain insertable after an unprocessed mask.
            _words = [];
            _updatingResult = true;
            ResultBox.Clear();
            _updatingResult = false;
            _manuallyEdited = false;
        }
        _draftMask = Rect.Empty;
        RenderOverlay();
        UndoMaskButton.IsEnabled = _masks.Count > 0;
    }

    private void UndoMask_Click(object sender, RoutedEventArgs e)
    {
        if (_masks.Count == 0) return;
        _masks.RemoveAt(_masks.Count - 1);
        RenderOverlay();
        UndoMaskButton.IsEnabled = _masks.Count > 0;
        StatusText.Text = "Маска отменена. Нажмите «Распознать заново» для обновления текста.";
    }

    private void Tool_Changed(object sender, RoutedEventArgs e) => RenderOverlay();
    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is not null) ImageScale.ScaleX = ImageScale.ScaleY = e.NewValue / 100;
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();
    private void FitImage() => ZoomSlider.Value = Math.Clamp(100 * Math.Min((ImageScroll.ActualWidth - 18) / _image.PixelWidth,
        (ImageScroll.ActualHeight - 18) / _image.PixelHeight), 10, 200);

    private void Result_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingResult && IsLoaded) _manuallyEdited = true;
        UpdateInsertButtons();
    }
    private void UpdateInsertButtons()
    {
        if (ReplaceButton is null || AppendButton is null || ResultBox is null) return;
        ReplaceButton.IsEnabled = AppendButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(ResultBox.Text);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Replace_Click(object sender, RoutedEventArgs e) => Insert(false);
    private void Append_Click(object sender, RoutedEventArgs e) => Insert(true);
    private void Insert(bool append)
    {
        if (_busy || string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        ResultText = OcrTextComposer.Insert(_existingText, ResultBox.Text, append);
        DialogResult = true;
    }
}
