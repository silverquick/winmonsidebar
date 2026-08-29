using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Shell;
using Monitor.Windows.Native;

namespace Monitor.App.Tests;

[TestClass]
public sealed class AppBarHostFullscreenTests
{
    [TestMethod]
    [DataRow("Progman", true)]
    [DataRow("WorkerW", true)]
    [DataRow("Shell_TrayWnd", true)]
    [DataRow("Shell_SecondaryTrayWnd", true)]
    [DataRow("Chrome_WidgetWin_1", false)]
    [DataRow("MozillaWindowClass", false)]
    [DataRow("ApplicationFrameWindow", false)]
    [DataRow("CabinetWClass", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsExcludedClassName_CorrectlyIdentifiesDesktopAndTaskbarClasses(string? className, bool expectedExcluded)
    {
        bool actual = AppBarHost.IsExcludedClassName(className);
        Assert.AreEqual(expectedExcluded, actual, $"Class '{className}' の除外判定が期待値と一致しません。");
    }

    [TestMethod]
    public void IsWindowCoveringMonitor_WhenWindowMatchesOrExceedsMonitor_ReturnsTrue()
    {
        var monitorRect = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // ぴったり一致
        var exactRect = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        Assert.IsTrue(AppBarHost.IsWindowCoveringMonitor(exactRect, monitorRect), "モニタと完全に一致する矩形はフルスクリーンと判定されること。");

        // モニタ外側へはみ出るフルスクリーン（マルチモニタやDWMオフセット等）
        var largerRect = new RECT { Left = -10, Top = -10, Right = 1930, Bottom = 1090 };
        Assert.IsTrue(AppBarHost.IsWindowCoveringMonitor(largerRect, monitorRect), "モニタ矩形を包含する矩形はフルスクリーンと判定されること。");
    }

    [TestMethod]
    public void IsWindowCoveringMonitor_WhenWindowDoesNotCoverMonitor_ReturnsFalse()
    {
        var monitorRect = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // 右側がサイドバー分空いている（最大化通常ウィンドウ）
        var normalMaximized = new RECT { Left = 0, Top = 0, Right = 1580, Bottom = 1080 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(normalMaximized, monitorRect), "作業領域内の最大化ウィンドウはフルスクリーンと誤判定されないこと。");

        // ウィンドウが中央に小さく表示されている
        var centeredWindow = new RECT { Left = 400, Top = 200, Right = 1400, Bottom = 800 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(centeredWindow, monitorRect), "通常ウィンドウはフルスクリーンと判定されないこと。");

        // 上部が足りない
        var topMissing = new RECT { Left = 0, Top = 50, Right = 1920, Bottom = 1080 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(topMissing, monitorRect));

        // 下部が足りない
        var bottomMissing = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1000 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(bottomMissing, monitorRect));

        // 左部が足りない
        var leftMissing = new RECT { Left = 50, Top = 0, Right = 1920, Bottom = 1080 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(leftMissing, monitorRect));
    }

    [TestMethod]
    public void IsWindowCoveringMonitor_WithSecondaryMonitorOffsets_EvaluatesCorrectly()
    {
        // 2枚目のモニタ (Left: 1920, Top: 0, Right: 3840, Bottom: 1080)
        var secondaryMonitor = new RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };

        // 2枚目モニタでのフルスクリーン
        var fullscreenSecondary = new RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };
        Assert.IsTrue(AppBarHost.IsWindowCoveringMonitor(fullscreenSecondary, secondaryMonitor));

        // 1枚目モニタのフルスクリーンウィンドウを2枚目のモニタ矩形と比較した場合は false
        var primaryFullscreen = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        Assert.IsFalse(AppBarHost.IsWindowCoveringMonitor(primaryFullscreen, secondaryMonitor));
    }
}
