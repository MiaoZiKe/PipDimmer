# PipDimmer — scroll-to-dim for Chrome's Picture-in-Picture window

[中文說明](README.md)

Hover Chrome's Picture-in-Picture window and **just scroll the wheel to change its opacity**.
Any other window works too, with Ctrl+Shift held. Ctrl+Shift+middle-click turns a window into
a click-through "ghost".

Same idea as Spooky View / WindowTop, except it does **not** identify the PiP window by its
title (which would break in every language but yours), and it is a single 45 KB executable
with no installer and no admin rights.

Requires Windows 10 1703 or later. .NET Framework 4.x ships with Windows, so there is nothing
to install.

---

## Quick start

Download `PipDimmer.exe` from [Releases](../../releases) and run it. It sits in the tray,
writes nothing to the registry, and needs no elevation.

| Gesture | Effect |
|---|---|
| Wheel over the **PiP window** | Change its opacity |
| **Ctrl+Shift**+wheel over **any other window** | Change that window's opacity |
| Wheel, or **Ctrl**+wheel, over any other window | **Untouched** — normal scrolling and browser zoom still work |
| **Ctrl+Shift+middle-click** | Toggle click-through ("ghost"); repeat to turn it off |
| **Left-click** the tray icon | Enable / disable |
| **Right-click** the tray icon | Full menu |

Defaults: 5% per notch, 10% floor, wheel up = more opaque.

### Why Ctrl+Shift and not plain Ctrl

`Ctrl`+wheel is **zoom** in browsers, VS Code, Explorer and Office, and `Ctrl`+middle-click
**opens a link in a new tab**. Binding transparency to those would shadow all of it.

`Win` and `Alt` are single keys, but releasing them pops the **Start menu** and the **menu
bar** respectively unless you inject a masking keystroke (PipDimmer does mask them, so those
options work — they are just noisier). `Shift`+wheel is horizontal scrolling in browsers and
Explorer.

`Ctrl+Shift` sits under one hand and almost nothing else uses it. To change it: tray →
**修飾鍵（其他視窗）** / *Modifier (other windows)* — eight choices, applied immediately.

> Starting PipDimmer does **not** touch windows that are already open. Only PiP windows that
> appear **afterwards** get the remembered opacity applied automatically.

---

## Two editions

| | `PipDimmer.exe` (primary) | `ahk/PipDimmer.ahk` |
|---|---|---|
| Dependencies | none (.NET Framework ships with Windows) | AutoHotkey v2 |
| Portable to another machine | yes, one file | only if AHK is installed there |
| Configuration | tray menu / `settings.ini` | same, plus edit the script |
| Use when | day to day | you want to change the logic yourself |

Both read and write the same `%APPDATA%\PipDimmer\settings.ini`, byte-for-byte identical
format. **Do not run both at once** — they will fight over the same windows.

---

## What "click-through" means

Technically it sets `WS_EX_TRANSPARENT` on the window: the window becomes invisible to the
mouse. Clicks, wheel and drags all **pass through** to whatever is underneath. You still see
the window — opacity is a separate thing — but it no longer accepts any mouse input at all.

The point: make the PiP window semi-transparent **and** click-through, and you can park it
directly on top of the document you are working in. You see the video, and you keep clicking
and typing in the document underneath as if the video were not there.

The catch is that you cannot click it any more either — no dragging, no closing. PipDimmer
keeps ghosted windows in its own list and resolves them by rectangle instead, so **the wheel
still adjusts them**, and the same gesture turns click-through back off.

---

## How it recognises the PiP window

Not by matching the string "Picture in picture". Measured values:

| Window | style | exstyle |
|---|---|---|
| **PiP window** | `0x16CC0000` | `0x00200108` |
| Ordinary Chrome window | `0x36CF0000` | `0x00200100` |
| Chrome `--app` window | `0x16CF0000` | `0x00200100` |

All of these must hold:

1. Window class is `Chrome_WidgetWin_1`
2. Process is one of chrome / msedge / brave / vivaldi / opera / chromium / thorium
3. `WS_EX_TOPMOST` is set — PiP is always-on-top, ordinary browser windows are not
4. `WS_EX_NOACTIVATE` is **not** set — this rules out Chrome's transient overlays
   (tooltips, autofill dropdowns) which are also topmost `Chrome_WidgetWin_1` windows
5. It does **not** have both a minimize box and a maximize box — ordinary browser windows
   have both, the PiP window has neither
6. At least 120×80 pixels

Rule 5 exists because otherwise an ordinary Chrome window that you pinned always-on-top with
some other tool would be mistaken for PiP, and PipDimmer would silently eat the wheel on it.
If a future Chrome build gives the PiP window those buttons, set `RequirePipShape=0` in
`settings.ini`.

There is also a **fallback**: an exact match against the localized PiP title
(子母畫面 / 画中画 / Picture in picture / ピクチャー イン ピクチャー / PIP 모드 /
Bild im Bild / Imagen en imagen / Mode PIP…). Note that string follows the **browser's** UI
language, not the OS language.

> YouTube's miniplayer and Google Meet use **Document PiP**, whose window title is the page
> title rather than "Picture in picture", so only the structural rules 1–6 catch those.

---

## settings.ini

At `%APPDATA%\PipDimmer\settings.ini`, plain `key=value` text.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | 1 | Master switch |
| `StepPercent` | 5 | Percent per notch (2 / 5 / 10 only) |
| `MinPercent` | 10 | Opacity floor (10 / 20 / 30 only) |
| `LastAlphaPercent` | 60 | Last value used, for auto-apply |
| `ShowOsd` | 1 | On-screen percentage readout |
| `AutoApply` | 1 | Apply the remembered opacity to new PiP windows |
| `AllowOtherWindows` | 1 | Modifier+wheel works on non-PiP windows |
| `GhostGesture` | 1 | Modifier+middle-click toggles click-through |
| `ModifierMask` | 3 | Bitmask: `1`=Ctrl `2`=Shift `4`=Alt `8`=Win, summed; `0` = no modifier. 3 = Ctrl+Shift |
| `InvertWheel` | 0 | Wheel up = more transparent |
| `ForceRedraw` | 0 | Force a repaint after applying |
| `RequirePipShape` | 1 | Rule 5 above; set to 0 only if detection fails |

---

## Troubleshooting

**The wheel does nothing**

1. Tray → *偵測視窗資訊…* (window info). It counts down 3 seconds, grabs the window under the
   cursor, and reports its class / title / process / style / exstyle **and why it was not
   classified as a PiP window**.
2. Tray → *重新掛載滑鼠鉤子* (re-install mouse hook). Windows **silently** removes a low-level
   hook that times out too often, with no way for the app to detect it. PipDimmer has a
   watchdog that re-installs automatically; this is the manual fallback.
3. If the target app runs **as administrator**, UIPI blocks a non-elevated process from both
   receiving its mouse events and changing its styles. That is by design in Windows and there
   is no way around it — run the target app unelevated instead.

**Black or stale window after applying**

Chrome's PiP window (which carries `WS_EX_NOREDIRECTIONBITMAP`) was verified working on
Windows 11. If another application misbehaves:

1. Tray → enable *套用後強制重繪* (force redraw)
2. Still wrong: turn off hardware acceleration in that application. For Chrome that is
   Settings → System → *Use graphics acceleration when available*.

**Conflicts with other transparency tools**

Spooky View, WindowTop and friends also change `WS_EX_LAYERED` on other windows. Running two
of them at once means they overwrite each other. Keep one.

**A window is stuck semi-transparent**

Tray → *還原所有視窗* (restore all windows), or just close and reopen that window. A normal
exit (tray → quit, or logging off) always restores everything it touched.

**Diagnostic log**

```
PipDimmer.exe -log
```

Writes hook installation results, the per-second PiP scan, and every wheel action to
`%APPDATA%\PipDimmer\log.txt`. Off by default.

---

## Building

```powershell
.\build.ps1
```

Uses the C# compiler that ships inside Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No .NET SDK, no Visual Studio.

> ⚠️ That compiler is the **pre-Roslyn C# 5.0** one. When editing `src/PipDimmer.cs` you
> cannot use string interpolation `$"..."`, `?.`, `nameof`, expression-bodied members, or
> `out var`. `build.ps1` passes `/langversion:5` explicitly so any slip fails at build time
> rather than mysteriously later.

> ⚠️ `build.ps1` is deliberately ASCII-only. Windows PowerShell 5.1 reads a `.ps1` without a
> UTF-8 BOM using the system ANSI codepage, so non-ASCII characters become mojibake and break
> parsing. `PipDimmer.ahk` carries a BOM, so its comments are fine.

---

## Tests

`tests/Run-Tests.ps1` drives both editions against a reproducible PiP fixture — a local HTML
page that feeds a `<video>` from `canvas.captureStream()`, so a real Picture-in-Picture window
can be opened without any video file, network access, or manual clicking.

It asserts the step arithmetic, the opacity floor, that returning to 100% clears
`WS_EX_LAYERED`, that a clean exit restores every window, that `Ctrl`+wheel is passed through
while `Ctrl+Shift`+wheel is captured, and that a ghosted window is still reachable by the
wheel.

See [tests/README.md](tests/README.md).

---

## Known limitations

- Elevated (administrator) windows cannot be adjusted, and their mouse events never reach a
  non-elevated hook. That is Windows UIPI, by design.
- The wheel is swallowed over the PiP window, so any wheel behaviour Chrome itself might add
  there would stop working (today's PiP window has none).
- Document PiP windows may carry `WDA_EXCLUDEFROMCAPTURE` on Windows. That does not affect
  transparency, but screenshots of them come out black.
- A ghosted window disappears from `WindowFromPoint`; PipDimmer finds it again through its own
  list and the window rectangle, so the wheel still works. But if PipDimmer is force-killed
  rather than exited normally, that window stays click-through until it is reopened.

---

## License

[MIT](LICENSE)
