package com.watchunlock.app

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat

class MainActivity : Activity() {

    private lateinit var logView: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(36, 32, 36, 24)
        }

        val title = TextView(this).apply {
            text = "WatchUnlock 通知测试"
            textSize = 22f
        }
        content.addView(title)

        val description = TextView(this).apply {
            text = "依次发送三种通知，并在 Watch Fit 3 上尝试查看和操作。"
            textSize = 15f
            setPadding(0, 12, 0, 20)
        }
        content.addView(description)

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
        if (::logView.isInitialized) refreshLog()
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
            true
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
    }
}
