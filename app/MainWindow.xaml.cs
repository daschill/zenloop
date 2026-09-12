using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZenLoop.App.Services;
using ZenLoop.Core;

namespace ZenLoop.App;

public partial class MainWindow : Window
{
    HwClient? _hw;
    AutotuneService? _tune;
    SmuService? _smu;
    AppSettings _settings = new();
    TrayService? _tray;
    bool _forceClose;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Queue<double> _clockHist = new();
    readonly Queue<double> _tempHist = new();
    CancellationTokenSource? _cts;
    bool _busy;
    bool _slidersReady;
    HwSnapshot? _last;
    readonly List<Slider> _coSliders = new();
    readonly List<TextBlock> _coLabels = new();

    public MainWindow()
    {
        InitializeComponent();
        _poll.Tick += async (_, _) => await PollAsync();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("Starting ADLX helper…");
        try
        {
            var prereq = await Task.Run(AmdPrerequisites.Probe);
            if (!prereq.AllReady)
            {
                Log(prereq.UserGuidance().Replace(Environment.NewLine, " | "));
                MessageBox.Show(this, prereq.UserGuidance(), "ZenLoop — missing AMD software",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _hw = await Task.Run(() => new HwClient());
            _tune = new AutotuneService(_hw);
            _smu = SmuService.CreateProduction();
            _settings = _tune.LoadSettings();
            LoadSettingsToUi();
            _tray = new TrayService(this);
            BuildCoreSliders();
            SetAdminPill();
            Log($"Helper: {_hw.HelperPath}");
            Log($"SMU backend: {_smu.BackendName} available={_smu.IsAvailable}");
            if (WindowsElevation.IsAdministrator())
                Log("Running as Administrator — GPU ADLX and CPU/BIOS SMU share this token.");
            else
                Log("Not Administrator. CPU/BIOS writes need elevation. Close and relaunch ZenLoop, then approve UAC.");
            if (!prereq.AdrenalinPresent)
                SetStatus("NO ADRENALIN", false);
            else if (!prereq.RyzenMasterPresent)
                SetStatus("NO RYZEN MASTER", false);
            await RefreshInfoAsync();
            await RefreshSmuAsync();
            TryLoadCpuProfileIntoUi();
            var ram = _tune.LoadRamProfile();
            if (ram is not null)
            {
                LoadRamToUi(ram);
                Log($"Loaded RAM timings DDR5-{ram.DataRateMts} {ram.Tcl}-{ram.Trcd}-{ram.Trp}-{ram.Tras}");
            }
            if (_settings.ApplyProfilesOnStart)
            {
                await TryApplyStartupProfileAsync();
                await TryApplyCpuStartupAsync();
            }
            RefreshBenchText();
            _poll.Start();
            if (prereq.AllReady)
                SetStatus("LIVE", true);
        }
        catch (Exception ex)
        {
            SetStatus("NO HELPER", false);
            var msg = AmdPrerequisites.FormatHelperError(ex.Message);
            Log("ERROR " + msg.Replace(Environment.NewLine, " | "));
            MessageBox.Show(this, msg, "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && _settings.MinimizeToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _poll.Stop();
        _tray?.Dispose();
        if (_busy)
        {
            _cts?.Cancel();
            try { _hw?.Reset(); } catch { /* ignore */ }
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            Hide();
    }

    void SetAdminPill()
    {
        var admin = WindowsElevation.IsAdministrator();
        TxtAdmin.Text = admin ? "ADMIN" : "USER";
        TxtAdmin.Foreground = admin ? (Brush)FindResource("Good") : (Brush)FindResource("Warn");
        PillAdmin.Background = admin ? brush("#143326") : brush("#3A2410");
        TxtFooterRight.Text = admin
            ? "Administrator  ·  GPU ADLX  ·  CPU/BIOS Ryzen Master"
            : "Not admin  ·  GPU works  ·  CPU/BIOS needs UAC";
    }

    void LoadSettingsToUi()
    {
        ChkApplyOnStart.IsChecked = _settings.ApplyProfilesOnStart;
        ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
        ChkTray.IsChecked = _settings.MinimizeToTray;
    }

    void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        _settings.ApplyProfilesOnStart = ChkApplyOnStart.IsChecked == true;
        _settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        _settings.MinimizeToTray = ChkTray.IsChecked == true;
        _tune?.SaveSettings(_settings);
        try { WindowsStartup.SetEnabled(_settings.StartWithWindows); }
        catch (Exception ex) { Log("Start with Windows: " + ex.Message); }
    }

    async Task RefreshSmuAsync()
    {
        if (_smu is null) return;
        try
        {
            if (_smu.Backend is AmdRyzenMasterBackend amd)
            {
                var info = await Task.Run(() => amd.InfoJson());
                TxtPboBackend.Text = SmuStatusLine(info);
                var live = CpuSmuProtocol.TryParseProfile(info);
                if (live is not null)
                    LoadProfileToUi(live);
            }
            else
                TxtPboBackend.Text = $"SMU: {_smu.BackendName}";
        }
        catch (Exception ex)
        {
            TxtPboBackend.Text = "SMU: " + ex.Message;
        }
    }

    static string SmuStatusLine(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool ok = r.TryGetProperty("ok", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
            bool elev = r.TryGetProperty("elevated", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
            bool co = r.TryGetProperty("co_bind", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
            bool bios = r.TryGetProperty("bios_bind", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.True;
            string err = r.TryGetProperty("error", out var er) && er.ValueKind == System.Text.Json.JsonValueKind.String
                ? er.GetString() ?? "" : "";
            if (ok) return $"SMU live  elevated={elev}  CO bind={co}  BIOS bind={bios}";
            return string.IsNullOrEmpty(err) ? "SMU not ready" : "SMU: " + err;
        }
        catch
        {
            return "SMU: " + (json.Length > 160 ? json[..160] : json);
        }
    }

    async Task TryApplyCpuStartupAsync()
    {
        if (_tune is null || _smu is null) return;
        var p = _tune.LoadCpuPboProfile();
        if (p is null) return;
        Log($"Applying saved CPU PBO on start (PPT {p.PptWatts} W)…");
        try
        {
            var r = await Task.Run(() => _smu.Apply(p, PersistMode.Session));
            Log($"CPU start apply session={r.SessionApplied} co={r.CoWritten} {(r.Error ?? "ok")}");
        }
        catch (Exception ex)
        {
            Log("CPU startup apply failed: " + ex.Message);
        }
    }

    public void ApplySavedProfilesFromTray()
    {
        Dispatcher.InvokeAsync(() => OnApplyProfile(this, new RoutedEventArgs()));
    }

    public void ForceClose()
    {
        _forceClose = true;
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    async Task PollAsync()
    {
        if (_busy || _hw is null) return;
        try
        {
            var snap = await Task.Run(() => _hw.Metrics());
            ApplyMetrics(snap);
        }
        catch (Exception ex)
        {
            TxtFooter.Text = ex.Message;
        }
    }

    async Task RefreshInfoAsync()
    {
        if (_hw is null) return;
        var snap = await Task.Run(() => _hw.Info());
        _last = snap;
        TxtSubtitle.Text = $"{snap.GpuName}  ·  {snap.VramMb} MB  ·  Ryzen 7 9800X3D";
        TxtFactory.Text = snap.AtFactory ? "FACTORY" : "TUNED";
        PillFactory.Background = brush(snap.AtFactory ? "#1A2330" : "#3A2410");
        TxtFactory.Foreground = snap.AtFactory ? (Brush)FindResource("Cyan") : (Brush)FindResource("Accent");
        ApplyMetrics(snap);
        InitSliders(snap);
    }

    void InitSliders(HwSnapshot s)
    {
        _slidersReady = false;
        SetRange(SldClock, s.ClockRange, s.MaxMhz ?? 2500, 500, 3025);
        SetRange(SldVolt, s.VoltageRange, s.VoltageMv ?? 1150, 700, 1150);
        SetRange(SldVram, s.VramRange, s.VramMhz ?? 2500, 2500, 2700);
        SetRange(SldPower, s.PowerRange, s.PowerPct ?? 0, -10, 15);
        ChkFast.IsChecked = s.FastTiming;
        _slidersReady = true;
        OnSlider(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    static void SetRange(Slider s, HwSnapshot.Range? r, int value, int dMin, int dMax)
    {
        s.Minimum = r?.Min ?? dMin;
        s.Maximum = r?.Max ?? dMax;
        s.TickFrequency = Math.Max(1, r?.Step ?? 1);
        s.Value = Math.Clamp(value, s.Minimum, s.Maximum);
    }

    void ApplyMetrics(HwSnapshot s)
    {
        var m = s.Metrics;
        var clock = Metric.Get(m, "clock_mhz");
        var volt = Metric.Get(m, "voltage_mv") ?? s.VoltageMv;
        var gpuT = Metric.Get(m, "gpu_temp_c");
        var hot = Metric.Get(m, "hotspot_c");
        var power = Metric.Get(m, "board_power_w", "power_w");
        var fan = Metric.Get(m, "fan_rpm");
        var vram = Metric.Get(m, "vram_clock_mhz") ?? s.VramMhz;
        var memT = Metric.Get(m, "mem_temp_c");

        ValClock.Text = Metric.Fmt(clock, " MHz");
        ValVolt.Text = Metric.Fmt(volt, " mV");
        SubVolt.Text = s.VoltageMv is int cap ? $"cap {cap} mV" : "cap";
        ValGpuTemp.Text = Metric.Fmt(gpuT, " C");
        ValHot.Text = Metric.Fmt(hot, " C");
        ValPower.Text = Metric.Fmt(power, " W");
        SubFan.Text = fan is null ? "fan —" : $"fan {fan:0} rpm";
        ValVram.Text = Metric.Fmt(vram, " MHz");
        SubMemTemp.Text = memT is null ? "mem —" : $"mem {memT:0} C";

        ValGpuTemp.Foreground = TempBrush(gpuT, 75, 88);
        ValHot.Foreground = TempBrush(hot, 90, 105);

        Push(_clockHist, clock);
        Push(_tempHist, gpuT);
        DrawSpark(SparkClock, _clockHist, (Brush)FindResource("Cyan"));
        DrawSpark(SparkTemp, _tempHist, (Brush)FindResource("Good"));

        TxtFooter.Text = _busy
            ? TxtFooter.Text
            : $"{s.GpuName}   usage {Metric.Get(m, "usage_pct"):0}%   {DateTime.Now:HH:mm:ss}";
    }

    static Brush TempBrush(double? v, double warn, double bad)
    {
        if (v is null) return Brushes.White;
        if (v >= bad) return new SolidColorBrush(Color.FromRgb(255, 92, 122));
        if (v >= warn) return new SolidColorBrush(Color.FromRgb(245, 197, 66));
        return new SolidColorBrush(Color.FromRgb(61, 220, 151));
    }

    static void Push(Queue<double> q, double? v)
    {
        if (v is null) return;
        q.Enqueue(v.Value);
        while (q.Count > 48) q.Dequeue();
    }

    static void DrawSpark(Polyline line, Queue<double> data, Brush stroke)
    {
        line.Stroke = stroke;
        if (data.Count < 2) return;
        var pts = new PointCollection();
        var arr = data.ToArray();
        var min = arr.Min();
        var max = arr.Max();
        var span = Math.Max(1, max - min);
        for (var i = 0; i < arr.Length; i++)
        {
            var x = i / (double)(arr.Length - 1) * 120;
            var y = 20 - (arr[i] - min) / span * 18;
            pts.Add(new Point(x, y));
        }
        line.Points = pts;
    }

    void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_slidersReady && sender != this) return;
        LblClock.Text = $"{SldClock.Value:0} MHz";
        LblVolt.Text = $"{SldVolt.Value:0} mV";
        LblVram.Text = $"{SldVram.Value:0} MHz";
        var p = SldPower.Value;
        LblPower.Text = p >= 0 ? $"+{p:0} %" : $"{p:0} %";
    }

    void OnSeconds(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblSeconds is not null)
            LblSeconds.Text = $"{SldSeconds.Value:0} s";
    }

    void BuildCoreSliders()
    {
        CorePanel.Children.Clear();
        _coSliders.Clear();
        _coLabels.Clear();
        int n = Math.Max(8, Environment.ProcessorCount);
        n = Math.Min(n, 16);
        for (int i = 0; i < n; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var name = new TextBlock { Text = $"C{i}", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center };
            var sld = new Slider { Minimum = -40, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true, Value = 0 };
            var lbl = new TextBlock { Text = "0", Style = (Style)FindResource("Label"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            int idx = i;
            sld.ValueChanged += (_, _) => { if (idx < _coLabels.Count) _coLabels[idx].Text = $"{sld.Value:+0;-0;0}"; };
            Grid.SetColumn(sld, 1);
            Grid.SetColumn(lbl, 2);
            row.Children.Add(name);
            row.Children.Add(sld);
            row.Children.Add(lbl);
            CorePanel.Children.Add(row);
            _coSliders.Add(sld);
            _coLabels.Add(lbl);
        }
    }

    void OnPboSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblPpt is null || LblTdc is null || LblEdc is null || LblBoost is null || LblScalar is null)
            return;
        LblPpt.Text = $"{SldPpt.Value:0} W";
        LblTdc.Text = $"{SldTdc.Value:0} A";
        LblEdc.Text = $"{SldEdc.Value:0} A";
        LblBoost.Text = $"+{SldBoost.Value:0} MHz";
        LblScalar.Text = $"{SldScalar.Value:0}x";
    }

    void OnRamSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblRamClk is null || LblVddio is null || LblTcl is null || LblTrcd is null || LblTrp is null || LblTras is null || LblTrfc is null)
            return;
        int mhz = (int)SldRamClk.Value;
        LblRamClk.Text = $"{mhz} MHz (DDR5-{mhz * 2})";
        LblVddio.Text = $"{SldVddio.Value:0} mV";
        LblTcl.Text = $"{SldTcl.Value:0}";
        LblTrcd.Text = $"{SldTrcd.Value:0}";
        LblTrp.Text = $"{SldTrp.Value:0}";
        LblTras.Text = $"{SldTras.Value:0}";
        LblTrfc.Text = $"{SldTrfc.Value:0}";
    }

    RamTimingProfile UiRamProfile() => new()
    {
        MemClockMhz = (int)SldRamClk.Value,
        VddioMv = (int)SldVddio.Value,
        Tcl = (int)SldTcl.Value,
        Trcd = (int)SldTrcd.Value,
        Trp = (int)SldTrp.Value,
        Tras = (int)SldTras.Value,
        Trfc = (int)SldTrfc.Value,
        Expo = ChkExpo.IsChecked == true,
    };

    void LoadRamToUi(RamTimingProfile p)
    {
        SldRamClk.Value = p.MemClockMhz;
        SldVddio.Value = p.VddioMv;
        SldTcl.Value = p.Tcl;
        SldTrcd.Value = p.Trcd;
        SldTrp.Value = p.Trp;
        SldTras.Value = p.Tras;
        SldTrfc.Value = p.Trfc;
        ChkExpo.IsChecked = p.Expo;
        OnRamSlider(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    async void OnRamRead(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        await RunExclusive("Reading RAM timings from AMD BIOS…", async ct =>
        {
            var p = await Task.Run(() => _smu.ReadRam(), ct);
            if (p is null)
            {
                Log("RAM read failed. Run as Administrator so CDefaultBIOS can bind.");
                return;
            }
            await Dispatcher.InvokeAsync(() => LoadRamToUi(p));
            Log($"RAM {p.DataRateMts} MT/s  tCL {p.Tcl}-{p.Trcd}-{p.Trp}-{p.Tras}  tRFC {p.Trfc}  VDDIO {p.VddioMv} mV");
        });
    }

    async void OnRamWrite(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        if (!ConfirmDangerousWrite(BiosWriteGuard.RamBiosWarning, "Write RAM BIOS", requireAdmin: true))
            return;
        var profile = UiRamProfile();
        if (MessageBox.Show(this,
                $"Confirm RAM values:\nDDR5-{profile.DataRateMts}  {profile.Tcl}-{profile.Trcd}-{profile.Trp}-{profile.Tras}  tRFC {profile.Trfc}\nVDDIO {profile.VddioMv} mV  EXPO={(profile.Expo ? "on" : "off")}",
                "Write RAM BIOS",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunExclusive("Writing RAM timings to BIOS…", async ct =>
        {
            var r = await Task.Run(() => _smu.ApplyRam(profile), ct);
            _tune?.SaveRamProfile(profile);
            Log($"RAM BIOS persist={r.BiosPersisted} {(r.Error ?? "ok")}");
            if (!r.BiosPersisted)
                throw new HwException(r.Error ?? "RAM BIOS apply failed");
        });
    }

    CpuPboProfile UiProfile()
    {
        var p = new CpuPboProfile
        {
            PboEnabled = ChkPbo.IsChecked == true,
            PptWatts = (int)SldPpt.Value,
            TdcAmps = (int)SldTdc.Value,
            EdcAmps = (int)SldEdc.Value,
            BoostOverrideMhz = (int)SldBoost.Value,
            Scalar = (int)SldScalar.Value,
        };
        for (int i = 0; i < _coSliders.Count; i++)
            p.Cores.Add(CurveOptimizerCore.FromSigned(i, (int)_coSliders[i].Value));
        return p;
    }

    void LoadProfileToUi(CpuPboProfile p)
    {
        ChkPbo.IsChecked = p.PboEnabled;
        SldPpt.Value = p.PptWatts;
        SldTdc.Value = p.TdcAmps;
        SldEdc.Value = p.EdcAmps;
        SldBoost.Value = p.BoostOverrideMhz;
        SldScalar.Value = Math.Clamp(p.Scalar, 1, 10);
        foreach (var c in p.Cores)
        {
            if (c.Core >= 0 && c.Core < _coSliders.Count)
                _coSliders[c.Core].Value = c.SignedOffset;
        }
        OnPboSlider(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    void TryLoadCpuProfileIntoUi()
    {
        var p = _tune?.LoadCpuPboProfile();
        if (p is null) return;
        LoadProfileToUi(p);
        Log($"Loaded CPU PBO profile PPT {p.PptWatts} W  TDC {p.TdcAmps} A  EDC {p.EdcAmps} A  CO cores {p.Cores.Count}");
    }

    async void OnPboApply(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDangerousWrite(BiosWriteGuard.SessionWarning, "Apply session PBO / Curve Optimizer", requireAdmin: true))
            return;
        await ApplyPboAsync(PersistMode.Session);
    }

    async void OnPboBios(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDangerousWrite(BiosWriteGuard.BiosWarning, "Write to BIOS", requireAdmin: true))
            return;
        await ApplyPboAsync(PersistMode.Bios);
    }

    bool ConfirmDangerousWrite(string warning, string title, bool requireAdmin)
    {
        if (requireAdmin && !WindowsElevation.IsAdministrator())
        {
            var again = MessageBox.Show(this,
                warning + "\n\nYou are NOT running as Administrator. ZenLoop will prompt for UAC before any BIOS/SMU write. "
                + "If you cancel UAC, the write is refused — nothing is applied silently.\n\nContinue?",
                title,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            return again == MessageBoxResult.OK;
        }
        return MessageBox.Show(this, warning, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;
    }

    async Task ApplyPboAsync(PersistMode persist)
    {
        if (_smu is null) return;
        var profile = UiProfile();
        await RunExclusive(persist == PersistMode.Bios ? "BIOS persist PBO + Curve Optimizer…" : "Applying PBO session via SMU…",
            async ct =>
            {
                var result = await Task.Run(() => _smu.Apply(profile, persist), ct);
                Log($"PBO apply backend={result.Backend} session={result.SessionApplied} bios={result.BiosPersisted} co={result.CoWritten}");
                if (result.Error is string err)
                    Log("PBO: " + err);
                TxtPboBackend.Text = $"SMU: {result.Backend}  session={(result.SessionApplied ? "applied" : "no")}  BIOS={(result.BiosPersisted ? "persisted" : "not persisted")}  CO={(result.CoWritten ? "written" : "not written")}";
                if (!result.SessionApplied || (profile.Cores.Count > 0 && !result.CoWritten))
                    throw new HwException(result.Error ?? "PBO apply failed (per-core Curve Optimizer not written)");
                if (persist == PersistMode.Bios && !result.BiosPersisted)
                    throw new HwException(result.Error ?? "BIOS persist failed");
                _tune?.SaveCpuPboProfile(profile);
            });
    }

    async void OnPboRead(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        await RunExclusive("Reading SMU / Ryzen Master parameters…", async ct =>
        {
            var p = await Task.Run(() => _smu.Read(), ct);
            if (p is null)
            {
                Log("SMU read returned nothing. Is AMDRyzenMasterDriverV27 running?");
                return;
            }
            await Dispatcher.InvokeAsync(() => LoadProfileToUi(p));
            Log($"Read PPT {p.PptWatts} W  TDC {p.TdcAmps} A  EDC {p.EdcAmps} A  boost override {p.BoostOverrideMhz}  cores {p.Cores.Count}");
        });
    }

    async void OnPboTune(object sender, RoutedEventArgs e)
    {
        if (_tune is null || _smu is null) return;
        if (!ConfirmDangerousWrite(BiosWriteGuard.SessionWarning
                + "\n\nAuto-tune will search negative Curve Optimizer offsets per logical core with CPU stress.",
                "Auto-tune per-core Curve Optimizer", requireAdmin: true))
            return;
        var seed = UiProfile();
        var secs = Math.Max(8, (int)SldSeconds.Value / 2);
        await RunExclusive("Per-core Curve Optimizer auto-tune…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.StepName ?? p.Message;
                Log(p.Message);
            });
            var profile = await _tune.CpuPboAutotuneAsync( _smu, seed, secs, new Limits(), progress, ct);
            await Dispatcher.InvokeAsync(() =>
            {
                LoadProfileToUi(profile);
                TxtCores.Text = $"Per-core Curve Optimizer: {string.Join(", ", profile.Cores.Select(c => $"C{c.Core}:{c.SignedOffset}"))}";
            });
            Log("Saved profiles/cpu-pbo.json");
        });
    }

    async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_hw is null) return;
        await RunExclusive("Applying GPU sliders…", async ct =>
        {
            var snap = await Task.Run(() => _hw.SetGpu(
                maxMhz: (int)SldClock.Value,
                minMhz: _last?.MinMhz,
                voltage: (int)SldVolt.Value,
                vram: (int)SldVram.Value,
                power: (int)SldPower.Value,
                fastTiming: ChkFast.IsChecked == true), ct);
            await Dispatcher.InvokeAsync(() =>
            {
                ApplyMetrics(snap);
                TxtFactory.Text = "TUNED";
            });
            Log($"Applied {SldVolt.Value:0} mV  {SldClock.Value:0} MHz  VRAM {SldVram.Value:0}");
        });
        await RefreshInfoAsync();
    }

    async void OnReset(object sender, RoutedEventArgs e)
    {
        if (_hw is null) return;
        if (MessageBox.Show(this, "Restore AMD factory GPU tuning (Adrenalin Default)?", "ZenLoop",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunExclusive("Resetting to factory…", async ct =>
        {
            await Task.Run(() => _hw.Reset(), ct);
            Log("Factory GPU tuning restored.");
        });
        await RefreshInfoAsync();
    }

    async void OnOptimize(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        var goal = ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString()?.ToLowerInvariant() ?? "balanced";
        var secs = (int)SldSeconds.Value;
        if (MessageBox.Show(this,
                "ZenLoop will:\n"
                + "1. Restore stock GPU (and session Curve Optimizer = 0 if SMU is live)\n"
                + "2. Benchmark stock (CPU + RAM + GPU)\n"
                + "3. Auto undervolt/overclock the GPU\n"
                + "4. Auto-tune per-core Curve Optimizer\n"
                + "5. Benchmark again and show faster / cooler / less power\n\n"
                + $"Goal: {goal}  ·  {secs}s per step\nClose games first. This takes several minutes.",
                "Optimize this PC",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var limits = new Limits { MinVoltageMv = 1025, MaxClockMhz = 3000 };
        var seed = UiProfile();
        await RunExclusive("Optimizing this PC…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.StepName ?? p.Message;
                Log(p.Message);
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            var delta = await _tune.RunAutonomousAsync(
                goal, secs, limits, ChkSkipVram.IsChecked == true, _smu, seed, progress, ct);
            RefreshBenchText();
            if (delta is not null)
            {
                Log(delta.Summary);
                Log(delta.Report(_tune.LoadBenchBaseline(), _tune.LoadBenchCurrent()));
                TxtStep.Text = delta.Summary;
            }
            else
                Log("Optimize finished but a baseline or current bench is missing.");
        }, restoreOnCancel: true);
        await RefreshInfoAsync();
        TryLoadCpuProfileIntoUi();
    }

    async void OnAutoGpu(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        var goal = ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString()?.ToLowerInvariant() ?? "balanced";
        var secs = (int)SldSeconds.Value;
        if (MessageBox.Show(this,
                "This changes live GPU clocks and voltage.\nClose games first.\n\n"
                + $"Goal: {goal}\n{secs}s per step.\n\nContinue?",
                "Auto GPU undervolt + overclock",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var limits = new Limits { MinVoltageMv = 1025, MaxClockMhz = 3000 };
        await RunExclusive("GPU auto-tune running…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.StepName ?? p.Message;
                Log(p.Message);
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            var result = await _tune.GpuAutotuneAsync(goal, secs, limits, ChkSkipVram.IsChecked == true, progress, ct);
            var ok = result.TryGetValue("ok", out var o) && o is true;
            Log(ok ? "Auto-tune finished. Profile saved." : "Auto-tune stopped: " + result.GetValueOrDefault("reason"));
        }, restoreOnCancel: true);
        await RefreshInfoAsync();
    }

    async void OnAmdAuto(object sender, RoutedEventArgs e)
    {
        if (_hw is null) return;
        if (MessageBox.Show(this, "Run AMD’s built-in Adrenalin auto-undervolt? This can take a couple of minutes.",
                "AMD auto undervolt", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunExclusive("AMD auto-undervolt…", async ct =>
        {
            var snap = await Task.Run(() => _hw.AmdAuto("undervolt"), ct);
            await Dispatcher.InvokeAsync(() => ApplyMetrics(snap));
            Log("AMD auto-undervolt finished.");
        });
        await RefreshInfoAsync();
    }

    async void OnStressGpu(object sender, RoutedEventArgs e)
        => await Stress("gpu", (int)SldSeconds.Value, "all");

    async void OnStressCpu(object sender, RoutedEventArgs e)
        => await Stress("cpu", (int)SldSeconds.Value, "all");

    async void OnPerCore(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        await RunExclusive("Per-core CPU stress (guided Curve Optimizer)…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.StepName ?? p.Message;
                Log(p.Message);
            });
            var results = await _tune.PerCoreStressAsync(Math.Max(15, (int)SldSeconds.Value / 2), new Limits(), progress, ct);
            var outcomes = results.Select(r => new ZenLoop.Core.PerCoreOutcome(r.Core, r.Ok, r.Reason)).ToList();
            var summary = ZenLoop.Core.PerCoreGuide.FormatSummary(outcomes);
            TxtCores.Text = summary;
            Log(summary);
        });
    }

    async void OnApplyProfile(object sender, RoutedEventArgs e)
    {
        if (_tune is null || _hw is null) return;
        var gpu = _tune.LoadStartupProfile();
        var cpu = _tune.LoadCpuPboProfile();
        if (gpu is null && cpu is null)
        {
            MessageBox.Show(this, "No saved profiles yet. Run Auto GPU UV + OC and/or apply CPU PBO first.", "ZenLoop",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (gpu is not null)
            await ApplyProfileAsync(gpu);
        if (cpu is not null && _smu is not null)
        {
            await RunExclusive("Applying saved CPU PBO…", async ct =>
            {
                var r = await Task.Run(() => _smu.Apply(cpu, PersistMode.Session), ct);
                Log($"CPU apply session={r.SessionApplied} co={r.CoWritten} bios={r.BiosPersisted}");
                if (r.Error is string err) Log("CPU: " + err);
                if (!r.SessionApplied)
                    throw new HwException(r.Error ?? "CPU PBO apply failed");
            });
        }
    }

    async Task TryApplyStartupProfileAsync()
    {
        if (_tune is null) return;
        var p = _tune.LoadStartupProfile();
        if (p is null) return;
        Log($"Applying saved GPU profile on start ({p.VoltageMv} mV @ {p.MaxMhz} MHz)…");
        try
        {
            await Task.Run(() => _tune.ApplyProfile(p));
            Log("Startup profile applied.");
            await RefreshInfoAsync();
        }
        catch (Exception ex)
        {
            Log("Startup profile apply failed: " + ex.Message);
        }
    }

    async Task ApplyProfileAsync(ZenLoop.Core.GpuProfile p)
    {
        await RunExclusive("Applying saved GPU profile…", async ct =>
        {
            await Task.Run(() => _tune!.ApplyProfile(p), ct);
            Log($"Applied profile {p.VoltageMv} mV @ {p.MaxMhz} MHz  VRAM {p.VramMhz}  fan points {p.Fan?.Points.Count ?? 0}");
        });
        await RefreshInfoAsync();
    }

    async void OnSoak(object sender, RoutedEventArgs e)
    {
        await Stress("cpu", 60, "all");
        if (!_cts?.IsCancellationRequested ?? true)
            await Stress("gpu", 180, "all");
    }

    async Task Stress(string kind, int seconds, string mode)
    {
        if (_tune is null) return;
        await RunExclusive($"Stress {kind} {seconds}s…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.Message;
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            var res = await _tune.StressAsync(kind, seconds, new Limits(), progress, ct, mode);
            Log(res.Ok ? $"PASS {kind} stress" : $"FAIL {kind}: {res.Reason}");
            TxtStep.Text = res.Ok ? "pass" : "fail";
        });
    }

    void OnCpuCap(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        _tune.CpuUnderclock(95);
        Log("Windows processor maximum set to 95%. Use Reset factory? No — set 100% from a future control, or powercfg.");
        MessageBox.Show(this, "Processor maximum is now 95% (underclock).\nTo restore full boost, run:\npowercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100\npowercfg /setactive SCHEME_CURRENT",
            "CPU underclock", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    async void OnBenchBaseline(object sender, RoutedEventArgs e) => await RunBenchAsync("baseline");

    async void OnBenchCurrent(object sender, RoutedEventArgs e) => await RunBenchAsync("current");

    async Task RunBenchAsync(string label)
    {
        if (_tune is null) return;
        var seconds = (int)SldSeconds.Value;
        await RunExclusive($"Benchmark {label} ({seconds}s CPU + RAM + GPU)…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = p.Fraction;
                TxtStep.Text = p.Message;
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            var run = await _tune.RunSystemBenchAsync(label, seconds, new Limits(), progress, ct);
            Log($"{label} CPU {run.Cpu.Throughput:0}  GPU {run.Gpu.Throughput:0}  RAM {run.Ram.Throughput:0}"
                + (run.Hwinfo ? "  (CPU PPT/temp live)" : "  (CPU PPT: approve UAC so Ryzen Master telemetry is live)"));
            if (run.Cpu.AvgPowerW is double cp) Log($"  CPU  {cp:0.0} W  {run.Cpu.PeakTempC:0.0} C  {run.Cpu.AvgClockMhz:0} MHz");
            if (run.Gpu.AvgPowerW is double gp) Log($"  GPU  {gp:0.0} W  hotspot {run.Gpu.PeakTempC:0.0} C  {run.Gpu.AvgClockMhz:0} MHz");
            RefreshBenchText();
            var d = _tune.CompareSavedBenches();
            if (d is not null)
            {
                Log(d.Summary);
                Log(d.Report(_tune.LoadBenchBaseline(), _tune.LoadBenchCurrent()));
            }
            TxtStep.Text = d is null ? $"{label} saved" : d.Summary;
        });
    }

    void RefreshBenchText()
    {
        if (_tune is null || TxtBench is null) return;
        var d = _tune.CompareSavedBenches();
        if (d is not null)
        {
            TxtBench.Text = d.Report(_tune.LoadBenchBaseline(), _tune.LoadBenchCurrent());
            return;
        }
        var a = _tune.LoadBenchBaseline();
        var b = _tune.LoadBenchCurrent();
        if (a is null && b is null)
            TxtBench.Text = "Click Optimize this PC. Stock bench → GPU UV+OC → per-core Curve Optimizer → tuned bench. CPU PPT from Ryzen Master; GPU board power from ADLX.";
        else if (a is not null && b is null)
            TxtBench.Text = $"Baseline saved {a.Utc:u}. Tune, then Bench current.";
        else if (a is null && b is not null)
            TxtBench.Text = $"Current saved {b.Utc:u}. Run Bench baseline at stock to compare.";
    }

    void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OnClearLog(object sender, RoutedEventArgs e) => TxtLog.Clear();

    async Task RunExclusive(string status, Func<CancellationToken, Task> work, bool restoreOnCancel = false)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusyUi(true);
        SetStatus("BUSY", true);
        TxtFooter.Text = status;
        TxtStep.Text = status;
        Log(status);
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("Stopped.");
            if (restoreOnCancel && _hw is not null)
            {
                try { await Task.Run(() => _hw.Reset()); Log("Factory restored after stop."); }
                catch (Exception ex) { Log("Reset after stop failed: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex.Message);
            MessageBox.Show(this, ex.Message, "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
            Bar.Value = 0;
            TxtStep.Text = "idle";
            SetBusyUi(false);
            SetStatus("LIVE", true);
        }
    }

    void SetBusyUi(bool busy)
    {
        BtnApply.IsEnabled = !busy;
        BtnReset.IsEnabled = !busy;
        if (BtnOptimize is not null) BtnOptimize.IsEnabled = !busy;
        BtnAutoGpu.IsEnabled = !busy;
        BtnAmdUv.IsEnabled = !busy;
        BtnStressGpu.IsEnabled = !busy;
        BtnStressCpu.IsEnabled = !busy;
        BtnSoak.IsEnabled = !busy;
        BtnPerCore.IsEnabled = !busy;
        BtnApplyProfile.IsEnabled = !busy;
        BtnCpuUnder.IsEnabled = !busy;
        if (BtnBenchBase is not null)
        {
            BtnBenchBase.IsEnabled = !busy;
            BtnBenchNow.IsEnabled = !busy;
        }
        if (BtnPboApply is not null)
        {
            BtnPboApply.IsEnabled = !busy;
            BtnPboBios.IsEnabled = !busy;
            BtnPboTune.IsEnabled = !busy;
            BtnPboRead.IsEnabled = !busy;
            if (BtnRamRead is not null)
            {
                BtnRamRead.IsEnabled = !busy;
                BtnRamWrite.IsEnabled = !busy;
            }
        }
        BtnStop.IsEnabled = busy;
        SldClock.IsEnabled = !busy;
        SldVolt.IsEnabled = !busy;
        SldVram.IsEnabled = !busy;
        SldPower.IsEnabled = !busy;
    }

    void SetStatus(string text, bool ok)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = ok ? (Brush)FindResource("Good") : (Brush)FindResource("Bad");
        PillStatus.Background = ok ? brush("#143326") : brush("#3A1822");
    }

    void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        TxtLog.AppendText(line);
        LogScroll.ScrollToEnd();
        TxtFooter.Text = msg;
    }

    static SolidColorBrush brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
