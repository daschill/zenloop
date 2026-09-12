# Code signing ZenLoop (EV / Authenticode)

ZenLoop ships **unsigned** by default. SmartScreen may warn until reputation builds or a purchased certificate is used. This document covers obtaining an EV code-signing certificate and wiring it into publish/CI **without** changing the unsigned path.

## When signing runs

`scripts/sign-artifacts.ps1` (called from `publish.ps1` and optionally `scripts/pack-installer.ps1`) signs only when certificate secrets exist:

| Secret / env | Purpose |
| --- | --- |
| `SIGNING_CERT_PFX` | Base64-encoded `.pfx` (CI-friendly) |
| `SIGNING_CERT_PATH` | Absolute path to a `.pfx` on the build machine (alternative to PFX) |
| `SIGNING_CERT_PASSWORD` | PFX password (`SIGNING_CERT_PWD` also accepted) |
| `SIGNING_TIMESTAMP_URL` | Optional RFC3161 timestamp URL (default DigiCert) |

If none of `SIGNING_CERT_PFX` / `SIGNING_CERT_PATH` are set, the script exits 0 and leaves binaries unsigned.

Signed targets (when present): `ZenLoop.exe`, `zenloop-hw.exe`, `zenloop-cpu.exe`, plus optional Setup.exe / MSIX passed as `-ExtraFiles`.

## Obtaining an EV code-signing certificate

1. **Choose a CA** that issues EV Authenticode certificates (examples historically used by ISVs: DigiCert, Sectigo, GlobalSign, SSL.com). Prefer a vendor that supports **hardware token / HSM** or cloud key attestation required by current CA/Browser forum rules.
2. **Organization validation** — EV requires business registration documents, a call-back, and identity checks. Budget calendar time for validation (not a same-day self-sign).
3. **Key storage** — Modern EV certs are typically issued to a USB token or cloud HSM. Export a build-time `.pfx` only if your CA/process still allows it; otherwise use `signtool` with `/sha1` thumbprint + token PIN via `SIGNING_CERT_PATH` workflows adapted to your token.
4. **Windows SDK SignTool** — Install “Windows SDK Signing Tools” so `signtool.exe` is on the packager. CI uses `windows-latest` which includes SignTool when the SDK component is present; the script searches `Windows Kits\10\bin\**\x64\signtool.exe`.
5. **Timestamp** — Always use an RFC3161 timestamp (`/tr`) so signatures remain valid after cert expiry.
6. **Store secrets** — Put `SIGNING_CERT_PFX` + `SIGNING_CERT_PASSWORD` in GitHub Actions encrypted secrets (or an org vault). Never commit the PFX.

## Local publish (optional sign)

```powershell
# Unsigned (default)
powershell -ExecutionPolicy Bypass -File .\publish.ps1

# Signed — set env first
$env:SIGNING_CERT_PATH = "C:\certs\zenloop-ev.pfx"
$env:SIGNING_CERT_PASSWORD = "***"
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Installer pack (signs Setup after Inno when secrets exist):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-installer.ps1 -Inno
```

Dry-run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\sign-artifacts.ps1 -DryRun
```

## CI

`.github/workflows/ci.yml` runs a **Sign artifacts (if secrets)** step after tests. Without secrets it prints the same unsigned notice and stays green. With repository secrets configured, it publishes and signs.

## What signing does *not* do

- Does not enable WinRing0 / raw SMU / unsigned drivers.
- Does not replace AMD Adrenalin / Ryzen Master prerequisites.
- Does not auto-submit to Microsoft Store (separate Store cert / Partner Center flow).

## Recovery if SmartScreen still warns

EV reduces warnings but reputation also comes from download volume. Ship `RECOVERY.md` and honest capability messaging regardless of signature state.
