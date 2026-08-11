package com.qingtoolbox.android

object QingTransferMetadata {
    const val serviceType = "_qingtransfer._tcp"
    const val androidServiceType = "_qingtransfer._tcp."
    private const val protocolVersion = "1"
    private const val maxServiceNameLength = 255
    private const val maxFieldLength = 128
    private const val maxDisplayNameLength = 64

    sealed interface ParseResult {
        data class Valid(val peer: QingTransferPeer) : ParseResult
        data object Invalid : ParseResult
    }

    fun create(platform: String, displayName: String): Map<String, String> = mapOf(
        "v" to protocolVersion,
        "pf" to platform,
        "name" to sanitizeName(displayName),
        "cap" to "file",
    )

    fun parse(
        serviceName: String?,
        fields: Map<String, String?>,
        addresses: List<String> = emptyList(),
        port: Int,
        lastSeen: Long? = null,
    ): ParseResult {
        val normalized = serviceName?.trim()?.trimEnd('.') ?: return ParseResult.Invalid
        if (normalized.length > maxServiceNameLength ||
            !normalized.endsWith(".$serviceType.local", ignoreCase = true)
        ) return ParseResult.Invalid
        val version = field(fields, "v") ?: return ParseResult.Invalid
        val platform = field(fields, "pf") ?: return ParseResult.Invalid
        val name = field(fields, "name") ?: return ParseResult.Invalid
        val capabilities = field(fields, "cap")?.split(',')
            ?.map(String::trim)
            ?.filter { it.isNotEmpty() && it.length <= maxFieldLength }
            ?.distinctBy(String::lowercase)
            ?: return ParseResult.Invalid
        if (version != protocolVersion || platform !in setOf("windows", "android") ||
            name.length !in 1..maxDisplayNameLength || name.any(Char::isISOControl) ||
            capabilities.none { it.equals("file", ignoreCase = true) } ||
            port !in 0..65535
        ) return ParseResult.Invalid
        return ParseResult.Valid(
            QingTransferPeer(
                serviceName = normalized,
                displayName = name,
                platform = platform,
                protocolVersion = version,
                capabilities = capabilities,
                addresses = addresses.asSequence().filter(::isSafeAddress).distinct().take(8).toList(),
                port = port,
                online = true,
                lastSeen = lastSeen,
            ),
        )
    }

    fun fullServiceName(name: String?): String {
        val value = name?.trim()?.trimEnd('.') ?: return ""
        return when {
            value.endsWith(".$serviceType.local", ignoreCase = true) -> value
            value.endsWith(".$serviceType", ignoreCase = true) -> "$value.local"
            else -> "$value.$serviceType.local"
        }
    }

    fun sanitizeName(value: String): String {
        val safe = value.trim().filter { it.isLetterOrDigit() || it == '-' || it == '_' }
        return (if (safe.isEmpty()) "QingToolbox" else safe).take(maxDisplayNameLength)
    }

    private fun field(fields: Map<String, String?>, key: String): String? {
        val value = fields.entries.firstOrNull { it.key.equals(key, ignoreCase = true) }?.value?.trim() ?: return null
        return value.takeIf { it.isNotEmpty() && it.length <= maxFieldLength && it.none(Char::isISOControl) }
    }

    private fun isSafeAddress(value: String): Boolean =
        value.length <= 64 && value.isNotBlank() && value.none(Char::isISOControl)
}
