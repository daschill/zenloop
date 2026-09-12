# RTSS-friendly metrics export

ZenLoop does **not** ship an in-game OSD. For overlays, use **HWiNFO + RivaTuner Statistics Server (RTSS)** as usual.

## Snapshot file

After **Optimize this PC** (and when opening **About**), ZenLoop writes:

```
%LocalAppData%\ZenLoop\zenloop-metrics.json
```

Example fields:

```json
{
  "product": "ZenLoop",
  "schema": 1,
  "captured_utc": "…",
  "source": "optimize",
  "goal": "balanced",
  "summary": "…",
  "pass": true,
  "metrics": {
    "cpu_temp_c": 62.5,
    "cpu_power_w": 88.0,
    "board_power_w": 210.0,
    "hotspot_c": 78.0
  }
}
```

Point a script or custom sensor at that path. Do not run Afterburner GPU control alongside Adrenalin on the same Radeon.
