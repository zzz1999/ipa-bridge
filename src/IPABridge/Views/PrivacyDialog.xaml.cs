using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using IPABridge.Infrastructure;

namespace IPABridge.Views;

public partial class PrivacyDialog : Window
{
    public PrivacyDialog()
    {
        InitializeComponent();
        SourceInitialized += PrivacyDialog_OnSourceInitialized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void PrivacyDialog_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void PrivacyDialog_OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowBackdropService.Apply(this);

        var ownerHandle = new WindowInteropHelper(Owner ?? this).Handle;
        var monitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);
        var monitorInformation = new MonitorInformation
        {
            Size = Marshal.SizeOf<MonitorInformation>()
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInformation))
        {
            ClampToWorkArea(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return;
        }

        var workAreaTopLeft = transform.Value.Transform(
            new Point(monitorInformation.WorkArea.Left, monitorInformation.WorkArea.Top));
        var workAreaBottomRight = transform.Value.Transform(
            new Point(monitorInformation.WorkArea.Right, monitorInformation.WorkArea.Bottom));
        var availableWidth = workAreaBottomRight.X - workAreaTopLeft.X;
        var availableHeight = workAreaBottomRight.Y - workAreaTopLeft.Y;
        ClampToWorkArea(availableWidth, availableHeight);
    }

    private void ClampToWorkArea(double availableWidth, double availableHeight)
    {
        var maximumWidth = Math.Max(1, availableWidth - 32);
        var maximumHeight = Math.Max(1, availableHeight - 32);
        MinWidth = Math.Min(MinWidth, maximumWidth);
        MinHeight = Math.Min(MinHeight, maximumHeight);
        MaxWidth = maximumWidth;
        MaxHeight = maximumHeight;
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInformation monitorInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
