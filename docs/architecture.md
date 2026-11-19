# WatchUnlock architecture

## Responsibilities

The phone validates a human confirmation; Windows verifies the authorization and performs final account authentication. The watch holds no Windows password or unlock key.

| Part | Responsibility | Status |
| --- | --- | --- |
| Watch Fit 3 | Display a notification and send a quick reply | This APK's callback path requires device validation |
| Android APK (Kotlin) | Validate the reply and active request; later authenticate BLE requests and responses | Local validation and foreground BLE GATT transport implemented; device validation pending |
| Windows background service (C++ or C#) | Generate challenges, communicate over BLE, validate responses and deadlines | Console BLE client implemented in C#; service and cryptographic verification planned |
| Credential Provider V2 DLL (C++) | Offer a login/unlock tile and submit credentials to Windows authentication | Planned |

The current APK emits its own notification and receives its `RemoteInput` callback. Listening to unrelated apps' notifications is unnecessary for this path. For this personal-use project, replies from either the phone or the watch are accepted. Distinguishing their origin is not required; both follow the same phrase, deadline, and single-use checks.

## Confirmation policy

- Accepted text defaults to `好` and is configurable on the phone. Replies must match exactly, including whitespace; no trimming or case conversion is performed.
- Only one request is pending at a time. Starting another invalidates the older one, including notifications cached by the watch.
- Each request snapshots its accepted text and lifetime. Settings changes affect subsequent requests only.
- Wrong or empty replies, cancellation, expiry, and phone reboot end the request without authorization. A completed request cannot be reused.
- The demo lifetime defaults to 10 seconds and supports 5–120 seconds. Windows must ultimately enforce its own challenge lifetime, regardless of phone settings or delayed delivery.
- The computer may start another request, or the user may use Windows' existing password, PIN, or Hello options.
- A matching phrase is human confirmation from either device. It is not a cryptographic secret or sufficient authentication by itself; Windows still validates the authenticated authorization and performs account authentication.

## BLE transport and planned authenticated authorization

The stage-2 Android server and Windows console client implement the unauthenticated [transport protocol](ble-protocol.md). Use them to validate real-device communication without enabling Windows unlocks. Secure pairing and authenticated authorization described below are not implemented yet.

Android is the BLE GATT server and advertises a dedicated service. Windows is the GATT client and discovers/connects to the paired phone. No router, Internet connection, or EasyTier is needed.

| Characteristic | Direction | Use |
| --- | --- | --- |
| Challenge | Windows → phone (write) | Authenticated unlock challenge |
| Approval | Phone → Windows (notify/indicate) | Authenticated approval or rejection |
| Status | Bidirectional | Connection/request status |

Use UTF-8 JSON for the prototype. Specify framing, size limits, and fragmentation before implementation; a JSON message may exceed a negotiated GATT payload. Do not assume one write or notification transports the entire message.

First pairing must securely establish a separate, random key and bind the phone and computer identities. Store key material using Android Keystore and Windows protected storage/CNG. Never derive this key from `好`, a Windows password, or the Bluetooth address, and never send it as an unauthenticated plaintext pairing message.

Authenticate both challenge and response with HMAC-SHA256 over explicitly defined canonical bytes. Bind the protocol version, message type, paired identities/key ID, request ID, random 256-bit nonce, expiry policy, and response decision to the MAC. Windows checks the MAC, its own outstanding request and deadline, and single-use consumption. Unknown devices, mismatched nonces, malformed messages, duplicates, and expired approvals are rejected. Link encryption and pairing policy must also be specified.

Keep BLE/background work outside the Activity. Check GATT server/advertising support on the actual phone and BLE support on the computer. Android background operation needs a supported foreground-service or Companion Device approach; process termination and vendor power management must be handled explicitly. Validate the Windows BLE client in the intended service account and while locked before relying on it for login.

## Windows authentication (planned)

The background service owns challenges, BLE communication, protocol validation, and the account/session binding. The Credential Provider communicates with it over an ACL-protected named pipe. Authorization must be tied to the requesting account and session, with no reusable global `approved` flag.

A Credential Provider serializes credentials; Windows LSA performs authentication. Choose the final credential scheme before implementing the provider. A prototype using a recoverable encrypted account password requires a specific storage/access policy; DPAPI alone is not a complete security design. A custom authentication package is a separate, more complex project.

Keep the built-in Windows credential providers enabled. Verify supported logon/unlock usage scenarios on the target Windows 11 version and validate the provider in a recoverable environment before deployment. Communication failures must leave native login options usable.

## Development order

1. **Reply path:** use this APK on OnePlus Ace 3 and Watch Fit 3. Confirm that a watch reply reaches the app and that matching, incorrect, expired, and stale replies have the intended outcomes.
2. **BLE transport:** implement Android GATT server and Windows GATT client, discovery, reconnect, and framed challenge/result exchange without unlocking Windows.
3. **Protocol:** implement secure pairing, authenticated messages, independent deadlines, replay rejection, and device binding.
4. **Windows integration:** implement the service and Credential Provider after settling the credential-storage scheme. Connect a successful, current authorization to Windows credential submission.
5. **Reliability and recovery:** check Bluetooth/range loss, watch disconnection, phone termination, reboot, malformed messages and replay, and Windows recovery/native sign-in options.

## Official references

- [Huawei message management and quick replies](https://consumer.huawei.com/en/support/content/en-us16066732/)
- [Android BLE background communication](https://developer.android.com/develop/connectivity/bluetooth/ble/background)
- [Android RemoteInput](https://developer.android.com/reference/android/app/RemoteInput)
- [Windows Credential Providers](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)
- [Credential Provider logon flow](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-provider-driven-windows-logon-experience)
