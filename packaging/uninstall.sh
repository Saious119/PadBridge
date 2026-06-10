#!/usr/bin/env bash
# Removes a PadBridge installation from ~/.local.
set -euo pipefail

systemctl --user disable --now padbridge.service 2>/dev/null || true
systemctl --user disable --now padbridge-flydigi.service 2>/dev/null || true
rm -f "$HOME/.config/systemd/user/padbridge.service"
rm -f "$HOME/.config/systemd/user/padbridge-flydigi.service"
systemctl --user daemon-reload

rm -f "$HOME/.local/bin/padbridge-daemon"
rm -f "$HOME/.local/bin/padbridge-flydigi"
rm -rf "$HOME/.local/share/padbridge"
rm -f "$HOME/.local/share/applications/padbridge.desktop"
rm -f "$HOME/.local/share/icons/hicolor/scalable/apps/padbridge.svg"
rm -f "$HOME"/.local/share/icons/hicolor/*/apps/padbridge.png

echo "PadBridge removed. Config kept at ~/.config/padbridge (delete manually if unwanted)."
echo "The udev rules at /etc/udev/rules.d/70-padbridge-*.rules need sudo to remove."
