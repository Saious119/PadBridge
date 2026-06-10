#!/usr/bin/env bash
# Builds a distributable PadBridge release tarball in dist/.
# The GUI is published self-contained, so recipients need no .NET runtime.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="$(cat VERSION)"
NAME="padbridge-${VERSION}-linux-x64"
STAGE="dist/${NAME}"

echo "==> Building daemon"
make -C daemon

echo "==> Publishing GUI (self-contained linux-x64)"
dotnet publish gui/PadBridge.Gui.csproj -c Release -r linux-x64 \
    --self-contained true -o "gui/bin/publish-sc"

echo "==> Staging ${STAGE}"
rm -rf "$STAGE"
mkdir -p "$STAGE/bin" "$STAGE/gui" "$STAGE/share"
cp daemon/padbridge-daemon daemon/padbridge-flydigi "$STAGE/bin/"
cp -r gui/bin/publish-sc/. "$STAGE/gui/"
cp packaging/padbridge.desktop packaging/padbridge.svg packaging/padbridge.service \
   packaging/padbridge-flydigi.service "$STAGE/share/"
if command -v ksvgtopng >/dev/null; then
    mkdir -p "$STAGE/share/icons"
    for s in 16 22 32 48 64 128 256; do
        ksvgtopng "$s" "$s" packaging/padbridge.svg "$STAGE/share/icons/padbridge-$s.png"
    done
fi
cp packaging/install.sh packaging/uninstall.sh README.md "$STAGE/"
chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh"

echo "==> Creating tarball"
tar -C dist -czf "dist/${NAME}.tar.gz" "$NAME"
rm -rf "$STAGE"
# Checksum asset: the in-app updater verifies the download against this.
(cd dist && sha256sum "${NAME}.tar.gz" > "${NAME}.tar.gz.sha256")

echo
echo "Release ready: dist/${NAME}.tar.gz (+ .sha256)"
echo "Recipients run:  tar xf ${NAME}.tar.gz && cd ${NAME} && ./install.sh"
