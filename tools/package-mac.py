"""Build self-contained macOS application bundles from any .NET SDK host."""
import argparse
import os
import plistlib
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile
from xml.etree import ElementTree
from pathlib import Path

root = Path(__file__).resolve().parents[1]
version = ElementTree.parse(root / "Directory.Build.props").findtext("./PropertyGroup/Version")
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--local", action="store_true", help="Sign a local build with Apple Development and write it under artifacts/local")
parser.add_argument("architectures", nargs="*", metavar="RID")
args = parser.parse_args()
out = root / "artifacts"
identity = os.environ.get("MIXAMP_SIGN_IDENTITY", "-")
if args.local:
    if sys.platform != "darwin":
        parser.error("--local requires macOS and a code-signing identity")
    out = out / "local"
    if "MIXAMP_SIGN_IDENTITY" not in os.environ:
        identities = subprocess.check_output(["security", "find-identity", "-v", "-p", "codesigning"], text=True)
        candidates = re.findall(r'^\s*\d+\) ([0-9A-Fa-f]{40}) "Apple Development:', identities, re.MULTILINE)
        if len(candidates) != 1:
            parser.error("Set MIXAMP_SIGN_IDENTITY to the local signing identity to use")
        identity = candidates[0]
    if not identity.strip() or identity == "-":
        parser.error("--local requires a signing certificate for continuity between builds")
out.mkdir(parents=True, exist_ok=True)
dotnet = os.environ.get("DOTNET_EXE", "dotnet")
signer = os.environ.get("RCODESIGN") or shutil.which("rcodesign")

def build(rid):
    with tempfile.TemporaryDirectory() as stage:
        app = Path(stage) / "Mixamp.app"
        binaries = app / "Contents/MacOS"
        binaries.mkdir(parents=True)
        resources = app / "Contents/Resources"
        payload = resources / "runtime"
        resources.mkdir(parents=True)
        arch = "arm64" if rid.endswith("arm64") else "x86_64"
        cc = (["clang","-arch",arch,"-mmacosx-version-min=14.0"] if sys.platform=="darwin" else
              [os.environ["ZIG_PYTHON"],"-m","ziglang","cc","-target",("aarch64" if arch=="arm64" else arch)+"-macos"])
        subprocess.run(cc+["-Os","-Wl,-headerpad,0x400",str(root/"launcher/mac.c"),"-o",str(binaries/"Mixamp")],check=True)
        subprocess.run([dotnet, "publish", str(root/"src/Mac/Mac.csproj"), "-r", rid, "-c", "Release",
                        "--self-contained", "true", "-p:DebugType=None", "-o", str(payload)], check=True)
        shutil.copyfile(root/"launcher/Mixamp.icns", resources/"Mixamp.icns")
        with (app/"Contents/Info.plist").open("wb") as f:
            plistlib.dump(dict(CFBundleName="Mixamp",CFBundleDisplayName="Mixamp",CFBundlePackageType="APPL",
                CFBundleIdentifier="app.mixamp.player",CFBundleExecutable="Mixamp",CFBundleIconFile="Mixamp",
                CFBundleVersion=version,CFBundleShortVersionString=version,NSHighResolutionCapable=True,
                NSPrincipalClass="NSApplication",LSMinimumSystemVersion="14.0",
                NSAppTransportSecurity={"NSExceptionDomains":{"127.0.0.1":{"NSExceptionAllowsInsecureHTTPLoads":True}}},
                NSBonjourServices=["_googlecast._tcp"],
                NSLocalNetworkUsageDescription="Mixamp connects to your Plex server and finds speakers when you choose Google Cast."), f)
        shutil.copytree(root/"notices", resources/"notices")
        shutil.copy2(root/"THIRD-PARTY-NOTICES.md",resources)
        (payload/"Mixamp").unlink()
        (binaries/"Mixamp").chmod(0o755)
        for name in ["Mixamp.dll","Photino.Native.dylib","libcoreclr.dylib","libe_sqlite3.dylib","wwwroot/app.js"]:
            if not (payload/name).is_file(): raise RuntimeError("Missing "+name)
        diagnostic = payload/"createdump"
        if diagnostic.exists(): diagnostic.unlink()
        if sys.platform == "darwin":
            for p in payload.glob("*.dylib"):
                subprocess.run(["codesign","--force","--sign",identity,str(p)],check=True)
            subprocess.run(["codesign","--force","--sign",identity,str(app)],check=True)
            subprocess.run(["codesign","--verify","--deep","--strict",str(app)],check=True)
        elif signer:
            for p in payload.glob("*.dylib"):
                subprocess.run([signer,"sign",str(p)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
            subprocess.run([signer,"sign",str(app)],check=True)
        else: raise RuntimeError("Set RCODESIGN for cross-platform ad-hoc signing.")
        chip = "silicon" if rid == "osx-arm64" else "intel"
        target = out / f"Mixamp-mac-{chip}.zip"
        with zipfile.ZipFile(target.with_suffix(".tmp"),"w",zipfile.ZIP_DEFLATED,compresslevel=5) as archive:
            for path in app.rglob("*"):
                if not path.is_file(): continue
                entry = zipfile.ZipInfo.from_file(path,path.relative_to(stage).as_posix())
                entry.create_system=3
                mode=0o755 if path.name=="Mixamp" or path.suffix==".dylib" else 0o644
                entry.external_attr=(stat.S_IFREG | mode)<<16
                archive.writestr(entry,path.read_bytes(),compress_type=zipfile.ZIP_DEFLATED,compresslevel=5)
        target.with_suffix(".tmp").replace(target)
        print(target)

for architecture in args.architectures or ["osx-arm64","osx-x64"]:
    if architecture not in ("osx-arm64","osx-x64"): raise ValueError(architecture)
    build(architecture)
