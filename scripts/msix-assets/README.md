# MSIX tile / store logos

Branded placeholder PNGs for `scripts/pack-installer.ps1 -Msix` (ZenLoop dark + accent).
Sizes match `scripts/AppxManifest.xml`:

| File | Size |
| --- | --- |
| `StoreLogo.png` | 50×50 |
| `Square44x44Logo.png` | 44×44 |
| `Square150x150Logo.png` | 150×150 |
| `Wide310x150Logo.png` | 310×150 |

Regenerate with `python3 scripts/generate-msix-assets.py` (requires Pillow).
