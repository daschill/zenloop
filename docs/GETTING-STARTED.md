# Getting started with ZenLoop

ZenLoop is a **Windows** app for **AMD Ryzen + Radeon**. It undervolts/overclocks through AMD’s signed paths (ADLX + Ryzen Master), then shows faster / cooler / less power.

**Supported entry:** `ZenLoop.exe` (C# WPF). Do not use the deprecated Python CLI as the daily product path.

## 1. Install prerequisites (target PC)

| Dependency | Why |
| --- | --- |
| **Windows 10/11 x64** | WPF + ADLX / Ryzen Master helpers |
| **AMD Software (Adrenalin)** | GPU clocks/voltage via ADLX (`amdadlx64.dll`) |
| **AMD Ryzen Master** | CPU PBO / Curve Optimizer / RAM BIOS mailbox (`Platform.dll` + signed driver) |
| **Administrator (UAC)** | Session SMU and BIOS writes |

Download Adrenalin and Ryzen Master from [amd.com](https://www.amd.com/en/support). Reboot after installing, then launch ZenLoop **as Administrator**.

If either product is missing, ZenLoop shows install guidance and **blocks Optimize / UV bake-off** instead of crashing with an opaque helper error.

## 2. Run Optimize

1. Double-click `ZenLoop.exe` (or `ZenLoop-UI.cmd` from a build tree).
2. Approve UAC once.
3. Accept the first-run safety / EULA prompt.
4. Click **Optimize this PC**.

Optimize restores stock GPU, benchmarks, searches GPU UV/OC via ADLX, tunes per-core **Curve Optimizer** (signed PBO path), benchmarks again, and shows the delta. Close games first; it takes several minutes.

**BIOS persist** (optional Advanced / Write to BIOS) always needs an explicit confirmation. ZenLoop refuses silent BIOS success. Reboot after a successful BIOS write.

## 3. Curve Shaper (honest limits)

Ryzen 9000 **Curve Shaper** is probed exhaustively on Ryzen Master `Platform.dll` / `Device.dll` (named exports + PE table).

| Capability flag | Meaning |
| --- | --- |
| `curve_shaper_export_found` | Probe matched a CS-related symbol (detect only) |
| `curve_shaper` / CanApply | Export **and** a **published C ABI** — required for Apply |

Today shipping Ryzen Master keeps Curve Shaper **GUI-only**. ZenLoop sets `abi_published=false`, keeps Apply disabled, and never invents temperature bands or fakes Apply success. Use signed **PBO + per-core Curve Optimizer** instead (Advanced panel copy).

Details: [`CURVE-SHAPER.md`](./CURVE-SHAPER.md).

## 4. Optional extras on the same box

- **DRAM lab** — read/guidance + export ([`DRAM-LAB.md`](./DRAM-LAB.md))
- **Metrics JSON** — `%LocalAppData%\ZenLoop\zenloop-metrics.json` ([`METRICS-EXPORT.md`](./METRICS-EXPORT.md))
- **RTSS OSD** — when RivaTuner is running ([`RTSS-OSD.md`](./RTSS-OSD.md))
- **UV bake-off** — stock vs ZenLoop vs optional Adrenalin profile ([`UV-BAKEOFF.md`](./UV-BAKEOFF.md))
- **Tune pack** — Export/Import GPU + CPU PBO/CO + RAM as one JSON backup

Multi-vendor (Intel/NVIDIA): detect + capability matrix only; Apply runs solely when a public signed API resolves (e.g. NVAPI power policies). No WinRing0 / raw SMU.

## 5. Publish / pack (builders)

On a Windows x64 machine with the .NET 10 SDK:

```powershell
# Portable folder + zip under dist\
powershell -ExecutionPolicy Bypass -File .\publish.ps1

# Optional Inno Setup installer (needs ISCC.exe)
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -Inno
```

Full packaging notes: [`PACKAGING.md`](./PACKAGING.md).

### Optional EV / Authenticode signing

Unsigned by default (SmartScreen may warn). When `SIGNING_CERT_PFX` or `SIGNING_CERT_PATH` + password are set, `publish.ps1` / `pack-installer.ps1` call `scripts/sign-artifacts.ps1`. See [`CODE-SIGNING.md`](./CODE-SIGNING.md). Signing does **not** enable WinRing0 or invent Curve Shaper Apply.

## 6. Verify (CI / local)

```powershell
dotnet test tests/ZenLoop.Tests.csproj --filter Category!=LiveHardware
dotnet build app/ZenLoop.App.csproj -p:EnableWindowsTargeting=true
```

Live ADLX / Ryzen Master smoke stays behind `Category=LiveHardware` (skipped in CI).

## Policy

- No WinRing0 / raw SMU / unsigned kernel drivers
- Never invent Curve Shaper bands or fake Apply success
- Not affiliated with AMD — overclocking can crash or damage hardware; use at your own risk

Recovery after a bad tune: [`RECOVERY.md`](./RECOVERY.md).
