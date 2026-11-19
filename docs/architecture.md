# WatchUnlock architecture

## Responsibilities

The phone validates a human confirmation; Windows verifies the authorization and performs final account authentication. The watch holds no Windows password or unlock key.

| Part | Responsibility | Status |
| --- | --- | --- |
| Watch Fit 3 | Display a notification and send a quick reply | This APK's callback path requires device validation |
| Android APK (Kotlin) | Validate the reply and active request; authenticate BLE requests and responses | Reply validation, foreground GATT server, Keystore Key pairing and HMAC implemented; device validation pending |
| Windows background service (C#) | Generate challenges, communicate over BLE, validate responses and deadlines | Service with protected Key persistence, ACL/token-checked pipe, optional reconnect and mode controls implemented; session-0 hardware validation pending |
| Credential Provider V2 DLL (C++) | Offer a login/unlock tile and submit credentials to Windows authentication | Local-account DLL, protected credential delivery, registration/removal scripts implemented in 0.6.0; lock-screen validation pending |

The current APK emits its own notification and receives its `RemoteInput` callback. Listening to unrelated apps' notifications is unnecessary for this path. For this personal-use project, replies from either the phone or the watch are accepted. Distinguishing their origin is not required; both follow the same phrase, deadline, and single-use checks.

## Confirmation policy

- Accepted text defaults to `好` and is configurable on the phone. Replies must match exactly, including whitespace; no trimming or case conversion is performed.
- Only one request is pending at a time. Starting another invalidates the older one, including notifications cached by the watch.
- Each request snapshots its accepted text and lifetime. Settings changes affect subsequent requests only.
- Wrong or empty replies, cancellation, expiry, and phone reboot end the request without authorization. A completed request cannot be reused.
- The demo lifetime defaults to 10 seconds and supports 5–120 seconds. Windows must ultimately enforce its own challenge lifetime, regardless of phone settings or delayed delivery.
- The computer may start another request, or the user may use Windows' existing password, PIN, or Hello options.
- A matching phrase is human confirmation from either device. It is not a cryptographic secret or sufficient authentication by itself; Windows still validates the authenticated authorization and performs account authentication.

## Authenticated BLE communication

Android server 0.4.0 and Windows client/service 0.6.0 implement the authenticated [v2 protocol](ble-protocol.md). Direct BLE communication and Key pairing have run successfully on the user's devices. Service communication and Windows login still need device validation.

Android is the BLE GATT server and advertises a dedicated service. Windows is the GATT client and discovers/connects to the paired phone. No router, Internet connection, or EasyTier is needed.

| Characteristic | Direction | Use |
| --- | --- | --- |
| Challenge | Windows → phone (write) | Authenticated unlock challenge |
| Approval | Phone → Windows (indicate) | Pairing proofs and authenticated approval or rejection |
| Status | Phone → Windows (read) | Protocol capability marker |

Messages are flat UTF-8 JSON followed by LF, with a 4096-byte frame limit and 20-byte GATT chunks. Results use acknowledged, serialized indications. All mutable Android transport state runs on the main handler; the foreground service owns the connection outside the Activity.

The user enters the same 16–128-character printable ASCII Key on both devices. PBKDF2-HMAC-SHA256 derives the long-term key; Android imports it into Keystore. Windows direct mode keeps it in memory, while the service persists machine-DPAPI ciphertext with a System/Administrators-only ACL and binds management access to the configured user's token SID. A four-message HMAC handshake proves mutual possession and derives a fresh session key from client/server random nonces. No Key is transmitted over BLE. Use a randomly generated Key separate from `好`, the Windows password and Bluetooth address. Key possession defines the pairing identity; a device with the same Key can connect.

Both challenges and responses use HMAC-SHA256 over the canonical bytes defined in the protocol. These bind version, message type, current session, request ID, random 256-bit nonce, request lifetime or decision, and display name where applicable. Windows checks the MAC, outstanding request and monotonic deadline. Invalid signatures, mismatched requests and expired results cannot confirm approval. The application authenticates messages but does not encrypt GATT payloads or require OS Bluetooth bonding; a live relay is outside the prototype's protection.

Keep BLE/background work outside the Activity. Check GATT server/advertising support on the actual phone and BLE support on the computer. Android background operation needs a supported foreground-service or Companion Device approach; process termination and vendor power management must be handled explicitly. Validate the Windows BLE client in the intended service account and while locked before relying on it for login.

## Windows authentication (0.6.0 prototype)

The background service owns challenges, BLE communication and protocol validation. Deployment configuration opens separately from direct BLE testing. Optional background reconnection, phone switching and persistent mode controls remain available; see [service deployment](../windows/service.md). Management replies carry decisions only. A separate System-only credential pipe verifies the actual LogonUI process, active console session and configured account SID, then starts a fresh challenge. Configuration changes cancel active work and invalidate the credential revision. There is no reusable global `approved` flag.

A Credential Provider serializes credentials; Windows LSA performs authentication. The prototype targets one local Windows account and supports `CPUS_LOGON` and `CPUS_UNLOCK_WORKSTATION`. Setup checks the password with Windows and stores machine-DPAPI ciphertext alongside the Key in the System/Administrators-only configuration file. Only a current successful BLE authorization permits delivery to LogonUI; the DLL protects the password with `CredProtect`, serializes a local interactive logon for Negotiate, and clears its copy after one submission. Administrators/System remain trusted and can recover stored secrets. A custom authentication package is a separate project. Microsoft/domain accounts, remote sessions and password-change flows are outside this version.

Keep the built-in Windows credential providers enabled. Verify supported logon/unlock usage scenarios on the target Windows 11 version and validate the provider in a recoverable environment before deployment. Communication failures must leave native login options usable.

## Development order

1. **Reply path:** use this APK on OnePlus Ace 3 and Watch Fit 3. Confirm that a watch reply reaches the app and that matching, incorrect, expired, and stale replies have the intended outcomes.
2. **BLE transport:** implement Android GATT server and Windows GATT client, discovery, reconnect, and framed challenge/result exchange without unlocking Windows.
3. **Protocol:** shared-Key pairing, authenticated messages and independent deadlines are implemented in 0.4.0; validate them on devices and define persistent account/device binding for Windows integration.
4. **Windows integration:** the service, local-account Credential Provider and protected password storage are implemented. Validate service BLE under LocalSystem and while locked, then verify actual local-account credential submission and native-login fallback.
5. **Reliability and recovery:** check Bluetooth/range loss, watch disconnection, phone termination, reboot, malformed messages and replay, and Windows recovery/native sign-in options.

## Official references

- [Huawei message management and quick replies](https://consumer.huawei.com/en/support/content/en-us16066732/)
- [Android BLE background communication](https://developer.android.com/develop/connectivity/bluetooth/ble/background)
- [Android RemoteInput](https://developer.android.com/reference/android/app/RemoteInput)
- [Windows Credential Providers](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)
- [Credential Provider logon flow](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-provider-driven-windows-logon-experience)
