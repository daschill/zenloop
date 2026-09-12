# GitHub Release notes template (unsigned builds)

**ZenLoop _X.Y.Z_** — one-click Optimize for AMD Ryzen + Radeon.

## Download

| Artifact | Notes |
| --- | --- |
| `ZenLoop-X.Y.Z-win-x64.zip` | Portable folder (primary) |
| `ZenLoop-X.Y.Z-Setup.exe` | Optional Inno installer (unsigned) |
| `ZenLoop-X.Y.Z-win-x64.msix` | Optional MSIX (unsigned; sideload / Dev Mode) |
| `version.json` | **Required for in-app update check** — see `docs/version.example.json` |

Builds are **not** EV code-signed. Windows SmartScreen may warn on first run.

## Prerequisites (still separate installs)

- Windows 10/11 x64
- AMD Software (Adrenalin)
- AMD Ryzen Master
- Administrator (UAC) for SMU/BIOS writes

## Update check

Ship this Release asset as `version.json`:

```json
{
  "version": "X.Y.Z",
  "url": "https://github.com/daschill/zenloop/releases/tag/vX.Y.Z",
  "notes": "Short release blurb"
}
```

Clients default to:
`https://github.com/daschill/zenloop/releases/latest/download/version.json`

Override via `app-settings.json` → `UpdateManifestUrl`.

## Recovery

See `RECOVERY.md` in the zip (Adrenalin Default, clear CO, CLR_CMOS warning).

## Safety

Not affiliated with AMD. Overclocking/undervolting can damage hardware — read `DISCLAIMER.txt` / `EULA.txt`.
