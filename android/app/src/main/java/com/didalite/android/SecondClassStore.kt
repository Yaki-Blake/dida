package com.didalite.android

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 第二课堂的本地存储：学号密码 + 活动列表缓存。
 *
 * 密码不落明文——用 Android Keystore 里那把不可导出的 AES 密钥做 GCM 加密，
 * 库里只存「iv + 密文」的 Base64。钥匙本身留在 Keystore，root 之外的东西读不出来。
 *
 * 活动列表按 JSON 存进 meta（表结构不动，省一次 onUpgrade）；
 * 报名这种写操作之后由调用方强制重拉，缓存只负责「打开就能先看见东西」。
 */
internal object SecondClassStore {

    private const val KEY_ALIAS = "dida_2kt"
    private const val TRANSFORM = "AES/GCM/NoPadding"
    private const val IV_BYTES = 12
    private const val TAG_BITS = 128

    private const val USER = "2kt_user"
    private const val PASS = "2kt_pass"
    private const val TOKEN = "2kt_token"
    private const val ACTS = "2kt_acts"
    private const val ACTS_AT = "2kt_acts_at"
    private const val PAST = "2kt_past"
    private const val PAST_AT = "2kt_past_at"

    /** 登录信息：学号 + 已解密的密码。 */
    data class Login(val user: String, val password: String)

    // ---------- 登录 ----------

    fun saveLogin(db: Db, user: String, password: String) {
        db.setMeta(USER, user)
        db.setMeta(PASS, encrypt(password))
    }

    fun loadLogin(db: Db): Login? {
        val user = db.getMeta(USER)?.trim().orEmpty()
        if (user.isEmpty()) return null
        val password = db.getMeta(PASS)?.let { decrypt(it) } ?: return null
        return Login(user, password)
    }

    fun clearLogin(db: Db) {
        db.setMeta(USER, null)
        db.setMeta(PASS, null)
        db.setMeta(TOKEN, null)
    }

    // ---------- 登录态 ----------

    fun saveToken(db: Db, token: String?) = db.setMeta(TOKEN, token?.ifBlank { null })

    fun loadToken(db: Db): String? = db.getMeta(TOKEN)?.ifBlank { null }

    // ---------- 活动列表缓存 ----------

    /**
     * 存活动列表。[past] 只决定写进哪一格：报名中与历史活动是两份独立的缓存，
     * 因为它们的 actiStatusId 不同，混在一起会互相覆盖。
     */
    fun saveActs(db: Db, acts: List<SecondClassApi.Act>, past: Boolean = false) {
        val array = JSONArray()
        acts.forEach { a ->
            array.put(
                JSONObject()
                    .put("id", a.id)
                    .put("name", a.name)
                    .put("org", a.org)
                    .put("category", a.category)
                    .put("type", a.typeName)
                    .put("start", a.startTime)
                    .put("end", a.endTime)
                    .put("hours", a.hours)
                    .put("online", a.online)
                    .put("logo", a.logo)
            )
        }
        db.setMeta(if (past) PAST else ACTS, array.toString())
        db.setMeta(if (past) PAST_AT else ACTS_AT, System.currentTimeMillis().toString())
    }

    fun loadActs(db: Db, past: Boolean = false): List<SecondClassApi.Act>? {
        val text = db.getMeta(if (past) PAST else ACTS) ?: return null
        val array = runCatching { JSONArray(text) }.getOrNull() ?: return null
        val acts = (0 until array.length()).mapNotNull { i ->
            val o = array.optJSONObject(i) ?: return@mapNotNull null
            val id = o.optString("id")
            if (id.isEmpty()) return@mapNotNull null
            SecondClassApi.Act(
                id = id,
                name = o.optString("name"),
                org = o.optString("org"),
                category = o.optString("category"),
                typeName = o.optString("type"),
                startTime = o.optString("start"),
                endTime = o.optString("end"),
                hours = o.optDouble("hours", 0.0),
                online = o.optBoolean("online"),
                logo = o.optString("logo"),
            )
        }
        return acts.ifEmpty { null }
    }

    fun actsAt(db: Db, past: Boolean = false): Long =
        db.getMeta(if (past) PAST_AT else ACTS_AT)?.toLongOrNull() ?: 0L

    // ---------- 加密 ----------

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return generator.generateKey()
    }

    /** 加密失败就返回空串——宁可下次让用户重输密码，也不把明文写进库里。 */
    private fun encrypt(plain: String): String = runCatching {
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val iv = cipher.iv
        val body = cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        val packed = ByteArray(iv.size + body.size)
        System.arraycopy(iv, 0, packed, 0, iv.size)
        System.arraycopy(body, 0, packed, iv.size, body.size)
        Base64.encodeToString(packed, Base64.NO_WRAP)
    }.getOrDefault("")

    private fun decrypt(stored: String): String? = runCatching {
        val packed = Base64.decode(stored, Base64.NO_WRAP)
        if (packed.size <= IV_BYTES) return null
        val iv = packed.copyOfRange(0, IV_BYTES)
        val body = packed.copyOfRange(IV_BYTES, packed.size)
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, iv))
        String(cipher.doFinal(body), Charsets.UTF_8)
    }.getOrNull()
}
