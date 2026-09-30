# pLaunch

The old Windows Quick Launch, for the Windows 11 taskbar: one taskbar button that opens a list of
shortcuts (programs, files, folders, web links, Store apps).

## Use

- **Click** the pLaunch taskbar button to open the list; click an item to launch it.
- **Add** items by dragging them onto the list. You can also drag them onto the taskbar button and
  hold there for a moment: the list opens and you drop into it. The *Add* button and **Ctrl+V** work too.
- **Right click** an item: open, run as administrator, open file location, rename, remove, and add a
  sub-folder or a separator right after it. Drag items to reorder them.
- **Sub-folders** (Add → New sub-folder) open inside the popup, with a back button. Drop an item on a
  sub-folder to move it in; hold a drag over a sub-folder to open it, over the back button to go up;
  drop on the back button to move an item up one level. **Separators** (Add → Separator) divide the list.
- **… → View**: List, Tiles (big icon, name below) or Icons only (names in the tooltips).
  **… → Size**: small, medium, large. **… → Sort**: custom (drag to arrange) or alphabetical — each
  section between separators is sorted on its own, sub-folders first, and the custom order is kept for
  when you switch back.
- **… → Theme**: System (follows Windows), Light, Dark. **… → Background**: the Windows acrylic, a preset
  color, or any color from the Windows color picker; *Translucent* lets a little of the acrylic show
  through. With a custom background the text turns light or dark by itself so it stays readable.
- **Keyboard**: arrows and first letters to move, Enter to launch or open a sub-folder
  (Ctrl+Shift+Enter = as administrator), Backspace or Alt+Left to go back, F2 rename, Del remove, Esc close.
- The taskbar button's jump list (right click on the button) contains the same items.

Pin pLaunch to the taskbar and turn on **… → Start with Windows**. The drag-and-hover trick needs
pLaunch to be running: Windows only brings forward the windows of apps that are already open.

The list is saved in `%AppData%\pLaunch\items.json` (the `PLAUNCH_DATA_DIR` environment variable
changes the folder).

## Install

Two installers, per user by default (no admin rights; "all users" can be chosen in the first dialog):

- `pLaunch-Setup-<version>-Light.exe` — small, needs the .NET 10 Desktop Runtime (x64); setup offers the download page if it is missing
- `pLaunch-Setup-<version>-Full.exe` — includes the .NET runtime, no prerequisites

A per-user setup offers "Start pLaunch with Windows" (ticked by default); after an all-users setup each
user turns it on from pLaunch's own menu. Uninstalling removes the program and its autostart entry; the
list of shortcuts in `%AppData%\pLaunch` is kept. Unexpected errors are logged to `%AppData%\pLaunch\errors.log`.

## Build

Requires the .NET 10 SDK.

```
dotnet build pLaunch.slnx
dotnet test pLaunch.slnx
```

Installers (needs Inno Setup 6 or 7): `installer\build.ps1` runs the tests, publishes `publish\light` and
`publish\full` and writes `installer\Output\pLaunch-Setup-<version>-<Light|Full>.exe`; the version comes
from `<Version>` in `src/pLaunch/pLaunch.csproj`.

`tools/MakeIcon.cs` rebuilds `src/pLaunch/Assets/pLaunch.ico` from the artwork in `tools/icon-source.jpg`
(smooths the background, enlarges the logo, uses a thin-frame variant at 32 px and below):
`dotnet run tools/MakeIcon.cs` (`-- --preview sheet.png` also renders a preview, `--large`/`--small` set the logo scale).

## How it works

Windows 11 has no taskbar toolbars and never hands a drop on a taskbar button to the app. So pLaunch is
an ordinary window that stays minimized while idle. Restoring it (a click on the button, or hovering it
during a drag) opens it as a flyout next to the taskbar, sized to its content, with the acrylic backdrop.
When it loses the focus it minimizes again. Restore/minimize animations are turned off, so it behaves
like a popup.

- `PopupWindow` — the flyout: open/close toggle, placement, drag and drop, context menus
- `Native/PopupPlacement` — where the popup goes (taskbar edge, auto-hide, multi-monitor, DPI)
- `Services/DropReader` — file drops, browser links, Shell IDList arrays (Start menu apps)
- `Services/IconProvider` — shell icons through `IShellItemImageFactory`
- `Services/SingleInstance` — a second start forwards its arguments to the running instance over a named pipe
- `Services/JumpListBuilder` — the jump list entries run `pLaunch --launch <id>`
