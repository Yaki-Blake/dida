package com.didalite.android

import org.json.JSONArray
import org.json.JSONObject

/**
 * 第二课堂活动接口（校团委 2ketang，uni-app 前端背后的那套 /apps）。
 *
 * 登录沿用 [EduSecondClass] 那一条：先查学校 id，再 POST /apps/common/login，
 * 登录态在响应头 x-token 里，之后所有请求都带上它。
 *
 * 校外必须先连学校 VPN，域名 2ketang.gdufe.edu.cn 只在校内网段解析。
 */
object SecondClassApi {

    const val BASE = EduSecondClass.DEFAULT_BASE

    /** 列表项：活动卡片要显示的那几项。 */
    data class Act(
        val id: String,
        val name: String,
        val org: String,
        val category: String,
        val typeName: String,
        val startTime: String,
        val endTime: String,
        val hours: Double,
        /** 0 = 线下，1 = 线上。 */
        val online: Boolean,
        val logo: String,
    )

    /** 详情：报名确认框与详情页要用的字段。 */
    data class Detail(
        val id: String,
        val name: String,
        val hours: Double,
        val sumHours: Double,
        val className: String,
        val typeName: String,
        val zhubanName: String,
        val xiebanName: String,
        val startTime: String,
        val endTime: String,
        val enrollEndTime: String,
        val place: String,
        val online: Boolean,
        val introduce: String,
        val adminName: String,
        val adminContact: String,
        val teacherName: String,
        val classHours: Double,
        val peopleCount: Int,
        val peopleLimit: Int,
        val enroll: EnrollState,
    ) {
        /** 报名确认框里那一行「时间 · 地点」。 */
        val whenAndWhere: String
            get() = listOf(startTime, place).filter { it.isNotBlank() }.joinToString(" · ")
    }

    /** 能不能报名，以及为什么不能：文案直接给用户看。 */
    data class EnrollState(
        val canJoin: Boolean,
        val reason: String,
    )

    /** 组织（社团 / 学生会 / 其他）。level 1=校级 2=院系级；nature 1=学生会 2=社团协会 3=其他。 */
    data class Org(
        val id: String,
        val name: String,
        val type: Int,
        val level: Int,
        val nature: Int,
        val rule: Int,
        val introduce: String,
        val memberCount: Int,
        val logo: String,
    ) {
        val kind: String
            get() = when (nature) {
                1 -> "学生会"
                2 -> "社团协会"
                3 -> "其他"
                else -> ""
            }
        val rank: String
            get() = when (level) {
                1 -> "校级"
                2 -> "院系级"
                else -> ""
            }
        /** 1=需要审核 2/3=点一下就加入。 */
        val needAudit: Boolean get() = rule == 1
    }

    /** 组织详情比列表多了人数，少了列表那堆归档字段。 */
    data class OrgDetail(
        val id: String,
        val name: String,
        val type: Int,
        val rule: Int,
        val nature: Int,
        val introduce: String,
        val peopleCount: Int,
    )

    /** 我的申报：一条申报记录。status 0=驳回 1=通过 2=审核中（照官方前端的 projectStatus）。 */
    data class Apply(
        val applyId: String,
        val proName: String,
        val optionName: String,
        val className: String,
        val hours: Double,
        val startTime: String,
        val endTime: String,
        val status: Int,
    ) {
        val statusText: String
            get() = when (status) {
                0 -> "驳回"
                1 -> "通过"
                2 -> "审核中"
                else -> "审核中"
            }
    }

    /**
     * 我的组织：我加入的组织/班级/机构。照官方「我的组织」页的字段与文案。
     * - type 1=机构 2=组织 3=班级 4=班团（type==2 时副标显示 nature：1 学生会 / 2 社团协会 / 3 其他）。
     * - role 1=负责人 2=管理员 3=成员。
     * - status 1=审核中。
     */
    data class MyOrg(
        val orgId: String,
        val name: String,
        val type: Int,
        val nature: Int,
        val role: Int,
        val status: Int,
        val logo: String,
    ) {
        val kindText: String
            get() = if (type == 2) {
                when (nature) {
                    1 -> "学生会"
                    2 -> "社团协会"
                    3 -> "其他"
                    else -> "组织"
                }
            } else {
                when (type) {
                    1 -> "机构"
                    3 -> "班级"
                    4 -> "班团"
                    else -> "其他"
                }
            }
        val roleText: String
            get() = when (role) {
                1 -> "负责人"
                2 -> "管理员"
                3 -> "成员"
                else -> ""
            }
        val pending: Boolean get() = status == 1
    }

    /** 学籍信息：第二课堂这边能看到的那几项。 */
    data class Profile(
        val userId: String,
        val identity: String,
        val name: String,
        val code: String,
        val gender: String,
        val ethnicName: String,
        val politics: String,
        val campusName: String,
        val collegeName: String,
        val majorName: String,
        val className: String,
        val gradeName: String,
    )

    /** 签到签退一段：什么时候进的、什么时候出的、算了多少分钟。 */
    data class SignSegment(
        val inTime: String,
        val outTime: String,
        val minutes: Int,
        val status: String,
        val invalidReason: String,
    ) {
        val statusText: String
            get() = when (status) {
                "effective" -> "有效"
                "ongoing" -> "进行中"
                else -> when (invalidReason) {
                    "no_out" -> "未签退"
                    "overtime" -> "跨天未签退"
                    "no_in" -> "未签到"
                    else -> "无效"
                }
            }
    }

    /** 一场活动的签到记录（可能有多段）。 */
    data class SignRecord(
        val activityId: String,
        val name: String,
        val startTime: String,
        val endTime: String,
        val effective: Int,
        val ongoing: Int,
        val invalid: Int,
        val segments: List<SignSegment>,
    )

    /**
     * 我的报名：一条我报过的活动。
     *
     * 审核结果在 `audit_status` 里（0=驳回 1=通过 2=待审核），而 `apply_status`
     * 表示「这个活动我现在还能不能报名」，两者不能混用——列表页看的是前者。
     */
    data class MyJoin(
        val id: String,
        val name: String,
        val org: String,
        val category: String,
        val typeName: String,
        val startTime: String,
        val endTime: String,
        val hours: Double,
        /** 0 = 线下，1 = 线上。 */
        val online: Boolean,
        val auditStatus: Int,
        val auditRemark: String,
    ) {
        val auditText: String
            get() = when (auditStatus) {
                0 -> "驳回"
                1 -> "通过"
                2 -> "待审核"
                else -> ""
            }

        /** 驳回时把原因接在后面，通过就不啰嗦。 */
        val auditLine: String
            get() = when {
                auditStatus == 0 && auditRemark.isNotBlank() -> "驳回：$auditRemark"
                auditText.isBlank() -> ""
                auditStatus == 0 || auditStatus == 2 -> "$auditText：$auditRemark".trimEnd('：')
                else -> auditText
            }
    }

    /** 口令签到：输入四位数字码后换回来的活动与动作。 */
    data class SignCode(
        val code: String,
        val actId: String,
        val actName: String,
        /** "in" = 签到，"out" = 签退。 */
        val mode: String,
    ) {
        val modeText: String
            get() = if (mode == "out") "签退" else "签到"
    }

    /**
     * 时间统一成人看的样子。
     *
     * 接口里两种写法混着来：`2026-12-06 19:00:00` 和 `2026-11-27T14:30:00`。
     * 统一成 `2026-12-06 19:00`：T 换空格、秒和毫秒砍掉。
     */
    fun prettyTime(raw: String): String {
        val s = raw.trim()
        if (s.isEmpty()) return ""
        var t = s.replace('T', ' ')
        val dot = t.indexOf('.')
        if (dot > 0) t = t.substring(0, dot)
        if (t.length > 16) t = t.substring(0, 16)
        return t
    }

    /** 「开始 – 结束」，同一天就只写一次日期。 */
    fun prettySpan(start: String, end: String): String {
        val a = prettyTime(start)
        val b = prettyTime(end)
        if (a.isEmpty()) return b
        if (b.isEmpty()) return a
        val aDay = a.substringBefore(' ')
        val bDay = b.substringBefore(' ')
        return if (aDay == bDay) "$a – ${b.substringAfter(' ', b)}" else "$a – $b"
    }

    private const val HOST = "2ketang.gdufe.edu.cn"

    /** 一次登录的产物：带着 Cookie 的会话 + x-token。 */
    class Login(val session: JwxtApi.Session, val token: String)

    /**
     * 登录。先按域名查学校 id，再交学号密码换 x-token。
     * 与 [EduSecondClass.fetch] 走同一套接口，但这里把 token 与 session 留下来给后续请求用。
     */
    fun login(user: String, pwd: String): Login {
        val session = EduHttp.session()
        val schoolId = try {
            val reply = EduHttp.get(session, "$BASE/apps/school?para=${EduHttp.enc("""{"url":"$HOST"}""")}")
            JSONObject(reply.text).getJSONArray("data").getJSONObject(0).optString("id")
        } catch (e: Exception) {
            throw EduException("连不上第二课堂：校外请先连学校 VPN（${e.message ?: e.javaClass.simpleName}）")
        }
        if (schoolId.isBlank()) throw EduException("没认出这所学校，第二课堂接口可能改版了")

        val para = JSONObject()
            .put("school", schoolId)
            .put("account", user)
            .put("password", pwd)
            .toString()
        val reply = EduHttp.post(session, "$BASE/apps/common/login", EduHttp.form(listOf("para" to para)))
        val body = runCatching { JSONObject(reply.text) }.getOrNull()
            ?: throw EduException("第二课堂返回的不是 JSON，接口可能改版了")
        val data = body.optJSONObject("data")
        if (data == null || data.has("detail") || !data.has("id")) {
            throw EduException(body.optString("msg").ifBlank { "登录失败：账号或密码不对" })
        }
        val token = reply.token
        if (token.isNullOrBlank()) throw EduException("登录没有返回凭证，接口可能改版了")
        return Login(session, token)
    }

    /** 拉活动列表。[page] 从 1 起，[size] 一页条数。 */
    fun list(
        session: JwxtApi.Session,
        token: String,
        page: Int = 1,
        size: Int = 20,
        keyword: String = "",
        online: Boolean? = null,
        statusId: String = "",
    ): List<Act> {
        val query = buildString {
            append("?oto=").append(
                when (online) {
                    true -> "1"
                    false -> "0"
                    null -> ""
                }
            )
            append("&cur=").append(page)
            append("&size=").append(size)
            append("&actiStatusId=").append(statusId)
            append("&actiClassId=&actiTypeId=&actiModeId=")
            append("&name=").append(EduHttp.enc(keyword))
        }
        val reply = EduHttp.get(session, "$BASE/apps/activityImpl/list/getActivityV2$query", token)
        val body = json(reply)
        val records = body.optJSONObject("data")?.optJSONArray("records")
            ?: body.optJSONArray("data")
            ?: JSONArray()
        return (0 until records.length()).mapNotNull { actOf(records.optJSONObject(it)) }
    }

    /** 拉一条活动的详情。 */
    fun detail(session: JwxtApi.Session, token: String, activityId: String): Detail {
        val reply = EduHttp.get(
            session,
            "$BASE/apps/activityImpl/detail?activityId=${EduHttp.enc(activityId)}",
            token,
        )
        val item = json(reply).optJSONObject("data")?.optJSONObject("item")
            ?: json(reply).optJSONObject("data")
            ?: throw EduException("活动详情是空的，可能已被撤下")
        return detailOf(item)
    }

    /**
     * 报名。成功返回服务端给的提示语。
     *
     * 编码必须是 `para=<json>` 表单，不能直接发原始 JSON——服务端只认前者，
     * 后者一律 422。官方前端所有 POST 都走 post() 里的 para 包装，这里照抄。
     */
    fun join(session: JwxtApi.Session, token: String, activityId: String): String {
        val reply = EduHttp.post(
            session,
            "$BASE/apps/activityImpl/join",
            para(JSONObject().put("activityId", activityId)),
            EduHttp.FORM,
            token,
        )
        val json = runCatching { JSONObject(reply.text) }.getOrNull()
        val ok = json?.optInt("code", reply.code) ?: reply.code
        if (!reply.ok || ok !in 200..299) {
            throw EduException(json?.optString("msg").orEmpty().ifBlank { "报名失败：HTTP ${reply.code}" })
        }
        return json?.optString("data").orEmpty().ifBlank { "报名成功" }
    }

    // ---------- 组织 ----------

    /**
     * 组织列表。
     *
     * 注意：这套接口只认 `para=<json>`——平铺的 `?cur=&size=` 虽然也回 200，
     * 但 size 被忽略（永远是 20 条），筛选条件也进不去。[level] 1=校级 2=院系级，
     * [nature] 1=学生会 2=社团协会 3=其他，空串表示不筛。
     */
    fun orgList(
        session: JwxtApi.Session,
        token: String,
        page: Int = 1,
        size: Int = 20,
        keyword: String = "",
        level: String = "",
        nature: String = "",
    ): List<Org> {
        val para = JSONObject()
            .put("name", keyword)
            .put("cur", page)
            .put("size", size)
            .put("level", level)
            .put("nature", nature)
            .toString()
        val reply = EduHttp.get(session, "$BASE/apps/org/list?para=${EduHttp.enc(para)}", token)
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { orgOf(records.optJSONObject(it)) }
    }

    /** 组织详情。[key] 是组织 id，[type] 是列表里的 type（2=社团类，1=学生会）。 */
    fun orgDetail(session: JwxtApi.Session, token: String, key: String, type: Int): OrgDetail {
        val para = JSONObject().put("key", key).put("type", type).toString()
        val reply = EduHttp.get(session, "$BASE/apps/org/detail?para=${EduHttp.enc(para)}", token)
        val data = json(reply).optJSONObject("data") ?: throw EduException("组织详情是空的")
        return OrgDetail(
            id = str(data, "id"),
            name = str(data, "name"),
            type = data.optInt("type", type),
            rule = data.optInt("rule", 1),
            nature = data.optInt("nature", 0),
            introduce = str(data, "introduce"),
            peopleCount = data.optInt("peopleCount", 0),
        )
    }

    /** 某个组织办过的活动，字段与活动列表一致。 */
    fun orgActivities(
        session: JwxtApi.Session,
        token: String,
        key: String,
        page: Int = 1,
        size: Int = 20,
    ): List<Act> {
        val para = JSONObject().put("key", key).put("cur", page).put("size", size).toString()
        val reply = EduHttp.get(session, "$BASE/apps/org/activity/list?para=${EduHttp.enc(para)}", token)
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { actOf(records.optJSONObject(it)) }
    }

    /**
     * 加入组织。成功返回服务端提示语。
     * [rule] 1 = 提交申请等审核，2/3 = 直接加入——这句话是官方前端的分支，文案也照抄。
     */
    fun joinOrg(
        session: JwxtApi.Session,
        token: String,
        key: String,
        userId: String,
        identity: String,
    ): String {
        val payload = JSONObject()
            .put("id", key)
            .put("userId", userId)
            .put("identity", identity)
        val reply = EduHttp.post(session, "$BASE/apps/org/joinorg", para(payload), EduHttp.FORM, token)
        val parsed = runCatching { JSONObject(reply.text) }.getOrNull()
        val code = parsed?.optInt("code", reply.code) ?: reply.code
        if (!reply.ok || code !in 200..299) {
            throw EduException(parsed?.optString("msg").orEmpty().ifBlank { "加入组织失败：HTTP ${reply.code}" })
        }
        return parsed?.optString("data").orEmpty().ifBlank { "已提交" }
    }

    // ---------- 我的申报 / 学籍 / 签到 ----------

    /** 我的申报列表。 */
    fun myApplies(session: JwxtApi.Session, token: String, page: Int = 1, size: Int = 50): List<Apply> {
        val reply = EduHttp.get(
            session,
            "$BASE/apps/project/myProjectApply/list?cur=$page&size=$size",
            token,
        )
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { o ->
            records.optJSONObject(o)?.let { item ->
                val applyId = str(item, "applyId").ifBlank { str(item, "tplId") }
                if (applyId.isBlank()) return@let null
                Apply(
                    applyId = applyId,
                    proName = str(item, "proName"),
                    optionName = str(item, "optionName"),
                    className = str(item, "className"),
                    hours = item.optDouble("hours", 0.0),
                    startTime = str(item, "startTime"),
                    endTime = str(item, "endTime"),
                    status = item.optInt("status", 2),
                )
            }
        }
    }

    /** 学籍信息。myinfo 比 myBaseInfo 多了「佛山校区 / 学院 / 专业 / 班级」这些名字。 */
    fun myProfile(session: JwxtApi.Session, token: String): Profile {
        val data = json(EduHttp.get(session, "$BASE/apps/user/myinfo", token)).optJSONObject("data")
            ?: throw EduException("没取到学籍信息")
        return Profile(
            userId = str(data, "id"),
            identity = str(data, "identity"),
            name = str(data, "name"),
            code = str(data, "code"),
            gender = when (data.optInt("gender", 0)) {
                1 -> "男"
                2 -> "女"
                else -> ""
            },
            ethnicName = str(data, "ethnicName"),
            politics = when (data.optInt("politics", -1)) {
                0 -> "群众"
                1 -> "团员"
                2 -> "党员"
                3 -> "其他党派"
                4 -> "预备党员"
                else -> ""
            },
            campusName = str(data, "campusName"),
            collegeName = str(data, "collegeName"),
            majorName = str(data, "majorName"),
            className = str(data, "className"),
            gradeName = str(data, "gradeName"),
        )
    }

    /** 签到签退记录。分页参数叫 page，不是 cur。 */
    fun signRecords(session: JwxtApi.Session, token: String, page: Int = 1, size: Int = 50): List<SignRecord> {
        val reply = EduHttp.get(
            session,
            "$BASE/apps/activity/sign/records?page=$page&size=$size",
            token,
        )
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { i ->
            val o = records.optJSONObject(i) ?: return@mapNotNull null
            val summary = o.optJSONObject("summary")
            val segs = o.optJSONArray("segments")
            SignRecord(
                activityId = str(o, "activityId"),
                name = str(o, "name"),
                startTime = str(o, "startTime"),
                endTime = str(o, "endTime"),
                effective = summary?.optInt("effective", 0) ?: 0,
                ongoing = summary?.optInt("ongoing", 0) ?: 0,
                invalid = summary?.optInt("invalid", 0) ?: 0,
                segments = (0 until (segs?.length() ?: 0)).mapNotNull { k ->
                    segs?.optJSONObject(k)?.let { s ->
                        SignSegment(
                            inTime = str(s, "inTime"),
                            outTime = str(s, "outTime"),
                            minutes = s.optInt("durationMinutes", 0),
                            status = str(s, "status"),
                            invalidReason = str(s, "invalidReason"),
                        )
                    }
                },
            )
        }
    }

    // ---------- 我的报名 / 签到 ----------

    /**
     * 我的报名（我报过的活动）。
     *
     * [type] 是官方前端从页面参数原样带下来的 mode，服务端按它分档：
     * 不传 / 0 / 4 / 5 都回全部 16 条，3 回 15 条，1 和 2 是空。这里默认不传，
     * 免得按一个还没弄懂的语义把记录挡掉一半。
     */
    fun myJoins(session: JwxtApi.Session, token: String, page: Int = 1, size: Int = 50): List<MyJoin> {
        val reply = EduHttp.get(
            session,
            "$BASE/apps/activityImpl/getmyjoinactivitylist?cur=$page&size=$size",
            token,
        )
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { i ->
            val o = records.optJSONObject(i) ?: return@mapNotNull null
            val id = str(o, "id").ifBlank { str(o, "actId") }
            val name = str(o, "name")
            if (id.isBlank() || name.isBlank()) return@mapNotNull null
            MyJoin(
                id = id,
                name = name,
                org = str(o, "orgName"),
                category = str(o, "category"),
                typeName = str(o, "typeName"),
                startTime = str(o, "startTime"),
                endTime = str(o, "endTime"),
                hours = o.optDouble("hours2", o.optDouble("hours", 0.0)),
                online = o.optInt("oto", 0) == 1,
                auditStatus = o.optInt("audit_status", -1),
                auditRemark = str(o, "audit_remark"),
            )
        }
    }

    /**
     * 我的组织：我加入的组织 / 班级 / 机构。
     * 官方「我的组织」页走 GET /org/my/list?cur=&size=（平铺参数即可，不需要 para）。
     * 返回记录的字段与组织列表不同：userid / role / duty / status / orgId / type / name 等。
     */
    fun myOrgs(session: JwxtApi.Session, token: String, page: Int = 1, size: Int = 50): List<MyOrg> {
        val reply = EduHttp.get(
            session,
            "$BASE/apps/org/my/list?cur=$page&size=$size",
            token,
        )
        val records = json(reply).optJSONObject("data")?.optJSONArray("records") ?: JSONArray()
        return (0 until records.length()).mapNotNull { i ->
            val o = records.optJSONObject(i) ?: return@mapNotNull null
            val orgId = str(o, "orgId")
            val name = str(o, "name")
            if (orgId.isBlank() || name.isBlank()) return@mapNotNull null
            MyOrg(
                orgId = orgId,
                name = name,
                type = o.optInt("type", 0),
                nature = o.optInt("nature", 0),
                role = o.optInt("role", 0),
                status = o.optInt("status", 0),
                logo = str(o, "logo"),
            )
        }
    }

    /**
     * 口令签到第一步：四位数字码换成「哪个活动、签到还是签退」。
     *
     * 码无效时服务端回 code=STS:2002「无效数字码」，[json] 会把它抛成 EduException。
     */
    fun signCode(session: JwxtApi.Session, token: String, code: String): SignCode {
        val data = json(
            EduHttp.get(session, "$BASE/apps/activity/getactinfobycode?code=${EduHttp.enc(code)}", token),
        ).optJSONObject("data") ?: throw EduException("这个口令查不到活动")
        val actId = str(data, "actId")
        if (actId.isBlank()) throw EduException("这个口令查不到活动")
        return SignCode(
            code = code,
            actId = actId,
            actName = str(data, "actName"),
            mode = str(data, "mode"),
        )
    }

    /** 口令签到第二步：拿 [SignCode] 提交。成功返回服务端那句提示。 */
    fun signByCode(session: JwxtApi.Session, token: String, sign: SignCode): String {
        val reply = EduHttp.post(
            session,
            "$BASE/apps/activity/signbycode",
            para(
                JSONObject()
                    .put("code", sign.code)
                    .put("actId", sign.actId)
                    .put("mode", sign.mode),
            ),
            EduHttp.FORM,
            token,
        )
        val body = json(reply)
        return body.optString("msg").orEmpty().ifBlank { "${sign.actName} ${sign.modeText}成功" }
    }

    /**
     * 扫码签到：二维码里那段 JSON 原样当正文提交。
     *
     * 官方前端就是 `JSON.parse(resultStr)` 之后把整个对象 POST 给 /activity/tosign，
     * 所以这里也不猜字段，解析出来直接转发（至少含 actId 与 mode）。
     *
     * @return 二维码里写的活动名与动作，供调用方拼提示语。
     */
    fun scanSign(session: JwxtApi.Session, token: String, payload: JSONObject): Pair<String, String> {
        val name = str(payload, "name").ifBlank { str(payload, "actName") }
        val mode = str(payload, "mode")
        val reply = EduHttp.post(session, "$BASE/apps/activity/tosign", para(payload), EduHttp.FORM, token)
        val body = json(reply)
        val ok = body.optString("msg").orEmpty().ifBlank { "已提交" }
        val action = if (mode == "out") "签退" else "签到"
        return (if (name.isBlank()) ok else "$name $action 成功") to ok
    }

    // ---------- 解析 ----------

    private fun orgOf(o: JSONObject?): Org? {
        o ?: return null
        val id = str(o, "id")
        val name = str(o, "name")
        if (id.isBlank() || name.isBlank()) return null
        return Org(
            id = id,
            name = name,
            type = o.optInt("type", 0),
            level = o.optInt("level", 0),
            nature = o.optInt("nature", 0),
            rule = o.optInt("rule", 1),
            introduce = str(o, "introduce"),
            memberCount = o.optInt("memberCount", 0),
            logo = str(o, "logo"),
        )
    }

    /**
     * 取字符串字段。
     *
     * 接口里大量字段是 JSON 的 null，而 [JSONObject.optString] 会把 null 转成字面量
     * "null"（不是空串），直接 text 到界面上就出现「地点 null」「承办 null」。
     * 这里统一把 "null" 当作没填。
     */
    private fun str(o: JSONObject, key: String): String {
        val v = o.optString(key)
        return if (v == "null" || v == "undefined") "" else v.trim()
    }

    private fun json(reply: EduHttp.Reply): JSONObject {
        if (reply.code == 401) throw EduException("登录已过期，重新登录一次")
        val body = runCatching { JSONObject(reply.text) }.getOrNull()
            ?: throw EduException("第二课堂返回的不是 JSON，接口可能改版了")
        val code = body.optInt("code", reply.code)
        if (!reply.ok || code !in 200..299) {
            throw EduException(body.optString("msg").orEmpty().ifBlank { "HTTP ${reply.code}" })
        }
        return body
    }

    /**
     * 把 JSON 包成 `para=<json>` 表单正文。
     *
     * 整套第二课堂接口的写操作只认这一种形状：直接发原始 JSON 一律 422，
     * 平铺 query 参数则被静默忽略（size 永远是 20、筛选条件进不去）。
     */
    private fun para(json: JSONObject): String = "para=" + EduHttp.enc(json.toString())

    private fun actOf(o: JSONObject?): Act? {
        o ?: return null
        val id = str(o, "id").ifBlank { str(o, "actId") }
        val name = str(o, "name")
        if (id.isBlank() || name.isBlank()) return null
        return Act(
            id = id,
            name = name,
            org = str(o, "orgName"),
            category = str(o, "category"),
            typeName = str(o, "typeName"),
            startTime = str(o, "startTime"),
            endTime = str(o, "endTime"),
            hours = o.optDouble("hours", 0.0),
            online = o.optInt("oto", 0) == 1,
            logo = str(o, "logo"),
        )
    }

    private fun detailOf(o: JSONObject): Detail {
        val place = str(o, "pitchName").ifBlank { str(o, "pitchAddress") }
        val peopleLimit = o.optInt("peopleLimit", 0)
        val peopleCount = o.optInt("peopleCount", 0)
        return Detail(
            id = str(o, "actId").ifBlank { str(o, "id") },
            name = str(o, "name"),
            hours = o.optDouble("hours", 0.0),
            sumHours = o.optDouble("sumHours", 0.0),
            className = str(o, "className"),
            typeName = str(o, "typeName"),
            zhubanName = str(o, "zhubanName"),
            xiebanName = str(o, "xiebanName"),
            startTime = str(o, "startTime"),
            endTime = str(o, "endTime"),
            enrollEndTime = str(o, "enrollEndTime"),
            place = place,
            online = o.optInt("oto", 0) == 1,
            introduce = str(o, "introduce"),
            adminName = str(o, "adminName"),
            adminContact = str(o, "adminContact"),
            teacherName = str(o, "teacherName"),
            classHours = o.optDouble("classHours", 0.0),
            peopleCount = peopleCount,
            peopleLimit = peopleLimit,
            enroll = enrollStateOf(o, peopleCount, peopleLimit),
        )
    }

    /**
     * 报名状态。字段含义按官方前端 `pages/acti/info` 的模板反推：
     *
     * - `apply_status`：1 = 可以报名（显示「我要报名」）；2 = 已报名，正在审核（「活动审核中」）；
     *   0 = 审核未通过（「重新申请」）。**它不是「我报没报」的布尔位**，所以不能拿 `!= 0` 当已报名。
     * - `auditStatus`：空串 = 还没报名；"1" = 已通过；"2" = 报名已结束；"0" = 审核未通过（带 auditRemark）。
     * - `daole`：1 = 报名截止。
     * - `peopleLimit`：0 = 不限名额；`peopleLimit == peopleCount` 才是满员（不是 `in 1..peopleCount`）。
     */
    private fun enrollStateOf(
        o: JSONObject,
        peopleCount: Int,
        peopleLimit: Int,
    ): EnrollState {
        val applyStatus = o.optInt("apply_status", -1)
        val audit = str(o, "auditStatus")
        val passed = audit == "1"
        return when {
            applyStatus == 2 -> EnrollState(false, "已报名，等待审核")
            applyStatus == 0 && audit == "0" -> {
                val why = str(o, "auditRemark")
                EnrollState(false, if (why.isBlank()) "审核未通过" else "审核未通过：$why")
            }
            o.optInt("daole", 0) == 1 -> EnrollState(false, "报名已结束")
            audit == "2" -> EnrollState(false, "报名已结束")
            passed -> EnrollState(false, "已报名，等待审核")
            peopleLimit > 0 && peopleCount >= peopleLimit -> EnrollState(false, "名额已满（$peopleCount/$peopleLimit）")
            else -> EnrollState(true, "")
        }
    }
}
