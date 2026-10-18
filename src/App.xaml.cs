using System.Windows;
using System.Windows.Media;

namespace RiffRig;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAccent();
        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            int code = SelfTest.Run(args[1], args.Length >= 3 && args[2] == "live");
            Shutdown(code);
            return;
        }
        string snapshot = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
        new MainWindow(snapshot).Show();
    }

    /// <summary>
    /// The Fluent theme follows the Windows accent color. Swap in RiffRig orange by writing into the
    /// theme dictionaries themselves, since their templates look the keys up there, not in App.Resources.
    /// </summary>
    void ApplyAccent()
    {
        var colors = new Dictionary<string, Color>
        {
            ["SystemAccentColor"] = Rgb(0xFF5A1F),
            ["SystemAccentColorLight1"] = Rgb(0xFF7A45),
            ["SystemAccentColorLight2"] = Rgb(0xFF9668),
            ["SystemAccentColorLight3"] = Rgb(0xFFB38F),
            ["SystemAccentColorDark1"] = Rgb(0xE04A12),
            ["SystemAccentColorDark2"] = Rgb(0xB83A0C),
            ["SystemAccentColorDark3"] = Rgb(0x8F2C08),
        };
        var main = Rgb(0xFF7A45);
        var brushes = new Dictionary<string, Color>
        {
            ["AccentFillColorDefaultBrush"] = main,
            ["AccentFillColorSecondaryBrush"] = Color.FromArgb(0xE6, main.R, main.G, main.B),
            ["AccentFillColorTertiaryBrush"] = Color.FromArgb(0xCC, main.R, main.G, main.B),
        };
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                                    "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
                                    "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver",
                                    "RadioButtonCheckOuterEllipseCheckedFillPointerOver", "RadioButtonCheckOuterEllipseCheckedFillPressed", "RadioButtonCheckOuterEllipseCheckedStrokePressed",
                                    "SliderInnerThumb" })
            brushes[key] = main;

        void Walk(ResourceDictionary rd)
        {
            foreach (var (k, c) in colors) if (rd.Contains(k)) rd[k] = c;
            foreach (var (k, c) in brushes) if (rd.Contains(k)) rd[k] = new SolidColorBrush(c);
            foreach (var md in rd.MergedDictionaries) Walk(md);
        }
        Walk(Resources);
        foreach (var (k, c) in colors) Resources[k] = c;
        foreach (var (k, c) in brushes) Resources[k] = new SolidColorBrush(c);

        // Sliders and a few other templates read the SystemColors accent keys added in .NET 9.
        var sys = new (object colorKey, object brushKey, string name)[]
        {
            (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey, "SystemAccentColor"),
            (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey, "SystemAccentColorLight1"),
            (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey, "SystemAccentColorLight2"),
            (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey, "SystemAccentColorLight3"),
            (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey, "SystemAccentColorDark1"),
            (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey, "SystemAccentColorDark2"),
            (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey, "SystemAccentColorDark3"),
        };
        foreach (var (ck, bk, name) in sys)
        {
            Resources[ck] = colors[name];
            Resources[bk] = new SolidColorBrush(colors[name]);
        }
    }

    static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
