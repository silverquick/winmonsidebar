using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Diagnostics;
using Monitor.App.Settings;
using Monitor.App.ViewModels;
using Monitor.Core;

namespace Monitor.App.Tests;

/// <summary>
/// B-03: 展開トグル由来の設定保存が debounce され、バックグラウンドで書き込まれることの検証。
/// 実ユーザーの設定ファイルには書かない（一時フォルダか、メモリ上の書き込み先だけを使う）。
/// </summary>
[TestClass]
public sealed class SettingsDebounceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Schedule_Repeatedly_CoalescesIntoSingleWriteOfLatestContent_OnFlush()
    {
        var writes = new ConcurrentQueue<string>();

        // タイマーでは発火しない長さにして、まとめられたことを Flush で決定的に確認する。
        using var writer = new DebouncedSettingsWriter(writes.Enqueue, TimeSpan.FromHours(1));

        for (int i = 1; i <= 5; i++)
        {
            writer.Schedule($"json-{i}");
        }

        Assert.AreEqual(0, writes.Count, "Nothing should be written before the debounce delay elapses.");

        writer.Flush();

        CollectionAssert.AreEqual(new[] { "json-5" }, writes.ToArray());

        // 保留が無ければ Flush しても書き込まない。
        writer.Flush();
        Assert.AreEqual(1, writes.Count);
    }

    [TestMethod]
    public void Schedule_WritesOnBackgroundThreadAfterDelay()
    {
        var writes = new ConcurrentQueue<(string Json, int ThreadId)>();
        using var written = new ManualResetEventSlim();
        using var writer = new DebouncedSettingsWriter(
            json =>
            {
                writes.Enqueue((json, Environment.CurrentManagedThreadId));
                written.Set();
            },
            TimeSpan.FromMilliseconds(20));

        writer.Schedule("a");
        writer.Schedule("b");

        Assert.IsTrue(written.Wait(WaitTimeout), "Debounced write should happen after the delay.");
        writer.Flush();

        var all = writes.ToArray();
        Assert.AreEqual(1, all.Length);
        Assert.AreEqual("b", all[0].Json);
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, all[0].ThreadId, "Write should run off the calling thread.");
    }

    [TestMethod]
    public void WriteNow_DiscardsPendingAndWritesImmediately()
    {
        var writes = new ConcurrentQueue<string>();
        using var writer = new DebouncedSettingsWriter(writes.Enqueue, TimeSpan.FromHours(1));

        writer.Schedule("old");
        writer.WriteNow("new");
        writer.Flush();

        CollectionAssert.AreEqual(new[] { "new" }, writes.ToArray());
    }

    [TestMethod]
    public void WriterThrows_DoesNotPropagate()
    {
        using var writer = new DebouncedSettingsWriter(_ => throw new IOException("disk full"), TimeSpan.FromHours(1));

        writer.Schedule("x");
        writer.Flush();
        writer.WriteNow("y");
    }

    [TestMethod]
    public void WriteJson_WritesViaTempFileAndReplaces()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            SettingsStore.WriteJson(path, "{\"a\":1}");
            SettingsStore.WriteJson(path, "{\"a\":2}");

            Assert.AreEqual("{\"a\":2}", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), "Temp file should be moved into place.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveDebounced_ThenFlush_WritesLatestSettingsToRedirectedPath()
    {
        // TestAssemblySetup で一時フォルダへ差し替え済み。念のため実ユーザーのパスでないことを確認する。
        StringAssert.StartsWith(SettingsStore.FilePath, Path.GetTempPath());

        var settings = new AppSettings();
        settings.ExpandedSections["cpu"] = false;
        SettingsStore.SaveDebounced(settings);

        // スナップショットは呼び出し時点で取られるため、後からの変更は次の保存要求まで反映されない。
        settings.ExpandedSections["cpu"] = true;
        settings.ExpandedSections["gpu"] = false;
        SettingsStore.SaveDebounced(settings);

        SettingsStore.FlushPendingSaves();

        AppSettings loaded = SettingsStore.Load();
        Assert.IsTrue(loaded.ExpandedSections["cpu"]);
        Assert.IsFalse(loaded.ExpandedSections["gpu"]);
    }

    [STATestMethod]
    public void ExpandedToggle_UsesInjectedSaver_WithSharedSettingsOnUiThread()
    {
        var hub = MetricsHub.CreateForTest();
        var settings = new AppSettings();
        var saved = new List<AppSettings>();

        using var vm = new SidebarViewModel(hub, Dispatcher.CurrentDispatcher, settings, saved.Add);

        vm.IsCpuExpanded = false;
        vm.IsCpuExpanded = true;
        vm.IsCpuExpanded = true; // 変化なしは保存しない

        Assert.AreEqual(2, saved.Count);
        Assert.IsTrue(saved.All(s => ReferenceEquals(s, settings)));
        Assert.IsTrue(settings.ExpandedSections["cpu"]);
    }

    [TestMethod]
    public void Load_ClampsTopProcessCount_NormalizesEdgeAndNullCollections()
    {
        string previousPath = SettingsStore.FilePath;
        string directory = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "ThicknessDip": 999,
                  "Edge": "Diagonal",
                  "TopProcessCount": 999999,
                  "ExpandedSections": null,
                  "SensorAliases": null,
                  "HiddenSensors": ["CPU Package", "   ", null]
                }
                """);

            SettingsStore.SetFilePathForTest(path);
            AppSettings loaded = SettingsStore.Load();

            Assert.AreEqual(AppSettings.FixedThicknessDip, loaded.ThicknessDip);
            Assert.AreEqual("Right", loaded.Edge);
            Assert.AreEqual(AppSettings.MaxTopProcessCount, loaded.TopProcessCount);
            Assert.IsNotNull(loaded.ExpandedSections);
            Assert.IsNotNull(loaded.SensorAliases);
            Assert.IsNotNull(loaded.HiddenSensors);
            CollectionAssert.AreEqual(new[] { "CPU Package" }, loaded.HiddenSensors);

            // 下限未満のクランプも検証する
            File.WriteAllText(path, """{ "TopProcessCount": -5 }""");
            AppSettings loadedMin = SettingsStore.Load();
            Assert.AreEqual(AppSettings.MinTopProcessCount, loadedMin.TopProcessCount);
        }
        finally
        {
            SettingsStore.SetFilePathForTest(previousPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Load_CorruptedJson_BacksUpToBakAndReturnsDefault()
    {
        string previousPath = SettingsStore.FilePath;
        string directory = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".bak", "older backup");
            const string brokenJson = "{ broken json ...";
            File.WriteAllText(path, brokenJson);

            SettingsStore.SetFilePathForTest(path);
            AppSettings loaded = SettingsStore.Load();

            Assert.AreEqual(8, loaded.TopProcessCount);
            Assert.AreEqual("Right", loaded.Edge);
            Assert.IsTrue(File.Exists(path + ".bak"), "Corrupted settings file should be backed up to .bak.");
            Assert.AreEqual(brokenJson, File.ReadAllText(path + ".bak"));
            Assert.IsFalse(File.Exists(path), "Corrupt settings must be moved, not copied.");
        }
        finally
        {
            SettingsStore.SetFilePathForTest(previousPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ordinary")]
    [DataRow("ordinary/deeper")]
    public void WriteJson_And_Load_RefusesReparsePoint(string child)
    {
        string previousPath = SettingsStore.FilePath;
        string root = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        string targetDir = Path.Combine(root, "target");
        string junctionDir = Path.Combine(root, "junction");
        string targetSettingsPath = Path.Combine(targetDir, child, "settings.json");
        string junctionSettingsPath = Path.Combine(junctionDir, child, "settings.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetSettingsPath)!);

            using (Process? proc = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", junctionDir, targetDir },
                CreateNoWindow = true,
                UseShellExecute = false,
            }))
            {
                Assert.IsNotNull(proc);
                proc.WaitForExit(5000);
                Assert.AreEqual(0, proc.ExitCode);
            }

            Assert.IsTrue(AppLog.IsReparsePoint(junctionDir), "Junction directory should be detected as a reparse point.");
            Assert.IsTrue(AppLog.HasReparsePointInDirectoryPath(Path.GetDirectoryName(junctionSettingsPath)!), "Shared log/settings helper must inspect every ancestor.");

            // ジャンクション経由の WriteJson は拒否されること
            SettingsStore.WriteJson(junctionSettingsPath, """{ "TopProcessCount": 3 }""");
            Assert.IsFalse(File.Exists(targetSettingsPath), "WriteJson must refuse to write through a reparse point.");

            // 実体側に設定が存在しても、ジャンクション経由の Load は読み込まず既定値を返すこと
            File.WriteAllText(targetSettingsPath, """{ "TopProcessCount": 3 }""");
            SettingsStore.SetFilePathForTest(junctionSettingsPath);
            AppSettings loaded = SettingsStore.Load();
            Assert.AreEqual(8, loaded.TopProcessCount);
        }
        finally
        {
            SettingsStore.SetFilePathForTest(previousPath);
            try
            {
                if (Directory.Exists(junctionDir))
                {
                    Directory.Delete(junctionDir, recursive: false);
                }
            }
            catch
            {
                // ignore
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    [TestMethod]
    [DataRow("")]
    [DataRow(".tmp")]
    [DataRow(".bak")]
    public void Settings_FinalTemporaryAndBackupReparsePoints_AreRefused(string suffix)
    {
        string previousPath = SettingsStore.FilePath;
        string root = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string file = Path.Combine(root, "settings.json");
        string link = file + suffix;
        try
        {
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "sentinel");
            File.WriteAllText(sentinel, "unchanged");
            using (Process? process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                CreateNoWindow = true, UseShellExecute = false,
            }))
            {
                Assert.IsNotNull(process);
                Assert.IsTrue(process.WaitForExit(5000));
                Assert.AreEqual(0, process.ExitCode);
            }
            Assert.IsTrue(AppLog.IsReparsePoint(link));
            if (suffix == ".bak")
            {
                File.WriteAllText(file, "{broken");
                SettingsStore.SetFilePathForTest(file);
                Assert.AreEqual(8, SettingsStore.Load().TopProcessCount);
                Assert.AreEqual("{broken", File.ReadAllText(file), "Refusing an unsafe backup must retain the source.");
            }
            else
            {
                SettingsStore.WriteJson(file, "{}");
                Assert.IsFalse(File.Exists(file));
            }
            Assert.AreEqual("unchanged", File.ReadAllText(sentinel));
            Assert.AreEqual(1, Directory.GetFileSystemEntries(target).Length);
        }
        finally
        {
            SettingsStore.SetFilePathForTest(previousPath);
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

}
