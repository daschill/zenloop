# RTSS-grade OSD path (ZenLoop)

ZenLoop does **not** ship a competing Afterburner-style overlay. It publishes Optimize / live telemetry into **RivaTuner Statistics Server (RTSS)** using the same shared-memory custom-layer pattern documented in the RTSS SDK (`RTSSSharedMemorySample`), plus two documented side channels.

JSON session export remains at `%LocalAppData%\ZenLoop\zenloop-metrics.json` (see [METRICS-EXPORT.md](./METRICS-EXPORT.md)).

## Install / config

1. Install **RivaTuner Statistics Server** (standalone from guru3D, or with MSI Afterburner).
2. On Radeon systems: leave **AMD Software (Adrenalin)** as the only GPU clock/voltage controller. Do **not** enable Afterburner GPU control alongside Adrenalin (dual-controller conflicts).
3. Start RTSS (tray icon). Show OSD can stay on; ZenLoop will claim a slot owned by `ZenLoop`.
4. Run ZenLoop → **Optimize this PC** (or open **About** for a telemetry sample). ZenLoop then:
   - Writes / refreshes the metrics **JSON** (atomic).
   - Claims an **RTSS OSD slot** via `RTSSSharedMemoryV2` when RTSS is running.
   - Publishes **`Local\ZenLoopMetricsOsd`** named shared memory (layout below).
   - Writes **`%LocalAppData%\ZenLoop\zenloop-osd.txt`** (UTF-8) for OverlayEditor / file-based layers.
5. Optional OverlayEditor: add a text layer fed by a small plugin/reader of `Local\ZenLoopMetricsOsd`, or use HWiNFO sensors for high-rate temps/FPS while ZenLoop shows Optimize score / UV context.

## Channels

| Channel | Name / path | Role |
| --- | --- | --- |
| RTSS OSD slot | Owner `ZenLoop` in `RTSSSharedMemoryV2` | In-game text via RTSS (SDK custom layer) |
| ZenLoop MMF | `Local\ZenLoopMetricsOsd` | Stable binary layout for custom OverlayEditor data sources |
| OSD text file | `%LocalAppData%\ZenLoop\zenloop-osd.txt` | Simple file watcher / layer input |
| Metrics JSON | `%LocalAppData%\ZenLoop\zenloop-metrics.json` | Durable session + live sample (schema 2) |

If RTSS is not installed or not running, JSON + text file (+ ZenLoop MMF on Windows) still work. Fail-closed: missing RTSS never invents overlay content.

## ZenLoop MMF layout (version 1)

Total size **628** bytes, little-endian:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | u32 | Signature `ZLRT` (`0x54524C5A`) |
| 4 | u32 | Layout version `1` |
| 8 | u32 | Flags (`1` = updated) |
| 12 | i64 | Captured Unix ms (UTC) |
| 20 | f64 | CPU temp °C (`NaN` if unknown) |
| 28 | f64 | CPU power W |
| 36 | f64 | Board power W |
| 44 | f64 | Hotspot / GPU temp °C |
| 52 | f64 | Score before |
| 60 | f64 | Score after |
| 68 | char[16] | Result UTF-8 (`Pass` / `Fail` / …) |
| 84 | char[32] | Goal UTF-8 |
| 116 | char[512] | OSD text UTF-8 |

## RTSS SDK reference

Third-party apps display custom OSD text by opening `RTSSSharedMemoryV2`, locating an OSD entry, writing `szOSD` / `szOSDOwner` (and `szOSDEx` when the entry size allows), then bumping `dwOSDFrame`. ZenLoop follows that pattern with owner id **`ZenLoop`**. Sample code ships with RTSS under `SDK\Samples\SharedMemory\RTSSSharedMemorySample`.

## Safety

- No WinRing0.
- No GPU control through Afterburner/RTSS — OSD only.
- Pair with HWiNFO shared memory for high-rate sensor OSD if desired; ZenLoop’s feed is Optimize context + cheap live sample, not a 60 FPS sensor bus.
