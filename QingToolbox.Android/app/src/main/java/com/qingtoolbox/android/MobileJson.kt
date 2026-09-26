package com.qingtoolbox.android

/**
 * Minimal JSON reader and writer used by the mobile module layer.
 *
 * The shell has to parse a module manifest before it can trust a module, and that
 * parsing must be verifiable in plain JVM unit tests — so the module layer carries its
 * own small reader instead of depending on a platform-only JSON class. Only the subset a
 * manifest and the module bridge can use is supported (objects, arrays, strings, numbers,
 * booleans, null); anything else fails loudly rather than being half-read.
 */
sealed class MobileJsonValue {
    data class Obj(val entries: Map<String, MobileJsonValue>) : MobileJsonValue()
    data class Arr(val items: List<MobileJsonValue>) : MobileJsonValue()
    data class Str(val value: String) : MobileJsonValue()
    data class Num(val value: Double) : MobileJsonValue()
    data class Bool(val value: Boolean) : MobileJsonValue()
    data object Null : MobileJsonValue()

    fun asObjectOrNull(): Map<String, MobileJsonValue>? = (this as? Obj)?.entries
    fun asArrayOrNull(): List<MobileJsonValue>? = (this as? Arr)?.items
    fun asStringOrNull(): String? = (this as? Str)?.value
    fun asBoolOrNull(): Boolean? = (this as? Bool)?.value

    /** Returns the value as an `Int` only when it is an integral number. */
    fun asIntOrNull(): Int? {
        val number = (this as? Num)?.value ?: return null
        if (number.isNaN() || number.isInfinite()) return null
        if (number != Math.floor(number)) return null
        if (number < Int.MIN_VALUE.toDouble() || number > Int.MAX_VALUE.toDouble()) return null
        return number.toInt()
    }
}

/** Reads a field of an object value, or `null` when absent or of the wrong shape. */
fun MobileJsonValue?.field(name: String): MobileJsonValue? = this?.asObjectOrNull()?.get(name)

fun MobileJsonValue?.stringField(name: String): String? = this.field(name)?.asStringOrNull()

fun MobileJsonValue?.intField(name: String): Int? = this.field(name)?.asIntOrNull()

/** Reads the value as an array of strings, or `null` when any item is not a string. */
fun MobileJsonValue?.asStringArrayOrNull(): List<String>? {
    val items = this?.asArrayOrNull() ?: return null
    return items.map { it.asStringOrNull() ?: return null }
}

/** Reads a field as an array of strings, or `null` when absent or of the wrong shape. */
fun MobileJsonValue?.stringArrayField(name: String): List<String>? =
    this.field(name)?.asStringArrayOrNull()

class MobileJsonException(message: String) : IllegalArgumentException(message)

object MobileJson {
    fun parse(text: String): MobileJsonValue = MobileJsonReader(text).parseDocument()

    /** Renders [value] as a JSON string literal, including the surrounding quotes. */
    fun quote(value: String): String {
        val builder = StringBuilder(value.length + 2)
        builder.append('"')
        value.forEach { character ->
            when (character) {
                '"' -> builder.append("\\\"")
                '\\' -> builder.append("\\\\")
                '\n' -> builder.append("\\n")
                '\r' -> builder.append("\\r")
                '\t' -> builder.append("\\t")
                '\b' -> builder.append("\\b")
                '\u000C' -> builder.append("\\f")
                else -> if (character < ' ') {
                    builder.append("\\u").append(character.code.toString(16).padStart(4, '0'))
                } else {
                    builder.append(character)
                }
            }
        }
        builder.append('"')
        return builder.toString()
    }
}

/**
 * Builds a flat JSON object.
 *
 * The bridge only ever returns flat objects of scalars and string arrays, so the writer
 * stays deliberately small instead of becoming a general serialization layer.
 */
class MobileJsonObject {
    private val builder = StringBuilder("{")
    private var count = 0

    fun string(name: String, value: String): MobileJsonObject = raw(name, MobileJson.quote(value))

    fun int(name: String, value: Int): MobileJsonObject = raw(name, value.toString())

    fun bool(name: String, value: Boolean): MobileJsonObject = raw(name, value.toString())

    fun strings(name: String, values: List<String>): MobileJsonObject = raw(
        name,
        values.joinToString(prefix = "[", postfix = "]", separator = ",") { MobileJson.quote(it) },
    )

    fun objectValue(name: String, json: String): MobileJsonObject = raw(name, json)

    private fun raw(name: String, json: String): MobileJsonObject {
        if (count > 0) builder.append(',')
        builder.append(MobileJson.quote(name)).append(':').append(json)
        count++
        return this
    }

    fun build(): String = builder.toString() + "}"
}

private class MobileJsonReader(private val text: String) {
    private var index = 0

    fun parseDocument(): MobileJsonValue {
        skipWhitespace()
        val value = parseValue()
        skipWhitespace()
        if (index != text.length) fail("Unexpected content after the JSON document")
        return value
    }

    private fun parseValue(): MobileJsonValue {
        if (index >= text.length) fail("Unexpected end of the JSON document")
        return when (val character = text[index]) {
            '{' -> parseObject()
            '[' -> parseArray()
            '"' -> MobileJsonValue.Str(parseString())
            't' -> bool(true)
            'f' -> bool(false)
            'n' -> literal()
            else -> if (character == '-' || character.isDigit()) parseNumber() else fail("Unexpected character '$character'")
        }
    }

    private fun parseObject(): MobileJsonValue {
        index++
        val entries = LinkedHashMap<String, MobileJsonValue>()
        skipWhitespace()
        if (peek() == '}') {
            index++
            return MobileJsonValue.Obj(entries)
        }
        while (true) {
            skipWhitespace()
            if (peek() != '"') fail("Expected an object key")
            val key = parseString()
            skipWhitespace()
            if (peek() != ':') fail("Expected ':' after the object key")
            index++
            skipWhitespace()
            entries[key] = parseValue()
            skipWhitespace()
            when (peek()) {
                ',' -> index++
                '}' -> {
                    index++
                    return MobileJsonValue.Obj(entries)
                }
                else -> fail("Expected ',' or '}' in the object")
            }
        }
    }

    private fun parseArray(): MobileJsonValue {
        index++
        val items = mutableListOf<MobileJsonValue>()
        skipWhitespace()
        if (peek() == ']') {
            index++
            return MobileJsonValue.Arr(items)
        }
        while (true) {
            skipWhitespace()
            items.add(parseValue())
            skipWhitespace()
            when (peek()) {
                ',' -> index++
                ']' -> {
                    index++
                    return MobileJsonValue.Arr(items)
                }
                else -> fail("Expected ',' or ']' in the array")
            }
        }
    }

    private fun parseString(): String {
        index++
        val builder = StringBuilder()
        while (true) {
            if (index >= text.length) fail("Unterminated string")
            val character = text[index]
            when {
                character == '"' -> {
                    index++
                    return builder.toString()
                }
                character == '\\' -> {
                    index++
                    if (index >= text.length) fail("Unterminated escape sequence")
                    when (val escape = text[index]) {
                        '"' -> builder.append('"')
                        '\\' -> builder.append('\\')
                        '/' -> builder.append('/')
                        'b' -> builder.append('\b')
                        'f' -> builder.append('\u000C')
                        'n' -> builder.append('\n')
                        'r' -> builder.append('\r')
                        't' -> builder.append('\t')
                        'u' -> {
                            if (index + 4 >= text.length) fail("Truncated unicode escape")
                            val hex = text.substring(index + 1, index + 5)
                            val code = hex.toIntOrNull(16) ?: fail("Invalid unicode escape")
                            builder.append(code.toChar())
                            index += 4
                        }
                        else -> fail("Invalid escape sequence '\\$escape'")
                    }
                    index++
                }
                character < ' ' -> fail("Unescaped control character in a string")
                else -> {
                    builder.append(character)
                    index++
                }
            }
        }
    }

    private fun parseNumber(): MobileJsonValue {
        val start = index
        if (peek() == '-') index++
        while (index < text.length && isNumberCharacter(text[index])) index++
        val literal = text.substring(start, index)
        val value = literal.toDoubleOrNull() ?: fail("Invalid number '$literal'")
        return MobileJsonValue.Num(value)
    }

    private fun bool(value: Boolean): MobileJsonValue {
        literal()
        return MobileJsonValue.Bool(value)
    }

    private fun literal(): MobileJsonValue {
        return when {
            text.startsWith("true", index) -> {
                index += 4
                MobileJsonValue.Bool(true)
            }
            text.startsWith("false", index) -> {
                index += 5
                MobileJsonValue.Bool(false)
            }
            text.startsWith("null", index) -> {
                index += 4
                MobileJsonValue.Null
            }
            else -> fail("Unexpected literal")
        }
    }

    private fun skipWhitespace() {
        while (index < text.length && text[index].isJsonWhitespace()) index++
    }

    private fun peek(): Char {
        if (index >= text.length) fail("Unexpected end of the JSON document")
        return text[index]
    }

    private fun fail(message: String): Nothing = throw MobileJsonException(message)

    private fun Char.isJsonWhitespace(): Boolean =
        this == ' ' || this == '\t' || this == '\n' || this == '\r'

    private fun isNumberCharacter(character: Char): Boolean =
        character.isDigit() || character == '.' || character == 'e' || character == 'E' ||
            character == '+' || character == '-'
}
