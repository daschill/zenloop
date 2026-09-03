from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass
class Limits:
    gpu_temp_c: float = 88.0
    hotspot_c: float = 105.0
    mem_temp_c: float = 96.0
    cpu_temp_c: float = 92.0
    min_voltage_mv: int = 1025
    max_clock_mhz: int = 3000
    max_vram_mhz: int = 2700
    min_clock_mhz: int = 2200


# Conservative defaults for this machine: 9800X3D + 7900 XTX.
DEFAULT_LIMITS = Limits()


def metrics_of(payload: dict[str, Any]) -> dict[str, Any]:
    return payload.get("metrics") or {}


def trip(metrics: dict[str, Any], limits: Limits, cpu_temp: float | None = None) -> str | None:
    def over(key: str, limit: float, label: str) -> str | None:
        val = metrics.get(key)
        if val is None:
            return None
        try:
            v = float(val)
        except (TypeError, ValueError):
            return None
        if v >= limit:
            return f"{label} {v:.1f} >= {limit:.0f}"
        return None

    return (
        over("gpu_temp_c", limits.gpu_temp_c, "GPU temp")
        or over("hotspot_c", limits.hotspot_c, "GPU hotspot")
        or over("mem_temp_c", limits.mem_temp_c, "VRAM temp")
        or (f"CPU temp {cpu_temp:.1f} >= {limits.cpu_temp_c:.0f}" if cpu_temp is not None and cpu_temp >= limits.cpu_temp_c else None)
    )
