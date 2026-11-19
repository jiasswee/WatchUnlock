package com.watchunlock.app

import android.content.Context
import android.security.keystore.KeyProperties
import android.security.keystore.KeyProtection
import android.util.Base64
import java.security.KeyStore
import java.security.MessageDigest
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/** Imports a derived pairing key; input text is never saved in preferences or logs. */
class PairingKey(private val context: Context) {
    private fun store() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    val version: String?
        get() = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("version", null)
    fun configured(): Boolean = version != null && store().containsAlias(ALIAS)

    fun save(input: CharArray) {
        require(input.size in 16..128 && input.all { it.code in 33..126 })
        val spec = PBEKeySpec(input, "WatchUnlock pairing v2".toByteArray(Charsets.UTF_8), 210000, 256)
        var bytes: ByteArray? = null
        try {
            bytes = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
            store().setEntry(ALIAS, KeyStore.SecretKeyEntry(SecretKeySpec(bytes, "HmacSHA256")),
                KeyProtection.Builder(KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                    .setDigests(KeyProperties.DIGEST_SHA256).setUserAuthenticationRequired(false).build())
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString("version", UUID.randomUUID().toString()).apply()
        } finally {
            spec.clearPassword()
            input.fill('\u0000')
            bytes?.fill(0)
        }
    }
    fun sign(vararg parts: String): String = Base64.encodeToString(rawSign(*parts), Base64.NO_WRAP)
    fun verify(signature: String, vararg parts: String): Boolean =
        MessageDigest.isEqual(BleProtocol.base64Bytes(signature), rawSign(*parts))
    fun sessionKey(id: String, clientNonce: String, serverNonce: String): ByteArray =
        rawSign("session_key", id, clientNonce, serverNonce)
    private fun rawSign(vararg parts: String): ByteArray {
        val key = store().getKey(ALIAS, null) as? SecretKey ?: error("Pairing key not configured")
        return Mac.getInstance("HmacSHA256").apply { init(key) }.doFinal(BleProtocol.canonical(*parts))
    }
    companion object {
        private const val PREFS = "watchunlock_pairing"
        private const val ALIAS = "watchunlock-pairing-v2"
    }
}
