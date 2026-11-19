# Windows BLE test and local-account login prototype

`WatchUnlock.BleClient` 0.6.0 opens directly into BLE communication testing. Testing needs no installed service or account password. The separate [deployment flow](service.md) configures the Windows service and local-account Credential Provider; login integration still needs device validation.

## Use the GitHub artifact

Run the existing **Android APK** workflow against the desired branch. The workflow builds Android, the Windows client, service and login DLL. Download and extract `WatchUnlock-Windows-BLE-client`, then run `WatchUnlock.BleClient.exe`. Keep the extracted files together. The Windows x64 executables include their .NET runtimes. Deployment configuration opens separately through `open-setup.cmd` (`WatchUnlock.BleClient.exe --setup`); direct testing has no service menu.

Use Android 0.4.0 or later with Windows 0.6.0; both use BLE v2. Enable Bluetooth on both devices. On Android, save a pairing Key of 16–128 printable ASCII characters (English letters, digits or symbols, no spaces), allow notifications and Nearby Devices access, save the accepted reply, and enable BLE. In Windows direct mode, enter exactly the same Key into the masked prompt. Discovery streams phones immediately with stable numbers; choose any displayed number and press Enter without waiting for the scan to finish. `r` restarts scanning and `q` exits even when no phones have been found. Mutual Key verification must succeed before sending a request. Replies from the phone and watch follow the same rule; the confirmation phrase defaults to `好` and is separate from the Key.

The Key is never transmitted over BLE. Android stores derived key material in Keystore; Windows keeps it only in memory and asks again after restart. Use `k` to replace the Windows Key. Changing the Android Key stops BLE and cancels a pending BLE request; re-enable BLE to reconnect. This is application-level pairing and does not require a Windows Bluetooth pairing dialog.

The computer allows 10 seconds from challenge transmission starting, even if the phone is configured for a longer lifetime. The phone uses the shorter of its configured lifetime and the challenge lifetime. An interrupted request is never automatically resent; pressing Enter creates a new request and connection. Use `r` to rescan after an address change. Windows device access/privacy policy can prevent BLE access; report the displayed error rather than changing login configuration.

Connection progress identifies service discovery, each characteristic read, protocol checking, result subscription, and Key verification. Errors preserve the failed step, exception type and hexadecimal Windows error code even when Windows supplies no explanatory text. Cleanup is best effort so it cannot replace the original failure. Explicit `GattSession` maintenance is no longer needed for the GATT operations used here; whether this changes the reported device failure still requires hardware validation.

## Build from source

Requirements: Windows 11 (or Windows 10 build 19041+), BLE hardware, and the **.NET 8 SDK**. The .NET runtime alone does not include build tools.

```powershell
dotnet build windows/WatchUnlock.BleClient/WatchUnlock.BleClient.csproj
dotnet run --project windows/WatchUnlock.BleClient/WatchUnlock.BleClient.csproj
```

To create the same self-contained x64 folder as GitHub Actions:

```powershell
dotnet publish windows/WatchUnlock.BleClient/WatchUnlock.BleClient.csproj --configuration Release --runtime win-x64 --self-contained true --output dist/windows-ble
```

The service source is `windows/WatchUnlock.Service/WatchUnlock.Service.csproj` and reuses the same BLE source files. Publish it into `dist/windows-ble/service`. Build `windows/WatchUnlock.CredentialProvider` with CMake, Visual Studio C++ Build Tools and Windows SDK, copy its Release DLL into `dist/windows-ble/credential-provider`, and include the scripts from `windows/`. The service uses protected Key/password persistence and separate management/login pipes. BLE access under LocalSystem while locked still requires hardware validation. See [protocol v2](../docs/ble-protocol.md) and [deployment instructions](service.md).
