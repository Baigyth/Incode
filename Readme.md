# InCode ![Icon](Doc/Logo.png "Incode Logo")
[![License](https://img.shields.io/github/license/Baigyth/Incode.svg?label=License&maxAge=86400)](./LICENSE) [![Release](https://img.shields.io/github/release/Baigyth/Incode.svg?label=Release&maxAge=60)](https://github.com/Baigyth/Incode/releases/latest)

Custom keyboard-driven mouse control for Windows. Hold the Interrupt key and use keyboard keys to move the cursor, scroll, and click — no mouse needed.

## Usage

### Interrupt Key

Hold the **Interrupt key** (default: Right-Control) to enter control mode. While held, keyboard input is intercepted and mapped to mouse actions.

- **Double-tap** the Interrupt key within 300ms to instantly center the cursor on the current monitor.
- Unmapped modifier keys (Shift, Ctrl, Alt) pass through to the OS, so you can still use shortcuts like Ctrl+C while in control mode.

> Set `"InterruptKey": "CapsLock"` in `Config.json` if you prefer Caps Lock as the trigger key.

### Default Key Bindings

| Key | Action |
|---|---|
| `W` `A` `S` `D` | Move cursor |
| `/` | Left click |
| Right-Shift | Right click |
| `R` | Scroll up (per line) |
| `F` | Scroll down (per line) |

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
  "Keymap": {
    "Up": "W",
    "Down": "S",
    "Left": "A",
    "Right": "D",
    "ScrollUp": "E",
    "ScrollDown": "C",
    "LeftDown": "OemQuestion",
    "RightDown": "RShiftKey",
    "ScrollUpAmount": "R",
    "ScrollDownAmount": "F"
  },
  "InterruptKey": "RControlKey",
  "Speed": 250.0,
  "Accel": 15.0,
  "AccelDelay": 0.3,
  "ScrollScale": 20.0,
  "ScrollAccel": 0.85,
  "ScrollAmount": 3,
  "MouseFilterResonance": 3.5,
  "MouseFilterFrequency": 2500
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
