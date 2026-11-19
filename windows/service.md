# Windows authorization service (0.5.0)

This phase moves BLE authorization into a real Windows service and exposes a protected local pipe for the client and future Credential Provider. It does not authenticate a Windows account yet. The initial account target is a local Windows account; its password is not requested or stored in this phase.

## Install and configure

1. Download and extract the complete `WatchUnlock-Windows-BLE-client` artifact. It contains the client at the root, the service in `service/`, and two PowerShell scripts.
2. In an administrator PowerShell terminal opened in that extracted folder, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
   ```

3. The script copies the service to `%ProgramFiles%\WatchUnlock\Service`, registers `WatchUnlock` with automatic startup under LocalSystem, and starts it. Run `WatchUnlock.BleClient.exe` as administrator **in the intended account's desktop session**, then enter `s`.
4. Choose **1 设置/切换手机**, select a phone from the live list, and enter its Key. The service completes mutual BLE pairing before saving configuration. The configured user SID is obtained from the pipe client's Windows token, not supplied in JSON. Using credentials for a different administrator account also changes the configured owner to that account.
5. Choose **2 请求确认** to issue a fresh 10-second request. The phone or watch may reply with the saved phrase. The service checks the current signed result and deadline before returning it. This still does not unlock Windows.

After configuration the same account can manage its service without elevation, except when setting/changing the phone. Administrators can change configuration and modes. Stop direct BLE mode before using service mode because the phone supports one active GATT peer/session.

## Deployment controls

The management menu offers **3 开启自动回连**, **4 关闭自动回连**, **5 停用** and **6 启用**.

- Automatic reconnection defaults to off. Enabling it keeps an authenticated connection when available and retries failures with 2/4/8/16/30-second backoff. A mode change wakes the retry loop promptly.
- Leaving Bluetooth range only breaks the transport. When the phone returns, the service uses fresh mutual proofs before accepting any new authorization. It tries the saved address first, then discovers candidates and accepts one only after its Key proof succeeds. Use a different Key for each phone when selecting a specific phone matters; phones holding the same Key have the same pairing identity.
- Reconnection does not create an approval request. Each explicit request gets a fresh UUID, nonce and independent deadline. While disconnected, failed or timed-out requests do not receive approval.
- Turning off automatic reconnection stops background attempts; an explicit request may still connect on demand. **停用** disables both paths, closes the current connection and cancels current work. **启用** restores the saved mode. Phone switching preserves the enabled/reconnect choices and invalidates the old connection.
- Closing the management client does not stop the installed service. Enabled/disabled and reconnection settings survive service and Windows restarts.

## Storage and local communication

`%ProgramData%\WatchUnlock\service.json` stores the phone address/name, configured Windows SID, mode settings and DPAPI-protected derived Key. Directory and file access is restricted to LocalSystem and Administrators; plaintext input and reply phrases are not stored. Existing configuration ownership/ACLs are checked before loading. Linked configuration paths are rejected. Machine DPAPI plus ACLs protect ordinary-user access; administrators and local system code remain trusted and can recover the secret.

The pipe is `\\.\pipe\WatchUnlock.v1`, with a maximum 4096-byte flat JSON frame followed by LF and one command per connection. The server permits local interactive pipe access but impersonates every client and restricts operations to the configured SID, administrators or System. Requests must originate in an interactive session, obtained from the actual pipe client. A second command, disconnect or timeout cancels that connection's operation. A maximum of eight clients allows disable/control messages to arrive while BLE work waits; BLE operations themselves are serialized.

Before transmitting configuration material, the client compares the pipe server PID with the running `WatchUnlock` service PID reported by Windows Service Control Manager. The service never returns Key material. Every reply includes the originating command ID. There is no global reusable `approved` state. Account credential delivery and the final Credential Provider/session binding remain the next phase.

## Update or remove

Run the new artifact's installation script as administrator to stop the existing service, copy new files and restart. If an existing `WatchUnlock` service points to a different installation folder, the script stops with an explanation.

To remove the service registration:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
```

The removal script retains configuration and binaries for reinstalling. It does not register or change any Windows login providers.

## Remaining validation

The direct client already ran successfully on the user's devices. This new service still needs confirmation that Windows BLE APIs work under LocalSystem/session 0 and while the screen is locked, and that vendor background restrictions permit the phone to advertise. A successful build alone cannot establish those behaviors. Native Windows login remains available because Credential Provider integration has not been installed.
