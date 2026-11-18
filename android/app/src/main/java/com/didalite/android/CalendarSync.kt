package com.didalite.android

import android.content.Context

/**
 * 日历删除回流：手机系统日历里被删掉的事件 → 对应任务打本地墓碑 → 下次同步让电脑端也删掉。
 *
 * 两种来源都管：
 * ① 从系统日历读进来的任务（C 开头）：日历是它的出处，事件没了任务就该没；
 * ② 电脑端同步下来、又被写进手机日历的任务：用户把我们写进去的事件删了，
 *    等于说「这条不要了」，那就该让电脑端也删掉，而不是下一轮又原样写回去。
 *
 * 事件和任务靠 calendar_link 表对应。老任务还没有链接行时，用任务 id 里编码的事件号回推一次；
 * 认不出来的宁可不处理，也不乱删。
 *
 * 墓碑只打本地，不直接联系服务端——同步面板会把它当普通改动推上去，电脑端那边自然就消失了。
 */
object CalendarSync {

    data class Result(
        /** 新登记上的「事件 ↔ 任务」对应关系条数。 */
        val linked: Int = 0,
        /** 因日历里被删而标记删除的任务条数。 */
        val removed: Int = 0,
        /** 被标记删除的任务标题，给同步摘要用。 */
        val titles: List<String> = emptyList(),
        /** 事件号撞车、认不出对应事件的老任务条数。 */
        val ambiguous: Int = 0,
        /** 这轮没做判断的原因（读不到日历、日历整体不可见等）。 */
        val note: String? = null,
    )

    /**
     * 算出来的结论：先补哪些「事件 ↔ 任务」链接，再删哪些任务。
     * 纯计算，不碰数据库，好单独拿真实数据验。
     */
    data class Plan(
        /** 要补登记的链接：任务 id → 日历事件 id。 */
        val linksToAdd: Map<String, Long>,
        /** 该打墓碑的任务 id。 */
        val tasksToRemove: List<String>,
        /** 事件号撞车、认不出对应事件的老任务条数。 */
        val ambiguous: Int,
    )

    /**
     * 扫一遍系统日历，把「日历里已经删掉的事件」对应到本地任务上，打上墓碑。
     * 幂等：同一个事件扫几次也只删一次。
     */
    fun scan(context: Context, db: Db): Result {
        val inventory = SystemCalendar.inventory(context)
            ?: return Result(note = "读不到系统日历，这轮没判断删除")
        if (inventory.isEmpty()) return Result(note = "系统日历里没有事件，这轮没判断删除")

        val tasks = db.all()
        val links = db.calendarLinks()

        // 事件突然少得离谱，多半是整个日历不可见（账号掉线、被隐藏），
        // 这种情况下不能当成「用户删了」，否则会误删一大片。
        val tracked = (links.keys + tasks.filter { SystemCalendar.isCalendarOrigin(it.id) }.map { it.id }).size
        if (tracked >= 4 && inventory.size < tracked / 4) {
            return Result(
                note = "系统日历里只剩 ${inventory.size} 个事件，像是日历整体不可见，这轮没判断删除"
            )
        }

        val plan = plan(inventory, tasks, links)

        // 补链接：老任务靠任务 id 里编码的事件号回推一次
        plan.linksToAdd.forEach { (taskId, eventId) -> db.linkCalendar(taskId, eventId) }

        // 打墓碑：标删除 + 标待上传 + 刷新时间，等同步推给电脑端
        val now = System.currentTimeMillis()
        val titles = mutableListOf<String>()
        for (id in plan.tasksToRemove) {
            val task = db.find(id) ?: continue
            if (task.deleted) continue
            if (db.tombstone(id, now)) titles.add(task.title)
        }

        return Result(
            linked = plan.linksToAdd.size,
            removed = titles.size,
            titles = titles,
            ambiguous = plan.ambiguous,
        )
    }

    /**
     * 核心判断，纯计算：
     *
     * ① 老任务（id 里编码过事件号）还没有链接行时，回推一次它在日历里对应哪条事件；
     *    回推不出来 = 那条事件在日历里已经没了，这条任务也就该删。
     * ② 按链接逐条核对：事件不见了或被标记删除，对应的任务就该删。
     */
    fun plan(
        inventory: List<SystemCalendar.EventRow>,
        tasks: List<Task>,
        links: Map<String, Long>,
    ): Plan {
        val eventsById = inventory.associateBy { it.eventId }
        val calendarTasks = tasks.filter { !it.deleted && SystemCalendar.isCalendarOrigin(it.id) }
        val linksToAdd = linkedMapOf<String, Long>()
        val doomed = linkedSetOf<String>()
        var ambiguous = 0

        for (task in calendarTasks) {
            if (links.containsKey(task.id)) continue
            if (SystemCalendar.isLegacyId(task.id)) {
                // 老任务：id 里编码过事件号，回推一次
                val candidates = inventory.filter { SystemCalendar.legacyEncode(it.eventId) == task.id }
                when {
                    candidates.isEmpty() -> doomed.add(task.id)
                    candidates.size == 1 -> linksToAdd[task.id] = candidates[0].eventId
                    else -> {
                        // 旧编码撞车了（3 和 30 会撞）：只有标题和时间都对得上才算数
                        val hit = candidates.filter { it.title == task.title && it.startAt == task.startAt }
                        if (hit.size == 1) {
                            linksToAdd[task.id] = hit[0].eventId
                        } else {
                            ambiguous++
                        }
                    }
                }
                continue
            }
            // 新编码：事件号能唯一反推回来，直接对号入座
            val eventId = SystemCalendar.decodeEventId(task.id) ?: continue
            val event = eventsById[eventId]
            if (event == null || event.deleted) {
                doomed.add(task.id)
            } else {
                linksToAdd[task.id] = eventId
            }
        }

        for ((taskId, eventId) in links + linksToAdd) {
            val event = eventsById[eventId]
            if (event != null && !event.deleted) continue
            val task = tasks.firstOrNull { it.id == taskId } ?: continue
            if (task.deleted) continue
            doomed.add(taskId)
        }

        return Plan(linksToAdd, doomed.toList(), ambiguous)
    }

    /** 给同步摘要用的一句话。 */
    fun describe(result: Result): String = when {
        result.note != null -> result.note
        result.removed > 0 -> "日历里删掉的 ${result.removed} 条已标记删除"
        else -> ""
    }
}
