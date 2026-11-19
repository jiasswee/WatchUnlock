package com.watchunlock.app

import android.content.Context
import android.os.SystemClock
import android.provider.Settings
import java.util.UUID

/** Human confirmation for local and BLE transport demos; neither unlocks Windows. */
class UnlockRequestStore(private val context: Context) {
    private val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    enum class State { PENDING, APPROVED, REJECTED, EXPIRED, REPLACED, CANCELLED }
    enum class Source { DEMO, BLE }

    data class Request(
        val id: String, val state: State, val remainingMillis: Long,
        val source: Source = Source.DEMO, val computerName: String = "电脑"
    )

    val expectedReply: String
        get() = prefs.getString(KEY_CONFIG_REPLY, null) ?: "好"

    val timeoutSeconds: Int
        get() = prefs.getInt(KEY_CONFIG_TIMEOUT, DEFAULT_TIMEOUT_SECONDS)

    fun saveSettings(reply: String, timeout: Int) = synchronized(lock) {
        require(reply.isNotBlank() && reply.length <= MAX_REPLY_LENGTH)
        require(timeout in MIN_TIMEOUT_SECONDS..MAX_TIMEOUT_SECONDS)
        prefs.edit()
            .putString(KEY_CONFIG_REPLY, reply)
            .putInt(KEY_CONFIG_TIMEOUT, timeout)
            .apply()
    }

    fun beginDemo(): Request? = begin(UUID.randomUUID().toString(), timeoutSeconds, Source.DEMO, "电脑")

    fun beginBle(id: String, lifetimeSeconds: Int, computerName: String): Request? =
        begin(id, minOf(lifetimeSeconds, timeoutSeconds), Source.BLE, computerName)

    private fun begin(id: String, lifetimeSeconds: Int, source: Source, computerName: String): Request? = synchronized(lock) {
        require(lifetimeSeconds in MIN_TIMEOUT_SECONDS..MAX_TIMEOUT_SECONDS)
        val boot = bootCount()
        if (boot < 0) {
            ActionReceiver.record(context, "无法读取开机标识，未发起模拟请求")
            return@synchronized null
        }
        if (current()?.state == State.PENDING) finish(State.REPLACED)

        val now = SystemClock.elapsedRealtime()
        val duration = lifetimeSeconds * 1000L
        // Snapshot settings: changing them later must not change an active request.
        prefs.edit()
            .putString(KEY_ID, id)
            .putString(KEY_STATE, State.PENDING.name)
            .putString(KEY_SOURCE, source.name)
            .putString(KEY_COMPUTER, computerName)
            .putString(KEY_REQUEST_REPLY, expectedReply)
            .putLong(KEY_STARTED, now)
            .putLong(KEY_DEADLINE, now + duration)
            .putInt(KEY_BOOT, boot)
            .apply()
        ActionReceiver.record(context, "${if (source == Source.BLE) "BLE 通信" else "模拟解锁"}请求已发起，有效期 ${duration / 1000} 秒")
        Request(id, State.PENDING, duration, source, computerName)
    }

    fun current(): Request? = synchronized(lock) {
        val id = prefs.getString(KEY_ID, null) ?: return@synchronized null
        val stateName = prefs.getString(KEY_STATE, null)
        val state = State.entries.firstOrNull { it.name == stateName }
            ?: return@synchronized null
        val source = source()
        val computerName = prefs.getString(KEY_COMPUTER, null) ?: "电脑"
        if (state != State.PENDING) return@synchronized Request(id, state, 0, source, computerName)

        val deadline = prefs.getLong(KEY_DEADLINE, 0)
        val boot = bootCount()
        val now = SystemClock.elapsedRealtime()
        // A monotonic deadline plus boot identity rejects clock changes and rebooted requests.
        if (boot < 0 || boot != prefs.getInt(KEY_BOOT, -1) ||
            now < prefs.getLong(KEY_STARTED, Long.MAX_VALUE) || now >= deadline
        ) {
            finish(State.EXPIRED)
            return@synchronized Request(id, State.EXPIRED, 0, source, computerName)
        }
        Request(id, state, deadline - now, source, computerName)
    }

    /** Null means a stale/duplicate callback. It must not affect the current request. */
    fun receiveReply(requestId: String?, reply: String?): State? = synchronized(lock) {
        val request = current() ?: return@synchronized null
        if (request.id != requestId || request.state != State.PENDING) {
            return@synchronized null
        }
        if (request.source == Source.BLE && !BleGattService.isRunning) {
            finish(State.CANCELLED)
            return@synchronized null
        }
        val expected = prefs.getString(KEY_REQUEST_REPLY, null)
        if (SystemClock.elapsedRealtime() >= prefs.getLong(KEY_DEADLINE, 0)) {
            finish(State.EXPIRED)
            return@synchronized null
        }
        val state = if (expected != null && reply == expected) State.APPROVED else State.REJECTED
        finish(state)
        state
    }

    fun cancel() = synchronized(lock) {
        if (current()?.state == State.PENDING) finish(State.CANCELLED)
    }

    private fun finish(state: State) {
        val isBle = source() == Source.BLE
        prefs.edit().putString(KEY_STATE, state.name).remove(KEY_REQUEST_REPLY).apply()
        NotificationFactory.cancelUnlockRequest(context)
        val message = when (state) {
            State.APPROVED -> if (isBle) "回复匹配，BLE 请求已确认（仅通信测试）" else "回复匹配，模拟请求已通过（未连接 Windows）"
            State.REJECTED -> "回复错误或为空，本次请求已拒绝"
            State.EXPIRED -> "请求已超时或因手机重启失效，不予授权"
            State.REPLACED -> "旧请求已作废，正在发起新请求"
            State.CANCELLED -> "请求已取消，不予授权"
            State.PENDING -> error("Cannot finish a pending request")
        }
        ActionReceiver.record(context, message)
        if (isBle) {
            context.sendBroadcast(android.content.Intent(ACTION_COMPLETED)
                .setPackage(context.packageName)
                .putExtra(EXTRA_REQUEST_ID, prefs.getString(KEY_ID, null))
                .putExtra(EXTRA_STATE, state.name))
        }
    }

    private fun source(): Source = if (prefs.getString(KEY_SOURCE, null) == Source.BLE.name) Source.BLE else Source.DEMO

    private fun bootCount(): Int = Settings.Global.getInt(
        context.contentResolver, Settings.Global.BOOT_COUNT, -1
    )

    companion object {
        const val PREFS = "watchunlock_approval"
        const val DEFAULT_TIMEOUT_SECONDS = 10
        const val MIN_TIMEOUT_SECONDS = 5
        const val MAX_TIMEOUT_SECONDS = 120
        const val MAX_REPLY_LENGTH = 64
        const val ACTION_COMPLETED = "com.watchunlock.app.REQUEST_COMPLETED"
        const val EXTRA_REQUEST_ID = "request_id"
        const val EXTRA_STATE = "state"
        private val lock = Any()
        private const val KEY_CONFIG_REPLY = "expected_reply"
        private const val KEY_CONFIG_TIMEOUT = "timeout_seconds"
        private const val KEY_ID = "request_id"
        private const val KEY_STATE = "request_state"
        private const val KEY_SOURCE = "request_source"
        private const val KEY_COMPUTER = "computer_name"
        private const val KEY_REQUEST_REPLY = "request_reply"
        private const val KEY_STARTED = "started_elapsed"
        private const val KEY_DEADLINE = "deadline_elapsed"
        private const val KEY_BOOT = "boot_count"
    }
}
