# WatchUnlock — quick-reply and BLE prototype

This project contains Android quick-reply validation and a Windows/Android BLE transport prototype. The phone validates the reply; Windows will perform final authentication. Cryptographic pairing, the Windows service, and the Credential Provider are not implemented yet. See [the architecture and development stages](docs/architecture.md).

## BLE communication prototype (0.3.0)

1. Install the APK, enable notifications/Bluetooth, and tap **开启 BLE 通信测试**. On Android 12 and later, grant Nearby Devices permission and tap the button again.
2. On Windows 11, run `WatchUnlock.BleClient.exe` from the Windows artifact, select the phone, and press Enter to issue a new 10-second request.
3. Reply with the saved confirmation phrase from the phone or watch. Windows prints the result. Incorrect, cancelled, disconnected, and expired requests never print approval.
4. Press Enter to try a new request after a connection failure. Enter `r` to rescan if the phone's Bluetooth address changes, or `q` to quit. Stop the Android service from its notification or the app when finished.

This is an **unauthenticated communication prototype**, not a Windows unlock mechanism. A nearby client can send test requests while the phone service is enabled. It carries no passwords/keys and cannot submit Windows credentials. Key pairing and authenticated messages are the next stage. Actual BLE/background operation must be checked on the devices.

The phone uses a foreground GATT service, one connected client, 20-byte framed chunks, and confirmed indications for results. Process termination invalidates the live transport; enable the service again to recover. Windows enforces its own deadline and binds results to both request ID and random nonce. See [the BLE protocol](docs/ble-protocol.md) and [Windows build/run instructions](windows/README.md).

## Quick-reply confirmation

1. Open the app and configure the accepted reply (default `好`) and request lifetime (default 10 seconds; configurable from 5 to 120 seconds). Save the settings.
2. Configure the same text in the watch's quick replies, where available.
3. Tap **模拟电脑发起解锁请求** and reply to that notification from the watch.
4. The reply must exactly match the saved text, including whitespace, before the deadline. The status and callback log show approval or rejection. Approval here is a local demonstration and does not unlock a computer.

A wrong or empty reply rejects that request immediately. An expired, cancelled, replaced, or completed request cannot be approved. A new request replaces the previous one. Settings are copied when a request starts; later edits apply to the next request. Unlock-confirmation replies are not written to the log.

Expiry uses Android's monotonic clock and boot counter. Phone reboot invalidates a pending request. Android expires the notification; the receiver independently checks the deadline on every reply. If the app process is absent, the expiry status/log is reconciled on the next app opening or callback. Force-stopping the app can prevent callback delivery; it never grants approval.

Replies from either the phone or the watch are accepted for this personal-use project; identifying their origin is not required. **Watch-path device validation is still needed:** being able to reply to messaging apps on Fit 3 does not establish that this APK's reply reaches its receiver.

## Other notification compatibility tests

1. **Ordinary notification** — custom Allow and Deny notification actions.
2. **MessagingStyle notification** — a simulated conversation with Allow and Deny actions.
3. **RemoteInput notification** — a quick-reply action.

These three diagnostic buttons only record BroadcastReceiver callbacks in the on-screen log and Android logcat (`WatchUnlock` tag); they never approve an unlock request. The log retains the latest 100 entries. The diagnostic quick-reply button records the supplied text, so do not use it for sensitive information.

> Important: successful Android notification action support does not guarantee that Watch Fit 3 exposes those actions. This must be tested on the actual watch and Huawei Health app version.

## Build with GitHub Actions

1. Create a **private** GitHub repository named `WatchUnlock`.
2. Upload/push the contents of this project to the repository root.
3. The workflow runs on pushes to `main` or `master`, pull requests, and manual `workflow_dispatch`.
4. Open **Actions → Android APK**, choose **Run workflow**, and select the branch containing the desired code (currently `codex/watch-reply-approval`).
5. After the jobs succeed, download `WatchUnlock-debug-apk` for `app-debug.apk` and `WatchUnlock-Windows-BLE-client` for the Windows program.

The APK is a debug build and is suitable for sideload testing. It is not a Play Store release.

## Install and test

1. Install the APK on the Android phone.
2. Grant notification permission when prompted.
3. Ensure Huawei Health is allowed to receive notifications from WatchUnlock.
4. Keep the phone and Watch Fit 3 connected.
5. Tap each of the three test buttons in order.
6. Try the quick-reply confirmation above, then interact with the three diagnostic notifications on the watch if needed.
7. Return to the app and inspect **操作回调日志**.

A callback is only confirmed when it appears in the app log. Merely seeing a notification on the watch is not proof that the action was delivered.

## Technical notes

- Current application ID: `org.telegram.messenger`, inherited from the upstream compatibility experiment. It conflicts with an installed Telegram app using that ID and must be given its own ID for normal distribution. This ID alone does not guarantee watch support.
- Kotlin namespace: `com.watchunlock.app`.
- Minimum Android version: Android 8.0 (API 26).
- Target SDK: API 35.
- Communication is local BLE only; no Internet permission, account passwords, Windows service, or login integration is included. The configured reply is a human confirmation phrase, not a pairing key or Windows credential.
- RemoteInput uses a mutable PendingIntent as required for inline reply delivery.
- Unlock replies use an explicit, one-shot PendingIntent with a unique request URI. The receiver is not exported, and stale callbacks cannot approve a newer request.
