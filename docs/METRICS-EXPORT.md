# RTSS / HWiNFO metrics export (schema 2)

ZenLoop does **not** ship an in-game OSD. For overlays, use **HWiNFO + RivaTuner Statistics Server (RTSS)** as usual.

## Snapshot file

After **Optimize this PC** (and when opening **About**), ZenLoop atomically writes:

```
%LocalAppData%\ZenLoop\zenloop-metrics.json
```

Write is temp-file + replace so file watchers never see half-written JSON. The same path is shown in **About**.

## Stable fields (schema 2)

| Field | Meaning |
| --- | --- |
| `schema` | `2` |
| `product` / `app_version` | Brand + build |
| `captured_utc` / `timestamp_utc` | ISO-8601 UTC (aliases) |
| `source` | `optimize` \| `telemetry` \| `about` |
| `result` | `Pass` \| `Fail` \| `Aborted` \| `Unknown` |
| `goal` / `summary` / `pass` | Optimize context |
| `profile_pack_id` | Optional pack id when known |
| `score_before` / `score_after` | Flat aliases of session scores |
| `temp_before_c` / `temp_after_c` | Flat aliases (°C) |
| `power_before_w` / `power_after_w` | Flat aliases (W) |
| `session` | Last Optimize before/after + deltas |
| `live` | Cheap live sample (`cpu_temp_c`, `board_power_w`, `hotspot_c`, …) |
| `metrics` | Raw sensor bag (ADLX / RM / HWiNFO merge) |

Example:

```json
{
  "product": "ZenLoop",
  "schema": 2,
  "timestamp_utc": "…",
  "source": "optimize",
  "result": "Pass",
  "goal": "balanced",
  "score_before": 100.0,
  "score_after": 110.0,
  "temp_before_c": 80.0,
  "temp_after_c": 78.0,
  "power_before_w": 300.0,
  "power_after_w": 270.0,
  "session": {
    "speed_pct": 10.0,
    "temp_delta_c": -2.0,
    "power_delta_w": -30.0,
    "efficiency_pct": 15.0
  },
  "live": {
    "cpu_temp_c": 62.5,
    "cpu_power_w": 88.0,
    "board_power_w": 210.0,
    "hotspot_c": 78.0
  }
}
```

Point a script or custom sensor at that path. Prefer `session` / flat before-after fields for Optimize results; use HWiNFO shared memory for high-rate OSD sensors. Do not run Afterburner GPU control alongside Adrenalin on the same Radeon.
