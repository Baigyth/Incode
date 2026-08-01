# InCode ![Icon](Doc/Logo.png "Incode Logo")
[![License](https://img.shields.io/github/license/Baigyth/Incode.svg?label=License&maxAge=86400)](./LICENSE) [![Release](https://img.shields.io/github/release/Baigyth/Incode.svg?label=Release&maxAge=60)](https://github.com/Baigyth/Incode/releases/latest)

Custom keyboard-driven mouse control for Windows. Hold the Interrupt key and use keyboard keys to move the cursor, scroll, and click — no mouse needed.

## Usage

### Interrupt Key

Hold the **Interrupt key** (default: Right-Control) to enter control mode. While held, keyboard input is intercepted and mapped to mouse actions.

- **Double-tap** the Interrupt key within 300ms to instantly center the cursor on the current monitor.
- Unmapped modifier keys (Shift, Ctrl, Alt) pass through to the OS, so you can still use shortcuts like Ctrl+C while in control mode.

> Set `"InterruptKey": "CapsLock"` in `Config.json` if you prefer Caps Lock as the trigger key.

### Fine Mode

While in control mode, hold the **Fine Modifier key** (default: Left Shift) to enter fine mode. In fine mode, cursor movement uses a fixed slow speed (`FineSpeed`, default 50 px/s) with no acceleration, giving you pixel-precise control.

- Fine mode is useful for precise targeting (links, UI elements, small buttons).
- Released by letting go of the Fine Modifier key.
- Configure via `"FineModifierKey"` (Keys enum name) and `"FineSpeed"` (float) in `Config.json`.
- Set `"FineModifierKey": ""` to disable fine mode.

> Unlike the regular speed formula (`Speed × (1 + Accel × time)`), fine mode uses a flat `FineSpeed` — no acceleration ramp.

### Grid Mode (9-Cell Navigation)

While in control mode, hold the **Grid key** (default: Left Alt) to enter grid mode. The current monitor is divided into a 3×3 grid. Press the key corresponding to a grid cell to instantly jump the cursor to that cell's center.

Grid key can be configured via `"GridKey"` in `Config.json` (Keys enum name, e.g. `"LMenu"`). Set to `""` to disable.

**Two-level sub-grid navigation (`SubGridEnabled`):**

By default grid mode is single-level — one press jumps to the cell center and that's it. Set `"SubGridEnabled": true` in `Config.json` (default `false`) to enable 2-level nested navigation:

1. Press a grid key → cursor jumps to that cell's center, and the cell is narrowed into its own 3×3 sub-grid.
2. Press a grid key again → cursor jumps to the sub-cell's center.
3. Release the Grid key (or the Interrupt key) to exit grid mode; press the Grid key again to restart at the full-screen level.

**On-screen grid HUD (`GridLabelFontSize`):**

When `SubGridEnabled` is `true`, entering grid mode also shows a semi-transparent 3×3 grid overlay that highlights the current cells and their key labels. The font size of the labels is configurable via `"GridLabelFontSize"` (default `48`, in points). The overlay follows the narrowing as you navigate into sub-cells, hides when you exit grid mode, and is click-through (does not intercept the mouse).

**Default grid key mapping (configurable via `GridKeys`):**
```
 Q  W  E     ← top row
 A  S  D     ← middle row
 Z  X  C     ← bottom row
```

| Key | Target |
|---|---|
| Q | Top-left |
| W | Top-center |
| E | Top-right |
| A | Middle-left |
| S | Center |
| D | Middle-right |
| Z | Bottom-left |
| X | Bottom-center |
| C | Bottom-right |

- Grid mode locks out all normal control-mode key bindings (A/S/D etc. do not move the cursor while grid mode is active).
- Release the Grid key to return to normal control mode.
- Multiple jumps can be performed while holding the Grid key.

### Default Key Bindings

| Key | Action |
|---|---|
| `W` `A` `S` `D` | Move cursor |
| `Space` | Left click (hold to keep pressed) |
| `F` | Right click (hold to keep pressed) |
| `R` | Scroll up (per line) |
| `V` | Scroll down (per line) |

All keys are configurable via `Config.json`. Set any command to `""` to disable it.

> Smooth scroll (`ScrollUp` / `ScrollDown`) is disabled by default.
> To enable, set them in `Keymap`, e.g. `"ScrollUp": "E", "ScrollDown": "C"`.

### System Tray

InCode runs as a **tray-only app** — no window. Right-click the tray icon to:

| Action | Effect |
|---|---|
| **Restart** | Reload `Config.json` and restart the engine |
| **Exit** | Quit the application |

Double-click the tray icon to restart.

## Changes from Upstream

This fork ([Baigyth/Incode](https://github.com/Baigyth/Incode)) is a purified version of [cschladetsch/Incode](https://github.com/cschladetsch/Incode).

### Removed Features

| Feature | Reason |
|---|---|
| **Settings window** | Replaced by tray-only operation. Edit `Config.json` directly. |
| **Abbreviations** | Text expansion removed to keep scope focused on mouse control. |
| **Key-press sounds** | Audio feedback caused stability issues (WaveOutEvent resource leak). |
| **Volume control** | Out of scope for a mouse-replacement tool. |

### Modified: Mouse Speed Formula

Old formula used `Speed` and `Accel` as additive components. The new formula introduces a delay-based acceleration:

```
velocity = Speed × (1 + Accel × max(0, t − AccelDelay))
```

| Parameter | Description |
|---|---|
| `Speed` | Base cursor speed in pixels/second at the start of movement |
| `Accel` | Multiplicative factor applied after the delay elapses |
| `AccelDelay` | Grace period in seconds before acceleration begins |

**Migration from old configs:** Old `Accel` was an absolute added speed — now it's a **multiplier** on `Speed`. You'll likely need to lower `Speed` and raise `Accel`. Start with `Speed: 150, Accel: 15, AccelDelay: 0.3`.

Cursor movement is smoothed by a 2nd-order IIR low-pass filter (configurable via `MouseFilterResonance` / `MouseFilterFrequency`), inspired by the IBM ThinkPad TrackPoint feel.

## Configuration

All settings in `Config.json` (application directory). Read on startup and on restart.

### Complete Example

```json
{
  "InterruptKey": "RControlKey",
  "Speed": 150.0,
  "Accel": 15.0,
  "AccelDelay": 0.3,
  "ScrollScale": 30.0,
  "ScrollAccel": 0.0,
  "ScrollAmount": 3,
  "MouseFilterResonance": 2.5,
  "MouseFilterFrequency": 2000,
  "FineModifierKey": "LShiftKey",
  "FineSpeed": 50.0,
  "GridKey": "LMenu",
  "GridKeys": ["Q", "W", "E", "A", "S", "D", "Z", "X", "C"],
  "SubGridEnabled": false,
  "GridLabelFontSize": 48,
  "Keymap": {
    "Up": "W",
    "Down": "S",
    "Left": "A",
    "Right": "D",
    "ScrollUp": "E",
    "ScrollDown": "C",
    "LeftDown": "Space",
    "RightDown": "F",
    "ScrollUpAmount": "R",
    "ScrollDownAmount": "V"
  }
}
```

### Keymap Commands

| Command | Behavior |
|---|---|
| `Up` `Down` `Left` `Right` | Cursor movement |
| `ScrollUp` `ScrollDown` | Smooth continuous scroll |
| `ScrollUpAmount` `ScrollDownAmount` | Discrete scroll (`ScrollAmount` lines per press) |
| `LeftDown` `RightDown` | Mouse button (hold to keep pressed) |

### Modifier Key Reference

The `System.Windows.Forms.Keys` enum names differ from keyboard labels. Use this reference when editing `Keymap` or `InterruptKey`:

| Keyboard Key | Config String |
|---|---|
| Left Shift | `LShiftKey` |
| Right Shift | `RShiftKey` |
| Left Ctrl | `LControlKey` |
| Right Ctrl | `RControlKey` |
| Left Alt | `LMenu` |
| Right Alt | `RMenu` |
| Left Win | `LWin` |
| Right Win | `RWin` |
| Caps Lock | `CapsLock` |
| Apps / Menu | `Apps` |
| Space | `Space` |
| Tab | `Tab` |
| Enter | `Return` |
| Escape | `Escape` |
| Backspace | `Back` |
| Delete | `Delete` |
| `/` | `OemQuestion` |
| `\` | `OemPipe` |
| `[` | `OemOpenBrackets` |
| `]` | `OemCloseBrackets` |
| `;` | `OemSemicolon` |
| `'` | `OemQuotes` |
| `,` | `Oemcomma` |
| `.` | `OemPeriod` |
| `-` | `OemMinus` |
| `=` | `OemPlus` |
| `` ` `` | `Oemtilde` |
| `0`–`9` | `D0`–`D9` |
| `A`–`Z` | `A`–`Z` |
| `F1`–`F12` | `F1`–`F12` |

> **Common pitfalls:**
> - `OemQuestion` is `/`, not `?`. `Oemcomma` is `,`. `OemPeriod` is `.`. The name comes from the base US layout, not the shifted character.
> - **Modifier pass-through:** `LShiftKey` / `RShiftKey` / `LControlKey` / `RControlKey` / `LMenu` / `RMenu` are automatically passed through to the OS when **not** mapped in `Keymap`. If you map them to a command, they trigger the command instead.
> - `LWin` / `RWin` are **not** in the pass-through list — they are eaten in control mode unless explicitly mapped.

## Build

```powershell
# Requires .NET SDK 6.0+ (MSBuild 17.x)
C:\Progra~1\dotnet\dotnet.exe msbuild Incode.sln /p:Configuration=Release
```

Output: `IncodeWindow/bin/Release/IncodeWindow.exe`

## License

MIT — see [LICENSE](./LICENSE).
