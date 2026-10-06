"""Check the actual QNN APK payload and Android manifest, without a physical device.

Usage: python tools/phase1/check-apk.py Builds/Android/WallDistanceDemo_QNN.apk
This checks packaging only; it does not execute the DSP or establish inference accuracy.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("apk", type=Path)
    parser.add_argument("--aapt", type=Path, default=Path("C:/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/build-tools/36.0.0/aapt.exe"))
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[2]
    models = root / "Assets/StreamingAssets/Models"
    checks = {}
    try:
        pin = json.loads((root / "tools/models.lock.json").read_text(encoding="utf-8-sig"))
        staged = json.loads((models / "qairt-runtime.json").read_text(encoding="utf-8-sig"))
        with zipfile.ZipFile(args.apk) as apk:
            packaged = json.loads(apk.read("assets/Models/qairt-runtime.json").decode("utf-8-sig"))
            checks["runtime_identity"] = packaged == staged and packaged["qairt"] == pin["qairt"]
            for name, digest in staged["libraries"].items():
                # Exact byte checks catch stale/mixed SDK files and unintended stripping of
                # the Hexagon skeleton by Android's ARM64 packaging tools.
                member = apk.getinfo("lib/arm64-v8a/" + name)
                checks[name] = hashlib.sha256(apk.read(member)).hexdigest() == digest
                checks[name + "_extractable"] = member.compress_type == zipfile.ZIP_DEFLATED
            context = "depth_anything_v2_w8a16.ctx.bin"
            checks[context] = hashlib.sha256(apk.read("assets/Models/" + context)).digest() == hashlib.sha256((models / context).read_bytes()).digest()
            for notice in ("QAIRT_LICENSE.pdf", "QAIRT_NOTICE.txt", "QAIRT_QNN_NOTICE.txt"):
                checks[notice] = apk.read("assets/Models/" + notice) == (models / notice).read_bytes()
        manifest = subprocess.run([str(args.aapt), "dump", "xmltree", str(args.apk), "AndroidManifest.xml"], capture_output=True, text=True, check=True).stdout
        checks["extractNativeLibs"] = bool(re.search(r"android:extractNativeLibs\([^\n]*=\(type 0x12\)0xffffffff", manifest))
        blocks = re.findall(r"E: uses-native-library[^\n]*\n(.*?)(?=\n\s*E:|\Z)", manifest, re.S)
        checks["optional_vendor_rpc"] = any("libcdsprpc.so" in block and re.search(r"android:required\([^\n]*=\(type 0x12\)0x0(?:\s|$)", block) for block in blocks)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        print(f"Cannot verify APK: {error}", file=sys.stderr)
        return 2
    print(json.dumps({"apk": str(args.apk), "qairt": pin["qairt"], "checks": checks, "passed": all(checks.values())}, indent=2))
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
