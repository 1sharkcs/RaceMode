# Race Mode

A small Windows app for iRacing. One switch gets your PC ready to race.

## What it does

- Closes the apps you list (browsers, launchers, anything you don't want running during a race). Setup and the Close list suggest heavy apps it finds on your PC, with how much memory each uses; nothing is ticked for you. iRacing, your Launch apps and Windows itself are never closed, and neither are your streaming apps while Streaming is on
- Starts your race apps, like SimHub and Crew Chief
- Puts iRacing first, so Windows gives it the processor ahead of your other apps
- Keeps iRacing on your racing monitor, as a borderless window that fills it
- Sets Race or Photo graphics, and only while iRacing is closed
- Logs FPS, temperatures and load, so you can look back at a race (GPU readings need an NVIDIA card; CPU temperature needs HWiNFO)
- Shows OBS stream stats while you stream (optional)
- Stays in the system tray (next to the clock) when you close its window, so it's always one right-click away (Quit is in that menu)

It never changes iRacing while iRacing is running, and it leaves your FPS cap alone.

## The window

- **Overview** has the switch, the live numbers, 60-second charts, a checklist of what Race Mode did, and your last session.
- The menu down the left opens Launch, Close, Stream, Graphics, Settings and Logs (Ctrl+1 to Ctrl+7). The switch stays under the menu on every page. Make the window narrow and the menu shrinks to icons.
- From 1280 pixels wide, the charts stay beside whichever page is open.
- The Small panel button in the title bar shrinks the window to just the switch and the numbers, for a corner of a side monitor. Click it again to go back.

## Install

Open **Command Prompt** (not PowerShell), paste this and press Enter:

```
curl -fLo "%TEMP%\racemode-install.cmd" https://github.com/1sharkcs/RaceMode/releases/latest/download/install.cmd && "%TEMP%\racemode-install.cmd"
```

The installer:

1. Checks your PC has what Race Mode needs: Windows 10 or 11 on an Intel or AMD (x64) PC, with .NET Framework 4.8. Almost every Windows 10 and 11 PC already has .NET 4.8. If yours doesn't, it asks whether to download Microsoft's installer (116 MB, checked against its known fingerprint before it runs); Windows asks for permission, and you may need to restart and run the command again.
2. Downloads Race Mode and checks the download against its published checksum.
3. Opens Race Mode's own setup, which installs it in your user folder. No admin rights are needed.
4. Adds it to the Start menu, and to Settings > Apps so you can uninstall it there.

Setup then asks a few short questions, and every one can be skipped:

- your racing monitor;
- which race apps to start;
- which apps to close (it looks at what's open and installed, and suggests the heavy ones);
- whether you stream.

It can also install HWiNFO for CPU temperature, but only if you click Install. Windows may ask for permission, and installing accepts HWiNFO's own license.

Running the same command again is a safe repair: it reinstalls the latest normal release and keeps your settings. (If you have a test version, this swaps it for the latest normal release.)

## Privacy

Race Mode reads the list of running apps, the apps in Settings > Apps and what starts with Windows (the Run keys and Startup folders) to suggest apps to close. That is only used on this PC and is never sent anywhere. The only internet access is the update check to api.github.com and github.com (you can switch it off in Settings), and HWiNFO's installer if you click Install. Logs stay on this PC, and Copy Summary copies only the session summary text.

## Where things live

- The app: `%LOCALAPPDATA%\RaceMode`
- Your settings, graphics and race logs: `%LOCALAPPDATA%\RaceMode\data`
- Setup's record of what it did: `%LOCALAPPDATA%\RaceMode\data\setup.log`

## Windows warnings

Race Mode isn't code-signed, so Windows SmartScreen or your antivirus may warn you the first time. To check a download yourself, compare its SHA-256 with the `RaceMode.zip.sha256` file on the release:

```
certutil -hashfile RaceMode.zip SHA256
```

## Updates

Race Mode checks for a new version every 12 hours while it runs. It shows what it found in Settings, under Updates. Nothing installs until you click Update, and only while Race Mode is off and iRacing is closed. Turn on **Try test versions** to get the next version early, while it's still being tested (a release candidate).

## Uninstall

Quit Race Mode (right-click its icon in the system tray (next to the clock), then Quit). Then go to **Settings > Apps > Installed apps > Race Mode > Uninstall**. It asks whether to keep your settings and logs. Your iRacing settings are left as they are. Race Mode's backups of iRacing's settings file stay in `Documents\iRacing` (their names start with `rendererDX11Monitor.ini.racemode`); the `.first.bak` one is your original. If Race Mode switched on OBS's remote control, that stays on; uninstall shows how to switch it off.

## Building it yourself

Run `build.cmd`. It uses the C# compiler that comes with Windows (.NET Framework 4.8), so there's nothing to install. See [docs/RELEASING.md](docs/RELEASING.md) for how releases are made.

This code is published to read, not to reuse: there's no license, so all rights are reserved. You're welcome to install and use the app.
