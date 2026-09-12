# Adrenalin UV bake-off

Local, scripted compare of **stock (Adrenalin Default)** vs **ZenLoop UV** vs an optional **Adrenalin profile import**. Runs on **user hardware** — no cloud GPU. Report JSON/Markdown format is covered by Core unit tests.

## Offline (no GPU) — report format

Given saved bench JSON files (from ZenLoop **Bench baseline** / **Bench current**, or bake-off legs):

```powershell
dotnet run --project scripts/bakeoff-host -c Release -- build `
  --stock path\to\bench-stock.json `
  --zenloop path\to\bench-zenloop.json `
  --adrenalin path\to\bench-adrenalin.json `
  --zenloop-profile path\to\gpu-profile.json `
  --adrenalin-profile path\to\adrenalin-export.json `
  --out %LOCALAPPDATA%\ZenLoop\bakeoff
```

Outputs:

- `%LOCALAPPDATA%\ZenLoop\bakeoff\uv-bakeoff-report.json`
- `%LOCALAPPDATA%\ZenLoop\bakeoff\uv-bakeoff-report.md`

## Live (Windows AMD PC)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uv-bakeoff.ps1 `
  -ZenLoopProfile "$env:LOCALAPPDATA\ZenLoop\profiles\startup-gpu.json" `
  -AdrenalinProfile "D:\exports\adrenalin-tuning.json" `
  -BenchSeconds 20
```

The script:

1. Restores stock GPU via `zenloop-hw.exe reset` (Adrenalin Default path).
2. Runs in-app-compatible stress/bench helpers when present, **or** instructs you to click **Bench baseline / Bench current** and re-run with `-OfflineOnly` paths.
3. Applies ZenLoop GPU profile, benches again.
4. Optionally imports an Adrenalin/CN/ZenLoop profile JSON (best-effort voltage/clock mapping) and benches.
5. Writes the local report.

Prefer the in-app **UV bake-off** button when the WPF UI is available — it uses the same Core report builder with live ADLX benches.

## Adrenalin profile import

`AdrenalinUvBakeOff.TryImportAdrenalinProfile` accepts:

- ZenLoop `GpuProfile` JSON (`voltage_mv`, `max_mhz`, …)
- Flexible JSON envelopes (`profile` / `Tuning` / `GPU`) with common Adrenalin field aliases
- Best-effort XML with `Voltage` / `MaxFreq`-style nodes

Unsupported proprietary CN blobs return **null** (never invent clocks/voltages). Export a readable JSON from Adrenalin when possible, or save a ZenLoop tune pack.

## Safety

No WinRing0. Restore **Adrenalin → Tuning → Default** (or ZenLoop **Reset factory**) if a leg is unstable.
