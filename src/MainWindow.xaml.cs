using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using RiffRig.Audio;
using RiffRig.Dsp;

namespace RiffRig;

public partial class MainWindow : Window
{
    static readonly Regex GuitarInputHint = new("guitar|instrument|usb audio|umc|scarlett|focusrite|irig|m-track|behringer|line 6", RegexOptions.IgnoreCase);
    static readonly DeviceInfo NoSend = new("", "None");

    readonly AudioEngine _engine = new();
    readonly Tuner _tuner;
    readonly string _snapshotPath;
    readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Dictionary<string, (Slider slider, TextBlock value)> _knobs = new();
    readonly List<Border> _stringChips = new();
    AppSettings _settings;
    bool _loading = true;

    Rectangle _inMeter, _outMeter, _needle, _inTuneZone;
    double _needleCents, _inMeterDb = -90, _outMeterDb = -90;
    DateTime _lastSignal = DateTime.MinValue, _lastInfo = DateTime.MinValue, _startedAt;

    public MainWindow(string snapshotPath = null)
    {
        _snapshotPath = snapshotPath;
        _tuner = new Tuner(_engine.InScope, () => _engine.SampleRate);
        InitializeComponent();
        _engine.Faulted += msg => Dispatcher.BeginInvoke(() =>
        {
            StopAudio();
            SetStatus("Audio stopped: " + msg + " Check the devices and press START again.", true);
        });
    }

    // ---------- Setup ----------

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _snapshotPath != null ? new AppSettings() : AppSettings.Load();

        LoadDevices();
        foreach (int ms in new[] { 5, 10, 20, 40 }) LatencyCombo.Items.Add(ms + " ms");
        LatencyCombo.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 5, 10, 20, 40 }, _settings.LatencyMs));

        foreach (var t in Notes.Tunings) TuningCombo.Items.Add(t);
        TuningCombo.SelectedIndex = Math.Clamp(_settings.TuningIndex, 0, Notes.Tunings.Length - 1);
        MuteTuneCheck.IsChecked = _settings.MuteWhileTuning;

        foreach (var p in AmpPreset.All) PresetCombo.Items.Add(p);
        PresetCombo.SelectedItem = AmpPreset.All.FirstOrDefault(p => p.Name == _settings.Preset);

        foreach (var (key, label) in new[] { ("Gate", "Noise gate"), ("Gain", "Gain"), ("Bass", "Bass"), ("Mid", "Mid"),
                                             ("Treble", "Treble"), ("Presence", "Presence"), ("Volume", "Amp volume") })
            KnobPanel.Children.Add(BuildKnob(key, label));

        BuildLevels();
        ShowAmp(_settings.Amp.ToSettings());
        PushAmp();
        BuildStrings();

        _engine.InputGainDb = _settings.InputGainDb;
        _engine.OutputGainDb = _settings.OutputGainDb;
        _engine.SendGainDb = _settings.SendGainDb;
        _engine.Muted = _settings.MuteWhileTuning;
        _loading = false;

        if (_snapshotPath != null) { RunSnapshot(); return; }

        _tuner.Start();
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();
        SetStatus("Pick your guitar cable under GUITAR IN and your headset under HEADSET OUT, then press START.");
    }

    void LoadDevices()
    {
        var inputs = _engine.GetDevices(DataFlow.Capture);
        var outputs = _engine.GetDevices(DataFlow.Render);
        foreach (var d in inputs) InputCombo.Items.Add(d);
        foreach (var d in outputs) OutputCombo.Items.Add(d);
        SendCombo.Items.Add(NoSend);
        foreach (var d in outputs) SendCombo.Items.Add(d);

        string defIn = _engine.DefaultId(DataFlow.Capture), defOut = _engine.DefaultId(DataFlow.Render);
        InputCombo.SelectedItem = inputs.FirstOrDefault(d => d.Id == _settings.InputId)
                                  ?? inputs.FirstOrDefault(d => GuitarInputHint.IsMatch(d.Name))
                                  ?? inputs.FirstOrDefault(d => d.Id == defIn)
                                  ?? inputs.FirstOrDefault();
        OutputCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == _settings.OutputId)
                                   ?? outputs.FirstOrDefault(d => d.Id == defOut)
                                   ?? outputs.FirstOrDefault();
        SendCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == _settings.SendId) ?? NoSend;
    }

    FrameworkElement BuildKnob(string key, string label)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") };
        var slider = new Slider { Minimum = 0, Maximum = 10, SmallChange = 0.1, LargeChange = 1, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeights.SemiBold };
        void Show() => value.Text = key == "Gate" && slider.Value < 0.05 ? "off" : slider.Value.ToString("0.0");
        Show();
        slider.ValueChanged += (_, _) => { Show(); OnAmpControl(null, null); };
        Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2);
        g.Children.Add(name); g.Children.Add(slider); g.Children.Add(value);
        _knobs[key] = (slider, value);
        return g;
    }

    void BuildLevels()
    {
        LevelPanel.Children.Add(new TextBlock { Text = "LEVELS", Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0, 0, 0, 8) });
        LevelPanel.Children.Add(LevelRow("Input gain", -12, 36, _settings.InputGainDb, v => { _engine.InputGainDb = (float)v; _settings.InputGainDb = (float)v; }, out _inMeter));
        LevelPanel.Children.Add(LevelRow("Headset", -30, 6, _settings.OutputGainDb, v => { _engine.OutputGainDb = (float)v; _settings.OutputGainDb = (float)v; }, out _outMeter));
        LevelPanel.Children.Add(LevelRow("Send level", -30, 6, _settings.SendGainDb, v => { _engine.SendGainDb = (float)v; _settings.SendGainDb = (float)v; }, out _));
    }

    FrameworkElement LevelRow(string label, double min, double max, double val, Action<double> set, out Rectangle meter)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") };
        var slider = new Slider { Minimum = min, Maximum = max, Value = val, SmallChange = 0.5, LargeChange = 3, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeights.SemiBold };
        void Show() => value.Text = (slider.Value >= 0 ? "+" : "") + slider.Value.ToString("0.0") + " dB";
        Show();
        slider.ValueChanged += (_, _) => { Show(); if (!_loading) set(slider.Value); };
        Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2);
        g.Children.Add(name); g.Children.Add(slider); g.Children.Add(value);

        meter = null;
        if (label != "Send level")
        {
            var track = new Grid { Height = 5, Margin = new Thickness(6, 0, 6, 2) };
            track.Children.Add(new Border { Background = (Brush)FindResource("PanelBrush"), CornerRadius = new CornerRadius(2) });
            meter = new Rectangle { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, RadiusX = 2, RadiusY = 2, Fill = (Brush)FindResource("GoodBrush") };
            track.Children.Add(meter);
            Grid.SetRow(track, 1); Grid.SetColumn(track, 1);
            g.Children.Add(track);
        }
        return g;
    }

    void BuildStrings()
    {
        StringsPanel.Children.Clear();
        _stringChips.Clear();
        var t = (GuitarTuning)TuningCombo.SelectedItem;
        if (t == null || t.Hz.Length == 0) { StringsPanel.Visibility = Visibility.Collapsed; return; }
        StringsPanel.Visibility = Visibility.Visible;
        for (int i = 0; i < t.Hz.Length; i++)
        {
            int midi = (int)Math.Round(Notes.Midi(t.Hz[i]));
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "string " + (6 - i), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") });
            sp.Children.Add(new TextBlock { Text = Notes.NameOct(midi), FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            var chip = new Border
            {
                Child = sp, Margin = new Thickness(3), Padding = new Thickness(0, 6, 0, 6), CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("PanelBrush"), BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1.5),
            };
            _stringChips.Add(chip);
            StringsPanel.Children.Add(chip);
        }
    }

    void OnNeedleSize(object sender, SizeChangedEventArgs e) => DrawNeedleScale();

    void DrawNeedleScale()
    {
        NeedleCanvas.Children.Clear();
        double w = NeedleCanvas.ActualWidth, h = NeedleCanvas.ActualHeight;
        if (w < 50) return;
        var muted = (Brush)FindResource("MutedBrush");
        var line = (Brush)FindResource("LineBrush");

        _inTuneZone = new Rectangle { Width = CentsToX(3) - CentsToX(-3), Height = h - 18, Fill = new SolidColorBrush(Color.FromArgb(40, 61, 220, 132)), RadiusX = 3, RadiusY = 3 };
        Canvas.SetLeft(_inTuneZone, CentsToX(-3));
        NeedleCanvas.Children.Add(_inTuneZone);

        for (int c = -50; c <= 50; c += 5)
        {
            bool major = c % 25 == 0;
            double x = CentsToX(c);
            NeedleCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = major ? 0 : 12, Y2 = h - 30, Stroke = c == 0 ? muted : line, StrokeThickness = major ? 2 : 1 });
            if (major)
            {
                var lbl = new TextBlock { Text = c > 0 ? "+" + c : c.ToString(), FontSize = 10, Foreground = muted };
                lbl.Measure(new Size(100, 100));
                Canvas.SetLeft(lbl, x - lbl.DesiredSize.Width / 2);
                Canvas.SetTop(lbl, h - 16);
                NeedleCanvas.Children.Add(lbl);
            }
        }
        _needle = new Rectangle { Width = 5, Height = h - 22, RadiusX = 2.5, RadiusY = 2.5, Fill = muted };
        NeedleCanvas.Children.Add(_needle);
        PlaceNeedle(_needleCents, false, 999);
    }

    double CentsToX(double cents)
    {
        double w = NeedleCanvas.ActualWidth;
        return w / 2 + Math.Clamp(cents, -50, 50) / 50 * (w / 2 - 12);
    }

    void PlaceNeedle(double cents, bool active, double absCents)
    {
        if (_needle == null) return;
        Canvas.SetLeft(_needle, CentsToX(cents) - _needle.Width / 2);
        _needle.Fill = !active ? (Brush)FindResource("LineBrush") : CentsBrush(absCents);
    }

    Brush CentsBrush(double absCents) => (Brush)FindResource(absCents <= 3 ? "GoodBrush" : absCents <= 15 ? "WarnBrush" : "AccentBrush");

    // ---------- Live updates ----------

    void OnUiTick(object sender, EventArgs e)
    {
        var (inPk, outPk) = _engine.TakePeaks();
        var now = DateTime.UtcNow;
        if (inPk > 0.003f) _lastSignal = now;
        UpdateMeter(_inMeter, ref _inMeterDb, inPk);
        UpdateMeter(_outMeter, ref _outMeterDb, outPk);

        var r = _tuner.Latest;
        bool live = _engine.IsRunning && r.Found && (now - r.At).TotalMilliseconds < 350;
        ShowTuner(live ? r.Hz : 0, live);

        if (_engine.IsRunning && (now - _lastInfo).TotalMilliseconds > 500)
        {
            _lastInfo = now;
            InfoText.Text = $"{_engine.InputFormat}  |  buffer {_settings.LatencyMs} ms  |  dropouts {_engine.Underruns}";
            if ((now - _startedAt).TotalSeconds > 4 && (now - _lastSignal).TotalSeconds > 4)
                HintText.Text = "No signal from the guitar. Turn the guitar volume up, check the cable, or raise Input gain.";
        }
    }

    void UpdateMeter(Rectangle meter, ref double shownDb, float peak)
    {
        if (meter?.Parent is not FrameworkElement track) return;
        double db = 20 * Math.Log10(peak + 1e-9);
        shownDb = db > shownDb ? db : Math.Max(db, shownDb - 1.5);
        double frac = Math.Clamp((shownDb + 60) / 60, 0, 1);
        meter.Width = track.ActualWidth * frac;
        meter.Fill = (Brush)FindResource(shownDb > -3 ? "AccentBrush" : shownDb > -12 ? "WarnBrush" : "GoodBrush");
    }

    void ShowTuner(double hz, bool found)
    {
        var tuning = (GuitarTuning)TuningCombo.SelectedItem;
        if (!found)
        {
            NoteText.Foreground = (Brush)FindResource("LineBrush");
            OctText.Text = "";
            CentsText.Text = _engine.IsRunning || _snapshotPath != null ? "play a string" : "press START";
            CentsText.Foreground = (Brush)FindResource("MutedBrush");
            HzText.Text = " ";
            _needleCents += (0 - _needleCents) * 0.15;
            PlaceNeedle(_needleCents, false, 999);
            HighlightString(-1, false);
            return;
        }

        int midi, stringIndex = -1;
        double cents;
        if (tuning == null || tuning.Hz.Length == 0)
        {
            midi = (int)Math.Round(Notes.Midi(hz));
            cents = Notes.Cents(hz, Notes.Hz(midi));
        }
        else
        {
            stringIndex = 0;
            for (int i = 1; i < tuning.Hz.Length; i++)
                if (Math.Abs(Notes.Cents(hz, tuning.Hz[i])) < Math.Abs(Notes.Cents(hz, tuning.Hz[stringIndex]))) stringIndex = i;
            cents = Notes.Cents(hz, tuning.Hz[stringIndex]);
            midi = (int)Math.Round(Notes.Midi(tuning.Hz[stringIndex]));
        }

        double abs = Math.Abs(cents);
        var brush = CentsBrush(abs);
        NoteText.Text = Notes.Name(midi);
        NoteText.Foreground = brush;
        OctText.Text = (midi / 12 - 1).ToString();
        CentsText.Text = abs < 0.5 ? "0 cents" : (cents > 0 ? "+" : "-") + abs.ToString("0") + " cents";
        CentsText.Foreground = brush;
        HzText.Text = hz.ToString("0.0") + " Hz";
        _needleCents += (cents - _needleCents) * 0.4;
        PlaceNeedle(_needleCents, true, abs);
        HighlightString(stringIndex, abs <= 3);

        HintText.Text = abs <= 3 ? "In tune. Next string."
            : cents > 0 ? (abs > 100 ? "Way too high. Loosen the string." : "A bit high. Loosen the string slowly.")
            : (abs > 100 ? "Way too low. Tighten the string." : "A bit low. Tighten the string slowly.");
    }

    void HighlightString(int index, bool inTune)
    {
        for (int i = 0; i < _stringChips.Count; i++)
        {
            var chip = _stringChips[i];
            bool on = i == index;
            chip.BorderBrush = (Brush)FindResource(on ? (inTune ? "GoodBrush" : "AccentBrush") : "LineBrush");
            chip.Background = on && inTune ? new SolidColorBrush(Color.FromArgb(50, 61, 220, 132)) : (Brush)FindResource("PanelBrush");
        }
    }

    // ---------- Audio control ----------

    void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning) { StopAudio(); SetStatus("Stopped."); }
        else StartAudio();
    }

    void StartAudio()
    {
        if (InputCombo.SelectedItem is not DeviceInfo inDev || OutputCombo.SelectedItem is not DeviceInfo outDev)
        {
            SetStatus("Pick a GUITAR IN and a HEADSET OUT device first.", true);
            return;
        }
        var send = SendCombo.SelectedItem as DeviceInfo;
        try
        {
            _engine.Start(inDev.Id, outDev.Id, send?.Id, _settings.LatencyMs);
            _startedAt = DateTime.UtcNow;
            StartBtn.Content = "STOP";
            HintText.Text = "Tip: play one string at a time and let it ring.";
            string warn = outDev.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) || outDev.Name.Contains("Buds", StringComparison.OrdinalIgnoreCase)
                ? " Bluetooth headsets add a lot of delay, so wired headphones feel much better." : "";
            SetStatus("Running. If you turned on \"Listen to this device\" in Windows sound settings, turn it off or you will hear the guitar twice." + warn);
            SaveSettings();
        }
        catch (Exception ex)
        {
            StopAudio();
            SetStatus("Could not start audio: " + ex.Message, true);
        }
    }

    void StopAudio()
    {
        _engine.Stop();
        StartBtn.Content = "START";
        InfoText.Text = "";
    }

    void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.InputId = (InputCombo.SelectedItem as DeviceInfo)?.Id;
        _settings.OutputId = (OutputCombo.SelectedItem as DeviceInfo)?.Id;
        _settings.SendId = (SendCombo.SelectedItem as DeviceInfo)?.Id;
        _settings.LatencyMs = new[] { 5, 10, 20, 40 }[Math.Max(0, LatencyCombo.SelectedIndex)];
        if (_engine.IsRunning) StartAudio();
    }

    void OnTuningChanged(object sender, SelectionChangedEventArgs e)
    {
        BuildStrings();
        if (_loading) return;
        _settings.TuningIndex = TuningCombo.SelectedIndex;
    }

    void OnMuteTune(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.MuteWhileTuning = MuteTuneCheck.IsChecked == true;
        _engine.Muted = _settings.MuteWhileTuning;
    }

    void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedItem is not AmpPreset p) return;
        _settings.Preset = p.Name;
        _loading = true;
        ShowAmp(p.Settings);
        _loading = false;
        PushAmp();
        if (p.TuningIndex >= 0 && TuningCombo.SelectedIndex != p.TuningIndex)
        {
            TuningCombo.SelectedIndex = p.TuningIndex;
            SetStatus($"Preset \"{p.Name}\" is meant for {Notes.Tunings[p.TuningIndex].Name}. The tuner switched to it.");
        }
    }

    void OnAmpControl(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        PushAmp();
    }

    void ShowAmp(AmpSettings s)
    {
        bool was = _loading;
        _loading = true;
        AmpOnCheck.IsChecked = s.Enabled;
        CleanRadio.IsChecked = s.Channel == AmpChannel.Clean;
        DriveRadio.IsChecked = s.Channel == AmpChannel.Drive;
        BoostCheck.IsChecked = s.Boost;
        CabCheck.IsChecked = s.Cab;
        _knobs["Gate"].slider.Value = s.Gate;
        _knobs["Gain"].slider.Value = s.Gain;
        _knobs["Bass"].slider.Value = s.Bass;
        _knobs["Mid"].slider.Value = s.Mid;
        _knobs["Treble"].slider.Value = s.Treble;
        _knobs["Presence"].slider.Value = s.Presence;
        _knobs["Volume"].slider.Value = s.Volume;
        _loading = was;
    }

    void PushAmp()
    {
        var s = new AmpSettings
        {
            Enabled = AmpOnCheck.IsChecked == true,
            Channel = DriveRadio.IsChecked == true ? AmpChannel.Drive : AmpChannel.Clean,
            Boost = BoostCheck.IsChecked == true,
            Cab = CabCheck.IsChecked == true,
            Gate = (float)_knobs["Gate"].slider.Value,
            Gain = (float)_knobs["Gain"].slider.Value,
            Bass = (float)_knobs["Bass"].slider.Value,
            Mid = (float)_knobs["Mid"].slider.Value,
            Treble = (float)_knobs["Treble"].slider.Value,
            Presence = (float)_knobs["Presence"].slider.Value,
            Volume = (float)_knobs["Volume"].slider.Value,
        };
        _engine.Amp.Set(s);
        _settings.Amp = AmpData.From(s);
    }

    // ---------- Misc ----------

    void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "AccentBrush" : "MutedBrush");
    }

    void SaveSettings()
    {
        if (_snapshotPath == null) _settings.Save();
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        _uiTimer.Stop();
        _tuner.Dispose();
        _engine.Dispose();
    }

    /// <summary>Renders the window with a demo tuner reading to a PNG, then closes. Used for screenshots and checks.</summary>
    void RunSnapshot()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _needleCents = 4;
            ShowTuner(110 * Math.Pow(2, 4 / 1200.0), true);
            UpdateMeter(_inMeter, ref _inMeterDb, 0.25f);
            UpdateMeter(_outMeter, ref _outMeterDb, 0.5f);
            InfoText.Text = "48 kHz, 1 ch  |  buffer 10 ms  |  dropouts 0";
            SetStatus("Snapshot with a demo reading: A string, 4 cents sharp.");
            UpdateLayout();

            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(_snapshotPath)) enc.Save(fs);
            Close();
        };
        timer.Start();
    }
}
