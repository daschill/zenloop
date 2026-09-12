# GitHub Release notes template

**ZenLoop _X.Y.Z_** — one-click Optimize for AMD Ryzen + Radeon (multi-vendor detect matrix included).

## Download

| Artifact | Notes |
| --- | --- |
| `ZenLoop-X.Y.Z-win-x64.zip` | Portable folder (primary) |
| `ZenLoop-X.Y.Z-Setup.exe` | Optional Inno installer |
| `ZenLoop-X.Y.Z-win-x64.msix` | Optional MSIX (sideload / Dev Mode) |
| `version.json` | **Required for in-app update check** — see `docs/version.example.json` |

Builds are **unsigned** unless repository `SIGNING_CERT_*` secrets are configured (see `docs/CODE-SIGNING.md`). Windows SmartScreen may warn on unsigned first run.

## Prerequisites (still separate installs)

- Windows 10/11 x64
- AMD Software (Adrenalin) for primary GPU path
- AMD Ryzen Master for primary CPU/BIOS path
- Administrator (UAC) for SMU/BIOS writes
- NVIDIA: GeForce/Studio driver (`nvapi64.dll`) for optional power-limit Apply when policies resolve

## Update check

Ship this Release asset as `version.json` (start from `docs/version.example.json`):

```json
{
  "version": "X.Y.Z",
  "url": "https://github.com/daschill/zenloop/releases/tag/vX.Y.Z",
  "notes": "Short release blurb"
}
```

Clients default to:
`https://github.com/daschill/zenloop/releases/latest/download/version.json`

(`UpdateChecker.DefaultManifestUrl` in `core/UpdateChecker.cs`.)

Override via `app-settings.json` → `UpdateManifestUrl`. Optional silent startup check:
`CheckForUpdatesOnStartup: true` (default false).

## Recovery

See `RECOVERY.md` in the zip (Adrenalin Default, clear CO, CLR_CMOS warning).

## Safety

Not affiliated with AMD / Intel / NVIDIA. No WinRing0 / raw SMU. Overclocking/undervolting can damage hardware — read `DISCLAIMER.txt` / `EULA.txt`.
