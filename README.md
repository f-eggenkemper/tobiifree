# tobiifree - ET5 on Linux & Web

An ongoing experiment to make Tobii eye trackers work on Linux.

The Tobii Eye Tracker 5 (ET5) ships with Windows-only drivers and no public protocol documentation. Under EU law — specifically the [European Interoperability Act](https://eur-lex.europa.eu/eli/dir/2009/24/oj) and related directives — reverse engineering for interoperability purposes is permitted. This project exercises that right to bring eye tracking to Linux users.

> **Status:** experimental. The USB wire protocol was decoded by observing the device's standard USB bulk transfers. Things work, things break, things change.

## Demo: https://aetherall.github.io/tobiifree/
<img width="2309" height="1219" alt="image" src="https://github.com/user-attachments/assets/8fe16a58-5d75-4104-a6bd-2c038be8d1e6" />


## What's here

- **driver/** — Zig implementation of the TTP/TLV framing protocol. Compiles to both WebAssembly (for the browser SDK) and native (for the Linux applications). Pure byte-level protocol engine — no allocator, no syscalls.
- **sdk/** — TypeScript SDK wrapping the wasm core. Works in the browser (WebUSB) and Node.js (`usb` package). `Tobii.fromUsb()` to connect, `subscribeToGaze()` to stream.
- **applications/tobiifreed** — native daemon/sidecar. Talks to the tracker over libusb, exposes gaze data over loopback WebSocket/TCP and, on Unix platforms, an optional Unix socket.
- **applications/tobiifree-overlay** — GTK4 + layer-shell overlay that draws a gaze dot on your Wayland desktop. Connects directly via USB or through the daemon.
- **applications/tobiifree-demo** — Browser app for live gaze visualization, calibration, and display area configuration. Hosted on GitHub Pages.
- **assets/** — Firmware tools (DFU flash/extract) and udev rules.

## Quick start

### Prerequisites

[Nix](https://nixos.org/) with flakes enabled, or Zig 0.14+ and Node.js 22+ installed manually.

```sh
# Enter the dev shell (provides zig, node, libusb, gtk4, etc.)
nix develop

# USB permissions (run once)
sudo cp assets/99-tobii.rules /etc/udev/rules.d/
sudo udevadm control --reload && sudo udevadm trigger
```

### Build and run

```sh
# Run the daemon sidecar (direct USB, WebSocket on ws://127.0.0.1:7081 by default)
just tobiifreed

# Run the gaze overlay (direct USB)
just overlay

# Run the overlay through the daemon (Unix socket)
just tobiifreed                # terminal 1
just overlay -- --socket   # terminal 2

# Run the browser demo (WebUSB, opens http://localhost:5173)
just bundle   # build wasm + embed in SDK
just demo
```

### Nix packages

```sh
nix build .#tobiifreed        # daemon binary
nix build .#tobiifree-overlay # GTK4 overlay binary
nix build .#tobiifree-demo     # static SPA (deployable to any web server)
```

## Supported hardware

| Device | VID:PID | Status |
|--------|---------|--------|
| Tobii Eye Tracker 5 (runtime) | `2104:0313` | Working — gaze, calibration, display area |
| Tobii Eye Tracker 5 (bootloader) | `2104:0102` | DFU flash only |

## .NET / Avalonia integration

For desktop apps, prefer running `tobiifreed` as a local sidecar and connect to
`ws://127.0.0.1:7081` from your app. That keeps USB ownership and protocol state
inside the daemon while your UI talks to a stable loopback WebSocket API.

- `tobiifreed` now enables the loopback WebSocket server by default
- pass `--no-ws` to disable WebSocket
- pass `--no-unix` to disable the Unix socket on macOS/Linux
- on Windows, WebSocket/TCP is the intended client transport

The binary payloads sent over WebSocket use the daemon protocol documented in
`ARCHITECTURE.md`.

## License

[GPL-3.0](LICENSE)
