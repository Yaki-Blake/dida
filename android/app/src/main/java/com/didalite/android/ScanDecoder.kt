package com.didalite.android

import com.google.zxing.BinaryBitmap
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer

/**
 * 相机 NV21 帧 → 二维码文本。纯 Java 逻辑，不碰 Activity，方便 JVM 单测。
 *
 * ScanActivity 拿到的每帧都是 NV21：前 w*h 字节是 Y（亮度），后面跟着交错 U/V。
 * zxing 只需要 Y 平面，所以直接交给 PlanarYUVLuminanceSource。
 */
internal object ScanDecoder {

    private val reader = MultiFormatReader()

    fun decodeNv21(data: ByteArray, width: Int, height: Int): String? {
        if (width <= 0 || height <= 0 || data.size < width * height) return null
        val source = PlanarYUVLuminanceSource(data, width, height, 0, 0, width, height, false)
        val bitmap = BinaryBitmap(HybridBinarizer(source))
        return runCatching { reader.decode(bitmap).text }.getOrNull()
    }
}
