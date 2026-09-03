# ZenLoop

Windows app that auto undervolts and overclocks **AMD Ryzen + Radeon**, then shows how much **faster, cooler, and less power** the tune uses.

Download, double-click `ZenLoop.exe`, approve Administrator once, click **Optimize this PC**. No HWiNFO, Python, or extra tuners.

Reference hardware:

- CPU: Ryzen 7 9800X3D
- GPU: Radeon RX 7900 XTX
- Board: Gigabyte B850 AI TOP

GPU clocks, voltage, VRAM, and power limit are applied live through AMD ADLX (the same API Adrenalin uses). The search steps voltage down and clocks up, stress-tests each step, and backs off on crash, driver timeout, WHEA, or thermal abort.

CPU **Precision Boost Overdrive**, **per-core Curve Optimizer**, and **RAM timings** are written from Windows through AMD’s signed Ryzen Master BIOS mailbox. **Write to BIOS** needs Administrator. Reboot after a successful write. This is not Gigabyte boot-order control.

Not affiliated with AMD. Overclocking and undervolting can crash the system; use at your own risk.

## Run

```powershell
git clone https://github.com/daschill/zenloop.git
cd zenloop
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Then copy `dist\ZenLoop\` anywhere and double-click `ZenLoop.exe`. One UAC at launch. Click **Optimize this PC**.

Needs **AMD Software (Adrenalin)** for the GPU and **AMD Ryzen Master** so the signed `AMDRyzenMasterDriver` can read PPT/temp and write PBO/CO. Profiles and benches save under `%LocalAppData%\ZenLoop` when you run the published folder.

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

Needs Visual Studio 2022 with the C++ workload, .NET 10, and AMD Software.

```powershell
powershell -ExecutionPolicy Bypass -File .\native\build.ps1
dotnet test .\tests\ZenLoop.Tests.csproj -c Release
dotnet build .\app\ZenLoop.App.csproj -c Release
```

`native\build.ps1` clones the AMD ADLX SDK into `vendor/ADLX` (not in this repo).

## Safety

- GPU edge abort 88 C, hotspot 105 C, VRAM 96 C
- Will not auto-search below 1025 mV or above 3000 MHz
- Each failed step is treated as the edge; the daily profile is one step safer
- Stop during GPU autotune restores factory tuning
- CPU package temperature abort uses Ryzen Master telemetry when the app is Administrator
- If BIOS persist is not available, ZenLoop reports that failure instead of pretending a text file is BIOS

## Recover

- GPU: Adrenalin → Tuning → Default, or the in-app factory reset
- Whole system will not POST: CLR_CMOS, load Optimized Defaults, re-enable EXPO

## License

MIT. AMD ADLX, Adrenalin, and Ryzen Master remain AMD’s.
