using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.Core.Alerts;

namespace Monitor.App.Tests;

[TestClass]
public sealed class StorageVolumeValueStyleTests
{
    [STATestMethod]
    [DataRow(AlertLevel.None, (byte)0x9A, (byte)0xA0, (byte)0xA6)]
    [DataRow(AlertLevel.Caution, (byte)0xF0, (byte)0xC2, (byte)0x3C)]
    [DataRow(AlertLevel.Critical, (byte)0xF0, (byte)0x4A, (byte)0x4A)]
    public void StorageVolumeValueStyle_AppliesTheExpectedForeground(
        AlertLevel alertLevel,
        byte red,
        byte green,
        byte blue)
    {
        var resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/Monitor.App;component/Themes/Dark.xaml", UriKind.Relative));
        var textBlock = new TextBlock
        {
            DataContext = new AlertLevelSource(alertLevel),
            Style = (Style)resources["StorageVolumeValueStyle"],
        };

        textBlock.ApplyTemplate();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);

        Assert.IsTrue(textBlock.Foreground is SolidColorBrush, "Foreground must resolve to a solid theme brush.");
        var foreground = (SolidColorBrush)textBlock.Foreground;
        Assert.AreEqual(Color.FromRgb(red, green, blue), foreground.Color);
    }

    [TestMethod]
    public void StorageVolumeRows_UseTheAlertableStyleWithoutALocalForegroundBinding()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "SidebarWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement[] volumeValues = document
            .Descendants(presentation + "TextBlock")
            .Where(element =>
                ((string?)element.Attribute("Text") == "{Binding UsagePercentText}" &&
                 (string?)element.Attribute("Grid.Column") == "4") ||
                ((string?)element.Attribute("Text") == "{Binding CapacityText}" &&
                 (string?)element.Attribute("Grid.Column") == "5"))
            .ToArray();

        Assert.AreEqual(2, volumeValues.Length);
        foreach (XElement value in volumeValues)
        {
            Assert.AreEqual("{StaticResource StorageVolumeValueStyle}", (string?)value.Attribute("Style"));
            Assert.IsNull(value.Attribute("Foreground"));
        }
    }

    private sealed class AlertLevelSource(AlertLevel alertLevel)
    {
        public AlertLevel AlertLevel { get; } = alertLevel;
    }
}
