# IPA Bridge architecture

## Application layers

- `Views`: WPF screens and the small amount of code needed for password and file pickers.
- `ViewModels`: navigation, user-visible state and asynchronous commands.
- `Services`: `ipatool`, device communication, configuration, local library scanning and verified tool installation.
- `Infrastructure`: process execution, ConPTY, commands, observable state and Windows backdrop handling.
- `Models`: immutable or simple data contracts used by the UI.

No third-party .NET package is required by the desktop application.

## Authentication flow

`ipatool` uses terminal-only password readers for the Apple password and keyring passphrase. Passing the corresponding command flags would expose values in the Windows process command line.

`ConPtyProcessRunner` therefore creates a Windows pseudo console, starts `ipatool` without sensitive command flags, watches for known terminal prompts and writes each secret through the pseudo-console input pipe. Returned output is redacted against every supplied secret before it leaves the runner.

If the account requires two-factor authentication and no code was supplied, the pseudo-console process is ended and the view model reveals the two-factor field. The user then retries; the code is supplied only when the terminal requests it.

## Historical version metadata

`ipatool list-versions` returns opaque external version identifiers. IPA Bridge keeps those identifiers as strings, then resolves each one with `get-version-metadata` to obtain the human-readable `displayVersion` and `releaseDate` fields. At most two metadata processes run concurrently because the upstream command performs partial IPA range requests for each version.

Successful metadata is cached for the application session. Individual failures remain selectable by their original external version identifier, so one unavailable historical package does not discard the rest of the list. Switching the selected app cancels the previous lookup queue.

## Tool supply chain

IPA Bridge pins the reviewed `ipatool` v2.3.0 packages and SHA-256 values in source. It:

1. Selects the embedded `windows-amd64` or `windows-arm64` package definition for the current process architecture.
2. Downloads the archive directly from the official v2.3.0 GitHub Release.
3. Verifies the archive against the corresponding embedded SHA-256 value.
4. Extracts only after verification and records `SOURCE.json` in a new versioned directory.
5. Saves the new configured path only after installation succeeds; a failure restores the previous configuration and removes the uncommitted directory.

The modern device tool is pinned to a reviewed release asset and checksum in source. It remains an upstream development/research-stage backend, so pairing is verified by opening a trusted Lockdown session after the pair command. Users can instead configure a standard `libimobiledevice` directory.

## Device backends

`ModernIdeviceTools`:

- `idevice_id.exe` for discovery.
- `idevice-tools.exe --udid <udid> lockdown get <key>` for properties.
- `idevice-tools.exe --udid <udid> pair <udid> --name "IPA Bridge"` for trust pairing.
- `idevice-tools.exe --udid <udid> ideviceinstaller install <ipa>` for installation.

`Libimobiledevice` compatibility:

- `idevice_id.exe -l`
- `ideviceinfo.exe -u <udid> -k <key>`
- `idevicepair.exe -u <udid> pair`
- `idevicepair.exe -u <udid> validate`
- `ideviceinstaller.exe -u <udid> install <ipa>`
- A directory is accepted only when all four required executables are colocated.
- Automatic fallback to the legacy `-i` interface only when an older user-supplied build explicitly reports an unknown install command or the known no-mode response from the `install` form.

Both backends depend on a running Apple Mobile Device service on Windows.

## Apple device support detection and acquisition

Apple device readiness is not represented by a single installation flag. `SystemPrerequisiteService` checks four independent layers:

1. The Apple Devices package registered for the current Windows user.
2. A trusted `appleusb.inf`, `usbaapl64.inf` or `usbaapl.inf` package staged in the Windows Driver Store, including its `DriverVer` value.
3. The Apple device transport endpoint on loopback port 27015, plus legacy Apple Mobile Device Service registration when present.
4. A real `idevice_id` process probe through the configured `idevice-tools` or `libimobiledevice` backend.

Device operations are enabled only after the configured backend's `idevice_id` probe exits successfully. Loopback port reachability remains diagnostic because it does not identify the listener or prove an Apple protocol exchange. Driver Store inspection is also diagnostic rather than a hard gate: a readable inventory with no matching driver is distinguished from an inventory that cannot be read, so a scan permission failure or a future Apple INF rename cannot disable a backend whose real transport probe succeeds.

The one-click acquisition path executes WinGet in the current interactive user context and pins both the Microsoft Store source and Apple Devices product ID `9NP83LWLPZ9K`. The exact command uses silent, non-interactive mode and explicit source/package agreement flags. It never downloads an unpackaged MSIX or driver from a third-party server. WinGet is considered available only after `winget --version` successfully executes.

If WinGet is unavailable, requires interactive Store authentication, is blocked by policy or fails, IPA Bridge opens `ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K`, with the official web Store page as a protocol fallback. Opening the Store is reported as a fallback, not as successful installation. A successful WinGet exit is recorded as an automatic installation success only after Windows package registration is detected. Operational readiness remains separate and still requires the backend `idevice_id` probe.
