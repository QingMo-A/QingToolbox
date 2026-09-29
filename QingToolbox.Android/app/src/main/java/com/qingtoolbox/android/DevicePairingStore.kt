package com.qingtoolbox.android

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import com.southernstorm.noise.protocol.Noise
import java.io.File
import java.io.ByteArrayOutputStream
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class PairedDevice(
    val id: String,
    val discoveryId: String,
    val name: String,
    val platform: String,
    val intimate: Boolean = false,
    val forwardNotifications: Boolean = true,
)

/** The Noise private key is wrapped by Android Keystore, not stored as plaintext. */
internal class DevicePairingStore(context: Context) {
    private val keyFile = AtomicFile(File(context.noBackupFilesDir, "device-pairing-key-v1"))
    private val recordsFile = AtomicFile(File(context.noBackupFilesDir, "device-pairs-v1.json"))
    private val revocationsFile = AtomicFile(File(context.noBackupFilesDir, "device-revocations-v1.json"))
    private val keystore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    init {
        // A restored record with a missing Keystore key must not silently become
        // a different trusted identity.
        if ((recordsFile.baseFile.exists() || revocationsFile.baseFile.exists()) && !keyFile.baseFile.exists()) {
            error("Paired device key is missing")
        }
        if (keyFile.baseFile.exists() && !keystore.containsAlias(KEY_ALIAS)) {
            error("Android Keystore pairing key is missing")
        }
        if (!keyFile.baseFile.exists()) createIdentity()
    }

    fun loadPrivateKey(): ByteArray {
        val envelope = keyFile.openRead().use { it.readBytes() }
        require(envelope.size == 1 + 12 + 48 && envelope[0] == 1.toByte()) { "Invalid pairing key envelope" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, wrappingKey(), GCMParameterSpec(128, envelope, 1, 12))
        return cipher.doFinal(envelope, 13, envelope.size - 13).also {
            require(it.size == 32) { "Invalid pairing key" }
        }
    }

    fun readRecords(): List<PairedDevice> = readRecords(recordsFile)

    fun readRevocations(): List<PairedDevice> = readRecords(revocationsFile)

    private fun readRecords(file: AtomicFile): List<PairedDevice> {
        if (!file.baseFile.exists()) return emptyList()
        val data = file.openRead().use { stream ->
            val output = ByteArrayOutputStream()
            val chunk = ByteArray(4096)
            while (true) {
                val count = stream.read(chunk)
                if (count < 0) break
                require(output.size() + count <= 128 * 1024) { "Pairing records are too large" }
                output.write(chunk, 0, count)
            }
            output.toByteArray()
        }
        val parsed = MobileJson.parse(String(data, Charsets.UTF_8))
        require(parsed.intField("version") == 1) { "Unsupported pairing record version" }
        val entries = parsed.field("peers")?.asArrayOrNull() ?: error("Invalid pairing records")
        require(entries.size <= 128)
        val ids = HashSet<String>()
        val discoveryIds = HashSet<String>()
        return entries.map { item ->
            val id = item.stringField("id") ?: error("Invalid peer key")
            val discoveryId = item.stringField("discoveryId") ?: error("Invalid peer discovery ID")
            val name = item.stringField("name") ?: error("Invalid peer name")
            val platform = item.stringField("platform") ?: error("Invalid peer platform")
            val relationship = item.stringField("relationship") ?: error("Invalid peer relationship")
            require(id.matches(Regex("[0-9a-f]{64}")) && ids.add(id))
            require(discoveryId.matches(Regex("[0-9a-f]{32}")) && discoveryIds.add(discoveryId))
            require(name.isNotBlank() && name.codePointCount(0, name.length) <= 64)
            require(platform == "android" || platform == "windows")
            require(relationship == "Connected" || relationship == "Intimate")
            val forwarding = item.field("forwardNotifications")?.asBoolOrNull() ?: true
            PairedDevice(id, discoveryId, name, platform, relationship == "Intimate", forwarding)
        }
    }

    fun writeRecords(records: List<PairedDevice>) = writeRecords(recordsFile, records)

    fun writeRevocations(records: List<PairedDevice>) = writeRecords(revocationsFile, records)

    private fun writeRecords(file: AtomicFile, records: List<PairedDevice>) {
        require(records.size <= 128)
        val peers = records.joinToString(",", "[", "]") { peer ->
            MobileJsonObject()
                .string("id", peer.id)
                .string("discoveryId", peer.discoveryId)
                .string("name", peer.name)
                .string("platform", peer.platform)
                .string("relationship", if (peer.intimate) "Intimate" else "Connected")
                .bool("forwardNotifications", peer.forwardNotifications)
                .build()
        }
        val data = "{\"version\":1,\"peers\":$peers}".toByteArray(Charsets.UTF_8)
        writeAtomic(file, data)
    }

    private fun createIdentity() {
        val key = wrappingKey(create = true)
        val dh = Noise.createDH("25519")
        val private = try {
            dh.generateKeyPair()
            ByteArray(32).also { dh.getPrivateKey(it, 0) }
        } finally {
            dh.destroy()
        }
        try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key)
            val envelope = byteArrayOf(1) + cipher.iv + cipher.doFinal(private)
            writeAtomic(keyFile, envelope)
        } finally {
            private.fill(0)
        }
    }

    private fun wrappingKey(create: Boolean = false): SecretKey {
        val existing = keystore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing
        check(create) { "Android Keystore pairing key is missing" }
        val spec = KeyGenParameterSpec.Builder(
            KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        ).setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setRandomizedEncryptionRequired(true)
            .build()
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
            .apply { init(spec) }.generateKey()
    }

    private fun writeAtomic(file: AtomicFile, data: ByteArray) {
        val output = file.startWrite()
        try {
            output.write(data)
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    companion object {
        private const val KEY_ALIAS = "qingtoolbox-device-pairing-wrap-v1"
    }
}
