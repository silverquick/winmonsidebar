using System.Runtime.InteropServices;

namespace Monitor.Windows.Native;

public enum DwmSystemBackdropType
{
    Auto = 0,
    None = 1,
    MainWindow = 2, // Mica
    TransientWindow = 3, // Acrylic
    TabbedWindow = 4,
}

public enum DwmWindowCornerPreference
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3,
}

public static partial class DwmApi
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Applies dark-mode titlebar, corner rounding and a modern system backdrop to a top-level
    /// window. Each attribute is set independently and failures (e.g. an older Windows build that does not
    /// know DWMWA_SYSTEMBACKDROP_TYPE) are swallowed so the rest still get applied.</summary>
    public static void ApplyModernFrame(IntPtr hwnd, DwmSystemBackdropType backdrop, bool darkMode, DwmWindowCornerPreference corner)
    {
        try
        {
            int dark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch
        {
        }

        try
        {
            int cornerValue = (int)corner;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerValue, sizeof(int));
        }
        catch
        {
        }

        try
        {
            int backdropValue = (int)backdrop;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropValue, sizeof(int));
        }
        catch
        {
        }
    }
}
