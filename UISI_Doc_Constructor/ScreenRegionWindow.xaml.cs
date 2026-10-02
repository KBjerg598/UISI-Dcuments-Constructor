using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class ScreenRegionWindow : Window
{
    private readonly DesktopCapture _capture;
    private Point? _start;
    private Rect _selection;

    public ScreenRegionWindow(DesktopCapture capture)
    {
        InitializeComponent();
        _capture = capture;
        DesktopImage.Source = capture.Image;
        Surface.SizeChanged += (_, _) => DrawSelection();
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    public BitmapSource? SelectedImage { get; private set; }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Use physical virtual-desktop coordinates, including monitors left/above the primary monitor.
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), _capture.Left, _capture.Top,
                _capture.Image.PixelWidth, _capture.Image.PixelHeight, 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        _selection = new Rect(_start.Value, _start.Value);
        Surface.CaptureMouse();
        DrawSelection();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_start is null) return;
        var current = e.GetPosition(Surface);
        current.X = Math.Clamp(current.X, 0, Surface.ActualWidth);
        current.Y = Math.Clamp(current.Y, 0, Surface.ActualHeight);
        _selection = new Rect(_start.Value, current);
        DrawSelection();
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        Surface_MouseMove(sender, e);
        _start = null;
        Surface.ReleaseMouseCapture();
        var pixels = ScreenCaptureService.ToPixelRect(_selection, Surface.ActualWidth, Surface.ActualHeight,
            _capture.Image.PixelWidth, _capture.Image.PixelHeight);
        if (pixels.Width < 8 || pixels.Height < 8)
        {
            _selection = Rect.Empty;
            DrawSelection();
            return;
        }
        var crop = new CroppedBitmap(_capture.Image, pixels);
        crop.Freeze();
        SelectedImage = crop;
        DialogResult = true;
    }

    private void DrawSelection()
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight)));
        var visible = !_selection.IsEmpty && _selection.Width > 0 && _selection.Height > 0;
        if (visible)
        {
            geometry.Children.Add(new RectangleGeometry(_selection));
            Canvas.SetLeft(SelectionBorder, _selection.Left);
            Canvas.SetTop(SelectionBorder, _selection.Top);
            SelectionBorder.Width = _selection.Width;
            SelectionBorder.Height = _selection.Height;
        }
        SelectionBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Shade.Data = geometry;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
