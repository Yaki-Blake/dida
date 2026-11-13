package com.didalite.android

import java.security.SecureRandom

/** ULID 生成：26 位 Crockford Base32，前 10 位时间戳（毫秒），后 16 位随机。 */
object Ulid {
    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    private val random = SecureRandom()

    fun new(time: Long = System.currentTimeMillis()): String {
        val chars = CharArray(26)
        var t = time
        for (i in 9 downTo 0) {
            chars[i] = ALPHABET[(t and 31L).toInt()]
            t = t shr 5
        }
        for (i in 10 until 26) {
            chars[i] = ALPHABET[random.nextInt(32)]
        }
        return String(chars)
    }
}
