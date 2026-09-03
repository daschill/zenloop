from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

from . import __version__, autotune, hw, hwinfo, safety


BANNER = """
ZenLoop  —  stress tester + auto OC/UV for this 9800X3D / 7900 XTX
GPU clocks and voltage are applied live through AMD ADLX (same path as Adrenalin).
CPU Curve Optimizer still has to be set in BIOS / Ryzen Master; this tool tests it.
""".strip()


def _print_info(data: dict) -> None:
    gpu = data.get("gpu") or {}
    tun = data.get("tuning") or {}
    met = data.get("metrics") or {}
    print(f"GPU: {gpu.get('name')}  ({gpu.get('type')}, {gpu.get('vram_mb')} MB)")
    print(
        f"Tuning: min {tun.get('min_mhz')}  max {tun.get('max_mhz')} MHz  "
        f"voltage {tun.get('voltage_mv')} mV  vram {tun.get('vram_mhz')}  "
        f"power {tun.get('power_limit_pct')}%  factory={tun.get('at_factory')}"
    )
    rng = tun.get("ranges") or {}
    if rng:
        def fmt(k: str) -> str:
            r = rng.get(k)
            if not r:
                return "?"
            return f"{r.get('min')}..{r.get('max')} step {r.get('step')}"

        print(f"Ranges: clock {fmt('max_mhz')}  volt {fmt('voltage_mv')}  vram {fmt('vram_mhz')}")
    if met:
        watts = met.get("power_w")
        if watts is None:
            watts = met.get("board_power_w")
        print(
            "Now: "
            f"{met.get('clock_mhz')} MHz  {met.get('voltage_mv')} mV  "
            f"{met.get('gpu_temp_c')} C / hot {met.get('hotspot_c')} C  "
            f"{watts} W"
        )
    cpu = hwinfo.cpu_package_temp()
    if cpu is not None:
        print(f"CPU package (HWiNFO): {cpu:.1f} C")
    elif hwinfo.snapshot().get("source") is None:
        print("CPU temp: start HWiNFO with Shared Memory enabled for CPU thermal abort.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=BANNER, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=f"zenloop {__version__}")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("info", help="Show GPU name, clocks, voltage, live metrics")
    sub.add_parser("metrics", help="Live GPU metrics only")
    sub.add_parser("reset", help="Restore AMD factory GPU tuning (Adrenalin Default)")

    p_set = sub.add_parser("set", help="Manually set GPU clocks/voltage")
    p_set.add_argument("--max-mhz", type=int)
    p_set.add_argument("--min-mhz", type=int)
    p_set.add_argument("--voltage", type=int, help="mV (RDNA3: absolute cap, typically 1150 stock)")
    p_set.add_argument("--vram", type=int)
    p_set.add_argument("--power", type=int, help="Power limit percent, e.g. 0 or 15")
    p_set.add_argument("--fast-timing", type=int, choices=(0, 1))

    p_stress = sub.add_parser("stress", help="Run CPU and/or GPU stress at current settings")
    p_stress.add_argument("--cpu", action="store_true")
    p_stress.add_argument("--gpu", action="store_true")
    p_stress.add_argument("--ram", action="store_true")
    p_stress.add_argument("--seconds", type=int, default=45)
    p_stress.add_argument("--per-core", action="store_true", help="Also cycle each logical CPU (CO tester)")

    p_gpu = sub.add_parser("autotune-gpu", help="Auto undervolt then overclock the 7900 XTX")
    p_gpu.add_argument("--goal", choices=("balanced", "performance", "efficiency"), default="balanced")
    p_gpu.add_argument("--seconds", type=int, default=40, help="Seconds per step (higher = safer, slower)")
    p_gpu.add_argument("--min-voltage", type=int, default=1025)
    p_gpu.add_argument("--max-clock", type=int, default=3000)
    p_gpu.add_argument("--skip-vram", action="store_true")
    p_gpu.add_argument("--dry-run", action="store_true", help="Show plan only, do not change clocks")

    p_cpu = sub.add_parser("autotune-cpu", help="CPU stress suite + optional Windows underclock")
    p_cpu.add_argument("--seconds", type=int, default=40)
    p_cpu.add_argument("--underclock-percent", type=int, help="Cap processor max (100 = full boost)")
    p_cpu.add_argument("--no-per-core", action="store_true")

    p_amd = sub.add_parser("amd-auto", help="Run AMD's own one-click Adrenalin auto UV/OC")
    p_amd.add_argument("kind", choices=("undervolt", "overclock", "vram"))

    p_soak = sub.add_parser("soak", help="Long combined CPU+GPU soak of current settings")
    p_soak.add_argument("--minutes", type=int, default=10)

    p_apply = sub.add_parser("apply", help="Apply a saved profiles/*.json")
    p_apply.add_argument("path")

    sub.add_parser("ui", help="Launch the Windows desktop app")

    args = parser.parse_args(argv)
    print(BANNER)
    print()

    try:
        if args.cmd in ("info", "metrics"):
            _print_info(hw.info())
            return 0
        if args.cmd == "reset":
            hw.reset()
            print("GPU tuning restored to factory.")
            _print_info(hw.info())
            return 0
        if args.cmd == "set":
            data = hw.set_gpu(
                max_mhz=args.max_mhz,
                min_mhz=args.min_mhz,
                voltage=args.voltage,
                vram=args.vram,
                power=args.power,
                fast_timing=None if args.fast_timing is None else bool(args.fast_timing),
            )
            _print_info(data)
            return 0
        if args.cmd == "stress":
            do_cpu = args.cpu or not args.gpu
            do_gpu = args.gpu or not args.cpu
            if args.ram:
                autotune.cpu_stress_suite(seconds=args.seconds, per_core=False, ram=True)
            if do_cpu:
                r = autotune.cpu_stress_suite(seconds=args.seconds, per_core=args.per_core, ram=False)
                print("CPU:", "PASS" if r["passed"] else "FAIL")
            if do_gpu:
                r = autotune.stress("gpu", args.seconds, safety.DEFAULT_LIMITS, on_tick=autotune._print_tick)
                print("GPU:", "PASS" if r.ok else f"FAIL {r.reason}")
                return 0 if r.ok else 3
            return 0
        if args.cmd == "autotune-gpu":
            limits = safety.Limits(
                min_voltage_mv=args.min_voltage,
                max_clock_mhz=args.max_clock,
            )
            if args.dry_run:
                data = hw.info()
                _print_info(data)
                print(
                    f"\nPlan ({args.goal}): voltage {args.min_voltage}..stock, "
                    f"clock stock..{args.max_clock}, {args.seconds}s per step."
                )
                print("Re-run without --dry-run to apply.")
                return 0
            print("This will change live GPU clocks. Ctrl+C restores factory if the helper is still running.")
            print("A failed step is normal — that is how the search finds the edge.")
            try:
                state = autotune.gpu_autotune(
                    goal=args.goal,
                    seconds=args.seconds,
                    limits=limits,
                    skip_vram=args.skip_vram,
                )
            except KeyboardInterrupt:
                print("\nInterrupted — restoring factory GPU tuning.")
                hw.reset()
                return 130
            print(json.dumps({"best": state.get("best"), "profile": state.get("profile")}, indent=2))
            return 0
        if args.cmd == "autotune-cpu":
            if args.underclock_percent is not None:
                autotune.cpu_underclock_percent(args.underclock_percent)
            r = autotune.cpu_stress_suite(seconds=args.seconds, per_core=not args.no_per_core, ram=True)
            print("CPU suite:", "PASS" if r["passed"] else "FAIL")
            print(
                "\nFor a 9800X3D overclock: BIOS PBO Advanced, +200 MHz, Curve Optimizer Negative 15,\n"
                "then re-run this command. If a single core fails, that core needs a weaker (closer to 0) offset."
            )
            return 0 if r["passed"] else 3
        if args.cmd == "amd-auto":
            data = hw.amd_auto(args.kind)
            _print_info(data)
            return 0
        if args.cmd == "soak":
            secs = max(30, args.minutes * 60)
            print(f"Soaking CPU+GPU for {args.minutes} minutes at current settings.")
            cpu = autotune.stress("cpu", min(secs, 120), safety.DEFAULT_LIMITS, on_tick=autotune._print_tick)
            gpu = autotune.stress("gpu", secs, safety.DEFAULT_LIMITS, on_tick=autotune._print_tick)
            print("CPU:", "PASS" if cpu.ok else cpu.reason)
            print("GPU:", "PASS" if gpu.ok else gpu.reason)
            return 0 if cpu.ok and gpu.ok else 3
        if args.cmd == "ui":
            root = Path(__file__).resolve().parent.parent
            exe = root / "app" / "bin" / "Release" / "net10.0-windows" / "ZenLoop.exe"
            if not exe.exists():
                print("Building UI…")
                subprocess.check_call(
                    ["dotnet", "build", str(root / "app" / "ZenLoop.App.csproj"), "-c", "Release"],
                )
            os.startfile(exe)  # type: ignore[attr-defined]
            return 0
        if args.cmd == "apply":
            profile = json.loads(open(args.path, encoding="utf-8").read())
            st = profile.get("settings") or profile
            hw.set_gpu(
                max_mhz=st.get("max_mhz"),
                min_mhz=st.get("min_mhz"),
                voltage=st.get("voltage_mv"),
                vram=st.get("vram_mhz"),
                fast_timing=st.get("fast_timing"),
                power=st.get("power_pct"),
            )
            _print_info(hw.info())
            return 0
    except hw.HwError as exc:
        print(f"Hardware helper error: {exc}", file=sys.stderr)
        return 2
    except FileNotFoundError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
