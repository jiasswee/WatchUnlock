package com.watchunlock.app

import android.util.Base64
import org.json.JSONObject
import org.json.JSONTokener
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.UUID
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object BleProtocol {
    val SERVICE: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a900")
    val CHALLENGE: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a901")
    val APPROVAL: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a902")
    val STATUS: UUID = UUID.fromString("6d87f6f0-3274-4f00-8e0a-770450f8a903")
    val CCCD: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
    const val CHUNK_BYTES = 20 // Fits the minimum ATT MTU of 23.
    const val MAX_MESSAGE_BYTES = 4096

    data class Challenge(val id: String, val nonce: String, val lifetime: Int, val computerName: String, val sessionId: String)

    fun canonical(vararg parts: String): ByteArray = (listOf("watchunlock-v2") + parts)
        .joinToString("\n").toByteArray(Charsets.UTF_8)
    fun base64Bytes(value: String): ByteArray {
        val bytes = Base64.decode(value, Base64.NO_WRAP)
        require(bytes.size == 32 && Base64.encodeToString(bytes, Base64.NO_WRAP) == value)
        return bytes
    }
    fun uuid(value: String): String {
        require(UUID.fromString(value).toString() == value)
        return value
    }
    fun sign(key: ByteArray, vararg parts: String): String = Base64.encodeToString(
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }
            .doFinal(canonical(*parts)), Base64.NO_WRAP)
    fun verify(key: ByteArray, signature: String, vararg parts: String): Boolean =
        MessageDigest.isEqual(base64Bytes(signature), base64Bytes(sign(key, *parts)))

    /** Flat JSON only: reject duplicate keys, nested values, coercions, and trailing data. */
    fun parseMessage(bytes: ByteArray): JSONObject {
        val text = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
        val reader = JSONTokener(text)
        require(reader.nextClean() == '{')
        val result = JSONObject()
        while (true) {
            val next = reader.nextClean()
            if (next == '}') break
            require(next == '"')
            reader.back()
            val key = reader.nextValue() as? String ?: error("Invalid field")
            require(!result.has(key) && reader.nextClean() == ':')
            val value = reader.nextValue()
            require(value is String || value is Int)
            result.put(key, value)
            when (reader.nextClean()) {
                '}' -> break
                ',' -> {
                    require(reader.nextClean() != '}')
                    reader.back()
                }
                else -> error("Invalid object")
            }
        }
        require(reader.nextClean() == '\u0000' && result.get("v") == 2)
        return result
    }
    fun fields(value: JSONObject, vararg names: String) {
        val expected = (listOf("v", "type") + names).toSet()
        require(value.keys().asSequence().toSet() == expected)
        for (name in names) require(value.get(name) is String || name == "expires_in")
    }

    fun parseChallenge(value: JSONObject, sessionId: String, key: ByteArray): Challenge {
        fields(value, "request_id", "nonce", "expires_in", "computer_name", "session_id", "mac")
        require(value.getString("type") == "unlock_request" && value.getString("session_id") == sessionId)
        val id = uuid(value.getString("request_id"))
        val nonce = value.getString("nonce").also { base64Bytes(it) }
        val lifetime = value.get("expires_in") as? Int ?: throw IllegalArgumentException("Invalid lifetime")
        require(lifetime in UnlockRequestStore.MIN_TIMEOUT_SECONDS..UnlockRequestStore.MAX_TIMEOUT_SECONDS)
        val name = value.getString("computer_name")
        require(name.isNotBlank() && name.length <= 64 && name.none { it.isISOControl() })
        val encodedName = Base64.encodeToString(name.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
        require(verify(key, value.getString("mac"), "unlock_request", sessionId, id, nonce, lifetime.toString(), encodedName))
        return Challenge(id, nonce, lifetime, name, sessionId)
    }

    fun result(challenge: Challenge, decision: String, key: ByteArray): JSONObject = JSONObject()
        .put("v", 2).put("type", "approval").put("request_id", challenge.id)
        .put("session_id", challenge.sessionId).put("nonce", challenge.nonce).put("decision", decision)
        .put("mac", sign(key, "approval", challenge.sessionId, challenge.id, challenge.nonce, decision))
    fun frame(value: JSONObject): ByteArray = (value.toString() + "\n").toByteArray(Charsets.UTF_8)

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
