# Packaging ZenLoop

Unsigned by default. SmartScreen may warn on first run of an unsigned binary. When `SIGNING_CERT_*` secrets are present, `publish.ps1` / `pack-installer.ps1` invoke `scripts/sign-artifacts.ps1` (Authenticode). See [`docs/CODE-SIGNING.md`](./CODE-SIGNING.md) for EV certificate acquisition.

## 1. Portable folder + zip (primary)

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces:

- `dist/ZenLoop/` — self-contained `ZenLoop.exe` + helpers + `LICENSE` + `EULA.txt` + `DISCLAIMER.txt` + `RECOVERY.md` + `README.txt`
- `dist/ZenLoop-<version>-win-x64.zip` — same contents, ready for GitHub Releases

## 2. Inno Setup installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`ISCC.exe`).

```powershell
# Publishes then compiles scripts\installer.iss
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -Inno

# Or skip republish if dist\ZenLoop is already built:
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -SkipPublish -Inno
```

Output: `dist/ZenLoop-<version>-Setup.exe` (unsigned unless `SIGNING_CERT_*` set).

Script source: [`scripts/installer.iss`](../scripts/installer.iss).

## 3. MSIX (optional)

Requires Windows SDK `MakeAppx.exe`. Sideload with Developer Mode; signing optional via `SIGNING_CERT_*`.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -SkipPublish -Inno:$false -Msix
```

Output: `dist/ZenLoop-<version>-win-x64.msix` (or a staged layout under `dist/msix-stage` if MakeAppx is missing).

Manifest template: [`scripts/AppxManifest.xml`](../scripts/AppxManifest.xml).

## 4. Code signing (optional)

| Env | Role |
| --- | --- |
| `SIGNING_CERT_PFX` | Base64 PFX |
| `SIGNING_CERT_PATH` | Path to PFX |
| `SIGNING_CERT_PASSWORD` | PFX password |
| `SIGNING_TIMESTAMP_URL` | Optional timestamp URL |

Absent secrets → unsigned path unchanged. Details: [`CODE-SIGNING.md`](./CODE-SIGNING.md).

## 5. Update check (no DRM)

The About dialog fetches a small JSON manifest and compares SemVer to the running build.

**Default URL:** `https://github.com/daschill/zenloop/releases/latest/download/version.json`

**Documented raw fallback:** `https://raw.githubusercontent.com/daschill/zenloop/main/docs/version.json`

Optional **silent startup check** (default **off**): set in the Optimize panel checkbox
“Check for updates on startup (silent)”, or in `%LocalAppData%\ZenLoop\profiles\app-settings.json`:

```json
{
  "CheckForUpdatesOnStartup": true,
  "UpdateManifestUrl": "https://example.com/zenloop-version.json"
}
```

When enabled, App startup fetches the manifest after idle and shows a **tray balloon + log line** only if an update is available (no modal MessageBox).

Override manifest URL alone:

```json
{
  "UpdateManifestUrl": "https://example.com/zenloop-version.json"
}
```

Manifest shape (see [`docs/version.example.json`](./version.example.json)):

```json
{
  "version": "1.2.0",
  "url": "https://github.com/daschill/zenloop/releases/latest",
  "notes": "Optional blurb"
}
```

Attach `version.json` as a **GitHub Release asset** on every release (template: [`.github/RELEASE_TEMPLATE.md`](../.github/RELEASE_TEMPLATE.md)). Copy from `docs/version.example.json` and bump `version` / `url` / `notes`. The client only shows “update available” — no license or payment gates.

MSIX logos live in [`scripts/msix-assets/`](../scripts/msix-assets/) (branded placeholders; regenerate via `scripts/generate-msix-assets.py`).

## 6. Recovery docs

[`docs/RECOVERY.md`](./RECOVERY.md) ships next to the exe (`RECOVERY.md`). About summarizes Adrenalin Default, clear CO, and CLR_CMOS warnings and can open the full guide.

## Prerequisites on the target PC

Primary path: AMD Software (Adrenalin) and AMD Ryzen Master. Multi-vendor detect (Intel/NVIDIA) shows a capability matrix; Apply only when a public signed API resolves. ZenLoop detects missing installs and shows guidance.
