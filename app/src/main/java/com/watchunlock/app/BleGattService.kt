package com.watchunlock.app

import android.annotation.SuppressLint
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothStatusCodes
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.BluetoothLeAdvertiser
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.ParcelUuid
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import java.util.ArrayDeque
import java.security.SecureRandom
import android.util.Base64
import org.json.JSONObject

/** Foreground BLE transport demo. All mutable GATT/request state is confined to the main thread. */
@SuppressLint("MissingPermission") // The Activity grants CONNECT/ADVERTISE before starting this service.
class BleGattService : Service() {
    private val handler = Handler(Looper.getMainLooper())
    private var server: BluetoothGattServer? = null
    private var advertiser: BluetoothLeAdvertiser? = null
    private var peer: BluetoothDevice? = null
    private var subscribed = false
    private var connectedAt = 0L
    private var closeAfterSending = false
    private var keyVersion: String? = null
    private var sessionId: String? = null
    private var sessionKey: ByteArray? = null
    private data class Pairing(val id: String, val clientNonce: String, val serverNonce: String, val deadline: Long)
    private var pairing: Pairing? = null
    private var pending: BleProtocol.Challenge? = null
    private val reader = BleProtocol.FrameReader()
    private var lastWrite = 0L
    private val outgoing = ArrayDeque<ByteArray>()
    private var sending = false
    private var sentAt = 0L
    private val seenRequests = ArrayDeque<String>()
    private val approval = BluetoothGattCharacteristic(
        BleProtocol.APPROVAL, BluetoothGattCharacteristic.PROPERTY_INDICATE, 0
    ).apply {
        addDescriptor(BluetoothGattDescriptor(
            BleProtocol.CCCD,
            BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE
        ))
    }

    private val completionReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (keyVersion != PairingKey(this@BleGattService).version) {
                peer?.let { disconnect(it) }
                clearConnection()
                return
            }
            val challenge = pending ?: return
            if (intent.getStringExtra(UnlockRequestStore.EXTRA_REQUEST_ID) != challenge.id) return
            val state = UnlockRequestStore.State.entries.firstOrNull {
                it.name == intent.getStringExtra(UnlockRequestStore.EXTRA_STATE)
            } ?: return
            if (state == UnlockRequestStore.State.PENDING) return
            pending = null
            queueResult(challenge, state.name.lowercase(java.util.Locale.ROOT))
            updateStatus("Key 配对成功，等待电脑请求")
        }
    }

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
            handler.post { if (server != null) updateStatus("蓝牙服务已开启，等待电脑连接") }
        }
        override fun onStartFailure(errorCode: Int) {
            handler.post { if (server != null) fail("蓝牙广播启动失败（$errorCode）") }
        }
    }

    private val gattCallback = object : BluetoothGattServerCallback() {
        override fun onServiceAdded(status: Int, service: BluetoothGattService) {
            handler.post {
                if (server == null) return@post
                if (status != BluetoothGatt.GATT_SUCCESS) {
                    fail("GATT 服务启动失败")
                    return@post
                }
                try {
                    advertiser?.startAdvertising(
                        AdvertiseSettings.Builder().setConnectable(true)
                            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
                            .setTimeout(0).build(),
                        AdvertiseData.Builder().addServiceUuid(ParcelUuid(BleProtocol.SERVICE)).build(),
                        advertiseCallback
                    )
                } catch (_: SecurityException) { fail("蓝牙权限已撤销") }
            }
        }

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            handler.post {
                if (server == null) return@post
                if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
                    if (peer != null && peer != device) {
                        disconnect(device)
                    } else {
                        peer = device
                        connectedAt = SystemClock.elapsedRealtime()
                        updateStatus("电脑已连接，等待订阅")
                    }
                } else if (peer == device && newState == BluetoothProfile.STATE_DISCONNECTED) {
                    clearConnection()
                    updateStatus("连接已断开，等待电脑重新连接")
                }
            }
        }

        override fun onDescriptorReadRequest(device: BluetoothDevice, requestId: Int, offset: Int, descriptor: BluetoothGattDescriptor) {
            handler.post {
                val valid = peer == device && descriptor.uuid == BleProtocol.CCCD && offset == 0
                respond(device, requestId, valid, if (subscribed) BluetoothGattDescriptor.ENABLE_INDICATION_VALUE else BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE)
            }
        }

        override fun onDescriptorWriteRequest(device: BluetoothDevice, requestId: Int, descriptor: BluetoothGattDescriptor,
            preparedWrite: Boolean, responseNeeded: Boolean, offset: Int, value: ByteArray) {
            val bytes = value.copyOf()
            handler.post {
                val valid = peer == device && descriptor.uuid == BleProtocol.CCCD && !preparedWrite && offset == 0 &&
                    (bytes.contentEquals(BluetoothGattDescriptor.ENABLE_INDICATION_VALUE) ||
                        bytes.contentEquals(BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE))
                if (valid) {
                    subscribed = bytes.contentEquals(BluetoothGattDescriptor.ENABLE_INDICATION_VALUE)
                }
                if (responseNeeded) respond(device, requestId, valid)
                if (valid) {
                    if (!subscribed) {
                        disconnect(device)
                        clearConnection()
                        updateStatus("订阅已取消，等待重新连接")
                    }
                    else updateStatus("已订阅，等待 Key 配对")
                }
            }
        }

        override fun onCharacteristicReadRequest(device: BluetoothDevice, requestId: Int, offset: Int,
            characteristic: BluetoothGattCharacteristic) {
            handler.post {
                val valid = peer == device && characteristic.uuid == BleProtocol.STATUS && offset == 0
                respond(device, requestId, valid, "paired-v2".toByteArray(Charsets.UTF_8))
            }
        }

        override fun onCharacteristicWriteRequest(device: BluetoothDevice, requestId: Int,
            characteristic: BluetoothGattCharacteristic, preparedWrite: Boolean, responseNeeded: Boolean,
            offset: Int, value: ByteArray) {
            val bytes = value.copyOf()
            handler.post {
                val valid = peer == device && !preparedWrite && offset == 0 && bytes.size <= BleProtocol.CHUNK_BYTES &&
                    characteristic.uuid == BleProtocol.CHALLENGE && subscribed && !closeAfterSending
                if (responseNeeded) respond(device, requestId, valid)
                if (!valid) return@post
                try {
                    if (SystemClock.elapsedRealtime() - lastWrite > 5000) reader.clear()
                    lastWrite = SystemClock.elapsedRealtime()
                    for (message in reader.append(bytes)) handleMessage(BleProtocol.parseMessage(message))
                } catch (_: Exception) {
                    reader.clear()
                    ActionReceiver.record(this@BleGattService, "已拒绝格式错误、过大或签名无效的 BLE 消息")
                    disconnect(device)
                    clearConnection()
                }
            }
        }

        override fun onNotificationSent(device: BluetoothDevice, status: Int) {
            handler.post {
                if (peer != device || !sending) return@post
                sending = false
                if (status == BluetoothGatt.GATT_SUCCESS) {
                    if (outgoing.isEmpty() && closeAfterSending) {
                        disconnect(device)
                        clearConnection()
                    } else sendNext()
                }
                else {
                    disconnect(device)
                    clearConnection()
                    updateStatus("结果发送失败，等待重新连接")
                }
            }
        }
    }

    private val tick = object : Runnable {
        override fun run() {
            UnlockRequestStore(this@BleGattService).current() // Reconciles expiry even when the Activity is closed.
            val now = SystemClock.elapsedRealtime()
            if (peer != null && keyVersion != PairingKey(this@BleGattService).version) {
                peer?.let { disconnect(it) }
                clearConnection()
                updateStatus("Key 已修改，请重新连接配对")
            }
            if (pairing?.let { now >= it.deadline } == true && !closeAfterSending) pairFailed("expired")
            if (peer != null && sessionKey == null && now - connectedAt > 60000) {
                peer?.let { disconnect(it) }
                clearConnection()
                updateStatus("配对未完成，连接已释放")
            }
            if (sending && SystemClock.elapsedRealtime() - sentAt > 5000) {
                peer?.let { disconnect(it) }
                clearConnection()
                updateStatus("结果传输超时，等待重新连接")
            }
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate() {
        super.onCreate()
        ContextCompat.registerReceiver(this, completionReceiver,
            IntentFilter(UnlockRequestStore.ACTION_COMPLETED), ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (server != null) return START_NOT_STICKY
        try {
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "WatchUnlock 蓝牙服务", NotificationManager.IMPORTANCE_LOW))
            val stop = PendingIntent.getService(this, 1, Intent(this, BleGattService::class.java).setAction(ACTION_STOP),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val open = PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val notification = NotificationCompat.Builder(this, CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setContentTitle("WatchUnlock 蓝牙通信测试")
                .setContentText("等待电脑连接；本阶段不执行解锁")
                .setContentIntent(open).setOngoing(true)
                .addAction(android.R.drawable.ic_menu_close_clear_cancel, "停止", stop).build()
            if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
            else startForeground(NOTIFICATION_ID, notification)

            val adapter = getSystemService(BluetoothManager::class.java)?.adapter
            if (adapter == null || !adapter.isEnabled || !adapter.isMultipleAdvertisementSupported) {
                fail("请开启蓝牙，并确认手机支持 BLE 广播")
                return START_NOT_STICKY
            }
            advertiser = adapter.bluetoothLeAdvertiser
            if (advertiser == null) {
                fail("手机 BLE 广播不可用")
                return START_NOT_STICKY
            }
            val store = UnlockRequestStore(this)
            val configuredKey = PairingKey(this)
            if (!configuredKey.configured()) {
                fail("请先保存配对 Key")
                return START_NOT_STICKY
            }
            keyVersion = configuredKey.version
            if (store.current()?.source == UnlockRequestStore.Source.BLE) store.cancel()
            server = getSystemService(BluetoothManager::class.java).openGattServer(this, gattCallback)
            val service = BluetoothGattService(BleProtocol.SERVICE, BluetoothGattService.SERVICE_TYPE_PRIMARY).apply {
                addCharacteristic(BluetoothGattCharacteristic(BleProtocol.CHALLENGE,
                    BluetoothGattCharacteristic.PROPERTY_WRITE, BluetoothGattCharacteristic.PERMISSION_WRITE))
                addCharacteristic(approval)
                addCharacteristic(BluetoothGattCharacteristic(BleProtocol.STATUS,
                    BluetoothGattCharacteristic.PROPERTY_READ, BluetoothGattCharacteristic.PERMISSION_READ))
            }
            if (server?.addService(service) != true) fail("无法创建 GATT 服务")
            else {
                isRunning = true
                updateStatus("正在启动蓝牙广播")
                handler.post(tick)
            }
        } catch (_: SecurityException) { fail("请授予附近设备和通知权限") }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun handleMessage(value: JSONObject) {
        val key = PairingKey(this)
        require(keyVersion == key.version && !closeAfterSending)
        when (value.getString("type")) {
            "pair_request" -> {
                BleProtocol.fields(value, "request_id", "client_nonce", "mac")
                require(sessionKey == null && pairing == null && outgoing.isEmpty() && !sending)
                val id = BleProtocol.uuid(value.getString("request_id"))
                val clientNonce = value.getString("client_nonce").also { BleProtocol.base64Bytes(it) }
                if (!key.verify(value.getString("mac"), "pair_request", id, clientNonce)) {
                    pairFailed("key_mismatch")
                    return
                }
                val serverNonce = Base64.encodeToString(ByteArray(32).also { SecureRandom().nextBytes(it) }, Base64.NO_WRAP)
                pairing = Pairing(id, clientNonce, serverNonce, SystemClock.elapsedRealtime() + 30000)
                queueMessage(JSONObject().put("v", 2).put("type", "pair_challenge").put("request_id", id)
                    .put("client_nonce", clientNonce).put("server_nonce", serverNonce)
                    .put("mac", key.sign("pair_challenge", id, clientNonce, serverNonce)))
                updateStatus("Key 校验中，等待电脑确认")
            }
            "pair_confirm" -> {
                BleProtocol.fields(value, "request_id", "client_nonce", "server_nonce", "mac")
                val expected = pairing ?: error("No pairing request")
                require(SystemClock.elapsedRealtime() < expected.deadline)
                require(value.getString("request_id") == expected.id && value.getString("client_nonce") == expected.clientNonce &&
                    value.getString("server_nonce") == expected.serverNonce)
                if (!key.verify(value.getString("mac"), "pair_confirm", expected.id, expected.clientNonce, expected.serverNonce)) {
                    pairFailed("key_mismatch")
                    return
                }
                sessionKey = key.sessionKey(expected.id, expected.clientNonce, expected.serverNonce)
                sessionId = expected.id
                pairing = null
                queueMessage(JSONObject().put("v", 2).put("type", "pair_ok").put("request_id", expected.id)
                    .put("client_nonce", expected.clientNonce).put("server_nonce", expected.serverNonce)
                    .put("mac", key.sign("pair_ok", expected.id, expected.clientNonce, expected.serverNonce)))
                updateStatus("Key 配对成功，等待电脑请求")
                ActionReceiver.record(this, "电脑与手机 Key 配对成功")
            }
            "unlock_request" -> {
                val id = sessionId ?: error("Not paired")
                val activeKey = sessionKey ?: error("Not paired")
                handleChallenge(BleProtocol.parseChallenge(value, id, activeKey))
            }
            else -> error("Unsupported message")
        }
    }

    private fun pairFailed(reason: String) {
        pairing = null
        closeAfterSending = true
        ActionReceiver.record(this, if (reason == "key_mismatch") "Key 不一致，配对失败" else "Key 配对超时")
        updateStatus("Key 配对失败，请检查两端设置")
        queueMessage(JSONObject().put("v", 2).put("type", "pair_error").put("reason", reason))
    }

    private fun handleChallenge(challenge: BleProtocol.Challenge) {
        if (seenRequests.contains(challenge.id)) {
            ActionReceiver.record(this, "已忽略重复 BLE 请求")
            return
        }
        if (pending != null || UnlockRequestStore(this).current()?.state == UnlockRequestStore.State.PENDING) {
            // Avoid building a queue of busy responses while a result is still being sent.
            if (!sending && outgoing.isEmpty()) queueResult(challenge, "busy")
            return
        }
        if (!notificationsEnabled()) {
            queueResult(challenge, "unavailable")
            return
        }
        seenRequests.addLast(challenge.id)
        if (seenRequests.size > 128) seenRequests.removeFirst()
        val request = UnlockRequestStore(this).beginBle(challenge.id, challenge.lifetime, challenge.computerName)
        if (request == null) {
            queueResult(challenge, "unavailable")
            return
        }
        pending = challenge
        try {
            NotificationFactory.sendUnlockRequest(this, request)
            updateStatus("已收到电脑请求，等待快捷回复")
        } catch (_: SecurityException) { cancelPending() }
    }

    private fun notificationsEnabled() = androidx.core.app.NotificationManagerCompat.from(this).areNotificationsEnabled()

    private fun queueResult(challenge: BleProtocol.Challenge, decision: String) {
        val key = sessionKey ?: return
        queueMessage(BleProtocol.result(challenge, decision, key))
    }

    private fun queueMessage(value: JSONObject) {
        if (peer == null || !subscribed) return
        for (chunk in BleProtocol.frame(value).asList().chunked(BleProtocol.CHUNK_BYTES)) {
            outgoing.addLast(chunk.toByteArray())
        }
        sendNext()
    }

    private fun sendNext() {
        val device = peer ?: return
        val gatt = server ?: return
        if (sending || outgoing.isEmpty() || !subscribed) return
        val bytes = outgoing.removeFirst()
        sending = true
        sentAt = SystemClock.elapsedRealtime()
        val accepted = try {
            if (Build.VERSION.SDK_INT >= 33) {
                gatt.notifyCharacteristicChanged(device, approval, true, bytes) == BluetoothStatusCodes.SUCCESS
            } else {
                @Suppress("DEPRECATION")
                approval.value = bytes
                @Suppress("DEPRECATION")
                gatt.notifyCharacteristicChanged(device, approval, true)
            }
        } catch (_: SecurityException) { false }
        if (!accepted) {
            disconnect(device)
            clearConnection()
            updateStatus("发送结果失败，等待重新连接")
        }
    }

    private fun cancelPending() {
        val challenge = pending ?: return
        val store = UnlockRequestStore(this)
        if (store.current()?.id == challenge.id) store.cancel()
    }

    private fun clearConnection() {
        peer = null
        subscribed = false
        outgoing.clear()
        sending = false
        closeAfterSending = false
        sessionKey?.fill(0)
        sessionKey = null
        sessionId = null
        pairing = null
        seenRequests.clear()
        reader.clear()
        cancelPending()
        pending = null
    }

    private fun respond(device: BluetoothDevice, id: Int, valid: Boolean, bytes: ByteArray? = null) {
        try { server?.sendResponse(device, id, if (valid) BluetoothGatt.GATT_SUCCESS else BluetoothGatt.GATT_FAILURE, 0, bytes) }
        catch (_: SecurityException) { fail("蓝牙权限已撤销") }
    }

    private fun disconnect(device: BluetoothDevice) {
        try { server?.cancelConnection(device) } catch (_: SecurityException) { }
    }

    private fun updateStatus(value: String) {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putString(KEY_STATUS, value).apply()
    }

    private fun fail(reason: String) {
        ActionReceiver.record(this, reason)
        stopSelf()
    }

    override fun onDestroy() {
        isRunning = false
        handler.removeCallbacksAndMessages(null)
        clearConnection()
        try { advertiser?.stopAdvertising(advertiseCallback) } catch (_: SecurityException) { }
        val closingServer = server
        server = null
        try { closingServer?.close() } catch (_: SecurityException) { }
        unregisterReceiver(completionReceiver)
        updateStatus("蓝牙服务已停止")
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    companion object {
        var isRunning = false
            private set
        const val PREFS = "watchunlock_ble"
        const val KEY_STATUS = "status"
        const val ACTION_STOP = "com.watchunlock.app.STOP_BLE"
        private const val CHANNEL = "watchunlock_ble_service"
        private const val NOTIFICATION_ID = 2001
    }
}
