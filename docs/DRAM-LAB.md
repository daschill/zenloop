# DRAM lab (ZenTimings-class read / guidance)

ZenLoop’s DRAM lab is a **read + guidance** surface on the AMD-signed **CDefaultBIOS** mailbox (same path as Ryzen Master). It is not a DDR5 calculator and does not use WinRing0.

## What is shown

| Field | Source | Notes |
| --- | --- | --- |
| Primaries (tCL / tRCD / tRP / tRAS / tRFC) + VDDIO + mem clock | RM BIOS getters when bound | Writable via **Write RAM BIOS** after confirm + reboot |
| EXPO flag | Apply path / UI | Prefer kit EXPO before manual tighten |
| FCLK | RM `GetRmCpuParameters` (FclkP0) and/or HWiNFO labels | Only when reported |
| UCLK / MCLK | HWiNFO labels and/or mem clock | UCLK never invented; MCLK may mirror mem clock when that is all RM returns |
| Secondaries (tRC, tFAW, tRRD_*, tWTR_*, tCWL, tWR, SCL…) | Only if JSON carries them | Stock CDefaultBIOS exposes **primaries only** — UI says unread and points at ZenTimings |

## Export

**Export lab** writes `%LocalAppData%\ZenLoop\exports\zenloop-dram-lab.json` (schema 1): primaries, optional secondaries, fabric clocks, soft warnings, stress advice, guidance text. Atomic temp-file replace.

## Stress advice (guidance only)

After Write RAM BIOS + reboot: memory stress (TM5 / Karhu / HCI), short all-core CPU stress, watch WHEA / reboot, verify FCLK:MCLK in BIOS/ZenTimings, keep CLR_CMOS ready. ZenLoop does not ship a DRAM stress suite and never hot-applies full DRAM tables on Ryzen.

## Won’t fake

- Fabricated DDR5 “safe tables”
- Invented secondary timings
- WinRing0 / raw IMC MMIO
- Claiming unread FCLK/UCLK as known

## Verify

```powershell
dotnet test tests/ZenLoop.Tests.csproj --filter Category!=LiveHardware
# On hardware: Read RAM → primaries + FCLK when RM reports; Export lab → JSON path in log
```
