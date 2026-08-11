package com.qingtoolbox.android

import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.WriterException
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel

data class QrMatrix(
    val size: Int,
    private val modules: BooleanArray,
) {
    init {
        require(size > 0) { "QR matrix must be square and non-empty." }
        require(modules.size == size * size) { "QR matrix data does not match its size." }
    }

    fun isDark(x: Int, y: Int): Boolean {
        require(x in 0 until size && y in 0 until size) { "QR matrix coordinate is out of bounds." }
        return modules[y * size + x]
    }

    fun darkModuleCount(): Int = modules.count { it }
}

object QrCodeEncoder {
    const val OUTPUT_SIZE = 768

    @Throws(WriterException::class)
    fun encode(input: String): QrMatrix {
        require(input.isNotEmpty()) { "Enter text to generate a QR code." }
        val hints = mapOf(
            EncodeHintType.CHARACTER_SET to "UTF-8",
            EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
            EncodeHintType.MARGIN to 4,
        )
        val bitMatrix = QRCodeWriter().encode(
            input,
            BarcodeFormat.QR_CODE,
            OUTPUT_SIZE,
            OUTPUT_SIZE,
            hints,
        )
        val size = bitMatrix.width
        val modules = BooleanArray(size * size) { index ->
            bitMatrix[index % size, index / size]
        }
        return QrMatrix(size, modules)
    }
}
