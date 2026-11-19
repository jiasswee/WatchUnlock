# WatchUnlock — Android notification compatibility test

This is the first-stage test app for checking whether Huawei Watch Fit 3 can expose and interact with actions from a third-party Android notification.

## What it tests

1. **Ordinary notification** — custom Allow and Deny notification actions.
2. **MessagingStyle notification** — a simulated conversation with Allow and Deny actions.
3. **RemoteInput notification** — a quick-reply action.

The app records BroadcastReceiver callbacks in its on-screen log and Android logcat (`WatchUnlock` tag).

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
6. Try to interact with each notification on the watch.
7. Return to the app and inspect **操作回调日志**.

A callback is only confirmed when it appears in the app log. Merely seeing a notification on the watch is not proof that the action was delivered.

## Technical notes

- Package: `com.watchunlock.app.debug` for debug builds.
- Minimum Android version: Android 8.0 (API 26).
- Target SDK: API 35.
- No network, BLE, account password, or Windows integration is included in this stage.
- RemoteInput uses a mutable PendingIntent as required for inline reply delivery.
