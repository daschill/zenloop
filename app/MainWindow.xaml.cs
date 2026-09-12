using System.Collections.Generic;
using System.IO;
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
    readonly DispatcherTimer _hotApplyPoll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly AppProfileHotApplySession _hotApply = new();
    bool _hotApplyRunning;
    readonly Queue<double> _clockHist = new();
    readonly Queue<double> _tempHist = new();
    CancellationTokenSource? _cts;
    bool _busy;
    bool _slidersReady;
    HwSnapshot? _last;
    readonly List<Slider> _coSliders = new();
    readonly List<TextBlock> _coLabels = new();
    WindowsControlCapabilities? _caps;
    WindowsControlSurface? _control;

    public MainWindow()
    {
        InitializeComponent();
        Title = ProductIdentity.WindowTitle;
        TxtVersion.Text = "v" + ProductIdentity.Version;
        TxtFooterRight.Text = $"v{ProductIdentity.Version}  ·  GPU ADLX  ·  CPU/BIOS AMD Ryzen Master";
        _poll.Tick += async (_, _) => await PollAsync();
        _hotApplyPoll.Tick += async (_, _) => await HotApplyTickAsync();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log($"{ProductIdentity.Name} {ProductIdentity.Version} — {ProductIdentity.Tagline}");
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
            _control = _smu.ControlSurface();
            _settings = _tune.LoadSettings();
            LoadSettingsToUi();
            if (!EnsureEulaAccepted(forcePrompt: !_settings.HasAcceptedCurrentEula))
            {
                Log("EULA not accepted — Optimize and BIOS/SMU writes stay locked until you accept in About.");
            }
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
                TxtRamGuidance.Text = RamTimingGuidance.Guidance(ram);
            }
            else
                TxtRamGuidance.Text = RamTimingGuidance.Guidance();
            if (_settings.ApplyProfilesOnStart && _settings.HasAcceptedCurrentEula)
                await TryReapplySavedTunesOnStartupAsync();
            RefreshBenchText();
            RefreshHistoryText();
            RefreshAppProfilesText();
            MaybeOfferOptimizeResume();
            _poll.Start();
            _hotApplyPoll.Start();
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
        _hotApplyPoll.Stop();
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
        if (ChkAppAutoApply is not null)
            ChkAppAutoApply.IsChecked = _settings.AppProfileAutoApply;
        ChkStartupUpdateCheck.IsChecked = _settings.CheckForUpdatesOnStartup;
    }

    void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        _settings.ApplyProfilesOnStart = ChkApplyOnStart.IsChecked == true;
        _settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        _settings.MinimizeToTray = ChkTray.IsChecked == true;
        if (ChkAppAutoApply is not null)
            _settings.AppProfileAutoApply = ChkAppAutoApply.IsChecked == true;
        _settings.CheckForUpdatesOnStartup = ChkStartupUpdateCheck.IsChecked == true;
        _tune?.SaveSettings(_settings);
        try { WindowsStartup.SetEnabled(_settings.StartWithWindows); }
        catch (Exception ex) { Log("Start with Windows: " + ex.Message); }
    }

    void OnAppAutoApplyChanged(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        var on = ChkAppAutoApply.IsChecked == true;
        _settings.AppProfileAutoApply = on;
        _tune.SaveSettings(_settings);
        var store = _tune.LoadAppProfiles();
        store.AutoApply = on;
        if (on) store.Enabled = true;
        _tune.SaveAppProfiles(store);
        if (!on)
            _hotApply.ClearApplied();
        RefreshAppProfilesText();
        Log(on
            ? "Per-app auto-apply enabled (foreground watch, 2.5s debounce; skipped while Optimize runs)."
            : "Per-app auto-apply disabled.");
    }

    /// <summary>Called from App startup when optional silent update check finds a newer version.</summary>
    public void NotifySilentUpdateAvailable(UpdateChecker.Result result)
    {
        if (!result.UpdateAvailable) return;
        var text = StartupUpdateCheck.FormatTrayText(result);
        Log(text);
        TxtFooter.Text = text.Length > 120 ? text[..120] + "…" : text;
        _tray?.ShowBalloon(StartupUpdateCheck.TrayTitle, text);
    }

    async Task RefreshSmuAsync()
    {
        if (_smu is null) return;
        try
        {
            string? info = null;
            if (_smu.Backend is AmdRyzenMasterBackend amd)
            {
                info = await Task.Run(() => amd.InfoJson());
                TxtPboBackend.Text = SmuStatusLine(info);
                var live = CpuSmuProtocol.TryParseProfile(info);
                if (live is not null)
                    LoadProfileToUi(live);
            }
            else
                TxtPboBackend.Text = $"SMU: {_smu.BackendName}";

            var gpuProbe = _last is null
                ? null
                : GpuControlProbe.FromRanges(
                    _last.ClockRange is not null || _last.VoltageRange is not null,
                    _last.Fan is not null);
            _caps = await Task.Run(() => _smu.ProbeCapabilities(info, gpuProbe));
            ApplyCapabilityUi(_caps);
        }
        catch (Exception ex)
        {
            TxtPboBackend.Text = "SMU: " + ex.Message;
            TxtControlCaps.Text = "Capabilities: probe failed — " + ex.Message;
        }
    }

    void ApplyCapabilityUi(WindowsControlCapabilities caps)
    {
        TxtControlCaps.Text = caps.StatusLine();
        BtnPboApply.IsEnabled = caps.SessionPbo || caps.SessionCo;
        BtnPboTune.IsEnabled = caps.SessionCo;
        BtnPboClearSession.IsEnabled = caps.SessionCo || caps.SessionPbo;
        BtnPboBios.IsEnabled = caps.BiosPbo || caps.BiosCo;
        BtnPboStockBios.IsEnabled = caps.BiosStockWrite;
        BtnRamWrite.IsEnabled = caps.BiosRam;
        BtnRamRead.IsEnabled = caps.BiosRam;
        SldBoost.IsEnabled = caps.BoostOverride;
        SldBoost.Opacity = caps.BoostOverride ? 1.0 : 0.55;
        if (!caps.BoostOverride)
            LblBoost.Text = $"+{(int)SldBoost.Value} MHz (not applied)";
        if (TxtCurveShaper is not null)
            TxtCurveShaper.Text = caps.CurveShaper
                ? "Curve Shaper: available (RM C export probed)."
                : caps.CurveShaperReason;
        if (BtnCurveShaperApply is not null)
        {
            BtnCurveShaperApply.IsEnabled = caps.CurveShaper;
            BtnCurveShaperApply.Opacity = caps.CurveShaper ? 1.0 : 0.55;
        }
        if (!caps.HelperAvailable && !string.IsNullOrEmpty(caps.Error))
            Log("Control: " + caps.Error);
        else if (!caps.BiosPbo)
            Log("BIOS persist unavailable (CDefaultBIOS not bound). Session SMU only until reboot.");
        if (!caps.BoostOverride)
            Log(BiosWriteGuard.BoostUnavailableNote);
        if (!caps.CurveShaper)
            Log(CurveShaperSupport.UnavailableShort);
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

    /// <summary>
    /// Reliable startup re-apply: GPU + CPU from saved profiles, with one retry and pack fallback.
    /// </summary>
    async Task TryReapplySavedTunesOnStartupAsync()
    {
        if (_tune is null) return;
        var gpu = _tune.LoadStartupProfile();
        var cpu = _tune.LoadCpuPboProfile();
        ProfilePack? pack = null;
        if (gpu is null && cpu is null)
        {
            var fallback = _tune.FindNewestPackFallback();
            if (fallback is not null)
            {
                try
                {
                    pack = ProfilePack.TryLoadFile(fallback);
                    if (pack is not null)
                    {
                        Log($"No discrete startup profiles — using pack fallback: {fallback}");
                        _tune.ApplyProfilePackFiles(pack);
                        gpu = pack.Gpu ?? _tune.LoadStartupProfile();
                        cpu = pack.Cpu ?? _tune.LoadCpuPboProfile();
                    }
                }
                catch (Exception ex)
                {
                    Log("Pack fallback load failed: " + ex.Message);
                }
            }
        }

        var plan = StartupReapply.BuildPlan(gpu is not null, cpu is not null, pack is not null);
        if (plan is null)
        {
            Log("Startup re-apply: no saved GPU/CPU profiles or packs.");
            return;
        }
        Log(StartupReapply.Describe(plan));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool ok = true;
            if (gpu is not null)
            {
                try
                {
                    Log($"Applying saved GPU profile ({gpu.VoltageMv} mV @ {gpu.MaxMhz} MHz)…");
                    await Task.Run(() => _tune.ApplyProfile(gpu));
                    Log("GPU startup profile applied.");
                }
                catch (Exception ex)
                {
                    ok = false;
                    Log($"GPU startup apply failed (attempt {attempt + 1}): " + ex.Message);
                }
            }
            if (cpu is not null && _smu is not null)
            {
                try
                {
                    Log($"Applying saved CPU PBO (PPT {cpu.PptWatts} W)…");
                    var r = await Task.Run(() => _smu.Apply(cpu, PersistMode.Session));
                    Log($"CPU start apply session={r.SessionApplied} co={r.CoWritten} {(r.Error ?? "ok")}");
                    if (!r.SessionApplied) ok = false;
                }
                catch (Exception ex)
                {
                    ok = false;
                    Log($"CPU startup apply failed (attempt {attempt + 1}): " + ex.Message);
                }
            }
            if (ok || !StartupReapply.ShouldRetry(attempt, ok))
            {
                if (ok)
                    await RefreshInfoAsync();
                else
                    Log("Startup re-apply did not fully succeed — use Restore last tune after Adrenalin/RM are ready.");
                return;
            }
            Log("Retrying startup re-apply once…");
            try { await Task.Delay(800); } catch { /* ignore */ }
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

    // (metrics snapshot is written after Optimize and from About — not every poll)

    async Task RefreshInfoAsync()
    {
        if (_hw is null) return;
        var snap = await Task.Run(() => _hw.Info());
        _last = snap;
        TxtSubtitle.Text = $"{snap.GpuName}  ·  {snap.VramMb} MB";
        var support = PlatformSupport.FromNames(snap.GpuName, cpuName: null);
        TxtPlatformHint.Text = support.HasUnsupportedHint ? support.Message : "";
        if (support.HasUnsupportedHint)
            Log(support.Message);
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
        if (TxtRamPrimaries is not null)
            TxtRamPrimaries.Text = RamTimingGuidance.FormatPrimaryLine(p);
    }

    async void OnCurveShaperApply(object sender, RoutedEventArgs e)
    {
        if (_caps is null || !_caps.CurveShaper)
        {
            MessageBox.Show(this, CurveShaperSupport.UnavailableReason, "Curve Shaper",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureEulaAccepted()) return;
        if (_smu is null) return;
        if (!ConfirmDangerousWrite(BiosWriteGuard.SessionWarning, "Apply Curve Shaper", requireAdmin: true))
            return;
        await RunExclusive("Applying Curve Shaper…", async ct =>
        {
            var surface = _smu.ControlSurface();
            var profile = new CurveShaperProfile { Enabled = true };
            var caps = _caps;
            var r = await Task.Run(() => surface.ApplyCurveShaper(profile, caps), ct);
            Log($"Curve Shaper apply session={r.SessionApplied} {(r.Error ?? "ok")}");
            if (!string.IsNullOrEmpty(r.Error))
                throw new HwException(r.Error);
        });
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
            await Dispatcher.InvokeAsync(() =>
            {
                LoadRamToUi(p);
                TxtRamGuidance.Text = RamTimingGuidance.Guidance(p);
            });
            Log(RamTimingGuidance.FormatPrimaryLine(p));
            foreach (var w in RamTimingGuidance.SoftWarnings(p))
                Log("RAM note: " + w);
        });
    }

    async void OnRamWrite(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        if (!EnsureEulaAccepted()) return;
        if (!ConfirmDangerousWrite(BiosWriteGuard.RamBiosWarning, "Write RAM BIOS", requireAdmin: true))
            return;
        var profile = UiRamProfile();
        foreach (var w in RamTimingGuidance.SoftWarnings(profile))
            Log("RAM note: " + w);
        if (MessageBox.Show(this,
                $"Confirm RAM values:\n{RamTimingGuidance.FormatPrimaryLine(profile)}\n\nReboot required after a successful BIOS write.",
                "Write RAM BIOS",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunExclusive("Writing RAM timings to BIOS…", async ct =>
        {
            var surface = _control ?? _smu.ControlSurface();
            var r = await Task.Run(() => surface.ApplyRam(profile), ct);
            _tune?.SaveRamProfile(profile);
            await Dispatcher.InvokeAsync(() => TxtRamGuidance.Text = RamTimingGuidance.Guidance(profile));
            Log($"RAM BIOS persist={r.BiosPersisted} reboot={r.RequiresReboot} {(r.Error ?? "ok")}");
            if (!r.BiosPersisted)
                throw new HwException(r.Error ?? "RAM BIOS apply failed");
            if (r.RequiresReboot)
                Log("Reboot required for RAM BIOS timings.");
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
        if (!EnsureEulaAccepted()) return false;
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

    bool EnsureEulaAccepted(bool forcePrompt = false)
    {
        if (_tune is null) return _settings.HasAcceptedCurrentEula;
        if (!forcePrompt && _settings.HasAcceptedCurrentEula)
            return true;

        var body =
            $"{ProductIdentity.Name} {ProductIdentity.Version}\n\n" +
            ProductIdentity.EulaSummary + "\n\n" +
            ProductIdentity.ShortDisclaimer + "\n\n" +
            "Full text ships as EULA.txt and DISCLAIMER.txt next to ZenLoop.exe.\n\n" +
            "Do you accept and continue?";

        var result = MessageBox.Show(this, body, "ZenLoop — first-run safety",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return false;

        _settings.AcceptedEulaVersion = ProductIdentity.EulaVersion;
        _tune.SaveSettings(_settings);
        Log($"Accepted EULA v{ProductIdentity.EulaVersion}");
        return true;
    }

    async void OnAbout(object sender, RoutedEventArgs e)
    {
        var extra = _settings.HasAcceptedCurrentEula
            ? $"\n\nEULA v{ProductIdentity.EulaVersion}: accepted."
            : $"\n\nEULA v{ProductIdentity.EulaVersion}: not accepted yet.";
        WriteMetricsSnapshot("telemetry");

        var updateLine = "\n\nUpdate check: (checking…)";
        try
        {
            var check = await UpdateChecker.CheckAsync(
                ProductIdentity.Version,
                _settings.UpdateManifestUrl);
            updateLine = "\n\n" + check.Message;
            if (check.UpdateAvailable)
                Log(check.Message);
        }
        catch (Exception ex)
        {
            updateLine = "\n\nUpdate check skipped: " + ex.Message;
        }

        var recoveryPath = FindShippedRecoveryDoc();
        var recoveryHint = recoveryPath is null
            ? "\n\nRecovery guide not found next to the exe (expected RECOVERY.md)."
            : "\n\nFull recovery guide: " + recoveryPath;

        var choice = MessageBox.Show(this,
            ProductIdentity.AboutText() + extra + updateLine + recoveryHint +
            "\n\n" + MetricsSnapshotExport.PathHelp +
            "\n\nYes = re-open EULA accept  ·  No = open recovery guide  ·  Cancel = close",
            "About ZenLoop",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (choice == MessageBoxResult.Yes)
            EnsureEulaAccepted(forcePrompt: true);
        else if (choice == MessageBoxResult.No)
            TryOpenRecoveryDoc(recoveryPath);
    }

    static string? FindShippedRecoveryDoc()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { "RECOVERY.md", System.IO.Path.Combine("docs", "RECOVERY.md") })
        {
            var path = System.IO.Path.Combine(baseDir, rel);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    void TryOpenRecoveryDoc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this,
                ProductIdentity.RecoverySummary + "\n\nRECOVERY.md was not found beside ZenLoop.exe. " +
                "See docs/RECOVERY.md in the source tree.",
                "Recovery", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open recovery guide:\n" + ex.Message + "\n\n" + path,
                "Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnExportPack(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        var goal = ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString();
        var pack = _tune.BuildProfilePack(goal, notes: "Exported from ZenLoop UI");
        if (pack.Gpu is null && pack.Cpu is null && pack.Ram is null)
        {
            MessageBox.Show(this, "Nothing to export yet. Run Optimize or save GPU/CPU/RAM profiles first.",
                "Export tune pack", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export ZenLoop tune pack",
            Filter = "ZenLoop profile pack (*.zenloop.json)|*.zenloop.json|JSON (*.json)|*.json",
            FileName = $"zenloop-tune-{DateTime.Now:yyyyMMdd-HHmm}.zenloop.json",
            InitialDirectory = Directory.Exists(_tune.ProfilePackExportDir)
                ? _tune.ProfilePackExportDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            pack.SaveFile(dlg.FileName);
            Log("Exported pack: " + pack.Summary() + " → " + dlg.FileName);
            MessageBox.Show(this, "Saved:\n" + dlg.FileName + "\n\n" + pack.Summary(),
                "Export tune pack", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OnImportPack(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        if (!EnsureEulaAccepted()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import ZenLoop tune pack",
            Filter = "ZenLoop profile pack (*.zenloop.json)|*.zenloop.json|JSON (*.json)|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var pack = ProfilePack.Parse(File.ReadAllText(dlg.FileName));
            if (MessageBox.Show(this,
                    "Import and overwrite saved profiles?\n\n" + pack.Summary() +
                    "\n\nThis does not apply live settings until you click Restore last tune / Apply / Write BIOS.",
                    "Import tune pack",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _tune.ApplyProfilePackFiles(pack);
            if (pack.Cpu is not null) LoadProfileToUi(pack.Cpu);
            if (pack.Ram is not null)
            {
                LoadRamToUi(pack.Ram);
                TxtRamGuidance.Text = RamTimingGuidance.Guidance(pack.Ram);
            }
            Log("Imported pack: " + pack.Summary() + " ← " + dlg.FileName);
            MessageBox.Show(this, "Profiles saved. Use Restore last tune to apply GPU+CPU session settings.",
                "Import tune pack", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    async Task ApplyPboAsync(PersistMode persist)
    {
        if (_smu is null) return;
        if (persist == PersistMode.Bios && _caps is { BiosPbo: false, BiosCo: false })
        {
            MessageBox.Show(this,
                "BIOS persist is not available on this PC (CDefaultBIOS not bound). Session apply still works until reboot.",
                "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var profile = UiProfile();
        await RunExclusive(persist == PersistMode.Bios ? "BIOS persist PBO + Curve Optimizer…" : "Applying PBO session via SMU…",
            async ct =>
            {
                var surface = _control ?? _smu.ControlSurface();
                var result = await Task.Run(() => surface.Apply(profile, persist), ct);
                Log($"PBO apply backend={result.Backend} session={result.SessionApplied} bios={result.BiosPersisted} co={result.CoWritten} reboot={result.RequiresReboot} boost_applied={result.BoostOverrideApplied}");
                if (result.Error is string err)
                    Log("PBO: " + err);
                if (result.Note is string note)
                    Log("PBO note: " + note);
                TxtPboBackend.Text = $"SMU: {result.Backend}  session={(result.SessionApplied ? "applied" : "no")}  BIOS={(result.BiosPersisted ? "persisted" : "not persisted")}  CO={(result.CoWritten ? "written" : "not written")}";
                if (!result.SessionApplied || (profile.Cores.Count > 0 && !result.CoWritten))
                    throw new HwException(result.Error ?? "PBO apply failed (per-core Curve Optimizer not written)");
                if (persist == PersistMode.Bios && !result.BiosPersisted)
                    throw new HwException(result.Error ?? "BIOS persist failed");
                if (result.RequiresReboot)
                    Log("Reboot required for BIOS values to take effect in firmware.");
                if (!result.BoostOverrideApplied && profile.BoostOverrideMhz != 0)
                    Log(BiosWriteGuard.BoostUnavailableNote);
                _tune?.SaveCpuPboProfile(profile);
            });
    }

    async void OnPboClearSession(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        if (!ConfirmDangerousWrite(
                "Clear Curve Optimizer offsets to 0 for this Windows session only.\n\nDoes not undo BIOS. Values reset on reboot unless you Write stock BIOS.",
                "Clear session CO", requireAdmin: true))
            return;
        await RunExclusive("Clearing session Curve Optimizer…", async ct =>
        {
            var surface = _control ?? _smu.ControlSurface();
            var r = await Task.Run(() => surface.RestoreSessionStock(UiProfile()), ct);
            Log($"Session stock CO clear session={r.SessionApplied} co={r.CoWritten} {(r.Error ?? "ok")}");
            if (!r.SessionApplied)
                throw new HwException(r.Error ?? "Session CO clear failed");
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var s in _coSliders) s.Value = 0;
                OnPboSlider(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
            });
        });
    }

    async void OnPboStockBios(object sender, RoutedEventArgs e)
    {
        if (_smu is null) return;
        if (_caps is { BiosStockWrite: false })
        {
            MessageBox.Show(this,
                "Stock BIOS write is not available (CDefaultBIOS not bound). Use Clear session CO for this boot, or CLR_CMOS for a full motherboard reset.",
                "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmDangerousWrite(BiosWriteGuard.StockBiosWarning, "Write stock BIOS", requireAdmin: true))
            return;
        await RunExclusive("Writing stock-like PBO + CO=0 to BIOS…", async ct =>
        {
            var surface = _control ?? _smu.ControlSurface();
            var r = await Task.Run(() => surface.WriteStockToBios(UiProfile()), ct);
            Log($"Stock BIOS persist={r.BiosPersisted} reboot={r.RequiresReboot} {(r.Error ?? "ok")}");
            if (!r.BiosPersisted)
                throw new HwException(r.Error ?? "Stock BIOS write failed");
            Log("Reboot required. This is not a full UEFI Optimized Defaults — use CLR_CMOS if POST fails.");
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
        var alsoCpu = _smu is { IsAvailable: true };
        var msg = alsoCpu
            ? "Restore AMD factory GPU tuning (Adrenalin Default) and clear session Curve Optimizer to 0?"
            : "Restore AMD factory GPU tuning (Adrenalin Default)?";
        if (MessageBox.Show(this, msg, "ZenLoop",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunExclusive("Resetting to factory…", async ct =>
        {
            await Task.Run(() => _hw.Reset(), ct);
            _tune?.ClearAutotuneProgress();
            if (alsoCpu && _smu is not null && _tune is not null)
            {
                try
                {
                    _tune.RestoreSessionCpuStock(_smu, UiProfile());
                    Log("Session Curve Optimizer cleared to 0.");
                }
                catch (Exception ex)
                {
                    Log("CPU stock session clear skipped: " + ex.Message);
                }
            }
            Log("Factory GPU tuning restored.");
        });
        await RefreshInfoAsync();
    }

    async void OnOptimize(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        if (!EnsureEulaAccepted()) return;
        if (_last is not null)
        {
            var support = PlatformSupport.FromNames(_last.GpuName);
            if (!support.AmdGpuLikely)
            {
                MessageBox.Show(this, support.Message, "Unsupported GPU", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var goal = ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString()?.ToLowerInvariant() ?? "balanced";
        var secs = (int)SldSeconds.Value;

        bool resume = false;
        var incomplete = _tune.LoadIncompleteOptimize(goal);
        if (incomplete is not null)
        {
            var choice = MessageBox.Show(this,
                incomplete.DescribeResume() + "\n\nYes = continue after reboot/crash\nNo = start fresh\nCancel = abort",
                "Resume Optimize?",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
                resume = true;
            else
                _tune.ClearOptimizeCheckpoint();
        }

        if (!resume && MessageBox.Show(this,
                "ZenLoop will:\n"
                + "1. Restore stock GPU (and session Curve Optimizer = 0 if SMU is live)\n"
                + "2. Benchmark stock (CPU + RAM + GPU)\n"
                + "3. Auto undervolt/overclock the GPU\n"
                + "4. Auto-tune per-core Curve Optimizer (with all-core confirm)\n"
                + "5. Benchmark again and show faster / cooler / less power\n\n"
                + $"{ZenLoop.Core.ClockSearch.DescribeGoal(goal)}\n"
                + $"{secs}s per step\nClose games first. This takes several minutes.\n\n"
                + ProductIdentity.ShortDisclaimer,
                "Optimize this PC",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        if (resume && MessageBox.Show(this,
                "Continue Optimize from the last finished phase?\n"
                + $"{incomplete!.DescribeResume()}\n\n"
                + ProductIdentity.ShortDisclaimer,
                "Continue Optimize",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var limits = new Limits { MinVoltageMv = 1025, MaxClockMhz = 3000 };
        var seed = UiProfile();
        string? optimizeBanner = null;
        await RunExclusive(resume ? "Resuming Optimize…" : "Optimizing this PC…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = Math.Max(Bar.Value, p.Fraction);
                var first = string.IsNullOrWhiteSpace(p.Message) ? (p.StepName ?? "") : p.Message.Split('\n')[0];
                TxtStep.Text = first.Length > 120 ? first[..120] : first;
                Log(p.Message);
                if (p.StepName == "done" && !string.IsNullOrWhiteSpace(p.Message))
                    optimizeBanner = p.Message;
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            try
            {
                var delta = await _tune.RunAutonomousAsync(
                    goal, secs, limits, ChkSkipVram.IsChecked == true, _smu, seed, progress, ct, resume);
                RefreshBenchText();
                RefreshHistoryText();
                if (!string.IsNullOrWhiteSpace(optimizeBanner))
                {
                    foreach (var line in optimizeBanner.Split('\n'))
                        Log(line);
                    TxtStep.Text = optimizeBanner.Split('\n')[0];
                    if (TxtBench is not null)
                        TxtBench.Text = optimizeBanner + (delta is null ? "" : "\n\n" + delta.Report(_tune.LoadBenchBaseline(), _tune.LoadBenchCurrent()));
                }
                else if (delta is not null)
                {
                    Log(delta.Summary);
                    Log(delta.Report(_tune.LoadBenchBaseline(), _tune.LoadBenchCurrent()));
                    TxtStep.Text = delta.Summary;
                }
                else
                    Log("Optimize finished but a baseline or current bench is missing.");
                WriteMetricsSnapshot(
                    "optimize",
                    goal,
                    delta?.Summary,
                    pass: delta is not null,
                    baseline: _tune.LoadBenchBaseline(),
                    tuned: _tune.LoadBenchCurrent(),
                    delta: delta);
            }
            catch (OperationCanceledException)
            {
                var abort = OptimizeSummary.FormatAbort("stopped by user (Stop / cancel)");
                Log(abort);
                TxtStep.Text = "=== Optimize ABORT ===";
                WriteMetricsSnapshot(
                    "optimize",
                    goal,
                    summary: abort,
                    pass: false,
                    abortReason: "stopped by user (Stop / cancel)");
                try { _tune.MarkOptimizeAborted("user", "stopped by user (Stop / cancel)"); }
                catch (Exception ex) { Log("Forensics abort write skipped: " + ex.Message); }
                throw;
            }
        }, restoreOnCancel: true, preserveStepOnSuccess: true);
        await RefreshInfoAsync();
        TryLoadCpuProfileIntoUi();
    }

    void WriteMetricsSnapshot(
        string source,
        string? goal = null,
        string? summary = null,
        bool? pass = null,
        BenchRun? baseline = null,
        BenchRun? tuned = null,
        BenchDelta? delta = null,
        string? abortReason = null,
        string? profilePackId = null)
    {
        try
        {
            var metrics = _last?.Metrics ?? new Dictionary<string, double?>();
            MetricsSnapshot snap;
            if (string.Equals(source, "optimize", StringComparison.OrdinalIgnoreCase)
                || baseline is not null || tuned is not null || delta is not null
                || !string.IsNullOrWhiteSpace(abortReason))
            {
                snap = MetricsSnapshotExport.FromOptimize(
                    baseline,
                    tuned,
                    delta,
                    liveMetrics: metrics,
                    goal: goal,
                    summary: summary,
                    pass: pass,
                    abortReason: abortReason,
                    profilePackId: profilePackId);
            }
            else
            {
                snap = MetricsSnapshotExport.FromMetrics(
                    metrics, source, goal, summary, pass, abortReason: abortReason, profilePackId: profilePackId);
            }
            var path = MetricsSnapshotExport.Write(snap, atomic: true);
            Log($"Metrics snapshot → {path}");
        }
        catch (Exception ex)
        {
            Log("Metrics export skipped: " + ex.Message);
        }
    }

    async void OnAutoGpu(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        var goal = ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString()?.ToLowerInvariant() ?? "balanced";
        var secs = (int)SldSeconds.Value;

        bool resume = true;
        if (_tune.HasProgress(goal))
        {
            var choice = MessageBox.Show(this,
                "An incomplete GPU auto-tune was found.\n\nYes = resume where it left off\nNo = start fresh\nCancel = abort",
                "Resume GPU tune?",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.No)
            {
                _tune.ClearAutotuneProgress();
                resume = false;
            }
        }

        if (MessageBox.Show(this,
                "This changes live GPU clocks and voltage.\nClose games first.\n\n"
                + $"{ZenLoop.Core.ClockSearch.DescribeGoal(goal)}\n{secs}s per step.\n\nContinue?",
                "Auto GPU undervolt + overclock",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var limits = new Limits { MinVoltageMv = 1025, MaxClockMhz = 3000 };
        await RunExclusive("GPU auto-tune running…", async ct =>
        {
            var progress = new Progress<TuneProgress>(p =>
            {
                Bar.Value = Math.Max(Bar.Value, p.Fraction);
                TxtStep.Text = string.IsNullOrWhiteSpace(p.Message) ? (p.StepName ?? "") : p.Message.Split('\n')[0];
                Log(p.Message);
                if (p.Metrics is not null && _last is not null)
                    ApplyMetrics(_last with { Metrics = p.Metrics });
            });
            var result = await _tune.GpuAutotuneAsync(goal, secs, limits, ChkSkipVram.IsChecked == true, progress, ct, resume);
            var ok = result.TryGetValue("ok", out var o) && o is true;
            Log(ok ? "Auto-tune finished. Profile saved." : "Auto-tune stopped: " + result.GetValueOrDefault("reason"));
            TxtStep.Text = ok ? "GPU tune saved" : "GPU tune stopped";
        }, restoreOnCancel: true, preserveStepOnSuccess: true);
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
            MessageBox.Show(this, "No saved profiles yet. Run Optimize or Auto GPU UV + OC first.", "ZenLoop",
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

    async Task HotApplyTickAsync()
    {
        if (_tune is null || _hotApplyRunning || !_settings.HasAcceptedCurrentEula)
            return;
        var store = _tune.LoadAppProfiles();
        var auto = _settings.AppProfileAutoApply || store.AutoApply;
        if (!store.Enabled && !auto)
        {
            RefreshAppProfilesTextQuiet();
            return;
        }

        _hotApplyRunning = true;
        try
        {
            var identity = await Task.Run(ForegroundProcess.TryGetIdentity);
            var (decision, matched) = _hotApply.Tick(
                store, auto, _busy, identity, DateTime.UtcNow,
                packExists: p => File.Exists(_tune.ResolveAppPackPath(p)));

            if (matched is not null && decision is AppProfileApplyDecision.Apply
                or AppProfileApplyDecision.SkipDebounce
                or AppProfileApplyDecision.SkipAlreadyApplied)
            {
                if (TxtAppProfiles is not null)
                    TxtAppProfiles.Text = store.StatusLine()
                        + $" Foreground: {matched.DisplayName ?? matched.Match} ({AppProfileHotApply.Describe(decision)}).";
            }
            else
                RefreshAppProfilesTextQuiet();

            if (decision != AppProfileApplyDecision.Apply || matched is null)
                return;

            var packPath = _tune.ResolveAppPackPath(matched.PackPath);
            var pack = ProfilePack.TryLoadFile(packPath);
            if (pack is null)
            {
                Log($"Hot-apply skipped: cannot load pack {packPath}");
                return;
            }

            Log($"Hot-apply: {matched.DisplayName ?? matched.Match} → {pack.Summary()}");
            if (await ApplyPackLiveAsync(pack, exclusive: true))
                _hotApply.MarkApplied(matched.PackPath);
            RefreshAppProfilesText();
        }
        catch (Exception ex)
        {
            Log("Hot-apply: " + ex.Message);
        }
        finally
        {
            _hotApplyRunning = false;
        }
    }

    void RefreshAppProfilesTextQuiet()
    {
        if (_tune is null || TxtAppProfiles is null) return;
        var store = _tune.LoadAppProfiles();
        var text = store.StatusLine();
        var identity = ForegroundProcess.TryGetIdentity();
        var active = AppProfileMatcher.MatchForeground(store, identity)
                     ?? AppProfileMatcher.FindActive(store);
        if (active is not null)
            text += $" Active match: {active.DisplayName ?? active.Match}.";
        TxtAppProfiles.Text = text;
    }

    async Task<bool> ApplyPackLiveAsync(ProfilePack pack, bool exclusive)
    {
        if (_tune is null) return false;
        if (exclusive && _busy) return false;
        _tune.ApplyProfilePackFiles(pack);
        if (pack.Cpu is not null)
            LoadProfileToUi(pack.Cpu);
        if (pack.Ram is not null)
        {
            LoadRamToUi(pack.Ram);
            TxtRamGuidance.Text = RamTimingGuidance.Guidance(pack.Ram);
        }

        async Task Work(CancellationToken ct)
        {
            if (pack.Gpu is not null)
            {
                await Task.Run(() => _tune.ApplyProfile(pack.Gpu), ct);
                Log($"Hot GPU apply {pack.Gpu.VoltageMv} mV @ {pack.Gpu.MaxMhz} MHz");
            }
            if (pack.Cpu is not null && _smu is not null)
            {
                var r = await Task.Run(() => _smu.Apply(pack.Cpu, PersistMode.Session), ct);
                Log($"Hot CPU apply session={r.SessionApplied} co={r.CoWritten} {(r.Error ?? "ok")}");
                if (!r.SessionApplied && r.Error is string err)
                    throw new HwException(err);
            }
        }

        if (exclusive)
        {
            if (_busy) return false;
            bool ok = false;
            await RunExclusive("Hot-applying per-app pack…", async ct =>
            {
                await Work(ct);
                ok = true;
            });
            await RefreshInfoAsync();
            return ok;
        }

        await Work(CancellationToken.None);
        await RefreshInfoAsync();
        return true;
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

    void RefreshHistoryText()
    {
        if (_tune is null || TxtHistory is null) return;
        TxtHistory.Text = _tune.LoadOptimizeHistory().FormatRecent(5);
    }

    void RefreshAppProfilesText() => RefreshAppProfilesTextQuiet();

    void MaybeOfferOptimizeResume()
    {
        if (_tune is null) return;
        var ck = _tune.LoadIncompleteOptimize();
        if (ck is null) return;
        Log(ck.DescribeResume());
        TxtFooter.Text = "Incomplete Optimize found — click Optimize this PC to continue or start fresh.";
    }

    void OnBindAppProfile(object sender, RoutedEventArgs e)
    {
        if (_tune is null) return;
        if (!EnsureEulaAccepted()) return;
        var pack = _tune.BuildProfilePack(
            ((ComboBoxItem)CmbGoal.SelectedItem).Content?.ToString(),
            notes: "Per-app bind");
        if (pack.Gpu is null && pack.Cpu is null && pack.Ram is null)
        {
            MessageBox.Show(this, "Nothing to bind yet. Run Optimize or save GPU/CPU/RAM profiles first.",
                "Bind pack to app", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose game/app executable to bind",
            Filter = "Executable (*.exe)|*.exe|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var match = System.IO.Path.GetFileName(dlg.FileName);
            var packsDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(_tune.AppProfilesPath) ?? "",
                "app-packs");
            Directory.CreateDirectory(packsDir);
            var packPath = System.IO.Path.Combine(packsDir, System.IO.Path.GetFileNameWithoutExtension(match) + ".zenloop.json");
            pack.SaveFile(packPath);

            var store = _tune.LoadAppProfiles();
            store.Enabled = true;
            store.AutoApply = _settings.AppProfileAutoApply;
            store.Upsert(match, packPath, displayName: System.IO.Path.GetFileNameWithoutExtension(match));
            _tune.SaveAppProfiles(store);
            RefreshAppProfilesText();
            Log($"Bound pack to '{match}' → {packPath}");
            var tip = _settings.AppProfileAutoApply
                ? "Auto-apply is on: when that exe is focused, ZenLoop will apply this pack (2.5s debounce; skipped during Optimize)."
                : "Enable “Auto-apply per-app pack when matched exe is focused” to hot-apply while gaming.";
            MessageBox.Show(this,
                $"Bound '{match}' to:\n{packPath}\n\n{tip}",
                "Per-app profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bind failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OnClearLog(object sender, RoutedEventArgs e) => TxtLog.Clear();

    async Task RunExclusive(string status, Func<CancellationToken, Task> work, bool restoreOnCancel = false, bool preserveStepOnSuccess = false)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusyUi(true);
        SetStatus("BUSY", true);
        TxtFooter.Text = status;
        TxtStep.Text = status;
        Log(status);
        string? successStep = null;
        try
        {
            await work(_cts.Token);
            if (preserveStepOnSuccess)
                successStep = TxtStep.Text;
        }
        catch (OperationCanceledException)
        {
            Log("Stopped.");
            if (restoreOnCancel)
            {
                try
                {
                    if (_hw is not null)
                    {
                        await Task.Run(() => _hw.Reset());
                        Log("Factory GPU restored after stop.");
                    }
                    _tune?.ClearAutotuneProgress();
                    if (_smu is { IsAvailable: true } && _tune is not null)
                    {
                        _tune.RestoreSessionCpuStock(_smu, UiProfile());
                        Log("Session Curve Optimizer cleared to 0 after stop.");
                    }
                }
                catch (Exception ex) { Log("Restore after stop failed: " + ex.Message); }
            }
            successStep = "stopped";
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex.Message);
            MessageBox.Show(this, ex.Message, "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Error);
            successStep = "error";
        }
        finally
        {
            _busy = false;
            _cts.Dispose();
            _cts = null;
            if (successStep is not null && preserveStepOnSuccess && successStep is not "stopped" and not "error")
            {
                Bar.Value = 1;
                TxtStep.Text = successStep;
            }
            else if (successStep is "stopped" or "error")
            {
                Bar.Value = 0;
                TxtStep.Text = successStep;
            }
            else
            {
                Bar.Value = 0;
                TxtStep.Text = "idle";
            }
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
        if (BtnExportPack is not null)
        {
            BtnExportPack.IsEnabled = !busy;
            BtnImportPack.IsEnabled = !busy;
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
