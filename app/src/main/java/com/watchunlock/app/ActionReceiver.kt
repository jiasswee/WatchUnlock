package com.watchunlock.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import android.widget.Toast
import androidx.core.app.RemoteInput
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class ActionReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_UNLOCK_REPLY) {
            val uri = intent.data
            val requestId = if (uri?.scheme == "watchunlock" && uri.host == "unlock-request") {
                uri.pathSegments.singleOrNull()
            } else {
                null
            }
            val reply = RemoteInput.getResultsFromIntent(intent)
                ?.getCharSequence(KEY_REPLY)?.toString()
            val result = when (UnlockRequestStore(context).receiveReply(requestId, reply)) {
                UnlockRequestStore.State.APPROVED -> "回复匹配，模拟请求已通过（未连接 Windows）"
                UnlockRequestStore.State.REJECTED -> "回复错误或为空，本次请求已拒绝"
                else -> {
                    record(context, "已忽略失效、重复或不属于当前请求的回复")
                    "本次回复已失效，请重新发起请求"
                }
            }
            Toast.makeText(context, result, Toast.LENGTH_LONG).show()
            return
        }

        val result = when (intent.action) {
            ACTION_ALLOW -> "收到操作：允许"
            ACTION_DENY -> "收到操作：拒绝"
            ACTION_REPLY -> {
                val reply = RemoteInput.getResultsFromIntent(intent)
                    ?.getCharSequence(KEY_REPLY)?.toString()
                "收到快捷回复：${reply ?: "（没有读取到回复内容）"}"
            }
            else -> "收到未知操作：${intent.action}"
        }

        record(context, result)
        Toast.makeText(context, result, Toast.LENGTH_LONG).show()
    }

    companion object {
        const val PREFS = "watchunlock_test"
        const val KEY_LOG = "action_log"
        const val KEY_REPLY = "watchunlock_reply"
        const val ACTION_ALLOW = "com.watchunlock.app.ALLOW"
        const val ACTION_DENY = "com.watchunlock.app.DENY"
        const val ACTION_REPLY = "com.watchunlock.app.REPLY"
        const val ACTION_UNLOCK_REPLY = "com.watchunlock.app.UNLOCK_REPLY"
        private const val TAG = "WatchUnlock"

        fun record(context: Context, result: String) {
            val timestamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
                .format(Date())
            val line = "[$timestamp] $result"
            val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val previous = prefs.getString(KEY_LOG, null)
            val lines = listOfNotNull(line, previous).joinToString("\n")
                .lineSequence().take(100).joinToString("\n")
            prefs.edit().putString(KEY_LOG, lines).apply()
            Log.i(TAG, line)
        }
    }
}
