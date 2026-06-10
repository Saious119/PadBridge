# PadBridge

Generic controller button remapper for Linux. Map any button on any
controller to a keyboard key or another controller button — including
extra buttons games can't see, like the back/function buttons on pads
such as the Flydigi Vader 5 Pro (which is what this project was born to
fix: those buttons make great F13–F24 hotkeys in MMOs).

A small background daemon does the remapping; a desktop app lets you
pick a controller, press buttons to find them, click to rebind, and
start/stop the bridge.

## Installation

Requirements: a 64-bit (x86_64) Linux with systemd. Works on both
Wayland and X11. No other dependencies — the download is self-contained.

1. Grab the latest `padbridge-<version>-linux-x64.tar.gz` from the
   [Releases page](https://github.com/Saious119/PadBridge/releases).
2. Unpack and run the installer:

   ```sh
   tar xf padbridge-*-linux-x64.tar.gz
   cd padbridge-*-linux-x64
   ./install.sh
   ```

The installer puts everything under your home directory (`~/.local`) —
no root needed for the app itself. It will offer one optional `sudo`
step to set up device permissions (adding you to the `input` group and
installing a udev rule for `/dev/uinput`); PadBridge can't read your
controller or create virtual devices without these. If you accept, **log
out and back in once** so the group change takes effect.

Then launch **PadBridge** from your application menu.

To remove it later, run `./uninstall.sh` from the same folder.

## Using it

1. Pick your controller in the dropdown. Press buttons on it — the
   matching rows light up so you can tell which physical button is which.
2. Click a "Mapped to" entry, then press the key or button you want it
   to become. Esc cancels a capture, ✕ clears a mapping.
   - If a button isn't in the list, click **＋ Add input** and press it:
     the input is added as a new row (and the hint tells you if the
     press actually arrived from a different device). The ✎ button on a
     row gives the input a nickname, e.g. "Back Paddle 1".
3. Hit **Save config**. The running bridge picks up changes instantly.
4. The ▶ / ■ buttons start and stop the background service; the dot
   shows whether it's running. The service starts automatically on login.

**Exclusive mode** (checkbox in the toolbar): by default PadBridge adds
remapped keys *alongside* your controller's normal input, which is
perfect for extra buttons → keyboard keys. If you want button-to-button
remaps (A acts as B), enable exclusive mode — the bridge takes the
controller over and replaces it with a remapped copy. Rumble is passed
through to the real controller. One trade-off: in this mode a running
bridge has exclusive access to the controller, which prevents the
PadBridge app from seeing your button presses — so stop the service (■)
before rebinding buttons, and start it again (▶) when you're done. If
the bridge can't take the controller (journal says "grab ... failed"),
another remapper has it — usually Steam Input.

## Config files

Configs are plain text in `~/.config/padbridge/`:

- `configs/*.conf` — your named configs (the "Config" dropdown in the
  app). Create new ones with the ＋ button; you'll be asked to name a
  config the first time you save it.
- `padbridge.conf` — a copy of whichever config is *active*; this is the
  file the bridge actually runs. Selecting a config in the dropdown
  activates it.

For an emergency manual edit, change `padbridge.conf` — the running
bridge reloads it instantly. The format:

```
device = Vader 5 Pro Virtual Gamepad
grab = false

map BTN_TRIGGER_HAPPY1 = KEY_I
map BTN_TRIGGER_HAPPY2 = KEY_O

label BTN_TRIGGER_HAPPY1 = Back Paddle 1
```

Names are the kernel's evdev names. `label` lines are display nicknames
shown by the app (set them with a row's ✎ button); the daemon ignores
them. Lines starting with `#` are comments. The GUI is optional — the daemon only cares about
`padbridge.conf`.

## Building from source

Needs gcc, make, python3, and the .NET 10 SDK:

```sh
cd daemon && make install          # -> ~/.local/bin/padbridge-daemon
cd ../gui && dotnet publish -c Release -o ~/.local/share/padbridge
systemctl --user enable --now padbridge.service
```

To build a redistributable tarball like the released ones:
`./scripts/make-release.sh` → `dist/padbridge-<version>-linux-x64.tar.gz`.

## How it works

- **`daemon/`** — small C daemon (`padbridge-daemon`). Reads EV_KEY
  events from the configured input device and re-emits them through
  uinput virtual devices: keyboard targets via "PadBridge Virtual
  Keyboard", and in exclusive mode the whole controller is forwarded
  through a capability-identical clone (`<name> (PadBridge)`) with
  mapped buttons rewritten in transit. Watches the config with inotify
  and hot-reloads on change.
- **`daemon/padbridge-flydigi.c`** — companion daemon for the Flydigi
  Vader 5 Pro. Its extra buttons (C, Z, back paddles M1–M4, extra
  bumpers LM/RM, O, Home) never reach the kernel's xpad driver; this
  daemon asks the controller to stream them over its vendor HID
  interface (no Flydigi software needed) and re-emits them as a normal
  evdev device, "Flydigi Vader 5 Pro Paddles" (BTN_TRIGGER_HAPPY1–10).
  Pick that device in the app to map the paddles. Hotplug-aware; the
  controller's regular input is untouched. Protocol per the
  [flydigi-vader5](https://github.com/BANANASJIM/flydigi-vader5) project.
- **`gui/`** — Avalonia (.NET) desktop app. Talks to evdev directly
  (works on Wayland, no compositor involvement) and controls the
  systemd user service.
- **`scripts/gen-event-codes.py`** — generates the KEY_*/BTN_* name
  tables (`daemon/event-names.h`, `gui/Evdev/EventCodes.g.cs`) from
  `/usr/include/linux/input-event-codes.h`.
- Runs entirely as your user: needs membership in the `input` group
  (for /dev/input) and write access to /dev/uinput — exactly what the
  installer's permissions step sets up.
- During capture the GUI ignores mouse buttons (BTN_LEFT..BTN_TASK) so
  clicking the UI can't bind a mapping to a mouse click. All other keys
  on any device are fair game, including keyboards that also report
  pointer capabilities (QMK wheel emulation, MMO-mouse keypads, etc.).
