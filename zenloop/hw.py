from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any, Iterator


def helper_path() -> Path:
    here = Path(__file__).resolve().parent
    cand = here / "bin" / "zenloop-hw.exe"
    if cand.exists():
        return cand
    alt = here.parent / "zenloop" / "bin" / "zenloop-hw.exe"
    if alt.exists():
        return alt
    found = shutil.which("zenloop-hw")
    if found:
        return Path(found)
    raise FileNotFoundError(
        "zenloop-hw.exe not found. From the project folder run:  .\\native\\build.ps1"
    )


class HwError(RuntimeError):
    def __init__(self, message: str, payload: dict[str, Any] | None = None):
        super().__init__(message)
        self.payload = payload or {}


def run_json(args: list[str], timeout: int = 240) -> dict[str, Any]:
    exe = helper_path()
    proc = subprocess.run(
        [str(exe), *args],
        capture_output=True,
        text=True,
        timeout=timeout,
        creationflags=0,
    )
    out = (proc.stdout or "").strip()
    last = out.splitlines()[-1] if out else ""
    try:
        data = json.loads(last) if last else {}
    except json.JSONDecodeError as exc:
        raise HwError(f"helper returned non-JSON: {out[-500:]}", {"stderr": proc.stderr}) from exc
    if proc.returncode not in (0, 3) and not data.get("ok", False):
        raise HwError(data.get("error") or f"helper exit {proc.returncode}", data)
    return data


def run_stream(args: list[str]) -> Iterator[dict[str, Any]]:
    exe = helper_path()
    proc = subprocess.Popen(
        [str(exe), *args],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )
    assert proc.stdout is not None
    try:
        for line in proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                print(line, file=sys.stderr)
        rc = proc.wait()
        if rc not in (0, 3):
            err = (proc.stderr.read() if proc.stderr else "") or ""
            raise HwError(f"helper exit {rc}: {err[-400:]}")
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()


def info() -> dict[str, Any]:
    return run_json(["info"])


def metrics() -> dict[str, Any]:
    return run_json(["metrics"], timeout=30)


def reset() -> dict[str, Any]:
    return run_json(["reset"])


def set_gpu(
    *,
    max_mhz: int | None = None,
    min_mhz: int | None = None,
    voltage: int | None = None,
    vram: int | None = None,
    power: int | None = None,
    fast_timing: bool | None = None,
) -> dict[str, Any]:
    args = ["set"]
    if max_mhz is not None:
        args += ["--max-mhz", str(max_mhz)]
    if min_mhz is not None:
        args += ["--min-mhz", str(min_mhz)]
    if voltage is not None:
        args += ["--voltage", str(voltage)]
    if vram is not None:
        args += ["--vram", str(vram)]
    if power is not None:
        args += ["--power", str(power)]
    if fast_timing is not None:
        args += ["--fast-timing", "1" if fast_timing else "0"]
    if len(args) == 1:
        raise HwError("set_gpu called with no fields")
    return run_json(args)


def amd_auto(kind: str) -> dict[str, Any]:
    cmd = {"undervolt": "auto-undervolt", "overclock": "auto-overclock", "vram": "auto-vram"}[kind]
    return run_json([cmd], timeout=200)
