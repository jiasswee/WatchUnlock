# WatchUnlock — Android quick-reply validation

This is the first-stage Android prototype for Watch Fit 3 notification interaction and unlock confirmation. The phone validates the reply; Windows will perform final authentication. BLE transport, cryptographic pairing, the Windows service, and the Credential Provider are not implemented yet. See [the architecture and development stages](docs/architecture.md).

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
4. Open **Actions → Android APK → latest successful run**.
5. Download the `WatchUnlock-debug-apk` artifact and extract `app-debug.apk`.

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
- No network, BLE, account password, or Windows integration is included in this stage. The configured reply is a human confirmation phrase, not a pairing key or Windows credential.
- RemoteInput uses a mutable PendingIntent as required for inline reply delivery.
- Unlock replies use an explicit, one-shot PendingIntent with a unique request URI. The receiver is not exported, and stale callbacks cannot approve a newer request.
