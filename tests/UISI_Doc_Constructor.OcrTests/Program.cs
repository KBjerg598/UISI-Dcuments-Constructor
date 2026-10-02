using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UISI_Doc_Constructor;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var fixture = DrawText("IGNORE THIS", "Contract 26-1536");
        if (args.Contains("--source"))
        {
            Velopack.VelopackApp.Build().Run();
            var app = new App();
            app.InitializeComponent();
            var window = new Window { Title = "Текст для проверки OCR", Width = 820, Height = 380,
                Style = (Style)app.FindResource("AppWindowStyle"), WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new System.Windows.Controls.Image { Source = fixture, Stretch = Stretch.Uniform, Margin = new Thickness(20) } };
            window.ShowDialog();
            return 0;
        }
        if (args.Contains("--preview"))
        {
            Velopack.VelopackApp.Build().Run();
            var app = new App();
            app.InitializeComponent();
            var dialog = new OcrReviewWindow(fixture, "Номер договора", "Существующее значение");
            dialog.ShowDialog();
            Console.WriteLine("Result: " + dialog.ResultText);
            return 0;
        }
        TestScreenSelectionHitTesting(fixture);
        Assert(OcrTextComposer.Insert("Existing", "New", true) == "Existing" + Environment.NewLine + "New", "append separator");
        Assert(OcrTextComposer.Insert("Existing\n", "New", true) == "Existing\nNew", "existing whitespace");
        Assert(OcrTextComposer.Insert("Existing", "New", false) == "New", "replace");
        Assert(OcrTextComposer.Insert("", "New", true) == "New", "append to empty");
        var words = new[]
        {
            new OcrWord { Text = "Hello", Line = 0, Bounds = new Rect(1, 1, 20, 10) },
            new OcrWord { Text = "ignore", Line = 0, Bounds = new Rect(22, 1, 20, 10), Included = false },
            new OcrWord { Text = "world", Line = 0, Bounds = new Rect(43, 1, 20, 10) },
            new OcrWord { Text = "Next", Line = 1, Bounds = new Rect(1, 15, 20, 10) }
        };
        Assert(OcrTextComposer.Compose(words) == "Hello world" + Environment.NewLine + "Next", "word exclusion and lines");
        words[1].Included = true;
        Assert(OcrTextComposer.Compose(words).StartsWith("Hello ignore world"), "word restore");
        Assert(ScreenCaptureService.ToPixelRect(new Rect(20, 10, 100, 50), 400, 200, 800, 400)
            == new Int32Rect(40, 20, 200, 100), "DPI crop mapping");
        Assert(ScreenCaptureService.ToPixelRect(new Rect(-20, -10, 500, 250), 400, 200, 800, 400)
            == new Int32Rect(0, 0, 800, 400), "crop clamping");

        var ocr = new LocalOcrService();
        CheckRecognition(ocr, DrawText("Українська громада", "Номер 26-1536"), "ukr", "Українська", "26-1536");
        CheckRecognition(ocr, DrawText("Сохраненные данные", "Номер 26-1536"), "rus", "данные", "26-1536");
        CheckRecognition(ocr, fixture, "eng", "IGNORE", "26-1536");
        CheckRecognition(ocr, DrawText("Українська громада", "Contract 26-1536"), "auto", "громада", "Contract");
        var masked = ocr.RecognizeAsync(fixture, [new Rect(-1, -1, 962, 90)], "eng").GetAwaiter().GetResult();
        var maskedText = OcrTextComposer.Compose(masked);
        Assert(!maskedText.Contains("IGNORE") && maskedText.Contains("26-1536"), "mask excludes only selected region");
        var blank = LocalOcrService.ApplyMasks(fixture, [new Rect(0, 0, fixture.PixelWidth, fixture.PixelHeight)]);
        Assert(ocr.RecognizeAsync(blank, [], "eng").GetAwaiter().GetResult().Count == 0, "blank screenshot");
        try
        {
            ocr.RecognizeAsync(fixture, [], "eng", new CancellationToken(true)).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation did not stop recognition.");
        }
        catch (OperationCanceledException) { }
        TestModalCaptureCancellation();
        Console.WriteLine("All local OCR tests passed: UA/RU/EN/mixed, word boxes, masks, blank image, cancellation, append/replace, DPI crop mapping, screen-selection mouse hit testing, and modal capture cancellation.");
        return 0;
    }

    private static void TestModalCaptureCancellation()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var dialog = new Window { Title = "OCR modal cancellation regression check", Width = 480, Height = 200,
            Opacity = 0.85, Style = null, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        Exception? failure = null;
        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var overlay = app.Windows.OfType<ScreenRegionWindow>().SingleOrDefault(window => window.IsVisible);
            if (overlay is null && ++attempts < 200) return;
            timer.Stop();
            try
            {
                Assert(overlay is not null, "screen capture opens the selection overlay");
                Assert(dialog.IsVisible && dialog.Opacity == 0, "modal dialog stays open but invisible during capture");
            }
            catch (Exception exception) { failure = exception; }
            if (overlay is not null) overlay.DialogResult = false;
            else dialog.Close();
        };
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                timer.Start();
                var result = await ScreenCaptureService.SelectAsync(dialog);
                Assert(result is null, "cancelled capture has no image");
                Assert(dialog.IsVisible && dialog.Opacity == 0.85, "capture restores original dialog opacity");
                // This must remain valid: Hide/Show would have ended ShowDialog too early.
                dialog.DialogResult = true;
            }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        };
        var completed = dialog.ShowDialog();
        timer.Stop();
        app.Shutdown();
        if (failure is not null) throw new InvalidOperationException("Modal capture regression failed.", failure);
        Assert(completed == true, "modal dialog can complete normally after capture cancellation");
    }

    private static void CheckRecognition(LocalOcrService ocr, BitmapSource image, string language, params string[] expected)
    {
        var words = ocr.RecognizeAsync(image, [], language).GetAwaiter().GetResult();
        var text = OcrTextComposer.Compose(words);
        Console.WriteLine(language + ": " + text.Replace(Environment.NewLine, " | "));
        foreach (var fragment in expected) Assert(text.Contains(fragment, StringComparison.OrdinalIgnoreCase), "recognition " + fragment);
        Assert(words.All(word => word.Bounds.Width > 0 && word.Bounds.Height > 0
            && word.Bounds.Right <= image.PixelWidth && word.Bounds.Bottom <= image.PixelHeight), "word bounding boxes");
    }

    private static void TestScreenSelectionHitTesting(BitmapSource fixture)
    {
        var window = new ScreenRegionWindow(new DesktopCapture(fixture, 0, 0));
        var surface = (System.Windows.Controls.Grid)window.FindName("Surface");
        surface.Measure(new Size(1920, 1080));
        surface.Arrange(new Rect(0, 0, 1920, 1080));
        Assert(surface.Background is not null, "selection surface has a hit-testable background");
        foreach (var point in new[] { new Point(1, 1), new Point(960, 60), new Point(400, 400), new Point(1919, 1079) })
        {
            DependencyObject? target = null;
            // The window is intentionally not shown. Test the visual hit area and ignore decorative
            // children using the same IsHitTestVisible policy as routed mouse input.
            VisualTreeHelper.HitTest(surface,
                visual => visual is UIElement element && !element.IsHitTestVisible
                    ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                hit => { target = hit.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
            Assert(ReferenceEquals(target, surface), "selection surface receives mouse at " + point);
        }
        window.Close();
    }

    private static BitmapSource DrawText(string firstLine, string secondLine)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 960, 200));
            context.DrawText(new FormattedText(firstLine, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 32, Brushes.Black, 1), new Point(24, 20));
            context.DrawText(new FormattedText(secondLine, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 32, Brushes.Black, 1), new Point(24, 110));
        }
        var bitmap = new RenderTargetBitmap(960, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Test failed: " + description);
    }

}
