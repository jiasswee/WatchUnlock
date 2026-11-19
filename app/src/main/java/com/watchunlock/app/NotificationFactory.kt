package com.watchunlock.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.Person
import androidx.core.app.RemoteInput

object NotificationFactory {
    private const val CHANNEL_ACTIONS = "watchunlock_actions"
    private const val CHANNEL_MESSAGES = "watchunlock_messages"
    private const val UNLOCK_NOTIFICATION_ID = 1004

    fun createChannels(context: Context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ACTIONS,
                    "WatchUnlock 操作测试",
                    NotificationManager.IMPORTANCE_HIGH
                ).apply {
                    description = "测试手表是否支持第三方通知操作按钮"
                }
            )
            manager.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_MESSAGES,
                    "WatchUnlock 消息测试",
                    NotificationManager.IMPORTANCE_HIGH
                ).apply {
                    description = "测试消息通知和快捷回复"
                }
            )
        }
    }

    fun sendActionTest(context: Context) {
        val notification = base(context, CHANNEL_ACTIONS, 1001)
            .setContentTitle("WatchUnlock：普通通知")
            .setContentText("请尝试在手表上展开通知并点击操作。")
            .addAction(action(context, "允许", ActionReceiver.ACTION_ALLOW, 1101))
            .addAction(action(context, "拒绝", ActionReceiver.ACTION_DENY, 1102))
            .build()
        notify(context, 1001, notification)
    }

    fun sendMessagingTest(context: Context) {
        val me = Person.Builder().setName("WatchUnlock").setKey("watchunlock").build()
        val message = NotificationCompat.MessagingStyle(me)
            .addMessage("这是一条模拟的解锁请求。", System.currentTimeMillis(), "Windows")
            .setConversationTitle("WatchUnlock 测试会话")
            .setGroupConversation(false)

        val notification = base(context, CHANNEL_MESSAGES, 1002)
            .setStyle(message)
            .setContentTitle("WatchUnlock 测试会话")
            .setContentText("请检查手表是否提供操作或回复入口。")
            .addAction(action(context, "允许", ActionReceiver.ACTION_ALLOW, 1201))
            .addAction(action(context, "拒绝", ActionReceiver.ACTION_DENY, 1202))
            .build()
        notify(context, 1002, notification)
    }

    fun sendReplyTest(context: Context) {
        val remoteInput = RemoteInput.Builder(ActionReceiver.KEY_REPLY)
            .setLabel("回复测试内容")
            .build()

        val intent = Intent(context, ActionReceiver::class.java).apply {
            action = ActionReceiver.ACTION_REPLY
        }
        // RemoteInput requires a mutable PendingIntent so the system/watch can attach reply text.
        val pendingIntent = PendingIntent.getBroadcast(
            context,
            1301,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
        )
        val replyAction = NotificationCompat.Action.Builder(
            android.R.drawable.ic_menu_send,
            "回复",
            pendingIntent
        ).addRemoteInput(remoteInput).build()

        val notification = base(context, CHANNEL_MESSAGES, 1003)
            .setContentTitle("WatchUnlock：快捷回复测试")
            .setContentText("尝试从手表发送一条回复。")
            .addAction(replyAction)
            .build()
        notify(context, 1003, notification)
    }

    fun sendUnlockRequest(context: Context, request: UnlockRequestStore.Request) {
        require(request.state == UnlockRequestStore.State.PENDING && request.remainingMillis > 0)
        val remoteInput = RemoteInput.Builder(ActionReceiver.KEY_REPLY)
            .setLabel("回复已设置的确认文字")
            .build()
        val intent = Intent(context, ActionReceiver::class.java).apply {
            action = ActionReceiver.ACTION_UNLOCK_REPLY
            // PendingIntent identity includes data. An old watch notification cannot target a new request.
            data = Uri.Builder().scheme("watchunlock").authority("unlock-request")
                .appendPath(request.id).build()
        }
        val pendingIntent = PendingIntent.getBroadcast(
            context,
            UNLOCK_NOTIFICATION_ID,
            intent,
            PendingIntent.FLAG_CANCEL_CURRENT or PendingIntent.FLAG_ONE_SHOT or PendingIntent.FLAG_MUTABLE
        )
        val replyAction = NotificationCompat.Action.Builder(
            android.R.drawable.ic_menu_send, "回复", pendingIntent
        )
            .addRemoteInput(remoteInput)
            .setSemanticAction(NotificationCompat.Action.SEMANTIC_ACTION_REPLY)
            .setAllowGeneratedReplies(false)
            .setShowsUserInterface(false)
            .build()
        val me = Person.Builder().setName("WatchUnlock").setKey("watchunlock").build()
        val sender = Person.Builder().setName("电脑（模拟请求）").setKey("windows-demo").build()
        val style = NotificationCompat.MessagingStyle(me)
            .setGroupConversation(false)
            .addMessage(
                "电脑请求解锁，请在 ${request.remainingMillis / 1000} 秒内回复已设置的确认文字。",
                System.currentTimeMillis(), sender
            )
        val notification = base(context, CHANNEL_MESSAGES, UNLOCK_NOTIFICATION_ID)
            .setStyle(style)
            .setContentTitle("WatchUnlock：模拟解锁请求")
            .setContentText("请回复已设置的确认文字。")
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setTimeoutAfter(request.remainingMillis)
            .addAction(replyAction)
            .build()
        notify(context, UNLOCK_NOTIFICATION_ID, notification)
    }

    fun cancelUnlockRequest(context: Context) {
        context.getSystemService(NotificationManager::class.java).cancel(UNLOCK_NOTIFICATION_ID)
    }

    private fun base(context: Context, channel: String, requestCode: Int): NotificationCompat.Builder {
        val openIntent = Intent(context, MainActivity::class.java)
        val openPendingIntent = PendingIntent.getActivity(
            context,
            requestCode,
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(context, channel)
            .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
            .setContentIntent(openPendingIntent)
            .setAutoCancel(false)
            .setOngoing(false)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
    }

    private fun action(
        context: Context,
        label: String,
        action: String,
        requestCode: Int
    ): NotificationCompat.Action {
        val intent = Intent(context, ActionReceiver::class.java).apply {
            this.action = action
        }
        val pendingIntent = PendingIntent.getBroadcast(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Action.Builder(
            android.R.drawable.ic_menu_view,
            label,
            pendingIntent
        ).build()
    }

    private fun notify(context: Context, id: Int, notification: Notification) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.notify(id, notification)
    }
}
