# Windows BLE communication prototype

`WatchUnlock.BleClient` is a .NET 8 console program for the BLE transport stage. It does not install a service, register a Credential Provider, store an account password, or unlock Windows.

## Use the GitHub artifact

Run the existing **Android APK** workflow against the desired branch. The workflow builds both Android and Windows. Download and extract `WatchUnlock-Windows-BLE-client`, then run `WatchUnlock.BleClient.exe`. Keep all files from the extracted folder together. The Windows x64 artifact includes the .NET runtime.

Enable Bluetooth on both devices. On Android, allow notifications and Nearby Devices access, save the accepted reply, and enable BLE communication testing. On Windows, select the discovered phone, then press Enter to make a request. Replies from the phone and watch follow the same rule.

The computer allows 10 seconds from challenge transmission starting, even if the phone is configured for a longer lifetime. The phone uses the shorter of its configured lifetime and the challenge lifetime. An interrupted request is never automatically resent; pressing Enter creates a new request and connection. Use `r` to rescan after an address change. Windows device access/privacy policy can prevent BLE access; report the displayed error rather than changing login configuration.

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

For the later Windows service, reuse the transport logic only after validating BLE access under the intended service account and after adding cryptographic pairing/authentication. A console application running in a user session does not establish that the service can communicate at the login screen.
