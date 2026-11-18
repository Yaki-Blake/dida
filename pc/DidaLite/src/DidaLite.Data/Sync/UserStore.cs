using DidaLite.Core;

namespace DidaLite.Data.Sync;

/// <summary>
/// 账号存储工厂。多用户版统一使用本机 SQLite（<see cref="LocalUserStore"/>），
/// MySQL 实现已移除——服务端与桌面内嵌服务共用同一套 SQLite 账号逻辑。
/// </summary>
public static class UserStore
{
    /// <summary>
    /// 取本机账号存储。桌面端本地库路径固定，服务端由调用方直接 new LocalUserStore(dbPath)。
    /// </summary>
    public static IUserStore Create() => new LocalUserStore(LocalStore.DefaultPath());
}
