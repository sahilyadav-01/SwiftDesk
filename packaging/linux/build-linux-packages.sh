#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/publish"
OUTPUT_DIR="$SCRIPT_DIR/dist"

echo "==> Publishing Linux binaries..."
rm -rf "$PUBLISH_DIR" "$OUTPUT_DIR"
mkdir -p "$PUBLISH_DIR" "$OUTPUT_DIR"

dotnet publish "$REPO_ROOT/src/RemoteDesk.Client/RemoteDesk.Client.csproj" -c Release -r linux-x64 --self-contained false -o "$PUBLISH_DIR"
dotnet publish "$REPO_ROOT/src/RemoteDesk.Agent/RemoteDesk.Agent.csproj" -c Release -r linux-x64 --self-contained false -o "$PUBLISH_DIR"
dotnet publish "$REPO_ROOT/src/RemoteDesk.PrivilegedHelper/RemoteDesk.PrivilegedHelper.csproj" -c Release -r linux-x64 --self-contained false -o "$PUBLISH_DIR"

echo "==> Building Debian (.deb) package..."
PKG_ROOT="$SCRIPT_DIR/deb-pkg"
rm -rf "$PKG_ROOT"
mkdir -p "$PKG_ROOT/DEBIAN"
mkdir -p "$PKG_ROOT/opt/swiftdesk"
mkdir -p "$PKG_ROOT/etc/systemd/system"

cp "$SCRIPT_DIR/debian/control" "$PKG_ROOT/DEBIAN/"
cp "$SCRIPT_DIR/debian/postinst" "$PKG_ROOT/DEBIAN/"
cp "$SCRIPT_DIR/debian/prerm" "$PKG_ROOT/DEBIAN/"
chmod 755 "$PKG_ROOT/DEBIAN/postinst" "$PKG_ROOT/DEBIAN/prerm"

cp -r "$PUBLISH_DIR"/* "$PKG_ROOT/opt/swiftdesk/"
cp "$SCRIPT_DIR/systemd/swiftdesk-privileged-reboot.service" "$PKG_ROOT/etc/systemd/system/"

if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --build "$PKG_ROOT" "$OUTPUT_DIR/swiftdesk_1.0.0_amd64.deb"
    echo "==> Successfully created Debian package: $OUTPUT_DIR/swiftdesk_1.0.0_amd64.deb"
else
    echo "dpkg-deb not found. Package tree created at $PKG_ROOT."
fi

echo "==> Linux package build complete."
