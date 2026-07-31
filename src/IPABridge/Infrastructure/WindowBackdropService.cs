using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPABridge.Infrastructure;

public static class WindowBackdropService
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var cornerPreference = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var backdropType = 2;
            _ = DwmSetWindowAttribute(handle, 38, ref backdropType, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
