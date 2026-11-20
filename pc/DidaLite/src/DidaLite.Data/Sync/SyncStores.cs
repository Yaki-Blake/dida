using DidaLite.Core.Models;

namespace DidaLite.Data.Sync;

/// <summary>
/// 同步权威库的读写契约。多用户版：所有方法都带 userId，按用户隔离数据。
/// 现在只保留 <see cref="LocalTaskStore"/> 一份实现（SQLite）——服务器与桌面内嵌服务共用。
/// </summary>
public interface ITaskStore
{
    /// <summary>读取该用户 rev 大于游标的全部任务，含墓碑。</summary>
    Task<IReadOnlyList<TaskItem>> GetChangedSinceAsync(int userId, long sinceRev, CancellationToken ct = default);

    /// <summary>读取该账号完整日历快照（全部任务，含墓碑）——全量同步用，不看游标。</summary>
    Task<IReadOnlyList<TaskItem>> GetAllAsync(int userId, CancellationToken ct = default);

    /// <summary>该用户当前最大 rev，用作客户端游标上限。</summary>
    Task<long> GetMaxRevAsync(int userId, CancellationToken ct = default);

    /// <summary>写入该用户的一批变更：逐条裁决、分配该用户自己的新 rev。</summary>
    Task<ApplyResult> ApplyAsync(int userId, IEnumerable<TaskItem> changes, CancellationToken ct = default);
}

/// <summary>
/// 同步服务端的闹钟库。闹钟不是增量同步：电脑端每次上传一份全量快照，服务器整表替换，
/// 手机端只上报「开关 / 删除」两种改动，由服务器写回。
/// 桌面内嵌服务（本地模式）不走这条——它和电脑端共用一个库，直接读本地 alarm 表。
/// </summary>
public interface IAlarmStore
{
    /// <summary>该账号全部未删除的闹钟。</summary>
    Task<IReadOnlyList<AlarmItem>> GetAlarmsAsync(int userId, CancellationToken ct = default);

    /// <summary>用电脑端上传的全量快照替换；列表里没出现的旧闹钟置墓碑。</summary>
    Task ReplaceAsync(int userId, IReadOnlyList<AlarmItem> alarms, CancellationToken ct = default);

    /// <summary>手机端改的开关 / 删除。</summary>
    Task ApplyChangesAsync(int userId, IReadOnlyList<AlarmChange> changes, CancellationToken ct = default);
}

/// <summary>
/// 同步服务端的便签库。与任务同构（增量 + 墓碑 + 时间戳裁决），
/// 但发号器与游标独立成一套，便签的改动不会推高任务的 rev。
/// </summary>
public interface INoteStore
{
    /// <summary>读取该用户 rev 大于游标的全部便签，含墓碑。</summary>
    Task<IReadOnlyList<NoteItem>> GetChangedSinceAsync(int userId, long sinceRev, CancellationToken ct = default);

    /// <summary>读取该账号全部便签快照（含墓碑）——全量同步用，不看游标。</summary>
    Task<IReadOnlyList<NoteItem>> GetAllAsync(int userId, CancellationToken ct = default);

    /// <summary>该用户当前最大便签 rev，用作客户端便签游标上限。</summary>
    Task<long> GetMaxRevAsync(int userId, CancellationToken ct = default);

    /// <summary>写入该用户的一批便签改动：逐条裁决、分配新 rev。</summary>
    Task<ApplyResult> ApplyAsync(int userId, IEnumerable<NoteItem> changes, CancellationToken ct = default);
}

/// <summary>
/// 同步账号的读写契约。多用户版：用户名唯一，密码只存 PBKDF2 派生值；
/// 登录时用户名不存在则自动注册（隐式注册），存在且密码正确才换发 token。
/// token 按用户独立保存，一个用户重新登录不会顶掉其他人。
/// </summary>
public interface IUserStore
{
    /// <summary>账号总数；健康检查和命令行展示用。</summary>
    Task<int> CountAsync();

    /// <summary>按用户名取该用户的展示名；不存在返回 null。</summary>
    Task<string?> GetNameAsync(string name);

    /// <summary>
    /// 登录：用户名不存在则创建账号并返回 token；存在且密码正确返回 token；
    /// 存在但密码错误返回 null。返回的 int 是稳定的 userId，供任务库隔离用。
    /// </summary>
    Task<(string Token, int UserId)?> LoginAsync(string name, string password);

    /// <summary>token 是否合法；合法时带出 userId。</summary>
    Task<int?> ResolveTokenAsync(string? token);

    /// <summary>设置/重设某账号密码（命令行 set-user 用），返回是否创建了新账号。</summary>
    Task<bool> SaveAsync(string name, string password);

    /// <summary>列出所有账号名（命令行 info 用）。</summary>
    Task<IReadOnlyList<string>> ListNamesAsync();
}

/// <summary>一批变更的处理结果。</summary>
public sealed class ApplyResult
{
    public long MaxRev { get; set; }
    public List<AcceptedChange> Accepted { get; } = [];
    public List<RejectedChange> Rejected { get; } = [];
}

/// <summary>被接纳的变更及服务端分配的新 rev。</summary>
public sealed record AcceptedChange(string Id, long Rev);

/// <summary>被拒的变更，附带服务端当前版本供客户端覆盖本地。</summary>
public sealed record RejectedChange(string Id, string Reason, TaskItem Server);
