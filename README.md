# MSFS Backup Tool

MSFS Backup Tool saves your Microsoft Flight Simulator add-on folders before you reinstall the sim
and puts them back afterwards, so you don't have to reinstall every add-on.

It works with these folders:

| Sim       | Folders                                                               |
|-----------|-----------------------------------------------------------------------|
| MSFS 2024 | `Community`, `Community2024`, `WASM\MSFS2020`, `WASM\MSFS2024`        |
| MSFS 2020 | `Community`, WASM data (the `work` folder inside each package folder) |

The tool finds the Microsoft Store / Xbox app and Steam versions on its own. If you moved your
Community folder, it reads the new location from `InstalledPackagesPath` in `UserCfg.opt`. You can
also type or browse to any folder yourself.

Only the folders above are backed up. Marketplace and other official content, which the sim
downloads again after a reinstall, is not included. Neither are your sim settings.

## Requirements

- Windows 10 or 11, 64-bit
- MSFS 2020 or MSFS 2024, from the Microsoft Store / Xbox app or Steam
- Enough free space for the backup (the tool checks before it starts)

## Download and run

Unzip anywhere and run `MSFSBackupTool.exe`. You don't need to install anything, because .NET is
built in. Close the sim first: the tool won't run while `FlightSimulator.exe` or
`FlightSimulator2024.exe` is open.

The exe isn't code-signed, so Windows SmartScreen may show "Windows protected your PC" the first
time you run it. Click **More info**, then **Run anyway**.

## Reinstalling the sim

1. On the Back up / Move tab, tick the folders, choose where to save, and click **Back up (copy)**
   (or **Move out**, see below).
2. Uninstall and reinstall the sim. Start it once so it creates its folders again, then quit.
3. On the Restore tab, pick the `MSFS Backup <date>` folder. The tool matches each folder to the new
   install, and you can change any destination. Click **Restore**.

Save the backup somewhere the uninstall won't touch, such as another drive or a folder outside the
sim's install and data folders.

## Back up or move out?

Both buttons on the Back up / Move tab copy your folders the same way. The difference is what
happens to the originals afterwards.

| | Back up (copy) | Move out |
|---|---|---|
| Copies the folders to the backup location | Yes | Yes |
| Checks every copied file | Yes | Yes |
| Changes the sim folders | No. Everything stays where it is. | Yes. After every copy passes the check, the files and links are deleted from the sim folders. |
| Space needed | The full size of the folders | None extra on the same drive (the folders are renamed into the backup). The full size on another drive. |
| Undo | Delete the backup folder | Restore from the backup |

Use Back up in most cases. Your add-ons keep working while you have a safe copy, and you can
restore it after the reinstall.

Use Move out when you want the sim folders empty, for example so the uninstaller or the
reinstall can't touch your add-ons, or to move them off the sim's drive to free up space. Some
details:

- If any copied file fails the check, Move out stops and deletes nothing.
- It empties the folders you ticked but leaves the folders themselves in place.
- For an add-on link, it deletes the link in the Community folder. The add-on the link points to
  stays where it is.
- It only removes what was there when it started. Anything added during the move is left alone.

## Add-on links

Many add-ons and add-on managers (Addons Linker, vendor installers and so on) put a junction or
symbolic link in the Community folder that points to where the add-on is stored. The backup
records each link's name, type and target and recreates the link on restore. It doesn't copy the
add-on the link points to. That folder is normally outside the sim's folders, so a reinstall
doesn't remove it.

The tool never follows a link when it copies or deletes, so nothing it does can reach the add-on
behind a link.

## What's in a backup

Each backup is a folder named `MSFS Backup <date>`. Your files are inside it in
`<sim>-<store>\<folder>`. It also has a `manifest.json`, which lists the folders and the link
definitions, and a `backup.log`. The files are plain copies you can browse in Explorer.

## Restoring

If a file or link already exists in the destination, the tool asks once whether to overwrite it with
the backup, skip it, or keep whichever is newer. Your answer applies to the whole restore.

Junctions don't need any special rights. Symbolic links need administrator rights unless Windows
Developer Mode is on. When a restore includes symbolic links that need them, you'll see one
administrator prompt.

If the folder a link points to no longer exists (say a drive letter changed), the tool lists those
links after the restore. You can browse to the add-on's new location or skip the link.

Tick "Move files back instead of copying" to empty the backup into the sim folders. On the same
drive this takes seconds. An add-on folder that already exists in the destination is copied instead,
following your overwrite/skip/newer choice, and stays in the backup.

## Command line

`msfsbackup_cli.exe` is in the same zip and does the same work from a script:

```
msfsbackup_cli list
msfsbackup_cli backup  --dest "D:\MSFS Backups"
msfsbackup_cli move    --dest "D:\MSFS Backups" --folders Community,Community2024 --yes
msfsbackup_cli info    --from "D:\MSFS Backups\MSFS Backup 2026-10-12 1430"
msfsbackup_cli restore --from "D:\MSFS Backups\MSFS Backup 2026-10-12 1430" --on-conflict skip --elevate
```

`backup` leaves the sim folders as they are. `move` deletes the originals after the copy is checked,
like Move out in the app, and asks first unless you pass `--yes`.

`--folders` takes a folder kind (`Community`, `Community2024`, `WasmMsfs2020`, `WasmMsfs2024`,
`WorkFolders`), a sim/folder key (`MSFS2024/Community`) or a full id from `list`
(`MSFS2024-Steam/Community`). `--target MSFS2024/Community=E:\Community` restores a folder to a
different path. `--elevate` shows the administrator prompt for symbolic links without asking first.
Run `msfsbackup_cli help` for all options.

Exit codes: `0` ok, `1` unexpected error, `2` error, `3` cancelled, `4` finished with problems
(the output and the log list them).

## Logs

Logs are saved in `%LOCALAPPDATA%\MSFS Backup Tool\Logs`. Each backup folder also has its own
`backup.log`. If something goes wrong, the log says which file or link failed and why.

## Issues and updates

Download the latest version from the
[Releases page](https://github.com/r3volol/MSFS-Backup-Tool/releases). To report a problem, open an
issue on the [Issues page](https://github.com/r3volol/MSFS-Backup-Tool/issues) and attach the log
from the operation that failed.

## Building

You need the .NET 8 SDK. Run `publish.ps1`. It creates `dist\` and `MSFSBackupTool-<version>.zip`.

## License

MIT. See [LICENSE](LICENSE).

## Disclaimer

MSFS Backup Tool is an independent project. It is not made by, affiliated with or endorsed by
Microsoft, Asobo Studio or Valve. Microsoft Flight Simulator is a trademark of Microsoft.

The tool checks every copy before Move out deletes anything, but you use it at your own risk.
Keep the backup until you've confirmed your add-ons work after the restore.
