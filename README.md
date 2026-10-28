# Emby Backup

> [Leer en español](README.es.md)

An Emby Server plugin for complete **Server Data Folder** backups and database snapshots, without movie metadata: no NFO files, posters, backdrops, cache, or media-library files.

Designed for Emby Server **4.9+** (tested with 4.9.1.90) on Linux, Docker, Windows, and NAS systems.

## Download

The installable build is available in [Releases](https://github.com/ratopro/emby-backup-plugin/releases/latest). Download the ZIP, extract `EmbyBackup.dll`, and copy it to the plugin directory as described in [Installation](#installation).

## Releases

- **1.4.4**: excludes `backup/` below the Data Folder so corrupt historical snapshots created by Emby Backup & Restore are not processed again.
- **1.4.3**: excludes desktop-user profile files accidentally stored in the Data Folder, including `.local/`, `.config/`, `.cache/`, and `.bashrc`.
- **1.4.2**: adds a master plugin enable/disable switch and an **Emby Backup** entry in Emby's side menu while active.
- **1.4.1**: ZIP and restore hardening: limits for ZIP metadata and restores, stricter header/manifest validation, staging-space checks, and scoped handling of self-signed FTPS certificates.
- **1.4.0**: optional AES-256 password-protected backups and in-page restore with preflight verification, double confirmation, and restart request.
- **1.3.0**: configurable automatic schedule: daily, every other day, weekly, or monthly.

## What a backup contains

- SQLite databases (`library.db`, `users.db`, `authentication.db`, `activitylog.db`, and any other database in the Data Folder). They are copied through the **SQLite Online Backup API**, not with a direct file copy, so snapshots remain consistent while Emby writes through WAL.
- Server settings (`config/system.xml`) and user policies (`config/users/**`).
- Playback states, favorites, collections, playlists, devices, Live TV timers, and plugin settings (`plugins/configurations/*.xml`).

## What is never included

- `metadata/`, including NFO files, artwork, posters, backdrops, and downloaded subtitles.
- `cache/`, `logs/`, and `transcoding-temp/`.
- Media libraries, Live TV recordings (`data/livetv/recordings`), and camera uploads.
- Historical backups kept below the Data Folder (`backup/`).
- Desktop-user profile content mistakenly stored below the Data Folder, such as `.local/`, `.config/`, `.cache/`, `Desktop/`, `Downloads/`, and `.bashrc`.
- Plugin binaries and symbols (`.dll`, `.exe`, `.pdb`, `.so`, `.dylib`).
- Symbolic links and junctions, including those pointing outside the Data Folder.
- The plugin's own history (`plugins/EmbyBackup/`) and temporary files.

The exclusions are documented inside every ZIP in `manifest.json`.

## Installation

1. Copy `EmbyBackup.dll` **directly** into Emby's plugin directory, without a subdirectory:

   | Platform | Path |
   |---|---|
   | Linux package install | `/var/lib/emby/plugins/` |
   | Manual Linux install | `<programdata>/plugins/` |
   | Official Docker image | `/config/plugins/` inside the container |
   | Windows | `%APPDATA%\Emby-Server\programdata\plugins\` |
   | Synology/QNAP | `<volume>/EmbyServer/programdata/plugins/` |

   Emby 4.9 does not discover the plugin if the DLL is in a subdirectory.

2. Restart Emby. The plugin is shown as **Emby Backup** in *Dashboard → Plugins*.
3. The **Emby Backup** task is available in *Dashboard → Scheduled Tasks* and follows the plugin schedule.

No additional dependencies are required. The plugin uses Emby's loaded `MediaBrowser.*` assemblies and the system SQLite library.

## Configuration

Open *Dashboard → Plugins → Emby Backup*.

| Setting | Description |
|---|---|
| **Enable Emby Backup** | Master switch. When disabled, no new backup, restore, test, or verification can start; the side-menu shortcut is hidden after an Emby refresh. |
| **Destination type** | **Local folder**, **Network share (SMB)**, or **FTP / FTPS server**. |
| **Backup folder** | Absolute local path outside the Data Folder. |
| **Share path** | Windows UNC path such as `\\server\share\folder`, or a mounted path on Linux, Docker, or NAS. |
| **FTP server / port / remote folder** | Host, port (default 21), and remote directory. The directory is created if needed. |
| **Backups to keep** | Number of managed ZIP files retained; default: 7. |
| **Compression** | `Fastest`, `Optimal`, or `NoCompression`. |
| **Database busy timeout** | Seconds to wait if Emby holds a database lock. |
| **Automatic backups** | Every day, every other day, once a week, or once a month. |
| **Run automatic backups** | Enables or disables the scheduled task; manual backups remain available. |
| **Verify archive after creating it** | Reopens the ZIP and checks all SHA-256 values. |
| **Protect new backups with a password** | Encrypts new manual and scheduled archives with AES-256 (WinZip AE-2). |
| **Test destination** | Saves settings and verifies that the destination exists and is writable without creating a backup. |

Examples:

- Linux: `/mnt/backups/emby`
- Windows: `D:\Backups\Emby`
- SMB: `\\nas\backups\emby` on Windows, or `/mnt/nas/backups` when mounted on Linux
- Docker: mount a volume such as `-v /host/backups:/backups`, then use `/backups/emby`
- FTP: `ftp.example.com`, port `21`, folder `/emby`

The destination cannot be inside Emby's Data Folder. Each manual run saves the current settings before starting.

### Scheduling

Automatic jobs use the server's **local time zone**.

| Period | Behaviour |
|---|---|
| **Every day** | One automatic backup every day at the selected time. |
| **Every other day** | One backup every two days, calculated from the last successful **automatic** backup. |
| **Once a week** | One backup on the selected day and time. |
| **Once a month** | One backup on the first day of each month at the selected time. |

Emby only exposes daily and weekly triggers, so the plugin uses a daily trigger and evaluates the chosen period at runtime. Manual backups do not delay automatic ones.

### SMB destinations

- **Windows**: enter a UNC path. Optional username, password, and domain are used to open an SMB session before writing.
- **Linux, Docker, and NAS**: the plugin does **not** mount SMB shares. Mount the share through the operating system and use its mount point. SMB credentials entered in the plugin are ignored on these systems.

### FTP / FTPS destinations

- **Use FTPS (explicit TLS)** enables `AUTH TLS`. Without FTPS, the connection and credentials are not encrypted.
- Enable acceptance of a self-signed certificate only for a server you control.
- ZIPs are uploaded first as `.partial`, then renamed to the final name, followed by the `.sha256` sidecar.
- FTP retention deletes only archives it can prove are owned by this plugin. A valid `.sha256` sidecar is the ownership marker.
- FTP verification downloads the ZIP to a temporary server folder, checks it, then removes the temporary file.

### Credentials and encryption

Network, FTP, and backup passwords are stored **in plain text** in `plugins/configurations/EmbyBackup.xml`. Use a dedicated account limited to the backup directory and do not reuse an administrator password.

Password-protected backups use **AES-256** in WinZip AE-2 format and work with 7-Zip, WinRAR, and WinZip. The password is not kept inside the ZIP; an encrypted archive cannot be recovered without it.

## Backup workflow

**Run backup now** starts a background job. The page displays progress, current message, history, destination files, and a **Cancel** button. Only one operation may run at a time: manual backup, scheduled backup, or restore.

Each successful destination receives:

```text
emby-backup-20261028-112343Z.zip
emby-backup-20261028-112343Z.zip.sha256
```

`manifest.json` records the format and plugin versions, Emby version, operating system, creation time, source path, compression, databases and their `quick_check` result, SHA-256 file inventory, exclusions, and warnings. The ZIP has no enclosing root directory: its content maps directly to the Data Folder root.

The plugin removes only its own recognized archives during retention. It never deletes unrelated files. Stale `.partial` files older than 24 hours are also removed.

## Verification

Verification is available after creating a backup and from the history/file tables. It checks:

- The ZIP can be opened and has a valid manifest.
- Every inventory entry exists and has its expected SHA-256.
- No entries exist outside the manifest inventory.
- Archive paths cannot escape the restore directory.
- The `.sha256` sidecar matches the ZIP.

For encrypted archives, verification requests the password and reports an incorrect password clearly.

```bash
sha256sum -c emby-backup-20261028-112343Z.zip.sha256
unzip -p emby-backup-20261028-112343Z.zip manifest.json | head
```

Use `7z x -p<password>` for encrypted archives if preferred.

## Restore

### From the plugin page

Choose an archive from **Restore from a backup**, then select **Restore selected backup…**:

1. Enter the password when the archive is encrypted.
2. Review the archive summary and accept **two confirmations**.
3. The plugin downloads, extracts, and verifies every SHA-256 in a staging directory before changing the Data Folder.
4. Files and databases are applied through the SQLite Online Backup API. Restart Emby when prompted to reload restored settings and caches.

The current `plugins/configurations/EmbyBackup.xml` is retained so the configured destination and credentials remain available. Files created after the backup are not deleted; only files included in the archive are replaced.

### Manual recovery

As a last resort, stop Emby, make a safety copy of the current Data Folder, verify the archive, and extract it directly into the Data Folder root:

```bash
cd /var/lib/emby
unzip /mnt/backups/emby/emby-backup-20261028-112343Z.zip
sudo chown -R emby:emby /var/lib/emby
```

Start Emby and review users, libraries, paths, and scheduled tasks. Plugin binaries are not part of the ZIP and must already be installed.

## Administrative API

All routes require an administrator token.

| Method | Route |
|---|---|
| `GET` | `/EmbyBackup/Status` |
| `GET` | `/EmbyBackup/History` |
| `GET` | `/EmbyBackup/Files` |
| `POST` | `/EmbyBackup/Run` |
| `POST` | `/EmbyBackup/Cancel` |
| `POST` | `/EmbyBackup/TestDestination` |
| `POST` | `/EmbyBackup/Verify` |
| `POST` | `/EmbyBackup/RestoreCheck` |
| `POST` | `/EmbyBackup/Restore` |
| `GET` | `/EmbyBackup/RestoreStatus` |

## Build and test

Requires the .NET SDK (tested with .NET 8) and NuGet access for `MediaBrowser.Server.Core 4.9.1.90`.

```bash
dotnet build -c Release src/EmbyBackup/EmbyBackup.csproj
dotnet run -c Release --project tests/EmbyBackup.Tests
```

The DLL is written to `src/EmbyBackup/bin/Release/netstandard2.0/EmbyBackup.dll`.

## Known limitations

- Only one backup or restore operation can run at once.
- Emby does not make up missed daily or weekly triggers while the server is off. Every-other-day and monthly schedules are evaluated during the next daily run.
- A database that remains locked beyond the configured timeout fails the backup rather than producing an inconsistent archive.
- Symlinks are skipped and recorded as warnings in the manifest.
- On Linux, Docker, and NAS, SMB mounts are managed by the administrator, not the plugin.
- FTP is synchronous and has no resume support.
- Passwords remain plain text in the plugin configuration, and encrypted archives cannot be recovered without their password.
- Restore replaces only files in the archive, keeps newer files, and requires an Emby restart.

Plugin GUID: `5a3c44fa-8c66-4f07-b75e-4a8c6123852f`.

## License

MIT. See [LICENSE](LICENSE).
