# Packaging ZenLoop (unsigned)

No paid code-signing certificate is required. SmartScreen may warn on first run of an unsigned binary. EV signing is a later ops step — document only until a cert is purchased.

## 1. Portable folder + zip (primary)

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces:

- `dist/ZenLoop/` — self-contained `ZenLoop.exe` + helpers + `LICENSE` + `EULA.txt` + `DISCLAIMER.txt` + `RECOVERY.md` + `README.txt`
- `dist/ZenLoop-<version>-win-x64.zip` — same contents, ready for GitHub Releases

## 2. Unsigned Inno Setup installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`ISCC.exe`).

```powershell
# Publishes then compiles scripts\installer.iss
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -Inno

# Or skip republish if dist\ZenLoop is already built:
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -SkipPublish -Inno
```

Output: `dist/ZenLoop-<version>-Setup.exe` (unsigned).

Script source: [`scripts/installer.iss`](../scripts/installer.iss).

## 3. Unsigned MSIX (optional)

Requires Windows SDK `MakeAppx.exe`. Sideload with Developer Mode; signing is optional and not EV.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -SkipPublish -Inno:$false -Msix
```

Output: `dist/ZenLoop-<version>-win-x64.msix` (or a staged layout under `dist/msix-stage` if MakeAppx is missing).

Manifest template: [`scripts/AppxManifest.xml`](../scripts/AppxManifest.xml).

## 4. Update check (no DRM)

The About dialog fetches a small JSON manifest and compares SemVer to the running build.

**Default URL:** `https://github.com/daschill/zenloop/releases/latest/download/version.json`

**Documented raw fallback:** `https://raw.githubusercontent.com/daschill/zenloop/main/docs/version.json`

Override in `%LocalAppData%\ZenLoop\profiles\app-settings.json`:

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

Attach `version.json` as a **GitHub Release asset** on every release (template: [`.github/RELEASE_TEMPLATE.md`](../.github/RELEASE_TEMPLATE.md)). The client only shows “update available” — no license or payment gates.

## 5. Recovery docs

[`docs/RECOVERY.md`](./RECOVERY.md) ships next to the exe (`RECOVERY.md`). About summarizes Adrenalin Default, clear CO, and CLR_CMOS warnings and can open the full guide.

## Prerequisites on the target PC

Still required separately: AMD Software (Adrenalin) and AMD Ryzen Master. ZenLoop detects missing installs and shows guidance.
