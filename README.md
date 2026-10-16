# Multi-SSH

A PuTTY-style SSH client for Windows, written in C# / WPF (.NET 8), with one big
difference: you can have **many SSH screens open at once** — as tabs, or tiled
side-by-side in a grid that automatically resizes every pane to fit as you add more.

![tiles](docs/tiles.png)

## Features

**Connections**
- SSH over the standard protocol (via [SSH.NET](https://github.com/sshnet/SSH.NET))
- Authentication: password, public-key (`.pem`/OpenSSH key + passphrase),
  keyboard-interactive, or agent-fallback
- If a password (or an encrypted key's passphrase) isn't saved, you're prompted
  securely at connect time — and re-prompted on rejection — instead of the login
  simply failing
- Per-session keepalive interval, connect timeout, TCP_NODELAY
- Dead connections are noticed on every session type (SSH, SFTP, SCP, WSCP,
  WebDAV), even when idle: a link that drops silently is reported within about
  35 seconds — red status dot, red status strip, and a notice in the terminal —
  so you know to click ⟳ Reconnect

**Multiple sessions, your way**
- **Tabbed view** — classic tabs, each with a close button
- **Tiled view** — every open session visible at once; panes shrink uniformly
  to fit as more are opened (2 → side by side, 4 → 2×2, 9 → 3×3, …)
- Switch between Tabs and Tiles at any time from the toolbar
- Duplicate the active session, reconnect, or close individual panes
- **Broadcast bar** on the right of the toolbar: type a command (or pick one from
  the common-commands dropdown) and send it to *every* open session at once

**Terminal**
- Custom VT100 / xterm-256color emulator: colours (16 / 256 / true-colour),
  bold / underline / dim / inverse, cursor movement, scroll regions,
  insert/delete lines & chars, alternate-cursor keys, window-title (OSC)
- Mouse-wheel scrollback (configurable depth)
- PuTTY-style **copy-on-select** and **right-click-paste**
  (plus `Ctrl+Shift+C` / `Ctrl+Shift+V`)
- Live PTY resize as the pane/window changes size

**Configuration** (a PuTTY-like categorised dialog)
- Session · Connection · Authentication · Terminal · Appearance · Behaviour
- Font family & size, colour schemes (Campbell, PuTTY, Solarized Dark, Dracula)
- Initial rows/columns, scrollback lines, terminal-type string, bell

**Saved sessions**
- Stored in `%AppData%\Multi-SSH\sessions.json`
- Passwords / passphrases encrypted per-Windows-user with **DPAPI**
- Sidebar list — double-click to open, right-click to edit/delete

## Build & run

```bash
dotnet build -c Release
dotnet run --project MultiSSH
```

Requires the .NET 8 (or newer) SDK with the Windows desktop workload.

## Notes / limitations

- Alternate screen buffer (used by full-screen apps like `vim`/`less` to restore
  the shell on exit) is not modelled separately — those apps still work, they
  just draw over the main buffer.
- Mouse reporting (clicking inside `tmux`/`htop`) is not forwarded yet.
- SSH agent auth falls back to password (SSH.NET has no agent transport).

## License

MIT
