using System;
using System.Runtime.InteropServices;

namespace SPTCoffeeModManager;

public static class WindowCornerHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public enum DwmWindowCorner
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static bool TrySetWindowCornerPreference(IntPtr hwnd, DwmWindowCorner preference)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;

            // Apply only on Windows 11 (build 22000+) to be safe
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return false;

            int pref = (int)preference;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }
}

