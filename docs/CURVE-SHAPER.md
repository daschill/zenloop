# Curve Shaper (Ryzen 9000)

ZenLoop probes AMD **Ryzen Master** `Platform.dll` / `Device.dll` for a real Curve Shaper C API before enabling UI write.

## Exhaustive probe

`zenloop-cpu` (`info` / `caps` / `cs-read` / `cs-apply`) runs:

1. **Named `GetProcAddress` list** — `GetCurveShaper`, `SetCurveShaper`, `GetCurveShaperParameters`, `SetCurveShaperParameters`, `EnableCurveShaper`, `DisableCurveShaper`, `GetCSParameters`, `SetCSParameters`, `GetCurveShaperStatus`, `SetCurveShaperStatus`, `GetCurveShaperBands`, `SetCurveShaperBands`, `ReadCurveShaper`, `WriteCurveShaper`, `GetCurveShaperOffset`, `SetCurveShaperOffset`, `ApplyCurveShaper`, `QueryCurveShaper`, plus common MSVC mangled forms (`?GetCurveShaper@@…`, `?SetCurveShaperBands@@…`, etc.).
2. **PE export-table scan** — every named export in Platform/Device; match undecorated / mangled names containing `CurveShaper`, `curve_shaper`, `GetCSParameters` / `SetCSParameters`, `csbands`.

Capabilities JSON includes:

| Field | Meaning |
| --- | --- |
| `curve_shaper` | **CanApply** — `true` only when export found **and** `abi_published` |
| `curve_shaper_export_found` | Detect-only: exhaustive probe matched a CS-related symbol |
| `curve_shaper_note` | Human reason (no invent) |
| `curve_shaper_probe` | Diagnostics: `platform_exports`, `device_exports`, `named_hits`, `pe_hits`, `match`, `match_dll`, `abi_published`, `alternative` |

## Read / write policy

| Probe result | Behavior |
| --- | --- |
| No match | `curve_shaper=false`, `curve_shaper_export_found=false`; Apply disabled; refuse `cs-apply`; **never invent bands** |
| Match, no published C ABI | `curve_shaper_export_found=true`, `curve_shaper=false`; show match + SignatureUnknownNote; **still refuse** band writes |
| Match + published ABI (future) | Set `kCurveShaperAbiPublished` + wire typed `GetProcAddress`; then `cs-read` / `cs-apply` under BiosWriteGuard |

As of current Ryzen Master shipping builds, Curve Shaper remains **GUI-only** — `abi_published` is hardcoded `false`. ZenLoop keeps an honest gate. **Export-found alone never enables Apply.**

## Signed alternative UX

When CS Apply is unavailable, ZenLoop points at the AMD-signed Windows path that **does** exist:

- Per-core **Curve Optimizer** (Negative = undervolt)
- **PBO** PPT / TDC / EDC / scalar (session + BIOS persist)

See `CurveShaperAlternative` in Core and the Advanced panel copy under Curve Shaper. Profile packs may store a `curve_shaper` section for future use, but live hot-apply **logs an honest refuse** and applies GPU/CPU only. Do **not** paste invented CS temperature-band tables into ZenLoop.

## AMD box Optimize

One-click Optimize uses ADLX + PBO/CO — **not** Curve Shaper. Preflight hard-blocks when Adrenalin or Ryzen Master is missing; soft-warns when driver/CO/GPU caps are weak.

## Verify

```powershell
zenloop-cpu.exe caps
# Stock RM: curve_shaper=false, curve_shaper_export_found=false, probe export counts > 0 when DLLs load
# cs-apply always refuses until abi_published + typed stubs
dotnet test tests/ZenLoop.Tests.csproj --filter Category!=LiveHardware
```

Policy: **no WinRing0 / raw SMU / unsigned kernel**. Never invent bands or fake Apply success.
