from __future__ import annotations

import json
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from . import events, hw, hwinfo, safety


@dataclass
class StepResult:
    ok: bool
    reason: str
    settings: dict[str, Any]
    metrics: dict[str, Any]
    throughput: float = 0.0
    faults: list[dict[str, Any]] | None = None


def _project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def logs_dir() -> Path:
    d = _project_root() / "logs"
    d.mkdir(parents=True, exist_ok=True)
    return d


def profiles_dir() -> Path:
    d = _project_root() / "profiles"
    d.mkdir(parents=True, exist_ok=True)
    return d


def _tuning(payload: dict[str, Any]) -> dict[str, Any]:
    return payload.get("tuning") or {}


def _range(payload: dict[str, Any], key: str) -> tuple[int, int, int] | None:
    rng = ((_tuning(payload).get("ranges") or {}).get(key)) or None
    if not rng:
        return None
    return int(rng["min"]), int(rng["max"]), int(rng.get("step") or 1)


def _clamp(val: int, lo: int, hi: int) -> int:
    return max(lo, min(hi, val))


def _log(msg: str) -> None:
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def _save_state(state: dict[str, Any]) -> None:
    path = logs_dir() / "autotune-state.json"
    path.write_text(json.dumps(state, indent=2), encoding="utf-8")


def _append_log(row: dict[str, Any]) -> None:
    path = logs_dir() / "autotune-log.jsonl"
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row) + "\n")


def stress(
    kind: str,
    seconds: int,
    limits: safety.Limits,
    on_tick: Callable[[dict[str, Any]], None] | None = None,
    cpu_mode: str = "all",
    cpu_core: int = -1,
) -> StepResult:
    mark = events.watermark()
    args = ["stress-gpu", "--seconds", str(seconds)] if kind == "gpu" else [
        "stress-cpu",
        "--seconds",
        str(seconds),
        "--mode",
        cpu_mode,
        "--core",
        str(cpu_core),
    ]
    last_metrics: dict[str, Any] = {}
    result: dict[str, Any] = {}
    trip_reason = ""
    try:
        for ev in hw.run_stream(args):
            if ev.get("type") == "tick":
                last_metrics = ev.get("metrics") or last_metrics
                cpu_t = hwinfo.cpu_package_temp()
                reason = safety.trip(last_metrics, limits, cpu_t)
                if on_tick:
                    on_tick(ev)
                if reason:
                    trip_reason = reason
                    break
            elif ev.get("type") == "result":
                result = ev
                last_metrics = ev.get("metrics") or last_metrics
    except hw.HwError as exc:
        trip_reason = str(exc)
    faults = events.new_faults_since(mark)
    if trip_reason:
        return StepResult(False, trip_reason, {}, last_metrics, 0.0, faults)
    if faults:
        return StepResult(False, events.describe_faults(faults), {}, last_metrics, 0.0, faults)
    passed = bool(result.get("passed", False))
    thr = float(result.get("throughput") or 0.0)
    return StepResult(passed, "ok" if passed else "stress failed", {}, last_metrics, thr, faults)


def _print_tick(ev: dict[str, Any]) -> None:
    m = ev.get("metrics") or {}
    parts = [f"{ev.get('elapsed_s', 0):3d}s"]
    if "gpu_temp_c" in m:
        parts.append(f"GPU {m['gpu_temp_c']:.0f}C")
    if "hotspot_c" in m:
        parts.append(f"hot {m['hotspot_c']:.0f}C")
    if "clock_mhz" in m:
        parts.append(f"{m['clock_mhz']} MHz")
    if "power_w" in m:
        parts.append(f"{m['power_w']:.0f} W")
    if "iters" in ev:
        parts.append(f"iters {ev['iters']}")
    print("    " + "  ".join(parts), flush=True)


def gpu_autotune(
    *,
    goal: str = "balanced",
    seconds: int = 45,
    limits: safety.Limits | None = None,
    keep: bool = True,
    skip_vram: bool = False,
    restore_on_fail: bool = True,
) -> dict[str, Any]:
    limits = limits or safety.DEFAULT_LIMITS
    _log("Reading stock GPU settings via ADLX")
    stock = hw.info()
    tun = _tuning(stock)
    vr = _range(stock, "voltage_mv")
    cr = _range(stock, "max_mhz")
    mr = _range(stock, "vram_mhz")
    if not vr or not cr:
        raise RuntimeError("Could not read GPU voltage/clock ranges. Is Adrenalin running?")

    stock_volt = int(tun.get("voltage_mv") or vr[1])
    stock_clock = int(tun.get("max_mhz") or tun.get("default_max_mhz") or 2500)
    stock_vram = int(tun.get("vram_mhz") or 2500)
    stock_min = int(tun.get("min_mhz") or 500)

    volt_lo = max(vr[0], limits.min_voltage_mv)
    volt_hi = vr[1]
    clock_hi = min(cr[1], limits.max_clock_mhz)
    clock_lo = max(cr[0], limits.min_clock_mhz)
    step_v = max(5, vr[2] if vr[2] >= 5 else 15)
    step_c = max(10, cr[2] if cr[2] >= 10 else 25)

    state = {
        "started": datetime.now(timezone.utc).isoformat(),
        "goal": goal,
        "stock": tun,
        "steps": [],
        "best": None,
    }
    _save_state(state)

    def record(name: str, res: StepResult, settings: dict[str, Any]) -> None:
        row = {
            "name": name,
            "ok": res.ok,
            "reason": res.reason,
            "settings": settings,
            "metrics": res.metrics,
            "throughput": res.throughput,
        }
        state["steps"].append(row)
        _save_state(state)
        _append_log(row)

    def score(res: StepResult, settings: dict[str, Any]) -> float:
        power = float((res.metrics or {}).get("board_power_w") or (res.metrics or {}).get("power_w") or 1.0)
        thr = max(res.throughput, 1.0)
        if goal == "efficiency":
            return thr / max(power, 1.0)
        if goal == "performance":
            return float(settings.get("max_mhz") or 0) + thr / 1e12
        return (thr / max(power, 1.0)) * 0.5 + float(settings.get("max_mhz") or 0) / 6000.0

    best: dict[str, Any] | None = None
    best_score = -1.0

    def consider(name: str, settings: dict[str, Any], res: StepResult) -> None:
        nonlocal best, best_score
        record(name, res, settings)
        if not res.ok:
            _log(f"FAIL {name}: {res.reason}")
            return
        s = score(res, settings)
        _log(f"PASS {name}  score={s:.3f}  {settings}")
        if s > best_score:
            best_score = s
            best = {"settings": settings, "metrics": res.metrics, "throughput": res.throughput, "score": s}
            state["best"] = best
            _save_state(state)

    def apply_and_test(name: str, settings: dict[str, Any]) -> StepResult:
        _log(f"Apply {settings}")
        hw.set_gpu(
            max_mhz=settings.get("max_mhz"),
            min_mhz=settings.get("min_mhz", stock_min),
            voltage=settings.get("voltage_mv"),
            vram=settings.get("vram_mhz"),
            fast_timing=settings.get("fast_timing"),
            power=settings.get("power_pct"),
        )
        time.sleep(1.0)
        res = stress("gpu", seconds, limits, on_tick=_print_tick)
        res.settings = settings
        consider(name, settings, res)
        return res

    _log("Baseline at current / stock settings")
    base_settings = {
        "max_mhz": stock_clock,
        "min_mhz": stock_min,
        "voltage_mv": stock_volt,
        "vram_mhz": stock_vram,
        "fast_timing": False,
        "power_pct": int(tun.get("power_limit_pct") or 0),
    }
    base = apply_and_test("baseline", base_settings)
    if not base.ok:
        _log("Baseline already unstable. Not searching further. Use Adrenalin -> Default if games crash.")
        if restore_on_fail:
            hw.reset()
        return state

    # Undervolt search (voltage down, clock held at stock).
    last_good_volt = stock_volt
    v = stock_volt - step_v
    while v >= volt_lo:
        st = {**base_settings, "voltage_mv": v}
        res = apply_and_test(f"uv-{v}mV", st)
        if not res.ok:
            _log(f"Undervolt floor reached. Last stable {last_good_volt} mV")
            break
        last_good_volt = v
        v -= step_v
    else:
        last_good_volt = volt_lo

    daily_volt = _clamp(last_good_volt, volt_lo, volt_hi)

    # Overclock search at the stable undervolt.
    last_good_clock = stock_clock
    if goal != "efficiency":
        c = stock_clock + step_c
        while c <= clock_hi:
            st = {**base_settings, "voltage_mv": daily_volt, "max_mhz": c}
            res = apply_and_test(f"oc-{c}MHz@{daily_volt}mV", st)
            if not res.ok:
                last_good_clock = max(clock_lo, last_good_clock)
                _log(f"Clock ceiling reached. Daily max {last_good_clock} MHz")
                break
            last_good_clock = c
            c += step_c
        else:
            last_good_clock = min(c - step_c, clock_hi)
    else:
        # Efficiency: try a slightly lower cap too.
        for c in (stock_clock, max(clock_lo, stock_clock - 100), max(clock_lo, stock_clock - 200)):
            st = {**base_settings, "voltage_mv": daily_volt, "max_mhz": c}
            apply_and_test(f"eff-{c}MHz@{daily_volt}mV", st)
            last_good_clock = c if (state["steps"][-1]["ok"]) else last_good_clock

    last_good_vram = stock_vram
    last_fast = False
    if not skip_vram and mr:
        vram_hi = min(mr[1], limits.max_vram_mhz)
        step_m = max(10, mr[2] if mr[2] >= 10 else 50)
        mem = stock_vram + step_m
        while mem <= vram_hi:
            st = {
                **base_settings,
                "voltage_mv": daily_volt,
                "max_mhz": last_good_clock,
                "vram_mhz": mem,
            }
            res = apply_and_test(f"vram-{mem}", st)
            if not res.ok:
                break
            last_good_vram = mem
            mem += step_m
        st = {
            **base_settings,
            "voltage_mv": daily_volt,
            "max_mhz": last_good_clock,
            "vram_mhz": last_good_vram,
            "fast_timing": True,
        }
        res = apply_and_test("fast-timing", st)
        last_fast = res.ok

    winner = best["settings"] if best else {
        "max_mhz": last_good_clock,
        "min_mhz": stock_min,
        "voltage_mv": daily_volt,
        "vram_mhz": last_good_vram,
        "fast_timing": last_fast,
        "power_pct": base_settings["power_pct"],
    }
    _log(f"Winner: {winner}")
    hw.set_gpu(
        max_mhz=winner.get("max_mhz"),
        min_mhz=winner.get("min_mhz"),
        voltage=winner.get("voltage_mv"),
        vram=winner.get("vram_mhz"),
        fast_timing=winner.get("fast_timing"),
        power=winner.get("power_pct"),
    )
    _log("Final validation soak")
    final = stress("gpu", max(seconds, 90), limits, on_tick=_print_tick)
    record("final-soak", final, winner)
    if not final.ok:
        _log("Final soak failed — restoring factory GPU tuning")
        hw.reset()
        state["applied"] = "factory"
        _save_state(state)
        return state

    profile = {
        "created": datetime.now(timezone.utc).isoformat(),
        "goal": goal,
        "gpu": (stock.get("gpu") or {}),
        "settings": winner,
        "metrics": final.metrics,
        "stock": tun,
    }
    out = profiles_dir() / f"gpu-{goal}.json"
    out.write_text(json.dumps(profile, indent=2), encoding="utf-8")
    state["profile"] = str(out)
    state["applied"] = winner if keep else "left-as-winner"
    _save_state(state)
    _log(f"Saved profile {out}")
    _log("Adrenalin will show these clocks until you Reset in the tool or set Default in AMD Software.")
    return state


def cpu_stress_suite(*, seconds: int = 45, per_core: bool = True, ram: bool = True) -> dict[str, Any]:
    limits = safety.DEFAULT_LIMITS
    results = []
    _log("CPU all-core AVX2 stress (Curve Optimizer / PBO check)")
    r = stress("cpu", seconds, limits, on_tick=_print_tick, cpu_mode="all")
    results.append({"name": "all-core", **asdict(r)})
    _log("PASS" if r.ok else f"FAIL {r.reason}")
    if per_core:
        import os

        n = os.cpu_count() or 8
        for core in range(n):
            _log(f"Per-core cycler: logical CPU {core}/{n-1}")
            r = stress("cpu", max(20, seconds // 2), limits, on_tick=_print_tick, cpu_mode="core", cpu_core=core)
            results.append({"name": f"core-{core}", **asdict(r)})
            if not r.ok:
                _log(f"Core {core} failed — typical Curve Optimizer instability. Raise that core toward 0.")
    if ram:
        _log("RAM hammer")
        r = stress("cpu", max(20, seconds // 2), limits, on_tick=_print_tick, cpu_mode="ram")
        results.append({"name": "ram", **asdict(r)})
    out = {"results": results, "passed": all(x["ok"] for x in results)}
    (logs_dir() / "cpu-stress.json").write_text(json.dumps(out, indent=2), encoding="utf-8")
    return out


def cpu_underclock_percent(percent: int) -> None:
    percent = max(50, min(100, percent))
    guid = "SCHEME_CURRENT"
    for acdc in (" /setacvalueindex", " /setdcvalueindex"):
        pass
    import subprocess

    for which in ("setacvalueindex", "setdcvalueindex"):
        subprocess.run(
            ["powercfg", f"/{which}", guid, "SUB_PROCESSOR", "PROCTHROTTLEMAX", str(percent)],
            check=False,
        )
    subprocess.run(["powercfg", "/setactive", guid], check=False)
    _log(f"Windows processor maximum set to {percent}% (underclock). 100% restores full boost.")
