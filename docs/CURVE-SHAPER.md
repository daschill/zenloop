# Curve Shaper (Ryzen 9000)

ZenLoop probes AMD **Ryzen Master** `Platform.dll` / `Device.dll` for a real Curve Shaper C API before enabling UI write.

## Exhaustive probe

`zenloop-cpu` (`info` / `caps` / `cs-read` / `cs-apply`) runs:

1. **Named `GetProcAddress` list** — `GetCurveShaper`, `SetCurveShaper`, `GetCurveShaperParameters`, `SetCurveShaperParameters`, `EnableCurveShaper`, `DisableCurveShaper`, `GetCSParameters`, `SetCSParameters`, `GetCurveShaperStatus`, `SetCurveShaperStatus`, `GetCurveShaperBands`, `SetCurveShaperBands`, `ReadCurveShaper`, `WriteCurveShaper`, `GetCurveShaperOffset`, `SetCurveShaperOffset`, `ApplyCurveShaper`, `QueryCurveShaper`, plus common MSVC mangled forms.
2. **PE export-table scan** — every named export in Platform/Device; match undecorated / mangled names containing `CurveShaper`, `curve_shaper`, `GetCSParameters` / `SetCSParameters`, `csbands`.

Capabilities JSON includes `curve_shaper`, `curve_shaper_note`, and `curve_shaper_probe` (`platform_exports`, `device_exports`, `named_hits`, `pe_hits`, `match`, `abi_published`).

## Read / write policy

| Probe result | Behavior |
| --- | --- |
| No match | `Capabilities.CurveShaper=false`; Apply disabled; refuse `cs-apply`; **never invent bands** |
| Match, no published C ABI | Flag + note the symbol; **still refuse** band writes (unknown calling convention = crash risk). BiosWriteGuard confirms would apply once AMD documents a C ABI |
| Match + published ABI (future) | Wire `cs-read` / `cs-apply` with session/BIOS confirms (same model as CO) |

As of current Ryzen Master shipping builds, Curve Shaper remains **GUI-only** — ZenLoop keeps an honest gate.

## Signed alternative UX

When CS is unavailable, ZenLoop points at the AMD-signed Windows path that **does** exist:

- Per-core **Curve Optimizer** (Negative = undervolt)
- **PBO** PPT / TDC / EDC / scalar (session + BIOS persist)

See `CurveShaperAlternative` in Core and the Advanced panel copy under Curve Shaper. Do **not** paste invented CS temperature-band tables into ZenLoop.

## Verify

```powershell
zenloop-cpu.exe caps
# capabilities.curve_shaper should be false on stock RM; probe counts > 0 when DLLs load
dotnet test tests/ZenLoop.Tests.csproj --filter Category!=LiveHardware
```
