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

        val timestamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
            .format(Date())
        val line = "[$timestamp] $result"

        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val previous = prefs.getString(KEY_LOG, null)
        prefs.edit().putString(KEY_LOG, listOfNotNull(line, previous).joinToString("\n")).apply()

        Log.i(TAG, line)
        Toast.makeText(context, result, Toast.LENGTH_LONG).show()
    }

    companion object {
        const val PREFS = "watchunlock_test"
        const val KEY_LOG = "action_log"
        const val KEY_REPLY = "watchunlock_reply"
        const val ACTION_ALLOW = "com.watchunlock.app.ALLOW"
        const val ACTION_DENY = "com.watchunlock.app.DENY"
        const val ACTION_REPLY = "com.watchunlock.app.REPLY"
        private const val TAG = "WatchUnlock"
    }
}
