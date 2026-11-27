# App Store

**Install, update and remove apps on Windows Mobile and Windows CE handhelds,
straight from GitHub releases.**

App Store is built for the Zebra/Motorola MC55A and similar enterprise devices.
It reads a catalog of apps from a GitHub repository and downloads each app's
latest release. It installs apps where they survive a clean boot and keeps them
up to date, itself included.

These devices' built-in TLS stops at TLS 1.0, which GitHub refuses. App Store
uses [CeHttps](https://github.com/oseart55/CeHttps) (TLS 1.3 / 1.2) for every
connection, so it works on devices that can't otherwise reach GitHub at all.

> **Tested on:** Zebra MC55A (Windows Embedded Handheld 6.5) and a Zebra MC55
> with .NET CF 2.0 (installed with AppStoreSetup).
>
> **Should work on:** other ARM devices running Windows Mobile 5, 6, 6.1 or 6.5.
> Not tested on them yet.

---

## Contents

- [Features](#features)
- [Installing on a device](#installing-on-a-device)
- [Using App Store](#using-app-store)
- [The catalog](#the-catalog)
- [Publishing an app](#publishing-an-app)
- [How installs, updates and removal work](#how-installs-updates-and-removal-work)
- [Settings (store.cfg)](#settings-storecfg)
- [Troubleshooting](#troubleshooting)
- [Limitations, and why](#limitations-and-why)
- [Building](#building)
- [Licence](#licence)

---

## Features

- **A catalog you control.**
  - One `catalog.json` file in a GitHub repository lists the apps.
  - Adding an app means adding an entry and pushing; nothing changes on the
    devices.
- **Installs from GitHub releases.**
  - App Store uses each app's latest release, ignoring drafts and pre-releases.
  - Release files can be a `.zip`, which App Store unpacks itself, or loose
    files.
  - Every download is checked against its expected size, and against GitHub's
    SHA-256 checksum when GitHub provides one.
- **Updates.**
  - Each app shows its installed and latest versions; `*` marks an update.
  - App Store reads each exe's own version number, so it doesn't offer an update
    to an app that has already updated itself (like Book Scout).
- **Updates itself.** Install App Store from its own catalog entry, and it
  replaces itself and restarts when a new version comes out.
- **Survives a clean boot.**
  - Apps go into `\Application\<folder>`. On Zebra/Motorola devices that folder
    survives a clean boot; elsewhere, `\Program Files` is used.
  - A clean boot wipes the Start menu, so App Store recreates any missing
    shortcuts every time it starts.
- **Safe updates and removal.**
  - App Store records exactly which files it installed for each app.
  - Updates replace those files and remove ones a new version dropped.
  - Settings files an app marks as "keep" are never overwritten once they exist.
  - Uninstall removes only what App Store installed, so your settings and data
    stay.
- **Setup steps.** An app can run its own install step after installing (and
  before uninstalling). For example, ClockSync's `/install` registers it to run
  at boot.
- **Start menu shortcuts**, with any name the catalog gives.
- **Sets the clock.** Certificate checks need the right date, and new or reset
  devices usually have the wrong one. App Store sets the clock over NTP on start
  and on Refresh, and reloads its certificates afterwards.
- **One file.** `AppStore.exe` carries its TLS library and root certificates
  and unpacks them on first start.
- **One-file setup for older devices.** `AppStoreSetup.exe` installs .NET CF 3.5
  if needed, ClockSync and App Store (see below).

---

## Installing on a device

### Devices with .NET Compact Framework 3.5 (e.g. Windows Embedded Handheld 6.5)

Copy `AppStore.exe` anywhere on the device (an SD card is fine) and run it. To
keep a permanent copy that updates itself, select **App Store** in the list and
choose **Install**: it goes into `\Application\AppStore`.

Or use `AppStoreSetup.exe` (below), which works on every device.

### Devices with .NET CF 2.0 (many Windows Mobile 5 / 6.0 / 6.1 devices)

App Store needs CF 3.5. On a CF 2.0 device, opening `AppStore.exe` gives *"This
application requires a newer version of the Microsoft .NET Compact Framework"*.
To check what a device has, run `\Windows\cgacutil.exe`.

Use **`AppStoreSetup.exe`**. It's a single native exe, about 3.7 MB, that runs
without .NET. It contains everything it installs:

1. **ClockSync.** It sets the clock now, at every boot and whenever the network
   connects, so secure connections work from the start.
2. **.NET Compact Framework 3.5**, if it isn't installed already (Microsoft's
   installer, built in).
3. **App Store**, in `\Application\AppStore`, with a Start menu shortcut.

Then it starts App Store.

**To install:**

1. Connect the device to Wi-Fi, so ClockSync can set the clock straight away.
2. Copy `AppStoreSetup.exe` to the device, by SD card or USB cable. On an SD
   card, eject it safely on your PC before removing it.
3. Check the copy: in File Explorer, tap and hold the file, choose
   **Properties**, and make sure it's about **3.5 MB**, not 0 bytes.
4. Tap `AppStoreSetup.exe`. It lists what it will install. Tap **OK**.
5. Tap **OK** when ClockSync says it's installed.
6. Wait 1–2 minutes while CF 3.5 installs. The screen may not change during this
   time; that's normal.
7. App Store opens. It's also under **Start → Programs → App Store**.

You only need the setup once per device: after that, App Store updates itself.
Things that are already installed are left alone, so the setup is safe to run
again. `AppStoreSetup.exe /quiet` skips the questions but still reports errors.

---

## Using App Store

| | |
|---|---|
| **Up / Down** | Pick an app. The text below the list shows its description, folder and status. |
| **Right softkey / Enter** | The obvious action: **Install**, **Update** (`*` in the list) or **Open**. |
| **Menu → Refresh** | Reload the catalog and check GitHub for new versions. |
| **Menu → Install / Update / Reinstall** | Install the latest release; Reinstall repairs an app. |
| **Menu → Uninstall** | Remove an app. Your settings and data are kept. |
| **Menu → Open** | Start the app. |
| **Menu → Release notes** | What's new in the latest release. |
| **Menu → Show log** | The last 40 log lines (`AppStore-log.txt`, next to `AppStore.exe`). |
| **Menu → About** | Version, catalog, install folder. |

Apps that are installed but no longer in the catalog stay in the list, so you
can still uninstall them.

---

## The catalog

The catalog is a `catalog.json` file on the default branch of a GitHub
repository. By default that's `oseart55/MC55A-Apps`; to use another one, see
[store.cfg](#settings-storecfg). Each app needs a `name` and a `repo`, and
everything else is optional:

```json
{
  "name": "MC55A Apps",
  "apps": [
    {
      "id": "clocksync",
      "name": "ClockSync",
      "repo": "oseart55/ClockSync",
      "description": "Sets the clock at every boot and whenever the network connects.",
      "folder": "ClockSync",
      "exe": "ClockSync.exe",
      "shortcut": "Sync Clock",
      "assets": ["ClockSync.exe"],
      "install": "/install",
      "uninstall": "/uninstall",
      "keep": ["ClockSync.ini"]
    }
  ]
}
```

| Field | Default | Meaning |
|---|---|---|
| `name` | *(required)* | Shown in the list. |
| `repo` | *(required)* | `owner/name` of the GitHub repository whose releases hold the app. |
| `id` | the repo | A stable id. It's what links an installed app to its catalog entry, so don't change it later. |
| `description` | | Shown below the list. |
| `folder` | the name without spaces | Install folder name, under `\Application` (or `\Program Files`). |
| `exe` | the first `.exe` | What **Open** and the shortcut start, and where the version number is read. |
| `shortcut` | the name | Start menu shortcut name, or `false` for none. |
| `assets` | see below | Which release files to install, by name; `*` and `?` work. |
| `install` | | Arguments to run the exe with after every install or update. |
| `uninstall` | | Arguments to run the exe with before uninstalling. |
| `keep` | | Files never overwritten once they exist (wildcards work). Use for settings the user edits. |

Folder, exe and shortcut names must be plain names: no `\`, `/`, `..` or
wildcards. App Store skips entries that break this, and entries with an invalid
`repo`.

### Which release files get installed

1. If `assets` is set: the release files matching it.
2. Otherwise, if the release has exactly one `.zip`: that zip, unpacked. A zip
   whose files all sit in one top folder is unpacked without that folder.
3. Otherwise: every file attached to the release.

A release with several `.zip` files and no `assets` list is refused, rather
than guessing.

---

## Publishing an app

1. Create a GitHub repository for it, and add at least one commit: GitHub can't
   make a release in an empty repository.
2. Publish a release with the app's files attached. The tag should be a version
   like `v1.2.0`, so App Store can compare versions.
   ```
   gh release create v1.2.0 MyApp.exe --repo you/MyApp --title v1.2.0 --notes "What's new"
   ```
3. Add the app to `catalog.json` and push. Devices see it on their next
   Refresh.

For updates, publish a new release with a higher version. Also bump the exe's
own version number (`AssemblyVersion` for .NET apps), since App Store reads it.

---

## How installs, updates and removal work

- **Downloads and checks.** Release files are downloaded into a staging folder
  next to `AppStore.exe` and checked (size, SHA-256), and zips are unpacked
  there. Only then is anything copied into the app's folder.
- **The manifest.** App Store writes `appstore.inf` into the app's folder. It
  lists the app's id, version, exe, shortcut and every file App Store installed.
  Keeping it there rather than in the registry means App Store still knows what
  it installed after a clean boot.
- **Updates.** An update copies the new files over the old ones. It skips `keep`
  files that already exist, and removes files the old version had but the new
  one doesn't. Files nobody installed (your data, logs, settings) are never
  touched. If the app is running, App Store asks you to close it first.
- **Uninstall.** It runs the app's `uninstall` step, then removes the listed
  files and the shortcut. The folder is deleted only if nothing else is left in
  it.
- **App Store updating itself.** A running exe can't be replaced. So App Store
  starts the new copy from the staging folder with `/apply` and exits. The new
  copy waits for the old one to close, copies itself into place, and restarts.
- **Version check.** An app counts as up to date when its exe's version number
  is at least the release's. Otherwise the version recorded at install is
  compared with the release tag.

---

## Settings (store.cfg)

Optional. Put a `store.cfg` text file next to `AppStore.exe`:

```
catalog=oseart55/MC55A-Apps            catalog.json on that repo's default branch
catalog=https://example.com/apps.json  ...or any HTTPS URL
token=ghp_...                          optional GitHub token
```

A token lets the store use private repositories, and raises GitHub's limit from
60 to 5,000 requests an hour. It's only ever sent to `api.github.com` and
`raw.githubusercontent.com`, never to the download servers that releases
redirect to. **Never put a token in a release or a public repository.**

---

## Troubleshooting

| Problem | Fix |
|---|---|
| *"This application requires a newer version of the Microsoft .NET Compact Framework"* | The device has CF 2.0. Use `AppStoreSetup.exe`. |
| *"The file cannot be opened. Either it is not signed with a trusted certificate, or one of its components cannot be found."* | Almost always an incomplete copy. Delete it, copy it again, and check its size. |
| *"The date on this device is wrong…"* / certificate errors | Connect to Wi-Fi and use **Menu → Refresh**, which sets the clock. Or set the date and time in Settings, then Refresh. |
| Clock still not set automatically | **Menu → Show log** and look for the line starting with `Clock`: it says which time server failed and why. Some networks block internet time (UDP port 123). |
| *"GitHub's limit of 60 requests an hour was reached"* | Wait for the time shown, or add a `token` to `store.cfg`. |
| *"… is running. Close it, then try again."* | Close the app and try again. |
| An app shows `?` under Latest | Its repository has no release yet, or GitHub couldn't be reached. The text below the list says which. |
| Setup: *"CF 3.5 … didn't finish"* | Soft-reset and run the setup again. If it still fails, install `NETCFv35.wm.armv4i.cab` by hand, then run the setup again. |
| Setup: *"Not enough free storage"* | Free about 13 MB and run it again. |

---

## Limitations, and why

| Limitation | Why |
|---|---|
| **GitHub releases only** | Releases give versioned, checksummed downloads and an API that's free to use. That's all an app store needs, with no server to run. |
| **One catalog at a time** | Simpler to manage and to trust. Point `store.cfg` at a different catalog, or merge catalogs into one file. |
| **No `.cab` packages** | App Store installs plain files. Windows' own `.cab` installer can't be checked or undone the same way. Setup is the one exception: it uses `.cab` for CF 3.5. |
| **Zip: deflate and stored only; no zip64, encryption or zips over 16 MB** | That's what app archives use, and the Compact Framework has no zip support, so App Store has its own small reader. The 16 MB cap protects device memory. |
| **Files that would land outside the app's folder are refused** | A malicious zip or catalog could otherwise overwrite system files. |
| **Requires .NET CF 3.5** | CeHttps and App Store are built for it. `AppStoreSetup.exe` installs it on older devices. |
| **No background update checks** | App Store checks when you open it or tap Refresh. Polling would drain the battery and use up GitHub's request limit. |
| **App Store can't uninstall itself** | It's running. Delete `\Application\AppStore` in File Explorer instead. |
| **60 GitHub requests an hour without a token** | GitHub's limit for anonymous use. Each Refresh uses one request per app (the catalog download doesn't count). |
| **IPv4 only, no proxies** | Inherited from CeHttps. |

---

## Building

You need **Visual Studio 2008 Professional** and the **Windows Mobile 6
Professional SDK**. Newer versions of Visual Studio can't load Smart Device
projects.

Folder layout. App Store finds CeHttps and ClockSync next to it:

```
Scanner Apps\
  AppStore\       this project (AppStore.sln)
  CeHttps\        https://github.com/oseart55/CeHttps (cloned with --recursive)
  ClockSync\      ClockSync (build its Release first, for the setup)
```

Open `AppStore.sln`, pick the **Mixed Platforms** or **Windows Mobile 6
Professional SDK** platform, and run **Rebuild All** in **Release**. (Under
"Any CPU" the native projects are skipped.) The solution builds in this order:

| Project | Output |
|---|---|
| **CeHttpsNative** | `CeHttpsNative.dll`, embedded in App Store |
| **AppStore** | `AppStore\bin\Release\AppStore.exe` |
| **AppStoreSetup** | `Setup\Windows Mobile 6 Professional SDK (ARMV4I)\Release\AppStoreSetup.exe` |

- **App Store** embeds `CeHttpsNative.dll` and `cacert.pem` and unpacks them on
  first start (`Embedded.cs`), so it ships as one exe.
- **AppStoreSetup** embeds `AppStore.exe`, ClockSync's Release `ClockSync.exe`,
  and Microsoft's `Setup\redist\NETCFv35.wm.armv4i.cab`.
  - That `.cab` is copied from Visual Studio:
    `C:\Program Files (x86)\Microsoft.NET\SDK\CompactFramework\v3.5\WindowsCE\`.
  - Always use **Rebuild All**, so the setup picks up the current App Store.
  - When you bundle a new ClockSync, update `CLOCKSYNC_TAG` in
    `Setup\resource.h` to its release tag.

### Releasing

1. Bump `AssemblyVersion` in `AppStore\Properties\AssemblyInfo.cs`.
2. Rebuild All (Release).
3. Run `release.bat "What's new"`.

`release.bat` publishes `AppStore.exe` to `oseart55/AppStore` with the GitHub
CLI. Before publishing, it stops if:
- that version is already on GitHub;
- the exe wasn't rebuilt after the version bump;
- `CeHttpsNative.dll` is newer than the exe, which would mean the exe holds an
  old copy.

Don't attach `AppStoreSetup.exe` to App Store's releases. App Store's catalog
entry has no `assets` list, so devices would download the setup along with
every update. Share the setup separately, or add `"assets": ["AppStore.exe"]`
to App Store's catalog entry first.

---

## Licence

App Store is free software under the **GNU General Public License v3**, the
same as CeHttps, which it includes along with wolfSSL. See `LICENSE`.

`AppStoreSetup.exe` also contains Microsoft's .NET Compact Framework 3.5
redistributable, which has its own licence terms from Microsoft. Visual
Studio 2008 generally allows shipping it alongside your app. Check those terms
before selling or redistributing the setup. This is not legal advice.
