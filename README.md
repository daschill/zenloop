# ZenLoop

Windows app that auto undervolts and overclocks **AMD Ryzen + Radeon**, then shows how much **faster, cooler, and less power** the tune uses.

**Supported entry:** `ZenLoop.exe` (C# WPF). Publish with `publish.ps1`, or use `ZenLoop-UI.cmd` from a build tree. The Python CLI (`zenloop.cmd` / `python -m zenloop`) is **deprecated** and kept only for legacy helper debugging — do not use it as the daily product path.

Download, double-click `ZenLoop.exe`, approve Administrator once, accept the first-run safety/EULA prompt, click **Optimize this PC**. No HWiNFO, Python, or extra tuners required for the WPF flow.

Export/Import **tune pack** saves GPU + CPU PBO/CO + RAM profiles as one JSON backup (Adrenalin/RM-style). Intel CPUs and NVIDIA GPUs are unsupported for hardware control — the UI says so honestly.

**Curve Shaper** (Ryzen 9000) is capability-gated: ZenLoop probes Platform/Device for a real C export; if absent, the UI stays disabled with an honest reason (no invented bands).

**RAM guidance** shows primaries + FCLK/MCLK when readable, EXPO-first steps, and soft warnings — not a fake DDR5 calculator.

**Metrics JSON** for RTSS/HWiNFO users: `%LocalAppData%\ZenLoop\zenloop-metrics.json` (see `docs/METRICS-EXPORT.md`). Not an in-app OSD.

## Requirements (Windows AMD PC)

ZenLoop does **not** run on Linux. On the target PC you need:

| Dependency | Why |
| --- | --- |
| **Windows 10/11 x64** | WPF app + ADLX / Ryzen Master helpers |
| **AMD Software (Adrenalin)** | GPU clocks/voltage via ADLX (`amdadlx64.dll`) |
| **AMD Ryzen Master** | CPU PBO / Curve Optimizer / RAM BIOS mailbox (`Platform.dll` + signed driver) |
| **Administrator (UAC)** | SMU and BIOS writes |

If Adrenalin or Ryzen Master is missing, the app detects that and shows install guidance instead of crashing with an opaque helper error.

Reference hardware (search bounds / docs assume this class of parts):

- CPU: Ryzen 7 9800X3D
- GPU: Radeon RX 7900 XTX
- Board: Gigabyte B850 AI TOP

GPU clocks, voltage, VRAM, and power limit are applied live through AMD ADLX (the same API Adrenalin uses). The search steps voltage down and clocks up, stress-tests each step, and backs off on crash, driver timeout, WHEA, or thermal abort.

CPU **Precision Boost Overdrive**, **per-core Curve Optimizer**, and **RAM timings** are written from Windows through AMD’s signed Ryzen Master BIOS mailbox. **Write to BIOS** needs Administrator and an explicit confirmation; ZenLoop refuses silent BIOS success. Reboot after a successful write. This is not Gigabyte boot-order control.

Not affiliated with AMD. Overclocking and undervolting can crash the system; use at your own risk.

## Run

```powershell
git clone https://github.com/daschill/zenloop.git
cd zenloop
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Then copy `dist\ZenLoop\` anywhere (or unzip `dist\ZenLoop-*-win-x64.zip`) and double-click `ZenLoop.exe`. One UAC at launch. Accept EULA on first run. Click **Optimize this PC**.

The publish folder includes `EULA.txt`, `DISCLAIMER.txt`, `LICENSE`, and `README.txt`. Builds are unsigned by default (see `docs/PACKAGING.md`).

Profiles and benches save under `%LocalAppData%\ZenLoop` when you run the published folder.

Daily-driver shortcut from a build tree: `ZenLoop-UI.cmd` or `app\bin\Release\net10.0-windows\ZenLoop.exe`.

Check **Start with Windows** and **Apply saved GPU + CPU on launch**. Closing minimizes to the tray; tray **Exit** quits.

## Optimize this PC

One click:

1. Restore stock GPU (session Curve Optimizer = 0 if SMU is live)
2. Benchmark stock (CPU + RAM + GPU)
3. Auto undervolt / overclock the GPU
4. Auto-tune per-core Curve Optimizer
5. Benchmark again and print faster / cooler / less power

## Benchmarks

Tuner-grade comparison is **score + temperature + watts**, not a clock number. That is how ClockTuner for Ryzen, Hydra, and SkatterBencher report a tune. 3DMark cannot be embedded; ZenLoop uses the same method with in-app workloads.

| What you see | How it is measured |
| --- | --- |
| **Faster** | Geometric mean of CPU/GPU/RAM throughput ratios. Also a harmonic-mean system score. |
| **Cooler** | Peak GPU hotspot (ADLX) and CPU package (Ryzen Master). Negative °C = cooler. Missing sensors are omitted, never invented. |
| **Less power** | GPU total board power (ADLX) + CPU PPT (Ryzen Master). Combined load watts and energy in joules. |
| **Efficiency** | Throughput per watt. Positive = more work per watt. |

## Build

Needs Visual Studio 2022 with the C++ workload, .NET 10, and AMD Software on a Windows machine.

```powershell
powershell -ExecutionPolicy Bypass -File .\native\build.ps1
dotnet test .\ZenLoop.sln -c Release --filter "Category!=LiveHardware"
dotnet build .\ZenLoop.sln -c Release
```

Solution projects: `ZenLoop.App` (WPF), `ZenLoop.Core`, `ZenLoop.Tests`.

`native\build.ps1` clones the AMD ADLX SDK into `vendor/ADLX` (not in this repo) and writes helpers to `zenloop\bin\` for the WPF app to copy — that layout is unchanged even though the Python CLI is deprecated.

### CI

GitHub Actions (`.github/workflows/ci.yml`) restores/builds the solution on `windows-latest` and runs Core unit tests with `--filter Category!=LiveHardware`. Live ADLX / Ryzen Master stress is skipped on CI runners.

## Safety

- GPU edge abort 88 C, hotspot 105 C, VRAM 96 C
- Will not auto-search below 1025 mV or above 3000 MHz
- Each failed step is treated as the edge; the daily profile is one step safer
- Stop during GPU autotune restores factory tuning
- CPU package temperature abort uses Ryzen Master telemetry when the app is Administrator
- BIOS / RAM writes require confirmation; if BIOS persist is not available, ZenLoop reports that failure instead of pretending a text file is BIOS

## Recover

- GPU: Adrenalin → Tuning → Default, or the in-app factory reset
- Whole system will not POST: CLR_CMOS, load Optimized Defaults, re-enable EXPO

## License

MIT. AMD ADLX, Adrenalin, and Ryzen Master remain AMD’s.
