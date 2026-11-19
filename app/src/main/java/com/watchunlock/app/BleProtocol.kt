package com.watchunlock.app

import android.util.Base64
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.UUID

/** Unauthenticated transport prototype, deliberately separate from final authorization. */
object BleProtocol {
    val SERVICE: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a900")
    val CHALLENGE: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a901")
    val APPROVAL: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a902")
    val STATUS: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a903")
    val CCCD: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
    const val CHUNK_BYTES = 20 // Fits the minimum ATT MTU of 23.
    const val MAX_MESSAGE_BYTES = 4096

    data class Challenge(val id: String, val nonce: String, val lifetime: Int, val computerName: String)

    fun parseChallenge(bytes: ByteArray): Challenge {
        val text = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
        val value = JSONObject(text)
        require(value.get("v") == 1 && value.getString("type") == "unlock_request")
        val id = value.getString("request_id")
        require(UUID.fromString(id).toString() == id)
        val nonce = value.getString("nonce")
        val decoded = Base64.decode(nonce, Base64.NO_WRAP)
        require(decoded.size == 32 && Base64.encodeToString(decoded, Base64.NO_WRAP) == nonce)
        val lifetime = value.get("expires_in") as? Int ?: throw IllegalArgumentException("Invalid lifetime")
        require(lifetime in UnlockRequestStore.MIN_TIMEOUT_SECONDS..UnlockRequestStore.MAX_TIMEOUT_SECONDS)
        val name = value.getString("computer_name")
        require(name.isNotBlank() && name.length <= 64 && name.none { it.isISOControl() })
        return Challenge(id, nonce, lifetime, name)
    }

    fun result(challenge: Challenge, decision: String): ByteArray = (JSONObject()
        .put("v", 1).put("type", "approval").put("request_id", challenge.id)
        .put("nonce", challenge.nonce).put("decision", decision).toString() + "\n").toByteArray(Charsets.UTF_8)

    class FrameReader {
        private val buffer = ByteArrayOutputStream()
        fun append(bytes: ByteArray): List<ByteArray> {
            val messages = mutableListOf<ByteArray>()
            for (byte in bytes) {
                if (byte == 10.toByte()) {
                    require(buffer.size() > 0)
                    messages.add(buffer.toByteArray())
                    buffer.reset()
                } else {
                    require(buffer.size() < MAX_MESSAGE_BYTES)
                    buffer.write(byte.toInt())
                }
            }
            return messages
        }
        fun clear() = buffer.reset()
    }
}
