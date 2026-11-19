# Windows service and local-account login (0.6.0)

Direct BLE testing needs no service and no Windows password. This separate deployment flow installs a Windows service and, after configuration, a local-account Credential Provider. The code and installation artifacts are implemented; real service BLE and lock-screen login still require hardware validation. Setup currently requires administrator elevation in the intended local account's own desktop session. Using another administrator's credentials configures that other account.

## Install and configure

1. Download and extract the complete `WatchUnlock-Windows-BLE-client` artifact. It contains the client at the root, the service in `service/`, the DLL in `credential-provider/`, and deployment scripts. Android 0.4.0 remains compatible.
2. In an administrator PowerShell terminal opened in that extracted folder, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
   ```

3. The script copies the service to `%ProgramFiles%\WatchUnlock\Service`, registers `WatchUnlock` with automatic startup under LocalSystem, and starts it. Run `open-setup.cmd` as administrator **in the intended account's desktop session**. This launches `WatchUnlock.BleClient.exe --setup`; opening the exe normally enters direct testing.
4. Choose **1 设置/切换手机**, select a phone from the live list, and enter its Key. The service completes mutual BLE pairing before saving configuration. The configured user SID is obtained from the pipe client's Windows token, not supplied in JSON. Using credentials for a different administrator account also changes the configured owner to that account.
5. Choose **2 请求确认** to check a fresh 10-second request from the service. The phone or watch may reply with the saved phrase. This diagnostic command only prints a decision. Confirm it works before installing the login tile.
6. Choose **7 保存本地账户密码**, then enter that account's actual password in the masked prompt. This is separate from its PIN and pairing Key. The service checks that it is a local account without an Internet identity, verifies the password through Windows, and confirms the resulting token SID before saving ciphertext. Empty passwords, Microsoft accounts and domain accounts are unsupported.
7. Run `powershell -ExecutionPolicy Bypass -File .\install-login.ps1` from a 64-bit administrator PowerShell terminal. The script requires a running service, saved credentials, an enabled configuration and the same account SID. It registers only the WatchUnlock CLSID, without a provider filter. Built-in password/PIN/Hello providers remain available.

## Login behavior

Lock Windows and choose **使用手表解锁** under sign-in options. Clicking its arrow starts a fresh asynchronous request; reply from the phone or watch within the displayed request's 10-second deadline. Connection setup has a separate timeout; switching login options cancels pending work. After a successful reply, a marshaled `ICredentialProviderEvents::CredentialsChanged` callback refreshes the tile and permits automatic submission of only that current result. Windows receives a credential only after the current signed result is approved. Rejection, expiry, disconnection and invalid configuration return no credential; retry or choose password/PIN. There is no automatic approval on reconnection.

The DLL associates its tile with the configured SID in Windows' user array. Windows 11 may use `CPUS_LOGON` for unlocking as well as `CPUS_UNLOCK_WORKSTATION`; both are supported. Credential delivery is restricted to the actual System LogonUI process in the active local console session. Existing session users must match the configured SID. Remote desktop, Microsoft/domain accounts, other users and password-change flows are outside this version.

After approval, the DLL has at most five seconds to serialize that credential once. It checks the current enabled flag and configuration revision again, uses `CredProtect`, and submits a `KERB_INTERACTIVE_UNLOCK_LOGON` buffer to Negotiate. Windows LSA decides whether the saved password is valid. Changing the Windows password requires saving it again through option 7; a failed Windows authentication clears the DLL's pending copy. Option **8 删除已保存密码** removes its ciphertext and disables the tile until a password is saved again.

After configuration the same account can manage its service without elevation, except when setting/changing the phone. Administrators can change configuration and modes. Stop direct BLE mode before using service mode because the phone supports one active GATT peer/session.

## Deployment controls

The management menu offers **3 开启自动回连**, **4 关闭自动回连**, **5 停用** and **6 启用**.

- Automatic reconnection defaults to off. Enabling it keeps an authenticated connection when available and retries failures with 2/4/8/16/30-second backoff. A mode change wakes the retry loop promptly.
- Leaving Bluetooth range only breaks the transport. When the phone returns, the service uses fresh mutual proofs before accepting any new authorization. It tries the saved address first, then discovers candidates and accepts one only after its Key proof succeeds. Use a different Key for each phone when selecting a specific phone matters; phones holding the same Key have the same pairing identity.
- Reconnection does not create an approval request. Each explicit request gets a fresh UUID, nonce and independent deadline. While disconnected, failed or timed-out requests do not receive approval.
- Turning off automatic reconnection stops background attempts; an explicit request may still connect on demand. **停用** disables both paths, closes the current connection and cancels current work. **启用** restores the saved mode. Phone switching preserves the enabled/reconnect choices and invalidates the old connection.
- Closing the management client does not stop the installed service. Enabled/disabled and reconnection settings survive service and Windows restarts.

## Storage and local communication

`%ProgramData%\WatchUnlock\service.json` stores the phone address/name, configured Windows SID, mode settings, DPAPI-protected derived Key and optional DPAPI-protected local password. Directory and file access is restricted to LocalSystem and Administrators; plaintext secrets and reply phrases are not stored. Existing configuration ownership/ACLs are checked before loading. Linked configuration paths are rejected. Machine DPAPI plus ACLs protect ordinary-user access; administrators and local system code remain trusted and can recover the secrets. Password input uses mutable buffers cleared after use. Public tile metadata under `HKLM\SOFTWARE\WatchUnlock` contains only SID, account name, revision and enabled state.

The pipe is `\\.\pipe\WatchUnlock.v1`, with a maximum 4096-byte flat JSON frame followed by LF and one command per connection. The server permits local interactive pipe access but impersonates every client and restricts operations to the configured SID, administrators or System. Requests must originate in an interactive session, obtained from the actual pipe client. A second command, disconnect or timeout cancels that connection's operation. A maximum of eight clients allows disable/control messages to arrive while BLE work waits; BLE operations themselves are serialized.

Before transmitting configuration material, the client compares the pipe server PID with the running `WatchUnlock` service PID reported by Windows Service Control Manager. The service never returns Key material or passwords through the management pipe. Every reply includes the originating command ID. There is no global reusable `approved` state.

Two additional pipes use length-bounded binary messages with UTF-16 payloads. `WatchUnlock.Setup.v1` accepts only elevated administrators/System at the ACL, then requires the actual interactive administrator SID to match the configured owner. Its header contains WCS1 magic, save/delete operation and password byte length (maximum 512). `WatchUnlock.Credential.v1` has a System-only ACL; the service additionally checks the client's PID/image path, SID and console session before and after BLE confirmation. Its 28-byte request contains WCU1 magic, usage scenario, GUID and SID byte length. Its 36-byte response contains magic, result, the same GUID and domain/username/password byte lengths; secrets are sent only for result 0. Only one request is accepted per connection; disconnection, extra input or deadline cancels login work. Configuration revisions are checked before decrypting and sending credentials.

## Update or remove

Run the new artifact's installation script as administrator to stop the existing service, copy new files and restart. If an existing `WatchUnlock` service points to a different installation folder, the script stops with an explanation.

To remove the service registration:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
```

First run `powershell -ExecutionPolicy Bypass -File .\uninstall-login.ps1` to remove the login tile, then run the service removal command above. Service removal refuses while the WatchUnlock provider remains registered. Both scripts retain binaries/configuration; remove the saved password with option 8 beforehand if desired. Updating the login DLL uses a hash-named folder so a currently loaded DLL is not overwritten; restart Windows to load a changed version.

## Remaining validation

The direct client already ran successfully on the user's devices. Confirm that Windows BLE APIs work under LocalSystem/session 0 and while the screen is locked, and that vendor background restrictions permit the phone to advertise. Then verify actual Windows login, cancellation, incorrect replies, out-of-range phones, password changes and restart behavior. A successful build alone cannot establish those behaviors. Native providers remain registered; only a fresh successful reply to an explicit click permits automatic submission. Keep the uninstall scripts accessible for removing this prototype.
