"""
Packs a published osx-x64 build into Vex.app and zips it for the Mac (called by build-mac.ps1).

    python pack.py <publish folder> <output zip>

A zip made the usual Windows way loses the Unix "executable" bit, and the Mac then won't start the app, so the zip
is written here with Unix permissions: 0755 for the app's executable and folders, 0644 for everything else.
The app icon (Vex's hood and eyes) is drawn with Pillow if it's installed; without it the app gets the default icon.
"""
import io
import os
import sys
import zipfile

VERSION = "0.1.0"

INFO_PLIST = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Vex</string>
    <key>CFBundleDisplayName</key><string>Vex</string>
    <key>CFBundleIdentifier</key><string>com.vex.companion</string>
    <key>CFBundleExecutable</key><string>Vex</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>{VERSION}</string>
    <key>CFBundleVersion</key><string>{VERSION}</string>
    <key>CFBundleIconFile</key><string>Vex.icns</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <!-- A little character, not an app you switch to: no Dock icon, no menu of his own. -->
    <key>LSUIElement</key><true/>
</dict>
</plist>
"""


def icon_png(size):
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 32
    d.ellipse([1 * s, 1 * s, 31 * s, 31 * s], fill=(0x82, 0x29, 0x2F, 255))      # hood
    d.ellipse([6 * s, 8 * s, 26 * s, 26 * s], fill=(0, 0, 0, 255))               # face
    d.ellipse([11 * s, 13 * s, 15 * s, 20 * s], fill=(255, 255, 255, 255))       # eyes
    d.ellipse([17 * s, 13 * s, 21 * s, 20 * s], fill=(255, 255, 255, 255))
    out = io.BytesIO()
    img.save(out, "PNG")
    return out.getvalue()


def icns():
    """An .icns holding PNGs (ic08 = 256 px, ic09 = 512 px), which every current macOS reads."""
    try:
        entries = b"".join(t + (8 + len(p)).to_bytes(4, "big") + p for t, p in ((b"ic08", icon_png(256)), (b"ic09", icon_png(512))))
    except ImportError:
        return None
    return b"icns" + (8 + len(entries)).to_bytes(4, "big") + entries


def add(z, name, data, mode):
    info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
    info.create_system = 3                        # Unix, so the Mac honours the permissions below
    info.external_attr = (mode & 0xFFFF) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    z.writestr(info, data)


def main(publish, out_zip):
    with zipfile.ZipFile(out_zip, "w") as z:
        for folder in ("Vex.app/", "Vex.app/Contents/", "Vex.app/Contents/MacOS/", "Vex.app/Contents/Resources/"):
            info = zipfile.ZipInfo(folder, date_time=(2026, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (0o40755 << 16) | 0x10
            z.writestr(info, b"")
        add(z, "Vex.app/Contents/Info.plist", INFO_PLIST.encode(), 0o100644)
        icon = icns()
        if icon:
            add(z, "Vex.app/Contents/Resources/Vex.icns", icon, 0o100644)
        for root, _, files in os.walk(publish):
            for f in sorted(files):
                path = os.path.join(root, f)
                rel = os.path.relpath(path, publish).replace(os.sep, "/")
                if rel.endswith(".pdb"):
                    continue
                with open(path, "rb") as fh:
                    data = fh.read()
                add(z, "Vex.app/Contents/MacOS/" + rel, data, 0o100755 if rel == "Vex" else 0o100644)
    print(f"{out_zip}: {os.path.getsize(out_zip) / 1024 / 1024:.1f} MB" + ("" if icon else " (no icon: Pillow isn't installed)"))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
