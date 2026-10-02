using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UISI_Doc_Constructor.Services;

public sealed record DesktopCapture(BitmapSource Image, int Left, int Top);

public static class ScreenCaptureService
{
    public static async Task<BitmapSource?> SelectAsync(Window owner)
    {
        var windows = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible)
            .Select(window => (Window: window, Opacity: window.Opacity)).ToArray();
        try
        {
            // Hide the visuals without ending a modal window's ShowDialog frame. Calling
            // Hide/Show would make the filling dialog modeless and invalidate DialogResult.
            foreach (var entry in windows) entry.Window.SetCurrentValue(Window.OpacityProperty, 0d);
            await Task.Delay(200);
            var capture = CaptureDesktop();
            var overlay = new ScreenRegionWindow(capture);
            return overlay.ShowDialog() == true ? overlay.SelectedImage : null;
        }
        finally
        {
            foreach (var entry in windows) entry.Window.SetCurrentValue(Window.OpacityProperty, entry.Opacity);
            owner.Activate();
            owner.Focus();
        }
    }

    public static DesktopCapture CaptureDesktop()
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new InvalidOperationException("Не удалось определить доступную область экрана.");
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            var header = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) != height)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            image.Freeze();
            return new DesktopCapture(image, left, top);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static Int32Rect ToPixelRect(Rect selection, double displayWidth, double displayHeight, int pixelWidth, int pixelHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0 || selection.IsEmpty) return Int32Rect.Empty;
        var left = (int)Math.Clamp(Math.Floor(selection.Left * pixelWidth / displayWidth), 0, pixelWidth);
        var top = (int)Math.Clamp(Math.Floor(selection.Top * pixelHeight / displayHeight), 0, pixelHeight);
        var right = (int)Math.Clamp(Math.Ceiling(selection.Right * pixelWidth / displayWidth), left, pixelWidth);
        var bottom = (int)Math.Clamp(Math.Ceiling(selection.Bottom * pixelHeight / displayHeight), top, pixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint count, [Out] byte[] pixels, ref BitmapInfo info, uint usage);
}
