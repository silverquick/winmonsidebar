using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Shell;

namespace Monitor.App.Tests;

[TestClass]
public sealed class AppBarCleanupTests
{
    [STATestMethod]
    public void Unregister_OffDispatcher_RetainsTimerUntilOwnerStopsIt()
    {
        var host = new AppBarHost(new Window());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        FieldInfo field = typeof(AppBarHost).GetField("_fullscreenTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(host, timer);
        timer.Start();
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try { host.Unregister(); }
            catch (Exception ex) { failure = ex; }
        });
        try
        {
            caller.Start();
            Assert.IsTrue(caller.Join(TimeSpan.FromSeconds(5)), "Off-dispatcher cleanup must queue without blocking.");
            Assert.IsNull(failure);
            Assert.AreSame(timer, field.GetValue(host), "Keep the reference until the owning dispatcher runs cleanup.");
            Assert.IsTrue(timer.IsEnabled);
            var frame = new DispatcherFrame();
            timer.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.IsFalse(timer.IsEnabled);
            Assert.IsNull(field.GetValue(host));
            host.Unregister();
        }
        finally
        {
            timer.Stop();
            host.Dispose();
        }
    }
}
