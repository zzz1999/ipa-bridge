<div align="center">
  <img src="src/IPABridge/Assets/IPA-Bridge.png" width="112" alt="IPA Bridge icon">
  <h1>IPA Bridge</h1>
  <p>A modern Windows GUI for ipatool: search and download App Store IPAs, inspect readable historical-version metadata, connect iOS devices, and install apps.</p>

  [![Build and release](https://github.com/zzz1999/ipa-bridge/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/zzz1999/ipa-bridge/actions/workflows/release.yml)
  ![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
  ![Architecture](https://img.shields.io/badge/architecture-x64-5C6BC0)
  ![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
  [![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](LICENSE)

  [Download an automatic build](https://github.com/zzz1999/ipa-bridge/releases) · [Architecture](docs/ARCHITECTURE.md) · [Third-party components](THIRD-PARTY-NOTICES.md) · [Report an issue](https://github.com/zzz1999/ipa-bridge/issues)
</div>

> [!IMPORTANT]
> IPA Bridge is an independent project. It is not affiliated with, sponsored by, or endorsed by Apple Inc. It does not defeat FairPlay or bypass Apple code signing, account licensing, device management, Trust This Computer, or any other security control.

## Overview

IPA Bridge combines the App Store capabilities of [`majd/ipatool`](https://github.com/majd/ipatool) with Windows iOS-device tooling in one WPF desktop application. Its original visual language is inspired by iOS 18, with soft materials, rounded cards, and a clear information hierarchy, but it does not copy Apple trademarks, icons, or product interfaces.

The project is currently an early preview intended for personal testing and development validation. Before production or large-scale use, run regression tests against the target Windows and iOS versions and a representative matrix of physical devices.

## Features

| Area | Current capability |
| --- | --- |
| App Store | Keep multiple Apple Account profiles, select the account used for each search and purchase, search up to 25 results in that account's App Store region, request a license for entitled apps, and download encrypted IPAs |
| Historical versions | Best-effort lookup of readable version numbers and release dates for opaque external version identifiers while preserving the real download identifier |
| IPA library | Scan only the top level of the selected directory for `*.ipa` files, display file information, and send a selected file to the device-installation page |
| Apple device support | Diagnose the Apple Devices app, Apple USB driver packages and versions, service registration, and the local transport port in separate layers; a successful `idevice_id` backend probe is required for operational readiness |
| One-click driver acquisition | Ask the official Microsoft Store product to install or update Apple Devices through WinGet, with a safe fallback to the official Store page if automation cannot complete |
| Device connection | Enumerate USB or Wi-Fi devices, read the device name, model, and iOS version, and initiate and verify trust pairing |
| IPA installation | Install an IPA on a selected paired device, retain recent low-level output, show progress when the backend reports a percentage, support stopping the wait, and enforce a 20-minute timeout |
| Tool management | Install verified official ipatool builds and a pinned `jkcoxson/idevice` release, or use a user-supplied `libimobiledevice` tool directory |
| Software updates | Query this repository's published GitHub Releases, ignore drafts and prereleases, require the `IPA-Bridge.exe` asset, and compare its release commit with the revision embedded in the running executable |
| Windows distribution | Produce a self-contained, single-file Windows x64 executable that does not require a separate .NET Runtime installation |

## Download and run

1. Open the project's [Releases](https://github.com/zzz1999/ipa-bridge/releases) page.
2. Download `IPA-Bridge.exe` and `IPA-Bridge.exe.sha256` from the newest automatic build.
3. Keep all project, .NET Runtime, WPF, .NET Library, and Windows SDK legal assets published beside the executable.
4. Optionally verify the SHA-256 hash in PowerShell.
5. Double-click `IPA-Bridge.exe` to run it.

```powershell
Get-FileHash .\IPA-Bridge.exe -Algorithm SHA256
Get-Content .\IPA-Bridge.exe.sha256
```

The two hash values must match exactly. The distributed executable is a self-contained, single-file Windows x64 build and does not require a separate .NET installation.

Each automatic Release also contains `IPA-BRIDGE-LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, the exact restored .NET Runtime pack license and third-party notices, the restored WPF package license, the .NET Library License, and a Windows SDK licensing notice for the embedded D3D compiler component.

Automatic builds are not currently signed with an Authenticode certificate, so Windows SmartScreen may identify the executable as coming from an unknown publisher. Download it only from this repository's Releases page and verify the SHA-256 hash before running it.

Automatic builds are published as normal Releases, not prereleases, but they are explicitly not marked as `Latest`. GitHub's Latest release shortcut may therefore omit them; use the complete [Releases list](https://github.com/zzz1999/ipa-bridge/releases). The GitHub Release asset is named `IPA-Bridge.exe`, while the original output from a local `dotnet publish` is named `IPA Bridge.exe`.

## System requirements

| Item | Requirement |
| --- | --- |
| Operating system | Target support baseline: Windows 10 22H2 or Windows 11, x64; the full physical-device matrix has not yet been covered |
| Apple device components | A configured device backend that can complete a real `idevice_id` probe against Apple Mobile Device transport; the Microsoft Store version of [Apple Devices](https://apps.microsoft.com/detail/9NP83LWLPZ9K) is recommended, while USB driver inventory remains a separate diagnostic |
| Apple accounts | One or more accounts that can use the App Store in their intended storefront regions |
| iOS device | An iPhone or iPad; USB connections require a cable that supports data transfer |
| Device state | The device must remain unlocked and Trust This Computer must be accepted when prompted |

## First-time setup

On the first launch, IPA Bridge creates a random 256-bit local settings key and asks Windows Data Protection to protect it for the current Windows user. The key is then used for authenticated encryption of IPA Bridge's saved settings. No setup prompt is required.

After the first launch, open the Settings page:

1. Select **Install / Update** in the official ipatool card. IPA Bridge downloads the architecture-specific official ipatool v2.3.1 archive and enables it only after verification against the matching SHA-256 value pinned in this source tree.
2. Select **Install Verified GitHub Release** in the iOS device tools card, or choose an existing complete `libimobiledevice` tool directory. A compatible directory must contain `idevice_id.exe`, `ideviceinfo.exe`, `idevicepair.exe`, and `ideviceinstaller.exe` together.
3. If Apple Devices is absent and the local Apple transport is unavailable, select **Install / Update via Microsoft Store** in the Apple device driver and service card. GitHub repair is offered only for the separate open-source device tools when Apple transport is already reachable.
4. If you use Apple Devices, open it once after installation. Then connect and unlock the device, return to IPA Bridge, and select **Check again**.
5. Select and save an IPA download directory. The default is `%USERPROFILE%\Downloads\IPA Bridge`.
6. Open the App Store page and add one or more Apple Accounts. Each profile is connected and stored independently.

### One-click Apple Devices installation request

IPA Bridge pins the request to official Microsoft Store product ID `9NP83LWLPZ9K`. It does not download Apple drivers from a third-party website or repackage them. The automatic request is equivalent to:

```powershell
winget install `
  --id 9NP83LWLPZ9K `
  --exact `
  --source msstore `
  --accept-source-agreements `
  --accept-package-agreements `
  --silent `
  --disable-interactivity
```

If WinGet is unavailable, Microsoft Store requires interactive authentication, an enterprise policy blocks installation, or the automatic request times out, IPA Bridge attempts to open the official Store page so the user can continue. Opening that page does not mean installation succeeded, and even a successful WinGet exit is not recorded as automatic success until Windows reports the Apple Devices package as registered for the current user. This feature does not elevate itself or bypass UAC, Store authentication, organizational policy, network or restart requirements, or the trust confirmation on the iOS device. Developer Mode is only handled by the user when the target IPA's signing or distribution method requires it; it is not a general prerequisite for installing Apple Devices.

## Usage

### Search for and download an app

1. Select **Add Apple Account**, then enter the account email and Apple password.
2. Select **Add & Sign In**. IPA Bridge creates a random 256-bit key for that profile automatically. If Apple requests two-factor authentication, a separate panel appears; enter the six-digit code and select **Verify & Continue**.
3. Repeat the first two steps for any additional accounts.
4. In **Account for search and purchase**, select the account whose App Store you want to use. On later launches, IPA Bridge automatically restores a valid isolated session with the encrypted generated key; **Check Session** remains available for an explicit retry or diagnostic check.
5. Enter an app name, search, and select the target app. Search results come from the App Store region Apple assigned to the selected account when it signed in.
6. Download the latest version directly, or select **Load version history** first when an older version is needed. The license request and download use the same selected account.
7. A completed download appears automatically in the IPA library.

IPA Bridge never asks a new user to invent or remember an ipatool vault passphrase. The application generates a different 256-bit local vault key for each profile, stores it only inside the AES-256-GCM encrypted settings envelope, and supplies it to ipatool through ConPTY when required. Apple Account passwords and two-factor codes remain memory-only and are cleared after success, cancellation, or leaving the App Store page.

Every profile receives a separate ipatool home below `%LOCALAPPDATA%\IPA Bridge\Accounts`, so its account record, cookie jar, and Apple-provided storefront cannot be mixed with another profile. IPA Bridge does not infer a country from the email address, IP address, or Windows region. Search returns no more than 25 results. During download, ipatool requests a license for the selected account, but paid apps, delisted apps, regional restrictions, missing account entitlement, or unavailable historical packages can still cause the operation to fail.

The **Software Update** card in Settings queries the repository's Releases API only when **Check for Updates** is selected. IPA Bridge chooses the newest normal published release that contains `IPA-Bridge.exe`, compares its target commit with the revision embedded by GitHub Actions, and opens only a validated `https://github.com/zzz1999/ipa-bridge/releases/...` page. It does not silently replace the running executable.

### Inspect historical-version metadata

`ipatool list-versions` returns external version identifiers such as `630253063`; the value is not the user-facing app version. IPA Bridge does not infer a version number from those digits. Instead, it invokes `get-version-metadata` to retrieve, on a best-effort basis:

- the upstream `displayVersion`, shown with a consistent `v` prefix such as `v7.18.1`; this value is not guaranteed to be three-part SemVer;
- the release date;
- the original external version identifier.

Metadata is cached only for the current application session, and no more than two versions are queried concurrently. A single lookup may fail because of insufficient entitlement, an unavailable historical package, a network problem, or an ipatool build that does not support the command. The original identifier remains available and selectable, and a failure does not block other versions. When no historical version is selected, IPA Bridge always downloads the latest version.

### Connect a device and install an IPA

1. Ensure a compatible Apple device service is running. With the recommended setup, open Apple Devices first, then connect the device with a data cable and unlock it.
2. On the Devices page, select **Refresh devices**.
3. If the device is not paired, select **Trust device**, then confirm Trust This Computer on the iPhone or iPad.
4. Choose an IPA from the IPA library, or select a local `.ipa` file on the Devices page.
5. Select the target device and start installation.

During installation, IPA Bridge displays output from the underlying device tool and keeps approximately the most recent 6,000 characters in the interface. Cancellation or timeout terminates the local device tool and stops waiting, but it does not guarantee rollback of an operation already submitted to the device; installation may still be finishing on the device.

## Driver and device status detection

IPA Bridge does not treat the mere installation of an app as proof that the device environment is usable. It checks these layers separately:

1. Whether Apple Devices is registered for the current Windows user, including the app version.
2. Whether the Windows Driver Store contains a known `appleusb.inf`, `usbaapl64.inf`, or `usbaapl.inf` package, including its `DriverVer` value.
3. Whether the Apple Mobile Device service is registered.
4. Whether a TCP connection can be established to the local endpoint at `127.0.0.1:27015`.
5. Whether the configured backend can complete a real `idevice_id` probe against Apple Mobile Device transport, even when no device is attached.
6. For an attached device, whether the backend can enumerate it and open a trusted Lockdown session.

These are layered signals. The presence of an INF does not prove that the driver is bound to the current device, and a reachable TCP port does not authenticate the remote process or validate the complete protocol. The driver inventory distinguishes **installed**, **not detected**, and **unavailable to read**. Insufficient permission to read the Driver Store, or a future Apple INF rename, does not disable a backend whose real `idevice_id` probe succeeds. Initial operational readiness requires that probe; use of an attached device additionally depends on successful enumeration and a trusted Lockdown session.

## Privacy and supply-chain security

- `%LOCALAPPDATA%\IPA Bridge\settings.secure.json` is a versioned AES-256-GCM envelope containing IPA Bridge's complete encrypted configuration: configured paths, Apple Account profile emails and IDs, generated per-profile local vault keys, the selected profile, and preferences.
- IPA Bridge creates a random 256-bit master key on first launch. Only the Windows Data Protection API `CurrentUser`-protected form is stored in `%LOCALAPPDATA%\IPA Bridge\master-key.v1`; the raw key is not written to disk.
- Every settings save uses a new 96-bit nonce and a 128-bit authentication tag. A copied, modified, truncated, or mismatched settings envelope is rejected instead of being silently replaced with defaults.
- An existing plaintext `%LOCALAPPDATA%\IPA Bridge\settings.json` from an earlier IPA Bridge build is migrated to the encrypted file. The encrypted result is reopened and verified before the legacy plaintext file is removed.
- Settings with an unsupported envelope version or unrecognized configuration fields are rejected and preserved instead of being rewritten by an older schema.
- If encrypted and legacy settings both exist with different values, IPA Bridge preserves both, reports a conflict, and blocks settings saves instead of guessing which file should win.
- Apple passwords and two-factor verification codes exist briefly in IPA Bridge and ipatool process memory, but they are not written to IPA Bridge settings, a process command line, or IPA Bridge's persistent logs.
- A new profile receives a random 256-bit local vault key. That generated key is persisted only inside the encrypted settings envelope, never shown to the user, and supplied to ipatool only through the local pseudo-console.
- Sensitive values are written to ipatool's terminal prompts through Windows ConPTY rather than passed as command-line arguments.
- Each Apple Account profile runs ipatool with a separate Windows home directory under `%LOCALAPPDATA%\IPA Bridge\Accounts\<profile-id>`. The isolated `.ipatool` directory contains ipatool's encrypted account record and its separate cookie jar.
- ipatool encrypts its account record with the generated per-profile vault key. Its cookie jar is not encrypted by IPA Bridge or by that key; it relies on the Windows user profile and inherited filesystem access controls.
- Removing a profile first moves its isolated session into a managed local quarantine. IPA Bridge commits the encrypted configuration before deleting that quarantine; after an interruption, startup recovery restores the session when the profile still exists or finishes deletion only when the profile is absent.
- ConPTY output is redacted against known sensitive values again before it leaves the execution layer.
- IPA Bridge does not enable `ipatool --verbose`, avoiding upstream detailed logs that may record authentication fields.
- Automatically downloaded ipatool and device tools must pass SHA-256 verification before they are enabled.
- A new tool version is written to a separate directory and becomes active only after validation; the version currently in use is not overwritten.
- Apple Devices acquisition always relies on Microsoft Store for validation and updates. IPA Bridge does not host an Apple installer.

After a successful login, ipatool communicates with Apple services and saves the Apple-provided storefront in that profile's isolated account record. IPA Bridge uses the selected profile for account checks, search, license acquisition, historical-version lookup, and download. It does not guess or override the storefront. Downloaded IPA files, installed tools, device pairing records, per-profile ipatool data, and files in user-selected folders are outside IPA Bridge's settings encryption.

Windows `CurrentUser` protection ties the master key to the Windows user profile that created it. It does not protect against malware, an administrator, memory inspection, or another process already running as that user. Losing the Windows profile or `master-key.v1` can make `settings.secure.json` unrecoverable; IPA Bridge does not generate a replacement key while encrypted settings still exist.

## IPA installation boundaries

ipatool retrieves encrypted App Store IPAs. IPA Bridge invokes the normal iOS installation service; it does not decrypt IPAs or bypass Apple security controls. A device may still reject installation when:

- the IPA signature or provisioning profile is invalid, expired, or does not include the target device;
- the App Store account entitlement does not match the target device state;
- the app version does not support the installed iOS version or device architecture;
- an enterprise, school, or parental-control policy restricts the device;
- the device is locked or has not trusted the computer, or the IPA's distribution method requires Developer Mode and it is not enabled.

IPA Bridge preserves and displays useful errors from the underlying tools whenever possible, but it cannot replace Apple's signing, authorization, or device-management processes.

## Local data directories

| Content | Default location |
| --- | --- |
| Encrypted settings | `%LOCALAPPDATA%\IPA Bridge\settings.secure.json` |
| Windows-protected settings key | `%LOCALAPPDATA%\IPA Bridge\master-key.v1` |
| Isolated ipatool account sessions | `%LOCALAPPDATA%\IPA Bridge\Accounts\<profile-id>\.ipatool` |
| Automatically installed tools | `%LOCALAPPDATA%\IPA Bridge\Tools` |
| Temporary files | `%LOCALAPPDATA%\IPA Bridge\Temporary` |
| IPA downloads | `%USERPROFILE%\Downloads\IPA Bridge` |

The tool and download directories can be replaced from Settings with paths the user has reviewed. Generated per-profile vault keys are written only inside the encrypted settings file; Apple passwords and verification codes are never persisted. A legacy plaintext `settings.json` may appear only until a previous installation has completed its verified one-time migration.

When upgrading from the earlier single-account configuration, the saved email becomes the first selected profile. IPA Bridge does not copy the old default-home `%USERPROFILE%\.ipatool` session into the new isolated profile, because copying an unverified account and storefront could associate the wrong session with the profile. Reconnect that profile once in the App Store page. The former `%USERPROFILE%\.ipatool` directory remains under the user's control and can be archived or removed after the new profile works.

Profiles created by the earlier manual-vault build migrate without deleting their isolated sessions. Because IPA Bridge never stored the old user-entered passphrase, those profiles show an explicit **Reset Session & Sign In** action. That action removes only the selected profile's old local ipatool sign-in, creates a generated vault key, and requires Apple sign-in again; downloaded IPA files are not removed.

## Troubleshooting

| Symptom | Suggested action |
| --- | --- |
| Apple Devices is not detected and no compatible device service is available | Select **Install / Update via Microsoft Store** in Settings, or open the [official Microsoft Store page](https://apps.microsoft.com/detail/9NP83LWLPZ9K) manually |
| The driver is installed but the device service is not running | Open Apple Devices once and reconnect the device; restart Windows and check again if necessary |
| Driver status is unavailable to read | The Driver Store could not be inspected; this does not prove the driver is missing, and IPA Bridge continues device discovery when the transport is working |
| The iPhone or iPad cannot be found | Try a data-capable cable or another USB port, keep the device unlocked, accept Trust This Computer, and refresh |
| The existing login cannot be checked | Reconnect the selected profile. IPA Bridge supplies its generated local key automatically; an older keyless profile uses the explicit **Reset Session & Sign In** upgrade action |
| An upgraded account profile is not connected | Reconnect it once. The saved email is migrated, but the earlier global `%USERPROFILE%\.ipatool` session is intentionally not imported into the profile's isolated directory |
| Search shows a different regional catalog than expected | Confirm the selected account and reconnect it if necessary. The region comes from the storefront Apple assigned to that account; IPA Bridge does not override it |
| A selected profile reports another account in its session | Reconnect the selected profile. IPA Bridge blocks search, purchase, version lookup, and download until the isolated session email matches the profile |
| Encrypted settings cannot be opened | Restore `settings.secure.json` and `master-key.v1` together from the same Windows user profile. If recovery is unnecessary, remove both files to start with new settings; the previous encrypted settings cannot then be recovered |
| Encrypted and legacy settings conflict | Back up both files, compare which configuration should be retained, then move the unwanted file out of `%LOCALAPPDATA%\IPA Bridge` and restart IPA Bridge |
| Version history shows only a number | Metadata for that version could not be resolved; the number is the original external version identifier and remains valid for selection and download |
| IPA installation is rejected | Inspect the low-level output on the Devices page and check signing, entitlement, provisioning, iOS compatibility, and device-management policy |
| SmartScreen reports an unknown publisher | Automatic builds are not currently code-signed; download only from this repository and verify the SHA-256 hash |

## Build from source

Build prerequisites are Windows x64, PowerShell, and the exact .NET SDK selected by [`global.json`](global.json), currently `8.0.423`.

The canonical [`scripts/Generate-Icon.ps1`](scripts/Generate-Icon.ps1) pipeline generates the shared 1024-pixel PNG, matching SVG, and multi-resolution ICO used by the application header, executable, taskbar, and README. Its verification mode detects generated-asset drift without changing files.

```powershell
git clone https://github.com/zzz1999/ipa-bridge.git
Set-Location .\ipa-bridge

dotnet restore .\IPABridge.sln
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Generate-Icon.ps1 -Verify
dotnet build .\IPABridge.sln -c Release --no-restore
dotnet run --project .\tests\IPABridge.SmokeTests\IPABridge.SmokeTests.csproj -c Release --no-build

dotnet publish .\src\IPABridge\IPABridge.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\artifacts\publish
```

The local publish command produces `artifacts\publish\IPA Bridge.exe`. The application itself has no third-party .NET NuGet package dependency; command-line tools are downloaded only after an explicit user action.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for more detail about component boundaries, authentication, historical-version resolution, and device backends.

## Automatic Releases

The build job in `.github/workflows/release.yml` runs on every push to `main` and when dispatched manually. It uses fixed operating-system runner labels, the exact SDK selected by `global.json`, and GitHub Actions pinned to verified full commit SHAs.

1. Download and checksum the pinned ipatool v2.3.1 contract binary, restore dependencies, verify formatting, build with warnings treated as errors, and run the smoke tests on a Windows runner.
2. Publish a self-contained, single-file Windows x64 executable.
3. Copy the exact 8.0.29 Runtime pack license and third-party notices plus the WPF pack license from the restored packages, then add the .NET Library License and Windows SDK licensing notice required by the Windows-native components.
4. Generate and independently verify `IPA-Bridge.exe.sha256`.
5. Create a new normal automatic Release with `prerelease=false` when the run's Git ref is `main`; a manual run from another branch retains only the build artifact and does not create a Release.
6. Only after the new Release is created successfully, delete older IPA Bridge automatic Releases and their tags.

Automatic cleanup requires both a tag matching `auto-build-<run_id>-<attempt>` and the IPA Bridge-specific marker in the Release body. It therefore does not delete manual Releases or tags outside the IPA Bridge automation. Automatic builds are full Releases rather than prereleases, but they are explicitly not marked as `Latest`, leaving room for independently selected releases in the future.

The workflow must be committed and pushed to GitHub before it can run. The repository or organization must also permit the workflow's `GITHUB_TOKEN` to use `contents: write` when creating and deleting Releases. If an immutable-release policy prevents Release or tag deletion, cleanup fails and the older automatic Release remains in the repository.

## Third-party projects and legal notice

- [`majd/ipatool`](https://github.com/majd/ipatool): App Store authentication, search, licensing, and IPA download; MIT License.
- [`jkcoxson/idevice`](https://github.com/jkcoxson/idevice): the Windows device-communication backend, pinned by default to `v0.1.65`; MIT License. Upstream still describes it as development/research-stage software.
- [`libimobiledevice`](https://github.com/libimobiledevice/libimobiledevice) and [`ideviceinstaller`](https://github.com/libimobiledevice/ideviceinstaller): optional, user-supplied compatible backends whose individual components use LGPL/GPL licenses.
- [Microsoft .NET Runtime](https://github.com/dotnet/runtime/tree/v8.0.29) and [WPF](https://github.com/dotnet/wpf/tree/v8.0.29): version `8.0.29` components embedded in the self-contained Windows executable. The workflow publishes the exact license/notices from the restored runtime packs plus the Windows-specific legal files stored under [`licenses/dotnet-8.0.29`](licenses/dotnet-8.0.29).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for exact versions, checksums, and distribution details. Apple, iPhone, iPad, iOS, iPadOS, and App Store are trademarks of Apple Inc.

## License

IPA Bridge is released under the [MIT License](LICENSE). Third-party components remain subject to their respective licenses as described above and in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
