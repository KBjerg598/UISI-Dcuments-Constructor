using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UISI_Doc_Constructor;

public partial class MaterialTitleBar : UserControl
{
    private Window? _window;

    public MaterialTitleBar()
    {
        InitializeComponent();
        Loaded += MaterialTitleBar_Loaded;
        Unloaded += MaterialTitleBar_Unloaded;
    }

    private void MaterialTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += Window_StateChanged;
        MaximizeButton.Visibility = _window.ResizeMode is ResizeMode.NoResize
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateMaximizeGlyph();
    }

    private void MaterialTitleBar_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null || _window.ResizeMode is ResizeMode.NoResize)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _window?.Close();
    }

    private void UpdateMaximizeGlyph()
    {
        if (_window is null)
        {
            return;
        }

        var isMaximized = _window.WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(
            isMaximized
                ? "M 4,1 L 12,1 L 12,9 M 1,4 L 9,4 L 9,12 L 1,12 Z"
                : "M 1,1 L 12,1 L 12,12 L 1,12 Z");
        MaximizeButton.ToolTip = isMaximized ? "Восстановить" : "Развернуть";
    }
}
