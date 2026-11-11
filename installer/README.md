# Installers

Feature 9 of `ROADMAP.md`. Each platform gets a self-contained, single-file build (no .NET runtime needed on the
target) made by `installer/build.ps1` (PowerShell 7 also runs it on Linux).

| Platform | Command | Result |
|----------|---------|--------|
| Windows x64 | `./installer/build.ps1` | `installer/out/Nexus-<v>-win-x64.zip` (portable) and, with [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed, `installer/out/Nexus-<v>-setup.exe` (per-user, no administrator rights; `nexus.iss`) |
| Linux x64 | `./installer/build.ps1 -Runtimes linux-x64` | `installer/out/Nexus-<v>-linux-x64.tar.gz`; unpack and run `./install.sh` (per-user, `~/.local/opt/nexus`, launcher `nexus`, desktop entry). Needs libsecret (Secret Service) |
| macOS | not built | the macOS key store (Keychain) is not supported yet; the app stops with an explicit error there |

Data is never part of an installer and never removed by uninstalling: the encrypted database stays in
`%LOCALAPPDATA%\Nexus` or `~/.local/share/Nexus`, its keys in the OS key store, and the daily backups in `backups/`
next to it. Installing on another machine starts an empty database: a backup only opens with the key store of the
machine that made it.

`installer/out/` is ignored by git.
