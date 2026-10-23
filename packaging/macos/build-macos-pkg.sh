#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/publish"
OUTPUT_DIR="$SCRIPT_DIR/dist"
PKG_ROOT="$SCRIPT_DIR/pkg-root"

echo "==> Publishing macOS binaries (osx-arm64 / osx-x64)..."
rm -rf "$PUBLISH_DIR" "$OUTPUT_DIR" "$PKG_ROOT"
mkdir -p "$PUBLISH_DIR" "$OUTPUT_DIR"
mkdir -p "$PKG_ROOT/Applications/SwiftDesk.app/Contents/MacOS"
mkdir -p "$PKG_ROOT/Library/Application Support/SwiftDesk"

dotnet publish "$REPO_ROOT/src/RemoteDesk.Client/RemoteDesk.Client.csproj" -c Release -r osx-arm64 --self-contained false -o "$PUBLISH_DIR/client"
dotnet publish "$REPO_ROOT/src/RemoteDesk.Agent/RemoteDesk.Agent.csproj" -c Release -r osx-arm64 --self-contained false -o "$PUBLISH_DIR/agent"
dotnet publish "$REPO_ROOT/src/RemoteDesk.PrivilegedHelper/RemoteDesk.PrivilegedHelper.csproj" -c Release -r osx-arm64 --self-contained false -o "$PUBLISH_DIR/helper"

cp "$PUBLISH_DIR/client/RemoteDesk.Client" "$PKG_ROOT/Applications/SwiftDesk.app/Contents/MacOS/SwiftDesk"
cp "$PUBLISH_DIR/agent/RemoteDesk.Agent" "$PKG_ROOT/Library/Application Support/SwiftDesk/"
cp "$PUBLISH_DIR/helper/RemoteDesk.PrivilegedHelper" "$PKG_ROOT/Library/Application Support/SwiftDesk/"
cp "$SCRIPT_DIR/LaunchDaemons/com.swiftdesk.privilegedhelper.plist" "$PKG_ROOT/Library/Application Support/SwiftDesk/"

chmod +x "$SCRIPT_DIR/scripts/postinstall"

if command -v pkgbuild >/dev/null 2>&1; then
    echo "==> Building macOS installer package (.pkg)..."
    pkgbuild \
        --root "$PKG_ROOT" \
        --scripts "$SCRIPT_DIR/scripts" \
        --identifier "com.swiftdesk.pkg" \
        --version "1.0.0" \
        --install-location "/" \
        "$OUTPUT_DIR/SwiftDesk-1.0.0.pkg"
    echo "==> Successfully created macOS package: $OUTPUT_DIR/SwiftDesk-1.0.0.pkg"
else
    echo "pkgbuild only available on macOS. Prepared package payload at $PKG_ROOT."
fi

echo "==> macOS packaging complete."
