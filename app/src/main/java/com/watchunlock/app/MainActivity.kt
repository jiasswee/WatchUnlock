package com.watchunlock.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputFilter
import android.text.InputType
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.core.app.ActivityCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

class MainActivity : Activity(), SharedPreferences.OnSharedPreferenceChangeListener {

    private lateinit var logView: TextView
    private lateinit var requestView: TextView
    private lateinit var cancelButton: Button
    private lateinit var bleStatusView: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val refreshTask = object : Runnable {
        override fun run() {
            refreshRequest()
            refreshBleStatus()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(36, 32, 36, 24)
        }

        val title = TextView(this).apply {
            text = "WatchUnlock 快捷回复验证"
            textSize = 22f
        }
        content.addView(title)

        val description = TextView(this).apply {
            text = "手机检查快捷回复是否匹配；Windows 负责最终认证。当前支持本地验证和 BLE 通信测试，尚未接入电脑登录。"
            textSize = 15f
            setPadding(0, 12, 0, 20)
        }
        content.addView(description)

        content.addView(TextView(this).apply { text = "配对 Key（两端一致，16～128 位英文、数字或符号，无空格）" })
        val keyInput = EditText(this).apply {
            setSingleLine(true)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            filters = arrayOf(InputFilter.LengthFilter(128))
            hint = if (runCatching { PairingKey(this@MainActivity).configured() }.getOrDefault(false)) "已保存；输入新 Key 可覆盖" else "请输入配对 Key"
        }
        content.addView(keyInput)
        val saveKeyButton = Button(this).apply { text = "保存配对 Key" }
        saveKeyButton.setOnClickListener {
            val chars = CharArray(keyInput.text.length) { keyInput.text[it] }
            if (chars.size !in 16..128 || chars.any { it.code !in 33..126 }) {
                chars.fill('\u0000')
                Toast.makeText(this, "Key 须为 16～128 位英文、数字或符号，不能含空格", Toast.LENGTH_LONG).show()
            } else {
                val store = UnlockRequestStore(this)
                if (store.current()?.source == UnlockRequestStore.Source.BLE) store.cancel()
                stopService(Intent(this, BleGattService::class.java))
                keyInput.text.clear()
                keyInput.isEnabled = false
                saveKeyButton.isEnabled = false
                Thread {
                    val saved = runCatching { PairingKey(applicationContext).save(chars) }.isSuccess
                    chars.fill('\u0000')
                    runOnUiThread {
                        saveKeyButton.isEnabled = true
                        keyInput.isEnabled = true
                        keyInput.hint = if (saved) "已保存；输入新 Key 可覆盖" else "保存失败，请重新输入"
                        Toast.makeText(this, if (saved) "Key 已保存，请重新开启 BLE 并连接电脑" else "Key 保存失败，请重试", Toast.LENGTH_LONG).show()
                    }
                }.start()
            }
        }
        content.addView(saveKeyButton)

        content.addView(makeButton("开启 BLE 通信测试") {
            if (!saveKeyButton.isEnabled) {
                Toast.makeText(this, "正在保存 Key，请稍后", Toast.LENGTH_LONG).show()
            } else if (!runCatching { PairingKey(this).configured() }.getOrDefault(false)) {
                Toast.makeText(this, "请先保存配对 Key", Toast.LENGTH_LONG).show()
            } else if (ensureNotificationPermission() && ensureBluetoothPermissions()) {
                try {
                    ContextCompat.startForegroundService(this, Intent(this, BleGattService::class.java))
                } catch (_: SecurityException) {
                    Toast.makeText(this, "请检查附近设备和通知权限", Toast.LENGTH_LONG).show()
                } catch (_: IllegalStateException) {
                    Toast.makeText(this, "无法启动蓝牙服务，请保持 App 在前台后重试", Toast.LENGTH_LONG).show()
                }
            }
        })
        content.addView(makeButton("停止 BLE 通信测试") {
            stopService(Intent(this, BleGattService::class.java))
        })
        bleStatusView = TextView(this).apply {
            textSize = 15f
            setPadding(0, 8, 0, 16)
        }
        content.addView(bleStatusView)

        val store = UnlockRequestStore(this)
        content.addView(TextView(this).apply { text = "允许授权的回复内容（默认：好）" })
        val replyInput = EditText(this).apply {
            setSingleLine(true)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            filters = arrayOf(InputFilter.LengthFilter(UnlockRequestStore.MAX_REPLY_LENGTH))
            setText(store.expectedReply)
        }
        content.addView(replyInput)
        content.addView(TextView(this).apply { text = "请求有效期（5～120 秒，默认 10 秒）" })
        val timeoutInput = EditText(this).apply {
            setSingleLine(true)
            inputType = InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(InputFilter.LengthFilter(3))
            setText(store.timeoutSeconds.toString())
        }
        content.addView(timeoutInput)
        content.addView(makeButton("保存回复设置") {
            val reply = replyInput.text.toString()
            val timeout = timeoutInput.text.toString().toIntOrNull()
            if (reply.isBlank() || timeout == null ||
                timeout !in UnlockRequestStore.MIN_TIMEOUT_SECONDS..UnlockRequestStore.MAX_TIMEOUT_SECONDS
            ) {
                Toast.makeText(this, "请输入非空回复和 5～120 秒的有效期", Toast.LENGTH_LONG).show()
            } else {
                store.saveSettings(reply, timeout)
                Toast.makeText(this, "已保存，对下一次请求生效", Toast.LENGTH_SHORT).show()
            }
        })
        content.addView(TextView(this).apply {
            text = "回复须完全一致，含空格。请在手表的快捷回复中配置同样的文字。修改设置后先保存。"
            setPadding(0, 8, 0, 12)
        })
        content.addView(makeButton("模拟电脑发起解锁请求") {
            if (ensureNotificationPermission()) {
                val request = store.beginDemo()
                if (request == null) {
                    Toast.makeText(this, "无法安全创建请求，请查看日志", Toast.LENGTH_LONG).show()
                } else {
                    try {
                        NotificationFactory.sendUnlockRequest(this, request)
                    } catch (_: SecurityException) {
                        store.cancel()
                        Toast.makeText(this, "通知无法发送，请检查通知权限", Toast.LENGTH_LONG).show()
                    }
                }
                refreshRequest()
            }
        })
        cancelButton = makeButton("取消当前请求") {
            store.cancel()
            refreshRequest()
        }
        content.addView(cancelButton)
        requestView = TextView(this).apply {
            textSize = 16f
            setPadding(0, 12, 0, 20)
        }
        content.addView(requestView)
        content.addView(TextView(this).apply {
            text = "通知兼容性测试（以下按钮只记录回调，不参与授权）"
            textSize = 18f
        })

        content.addView(makeButton("测试 1：普通通知 + 允许/拒绝") {
            if (ensureNotificationPermission()) NotificationFactory.sendActionTest(this)
        })
        content.addView(makeButton("测试 2：消息通知 + 允许/拒绝") {
            if (ensureNotificationPermission()) NotificationFactory.sendMessagingTest(this)
        })
        content.addView(makeButton("测试 3：快捷回复") {
            if (ensureNotificationPermission()) NotificationFactory.sendReplyTest(this)
        })
        content.addView(makeButton("清空操作日志") {
            getSharedPreferences(ActionReceiver.PREFS, MODE_PRIVATE).edit()
                .remove(ActionReceiver.KEY_LOG).apply()
            refreshLog()
        })

        val logTitle = TextView(this).apply {
            text = "操作回调日志"
            textSize = 18f
            setPadding(0, 24, 0, 8)
        }
        content.addView(logTitle)

        logView = TextView(this).apply {
            textSize = 14f
            text = "尚无操作记录"
        }
        content.addView(logView)

        val scroll = ScrollView(this).apply { addView(content) }
        setContentView(scroll)

        NotificationFactory.createChannels(this)
        requestNotificationPermissionIfNeeded()
    }

    override fun onResume() {
        super.onResume()
        getSharedPreferences(ActionReceiver.PREFS, MODE_PRIVATE)
            .registerOnSharedPreferenceChangeListener(this)
        getSharedPreferences(UnlockRequestStore.PREFS, MODE_PRIVATE)
            .registerOnSharedPreferenceChangeListener(this)
        getSharedPreferences(BleGattService.PREFS, MODE_PRIVATE)
            .registerOnSharedPreferenceChangeListener(this)
        refreshLog()
        handler.post(refreshTask)
    }

    override fun onPause() {
        handler.removeCallbacks(refreshTask)
        getSharedPreferences(ActionReceiver.PREFS, MODE_PRIVATE)
            .unregisterOnSharedPreferenceChangeListener(this)
        getSharedPreferences(UnlockRequestStore.PREFS, MODE_PRIVATE)
            .unregisterOnSharedPreferenceChangeListener(this)
        getSharedPreferences(BleGattService.PREFS, MODE_PRIVATE)
            .unregisterOnSharedPreferenceChangeListener(this)
        super.onPause()
    }

    override fun onSharedPreferenceChanged(sharedPreferences: SharedPreferences, key: String?) {
        if (key == ActionReceiver.KEY_LOG) refreshLog()
        else if (key == BleGattService.KEY_STATUS) refreshBleStatus()
        else refreshRequest()
    }

    private fun refreshRequest() {
        val request = UnlockRequestStore(this).current()
        requestView.text = when (request?.state) {
            null -> "尚未发起模拟请求"
            UnlockRequestStore.State.PENDING -> "等待快捷回复，剩余 ${(request.remainingMillis + 999) / 1000} 秒"
            UnlockRequestStore.State.APPROVED -> "回复匹配，请求已确认（仅验证，不执行解锁）"
            UnlockRequestStore.State.REJECTED -> "回复错误，本次请求已拒绝，可重新发起"
            UnlockRequestStore.State.EXPIRED -> "请求已超时或失效，可重新发起"
            UnlockRequestStore.State.REPLACED -> "旧请求已作废"
            UnlockRequestStore.State.CANCELLED -> "请求已取消，可重新发起"
        }
        cancelButton.isEnabled = request?.state == UnlockRequestStore.State.PENDING
    }

    private fun refreshBleStatus() {
        val value = if (BleGattService.isRunning) {
            getSharedPreferences(BleGattService.PREFS, MODE_PRIVATE).getString(BleGattService.KEY_STATUS, null)
                ?: "正在启动"
        } else "蓝牙服务未开启"
        bleStatusView.text = value
    }

    private fun ensureBluetoothPermissions(): Boolean {
        if (Build.VERSION.SDK_INT < 31) return true
        val required = arrayOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_ADVERTISE)
        val missing = required.filter { ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) return true
        ActivityCompat.requestPermissions(this, missing.toTypedArray(), REQUEST_BLUETOOTH)
        Toast.makeText(this, "授权后请再次点击开启 BLE 通信测试", Toast.LENGTH_LONG).show()
        return false
    }

    private fun makeButton(label: String, action: () -> Unit): Button {
        return Button(this).apply {
            text = label
            setOnClickListener { action() }
        }
    }

    private fun ensureNotificationPermission(): Boolean {
        return if (Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            == PackageManager.PERMISSION_GRANTED
        ) {
            if (NotificationManagerCompat.from(this).areNotificationsEnabled()) {
                true
            } else {
                Toast.makeText(this, "请在系统设置中启用 WatchUnlock 通知", Toast.LENGTH_LONG).show()
                false
            }
        } else {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                REQUEST_NOTIFICATIONS
            )
            false
        }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                REQUEST_NOTIFICATIONS
            )
        }
    }

    private fun refreshLog() {
        val value = getSharedPreferences(ActionReceiver.PREFS, MODE_PRIVATE)
            .getString(ActionReceiver.KEY_LOG, null)
        logView.text = value ?: "尚无操作记录"
    }

    companion object {
        private const val REQUEST_NOTIFICATIONS = 1001
        private const val REQUEST_BLUETOOTH = 1002
    }
}
