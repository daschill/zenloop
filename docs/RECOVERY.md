# ZenLoop recovery guide

When a tune goes wrong, work from softest fix to hardest. ZenLoop never automates CLR_CMOS.

## 1. GPU — Adrenalin Default

1. Open **AMD Software: Adrenalin Edition**.
2. Go to **Performance → Tuning**.
3. Choose **Default** (or Reset) for the GPU profile.
4. Or in ZenLoop: **Reset factory** / restore stock GPU (session ADLX).

Do not leave MSI Afterburner (or another GPU tuner) running at the same time — controllers conflict on Radeon.

## 2. CPU — clear Curve Optimizer (session)

- In ZenLoop: set per-core CO offsets to **0** for this boot (Clear session CO / restore stock path).
- This clears the **Windows session** mailbox apply. It is **not** the same as loading UEFI Optimized Defaults.

## 3. BIOS mailbox — Write stock (still not full UEFI reset)

- ZenLoop **Write stock BIOS** (CO=0 / stock-like PBO) then **reboot**.
- Confirm each BIOS write. If UAC is cancelled, nothing is applied.
- This uses AMD’s Ryzen Master BIOS mailbox. It does **not** replace motherboard Optimized Defaults for DRAM training, boot order, or full UEFI state.

## 4. Will not POST — CLR_CMOS (manual)

**Warning:** Clearing CMOS resets motherboard settings (clocks, EXPO/DOCP, boot order, fans). You must re-enter BIOS afterward.

1. Power off and unplug the PSU cord (or switch PSU off).
2. Use the board’s **CLR_CMOS** jumper/button per the motherboard manual (or remove the CMOS battery for several minutes).
3. Power on, enter BIOS, load **Optimized Defaults**.
4. Re-enable **EXPO/DOCP** carefully; restore known-good memory settings from ZenTimings or screenshots.
5. Reinstall/relaunch Adrenalin + Ryzen Master if needed, then re-apply a known-good ZenLoop tune pack.

## 5. After recovery

- Re-test with a short stress before gaming.
- Re-accept that overclocking/undervolting remains at your own risk (see `DISCLAIMER.txt`).

Full product packaging notes: [PACKAGING.md](./PACKAGING.md).
