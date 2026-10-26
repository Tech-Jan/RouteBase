package com.routebase.gpsrecorder.util

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Encrypts the server API key at rest with an AES/GCM key held in the Android Keystore (non-exportable,
 * hardware-backed where available). Replaces plaintext SharedPreferences storage so a rooted read or a
 * debuggable-build backup can't lift the credential. The Keystore key never leaves the device, so the
 * prefs file is also excluded from cloud backup (a restored ciphertext would be undecryptable anyway).
 *
 * Wire format: Base64( iv(12) || ciphertext+tag ). Jetpack Security's EncryptedSharedPreferences is
 * deprecated, so this uses the Keystore API directly (available since API 23; app minSdk is 26).
 */
object SecureStore {

    private const val KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "gpsrecorder_apikey_v1"
    private const val TRANSFORM = "AES/GCM/NoPadding"
    private const val IV_LEN = 12
    private const val TAG_BITS = 128

    fun encrypt(plaintext: String): String {
        val cipher = Cipher.getInstance(TRANSFORM).apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        val iv = cipher.iv
        val ct = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(iv + ct, Base64.NO_WRAP)
    }

    /** Returns null if the blob can't be decrypted (corrupt, or Keystore key lost after a wipe/restore). */
    fun decrypt(stored: String): String? = try {
        val raw = Base64.decode(stored, Base64.NO_WRAP)
        val iv = raw.copyOfRange(0, IV_LEN)
        val ct = raw.copyOfRange(IV_LEN, raw.size)
        val cipher = Cipher.getInstance(TRANSFORM)
            .apply { init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(TAG_BITS, iv)) }
        String(cipher.doFinal(ct), Charsets.UTF_8)
    } catch (e: Exception) {
        null
    }

    private fun secretKey(): SecretKey {
        val ks = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        gen.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return gen.generateKey()
    }
}
