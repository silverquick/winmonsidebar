namespace Monitor.App.Shell;

/// <summary>Screen edge an AppBar is docked to. Values are intentionally identical to the
/// Win32 ABE_* constants (Shell32.ABE_LEFT / ABE_TOP / ABE_RIGHT / ABE_BOTTOM) so they can be
/// cast directly when filling in APPBARDATA.uEdge.</summary>
public enum AppBarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}
