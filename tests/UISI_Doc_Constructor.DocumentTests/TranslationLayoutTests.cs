using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using UISI_Doc_Constructor;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

internal static class TranslationLayoutTests
{
    public static void Run(string root)
    {
        Velopack.VelopackApp.Build().Run();
        var app = new Application();
        System.Reflection.Assembly.Load("MaterialDesignThemes.Wpf");
        System.Reflection.Assembly.Load("MaterialDesignColors");
        // Reuse the actual application styles without starting the production window or template library.
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ApplicationStyles.xaml"));
        const string localNamespace = "clr-namespace:UISI_Doc_Constructor";
        const string qualifiedNamespace = localNamespace + ";assembly=UISI_Doc_Constructor";
        foreach (var element in source.Descendants().Where(element => element.Name.NamespaceName == localNamespace))
            element.Name = XName.Get(element.Name.LocalName, qualifiedNamespace);
        var dictionary = source.Descendants().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
        foreach (var declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            dictionary.SetAttributeValue(declaration.Name, declaration.Value == "clr-namespace:UISI_Doc_Constructor"
                ? "clr-namespace:UISI_Doc_Constructor;assembly=UISI_Doc_Constructor" : declaration.Value);
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var import = new TemplateNameWindow("Тестовый шаблон", []);
        CheckWindow(import, root, "import-template", window =>
        {
            var button = (Button)window.FindName("ConfirmButton");
            CheckButton(button, "Импортировать", extraWidth: 25);
        });
        var service = new TranslationPackageService(sessionRoot: Path.Combine(root, "LayoutSessions"));
        using var session = service.OpenPackage(Path.Combine(root, "edited.uisi-translation"));
        var translation = new TranslationFillWindow(service, session);
        CheckWindow(translation, root, "editable-translation", window =>
        {
            CheckButton((Button)window.FindName("FinalDocxButton"), "Создать финальный DOCX");
            var sourceBox = Descendants<TextBox>(window).First(box =>
                BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == nameof(TranslationFieldValueItem.SourceValue));
            Assert(!sourceBox.IsReadOnly, "new-package source TextBox is editable");
            var value = (TranslationFieldValueItem)sourceBox.DataContext;
            sourceBox.Text = "Исправлено через украинскую строку";
            Assert(value.SourceValue == sourceBox.Text && translation.Fields.All(field => field.SourceValue == sourceBox.Text),
                "typing in the source box updates the model and sibling row");
            sourceBox.Text += Environment.NewLine + "Друга строка українського тексту";
            Assert(value.SourceValue.Contains(Environment.NewLine), "source editing supports multiline values");
            sourceBox.Text = session.Manifest.Fields.Single(field => field.Tag == value.Source!.Tag).Value;
        });
        using var legacy = service.OpenPackage(Path.Combine(root, "old.uisi-translation"));
        var legacyWindow = new TranslationFillWindow(service, legacy);
        CheckWindow(legacyWindow, root, "legacy-translation", window =>
        {
            Assert(Descendants<TextBox>(window).Where(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path ==
                nameof(TranslationFieldValueItem.SourceValue)).All(box => box.IsReadOnly), "legacy sources remain selectable but read-only");
        });
        app.Shutdown();
        Console.WriteLine("Window layout: import/final buttons, minimum window size, 100/150/200% render scaling, editable bindings and legacy read-only state passed.");
    }

    private static void CheckWindow(Window window, string root, string name, Action<Window> check)
    {
        window.ShowActivated = false;
        window.Show();
        FlushLayout(window);
        check(window);
        SavePreview(window, Path.Combine(root, name + "-100.png"), 1);
        SavePreview(window, Path.Combine(root, name + ".png"), 1.5);
        if (window is TranslationFillWindow)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            FlushLayout(window);
            check(window);
            var final = (Button)window.FindName("FinalDocxButton");
            var bounds = final.TransformToAncestor(window).TransformBounds(new Rect(final.RenderSize));
            Assert(bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight, "footer stays inside the minimum-size window");
            SavePreview(window, Path.Combine(root, name + "-minimum.png"), 2);
        }
        window.Close();
    }

    private static void CheckButton(Button button, string label, double extraWidth = 0)
    {
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            button.FontSize, Brushes.Black, 1);
        Assert(double.IsNaN(button.Width), "button measures its content instead of imposing a clipping fixed width");
        Assert(button.ActualWidth - button.Padding.Left - button.Padding.Right >= text.WidthIncludingTrailingWhitespace + extraWidth,
            "button has room for its full label and icon: " + label);
    }

    private static void FlushLayout(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static void SavePreview(Window window, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Translation UI regression failed: " + message);
    }
}
