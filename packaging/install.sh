#!/usr/bin/env bash
# PadBridge installer: installs to ~/.local for the current user.
set -euo pipefail
cd "$(dirname "$0")"

BIN_DIR="$HOME/.local/bin"
APP_DIR="$HOME/.local/share/padbridge"
ICON_DIR="$HOME/.local/share/icons/hicolor/scalable/apps"
DESKTOP_DIR="$HOME/.local/share/applications"
UNIT_DIR="$HOME/.config/systemd/user"

echo "Installing PadBridge..."

install -D -m 755 bin/padbridge-daemon "$BIN_DIR/padbridge-daemon"
install -D -m 755 bin/padbridge-flydigi "$BIN_DIR/padbridge-flydigi"

rm -rf "$APP_DIR/gui"
mkdir -p "$APP_DIR/gui"
cp -r gui/. "$APP_DIR/gui/"
chmod 755 "$APP_DIR/gui/padbridge-gui"

install -D -m 644 share/padbridge.svg "$ICON_DIR/padbridge.svg"
for f in share/icons/padbridge-*.png; do
    [ -e "$f" ] || continue
    s=$(basename "$f" .png); s=${s#padbridge-}
    install -D -m 644 "$f" \
        "$HOME/.local/share/icons/hicolor/${s}x${s}/apps/padbridge.png"
done
mkdir -p "$DESKTOP_DIR"
sed "s|@INSTALL_DIR@|$APP_DIR|" share/padbridge.desktop > "$DESKTOP_DIR/padbridge.desktop"
install -D -m 644 share/padbridge.service "$UNIT_DIR/padbridge.service"
install -D -m 644 share/padbridge-flydigi.service "$UNIT_DIR/padbridge-flydigi.service"

command -v update-desktop-database >/dev/null && update-desktop-database "$DESKTOP_DIR" || true
systemctl --user daemon-reload

# --- permissions -----------------------------------------------------------
# The daemons need write access to /dev/uinput; the daemon and the GUI both
# need read access to /dev/input/event* (usually the "input" group). The
# Flydigi paddle daemon additionally needs the controller's hidraw nodes.
NEED_SUDO=0
if [ ! -w /dev/uinput ] 2>/dev/null; then NEED_SUDO=1; fi
if ! id -nG "$USER" | grep -qw input; then NEED_SUDO=1; fi
# A Flydigi controller is plugged in but its raw interface isn't accessible.
for h in /sys/class/hidraw/hidraw*; do
    [ -e "$h" ] || continue
    if grep -q "37D7:00002401" "$h/device/uevent" 2>/dev/null \
       && [ ! -r "/dev/$(basename "$h")" ]; then NEED_SUDO=1; fi
done

if [ "$NEED_SUDO" = 1 ]; then
    echo
    echo "PadBridge needs device permissions that require sudo:"
    echo "  - add $USER to the 'input' group (read controller events)"
    echo "  - install a udev rule granting logged-in users access to /dev/uinput"
    echo "  - install a udev rule for Flydigi controllers' raw HID interface"
    read -r -p "Set these up now? [Y/n] " answer
    if [ "${answer:-Y}" != "${answer#[Yy]}" ] || [ -z "${answer:-}" ]; then
        sudo usermod -aG input "$USER"
        sudo tee /etc/udev/rules.d/70-padbridge-uinput.rules >/dev/null <<'EOF'
# Allow active local sessions to use /dev/uinput (PadBridge virtual devices)
KERNEL=="uinput", SUBSYSTEM=="misc", TAG+="uaccess", OPTIONS+="static_node=uinput"
EOF
        sudo tee /etc/udev/rules.d/70-padbridge-flydigi.rules >/dev/null <<'EOF'
# Allow active local sessions to read the Flydigi Vader 5 Pro's vendor HID
# interface (PadBridge paddle daemon)
KERNEL=="hidraw*", ATTRS{idVendor}=="37d7", ATTRS{idProduct}=="2401", TAG+="uaccess"
EOF
        sudo udevadm control --reload-rules
        sudo udevadm trigger /dev/uinput || true
        sudo udevadm trigger -s hidraw || true
        echo "NOTE: log out and back in for the group change to take effect."
        echo "      (That also refreshes the app-menu icon cache.)"
    else
        echo "Skipped. PadBridge will not work until these permissions exist."
    fi
fi

echo
read -r -p "Enable and start the PadBridge background services now? [Y/n] " answer
if [ "${answer:-Y}" != "${answer#[Yy]}" ] || [ -z "${answer:-}" ]; then
    systemctl --user enable --now padbridge.service
    # Paddle daemon for Flydigi controllers; idles harmlessly without one.
    systemctl --user enable --now padbridge-flydigi.service
fi

echo
echo "Done. Launch 'PadBridge' from your application menu to configure mappings."
