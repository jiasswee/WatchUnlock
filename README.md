# WatchUnlock — quick-reply and BLE prototype

This project contains Android quick-reply validation, authenticated BLE communication, and a Windows local-account login prototype. The phone validates the reply; Windows verifies the result and submits credentials to Windows authentication. Windows 0.6.0 adds a Credential Provider DLL and protected local-password configuration; service/lock-screen behavior still needs device validation. See [the architecture and development stages](docs/architecture.md).

## Direct BLE communication (Windows 0.6.0, Android 0.4.0)

1. Install Android APK 0.4.0 or later and Windows client 0.6.0. The phone APK/protocol remains unchanged. On the phone, enter a pairing Key and tap **保存配对 Key**. The Key must contain 16–128 printable ASCII characters (English letters, digits or symbols), without spaces; use a randomly generated value. This is separate from the confirmation phrase `好` and from your Windows password.
2. Enable notifications/Bluetooth and tap **开启 BLE 通信测试**. On Android 12 and later, grant Nearby Devices permission and tap the button again. On Windows 11, run `WatchUnlock.BleClient.exe` and enter the same Key. Phones appear as soon as discovered; type a displayed number and press Enter immediately. Scanning stops on selection, rescan or exit, with no fixed 8-second wait. Press Enter to request confirmation. Only a successful mutual Key check permits a new 10-second request.
3. Reply with the saved confirmation phrase from the phone or watch. Windows prints the result. Incorrect, cancelled, disconnected, and expired requests never print approval.
4. Press Enter to try a new request after a connection failure. Enter `r` to rescan if the phone's Bluetooth address changes, `k` to change the Windows Key, or `q` to quit. Changing the phone Key stops BLE and cancels a pending BLE request; re-enable BLE afterward. Stop the Android service from its notification or the app when finished.

The Key is never sent over BLE. Both sides derive it using PBKDF2-HMAC-SHA256 and establish a fresh session with mutual HMAC proofs. Android stores the derived Key in Android Keystore. Windows direct mode keeps it only in process memory, so enter it again after restarting the program; the optional service protects its persisted key with machine DPAPI and a restricted file ACL. Requests and decisions are authenticated with the session key. Anyone possessing the same Key can connect; this is application pairing, without a Windows Bluetooth pairing dialog. The prototype does not submit Windows credentials or unlock Windows.

The phone uses a foreground GATT service, one connected client, 20-byte framed chunks, and confirmed indications for results. Process termination invalidates the live transport; enable the service again to recover. Windows enforces its own deadline and binds results to both request ID and random nonce. See [the BLE protocol](docs/ble-protocol.md) and [Windows build/run instructions](windows/README.md).

Windows now displays service discovery, characteristic reads, protocol checking, subscription and Key pairing as separate steps. Connection errors include the failure step, exception type and Windows error code even when the native error message is empty. If the phone remains at **电脑已连接，等待订阅**, retain the complete Windows error to identify which operation failed.

## Windows deployment (0.6.0)

Direct testing opens immediately and requires no service installation. Deployment is a separate flow: run `install-service.ps1`, open `open-setup.cmd` as administrator in the intended local account's desktop, configure the phone and check a service confirmation request. Save the local account's actual password (not its PIN), then run `install-login.ps1` to register the login tile. Passwords stay on Windows; ordinary test commands cannot receive them. Native password, PIN and Hello providers remain registered. See [the complete deployment and removal instructions](windows/service.md).

Automatic reconnection is **off by default**. Enable it for deployment: the service retries with 2/4/8/16/30-second backoff, revalidates the Key after reconnection, and rediscovers a phone whose BLE address changed. Reconnection itself never requests or grants authorization. Switching phones or disabling WatchUnlock cancels current BLE work. Disabling reconnection alone still permits connection on demand for an explicit request.

The service returns a result for the current local pipe request and never keeps a global approval flag. The login pipe is restricted to System and checks the actual LogonUI process, console session and target SID. Every login attempt starts a fresh signed BLE challenge. BLE access under LocalSystem/session 0 and while Windows is locked still needs device validation; a compiled DLL alone does not establish successful unlocking.

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

Build runners are pinned to `ubuntu-24.04` and `windows-2025` to avoid automatic OS-version migration. The images' installed tools still receive updates. Gradle 8.9 currently meets Android Gradle Plugin 8.7.3's minimum; an out-of-date notice is not a build failure. Upgrade Gradle together with compatible Android/Kotlin plugins. Read-only caching on a feature branch is the Gradle action's default policy and does not prevent a build.

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
- Communication is local BLE only. The APK needs no Internet permission. Windows includes an optional authorization service; account passwords and login integration are not included yet. The configured reply is a human confirmation phrase; the separate Key authenticates BLE communication.
- RemoteInput uses a mutable PendingIntent as required for inline reply delivery.
- Unlock replies use an explicit, one-shot PendingIntent with a unique request URI. The receiver is not exported, and stale callbacks cannot approve a newer request.
