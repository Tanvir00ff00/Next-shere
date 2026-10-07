"""Bundle the built application with source, dependencies and notices; no build or system changes."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / "src/NextShare.App/NextShare.App.csproj").findtext("PropertyGroup/Version")
if not version or not all(part.isdigit() for part in version.split(".")):
    raise SystemExit("Invalid app version")
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, default=root / f"publish/NextShare-{version}")
release = parser.parse_args().output.resolve()
for executable in ("NextShare.exe", "nextshare-quickshare.exe", "NextShare.OfflineGuard.exe"):
    if not (release / executable).is_file():
        raise SystemExit(f"Build the release first: missing {executable}")
vendor = root / ".tools/native-vendor"
if not vendor.is_dir():
    raise SystemExit("Run cargo vendor before packaging corresponding source")
patchdir = root / "third_party/patches"
patchdir.mkdir(exist_ok=True)
if (root / "third_party/open-quickshare/.git").exists():
    (patchdir / "open-quickshare-windows.patch").write_bytes(subprocess.check_output(
        ["git", "-C", str(root / "third_party/open-quickshare"), "diff", "--binary"]))
elif not (patchdir / "open-quickshare-windows.patch").is_file():
    raise SystemExit("Missing corresponding protocol patch")
for name in ("README.md", "THIRD-PARTY-NOTICES.md"):
    shutil.copy2(root / name, release / name)
shutil.copy2(root / "tools/Setup-LocalReceiver.ps1", release / "Setup-LocalReceiver.ps1")
licenses = release / "licenses"
licenses.mkdir(exist_ok=True)
shutil.copy2(root / "third_party/open-quickshare/LICENSE", licenses / "GPL-3.0.txt")
shutil.copy2(root / "third_party/licenses/NAudio-MIT.txt", licenses / "NAudio-MIT.txt")
shutil.copy2(root / "third_party/licenses/DotNet-MIT.txt", licenses / "DotNet-MIT.txt")
shutil.copy2(root / "third_party/licenses/DotNet-THIRD-PARTY.txt", licenses / "DotNet-THIRD-PARTY.txt")
shutil.copy2(root / "third_party/licenses/NSIS-zlib.txt", licenses / "NSIS-zlib.txt")
source = release / f"NextShare-{version}-source.zip"
temporary = source.with_suffix(".zip.tmp")
with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, strict_timestamps=False) as bundle:
    for base in ("src", "tests", "tools", "docs", "installer", "third_party/open-quickshare/core_lib", "third_party/patches", "third_party/licenses", ".tools/native-vendor"):
        for p in (root / base).rglob("*"):
            if p.is_file() and p.relative_to(root).as_posix() not in {"docs/bluetooth-capabilities.json", "docs/hardware-readiness.json"} and not any(s in {"bin", "obj", "target", ".git", "__pycache__"} for s in p.relative_to(root).parts):
                bundle.write(p, p.relative_to(root))
    for name in ("README.md", "THIRD-PARTY-NOTICES.md", ".gitignore", "third_party/open-quickshare/LICENSE"):
        bundle.write(root / name, name)
    config = root / ".cargo/config.toml"
    if not config.is_file():
        config = root / "artifacts/native-vendor-config.toml"
    bundle.writestr(".cargo/config.toml", config.read_text(encoding="utf-8-sig"))
    for p in licenses.iterdir():
        if p.is_file():
            bundle.write(p, "licenses/" + p.name)
temporary.replace(source)
files = {}
for p in release.iterdir():
    if p.is_file() and p.suffix in {".exe", ".zip"}:
        with p.open("rb") as stream:
            files[p.name] = {"bytes": p.stat().st_size, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}
(release / "release-manifest.json").write_text(json.dumps({"version": version, "files": files}, indent=2), encoding="utf-8")
print(json.dumps(files, indent=2))
