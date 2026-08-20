# OP1w Battery

A system tray battery indicator for the Endgame Gear OP1w 4k v2 wireless mouse.

The vendor configuration tool only shows the battery level while it is open. This
puts the number in your tray and tells you when it is time to charge. It talks to
the mouse over raw HID using the protocol reverse-engineered from that tool,
documented in the doc comment on `MouseBattery`. A .NET 10 WinForms app with no
third-party dependencies.

## Features

- The battery percentage is drawn onto the tray icon itself, so the level is
  readable at a glance without hovering or clicking.
- Colour-coded as the level falls: green, yellow, orange, then red at 10%. Blue
  means the mouse is plugged in, and `?` means it is asleep or out of range.
- Hover for the exact level and the measured cell voltage, e.g. `55%  3.84 V`.
- A notification when the battery reaches 10%, fired once per discharge cycle
  rather than on every poll.
- Polls every 5 minutes, dropping to every 2 minutes once the battery is low.
- Right click for the current level, a manual refresh, and a **Start with
  Windows** toggle. Nothing is written to the registry unless you pick it.
- Single instance, no main window, no taskbar entry.

## Download

Grab `OP1wBattery.exe` from the [latest release](../../releases/latest) and run
it. The small release executable does not include .NET. Install the [.NET 10
Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) first.

## Build

Needs the .NET 10 SDK.

```bash
dotnet build -c Release
```

The executable lands in `bin\Release\net10.0-windows\OP1wBattery.exe`.

To create the same small, framework-dependent executable as the release:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

To create a larger, self-contained executable that does not need .NET:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
