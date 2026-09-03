from __future__ import annotations

import ctypes
from ctypes import wintypes
from typing import Any


class _Hdr(ctypes.Structure):
    _fields_ = [
        ("dwSignature", wintypes.DWORD),
        ("dwVersion", wintypes.DWORD),
        ("dwRevision", wintypes.DWORD),
        ("poll_time", wintypes.LONG),
        ("dwOffsetOfSensorSection", wintypes.DWORD),
        ("dwSizeOfSensorSection", wintypes.DWORD),
        ("dwNumSensorElements", wintypes.DWORD),
        ("dwOffsetOfReadingSection", wintypes.DWORD),
        ("dwSizeOfReadingSection", wintypes.DWORD),
        ("dwNumReadingElements", wintypes.DWORD),
    ]


class _Reading(ctypes.Structure):
    _pack_ = 1
    _fields_ = [
        ("tReading", wintypes.DWORD),
        ("dwSensorIndex", wintypes.DWORD),
        ("dwReadingID", wintypes.DWORD),
        ("szLabelOrig", ctypes.c_char * 128),
        ("szLabelUser", ctypes.c_char * 128),
        ("szUnit", ctypes.c_char * 16),
        ("value", ctypes.c_double),
        ("valueMin", ctypes.c_double),
        ("valueMax", ctypes.c_double),
        ("valueAvg", ctypes.c_double),
    ]


FILE_MAP_READ = 0x0004
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


def cpu_package_temp() -> float | None:
    """Read CPU package/Tctl from HWiNFO shared memory if it is running."""
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenFileMappingW.restype = wintypes.HANDLE
    k32.MapViewOfFile.restype = ctypes.c_void_p
    handle = k32.OpenFileMappingW(FILE_MAP_READ, False, "Global\\HWiNFO_SENS_SM2")
    if not handle:
        handle = k32.OpenFileMappingW(FILE_MAP_READ, False, "HWiNFO_SENS_SM2")
    if not handle:
        return None
    view = k32.MapViewOfFile(handle, FILE_MAP_READ, 0, 0, 0)
    if not view:
        k32.CloseHandle(handle)
        return None
    try:
        hdr = _Hdr.from_address(view)
        if hdr.dwSignature not in (0x53776948, 0x73696C53):  # 'HwiS' / sometimes swapped
            pass
        best: float | None = None
        for i in range(min(int(hdr.dwNumReadingElements), 4000)):
            addr = view + hdr.dwOffsetOfReadingSection + i * hdr.dwSizeOfReadingSection
            rec = _Reading.from_address(addr)
            label = (rec.szLabelUser or rec.szLabelOrig or b"").decode("latin-1", "ignore")
            unit = (rec.szUnit or b"").decode("latin-1", "ignore")
            low = label.lower()
            if "°" in unit or unit.lower() in ("c", "°c", "celsius"):
                if any(k in low for k in ("cpu package", "tctl", "cpu (tctl", "ccd")):
                    try:
                        val = float(rec.value)
                    except (TypeError, ValueError):
                        continue
                    if 0 < val < 120:
                        if best is None or val > best:
                            best = val
        return best
    except OSError:
        return None
    finally:
        k32.UnmapViewOfFile(ctypes.c_void_p(view))
        k32.CloseHandle(handle)


def snapshot() -> dict[str, Any]:
    t = cpu_package_temp()
    return {"cpu_temp_c": t, "source": "hwinfo" if t is not None else None}
