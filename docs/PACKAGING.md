# Packaging ZenLoop (unsigned)

## Product folder

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces:

- `dist/ZenLoop/` — self-contained `ZenLoop.exe` + `zenloop-hw.exe` + `zenloop-cpu.exe` + `LICENSE` + `EULA.txt` + `DISCLAIMER.txt` + `README.txt`
- `dist/ZenLoop-<version>-win-x64.zip` — same contents, ready for GitHub Releases

No paid code-signing certificate is required. SmartScreen may warn on first run of an unsigned binary.

## Optional Inno Setup (unsigned installer)

If Inno Setup 6 is installed, a minimal script can wrap `dist/ZenLoop`:

```iss
; tools/zenloop.iss (example — not required for MVP)
[Setup]
AppName=ZenLoop
AppVersion=1.1.0
DefaultDirName={autopf}\ZenLoop
OutputBaseFilename=ZenLoop-Setup
PrivilegesRequired=admin
[Files]
Source: "..\dist\ZenLoop\*"; DestDir: "{app}"; Flags: recursesubdirs
[Run]
Filename: "{app}\ZenLoop.exe"; Description: "Launch ZenLoop"; Flags: nowait postinstall skipifsilent
```

Signed builds are a later ops step (EV certificate), not part of the MVP publish script.

## Prerequisites on the target PC

Still required separately: AMD Software (Adrenalin) and AMD Ryzen Master. ZenLoop detects missing installs and shows guidance.
