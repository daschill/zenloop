from __future__ import annotations

import json
import subprocess
from datetime import datetime, timezone
from typing import Any


_PROVIDERS = [
    "Microsoft-Windows-WHEA-Logger",
    "Microsoft-Windows-Kernel-WHEA",
    "Display",
    "Microsoft-Windows-Kernel-Power",
]


def _ps(script: str) -> str:
    proc = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        capture_output=True,
        text=True,
        timeout=30,
    )
    return proc.stdout or ""


def watermark() -> str:
    return datetime.now(timezone.utc).isoformat()


def new_faults_since(since_iso: str) -> list[dict[str, Any]]:
    """Return WHEA / display-timeout events newer than the ISO watermark."""
    script = f"""
$since = [datetime]::Parse('{since_iso}', $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
$providers = @({", ".join("'" + p + "'" for p in _PROVIDERS)})
Get-WinEvent -FilterHashtable @{{ LogName = 'System'; StartTime = $since }} -ErrorAction SilentlyContinue |
  Where-Object {{ $providers -contains $_.ProviderName -or $_.Id -in 4101,1001,18,19,47,41 }} |
  Select-Object -First 20 TimeCreated, Id, ProviderName, LevelDisplayName, Message |
  ConvertTo-Json -Compress
"""
    raw = _ps(script).strip()
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return []
    if isinstance(data, dict):
        data = [data]
    out = []
    for ev in data:
        msg = (ev.get("Message") or "")[:240]
        out.append(
            {
                "time": ev.get("TimeCreated"),
                "id": ev.get("Id"),
                "provider": ev.get("ProviderName"),
                "level": ev.get("LevelDisplayName"),
                "message": msg,
            }
        )
    return out


def describe_faults(faults: list[dict[str, Any]]) -> str:
    if not faults:
        return ""
    bits = []
    for f in faults:
        bits.append(f"{f.get('provider')} id={f.get('id')}: {f.get('message')}")
    return " | ".join(bits)
