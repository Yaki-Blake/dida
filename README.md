# 滴答 · 源码

电脑端（.NET / WPF）+ 手机端（Kotlin / Android）的完整源码，数据存本机 MySQL。

## 目录

```
dida
├─ pc\DidaLite\            电脑端
│  ├─ DidaLite.slnx
│  ├─ src\DidaLite.App     WPF 界面、托盘、提醒、内嵌同步服务
│  ├─ src\DidaLite.Core    模型、重复规则、提醒推导
│  ├─ src\DidaLite.Data    SQLite 本地库、MySQL 同步存储
│  ├─ src\DidaLite.Tests   集成测试
│  ├─ sql\schema.sql       建表语句
│  ├─ installer\           MSI 打包（build-msi.ps1 一键出包）
│  └─ scripts\             setup-db（装库/起库）、start-server（拉起同步服务）、start-frp（樱花 frp 一键上线）
├─ android\                手机端（Gradle 工程，包名 com.didalite.android）
└─ docs\
   └─ design\              开发计划书、UI 草图、设计令牌
```

## 编译

```
# 电脑端
dotnet publish pc\DidaLite\src\DidaLite.App\DidaLite.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64

# 手机端（Android Studio 直接打开 android\ 目录，或）
cd android && gradlew.bat assembleDebug

# MSI 安装包（WiX SDK 由 NuGet 还原，无需全局安装；如需 wix 命令行则装到任意目录）
#   dotnet tool install wix --tool-path <目录>
powershell -ExecutionPolicy Bypass -File pc\DidaLite\installer\build-msi.ps1
```

安装包（`dida.msi` / `dida.apk`）见仓库 Releases。

## 脚本放在哪

`pc\DidaLite\scripts\` 里的脚本不会打进 MSI。需要时从 Releases 下载对应文件，放到**安装目录**（也就是 `dida.exe` 所在的那一层）再双击运行：

| 脚本 | 作用 |
| --- | --- |
| `setup-db.cmd` | 装 MySQL（没装时）/ 启动 MySQL，最后打印本机和手机可用的地址 |
| `start-server.cmd` | 只拉起同步服务 |
| `start-frp.cmd` | 一键上线：检测樱花 frp 与隧道、确认自动 HTTPS、把公网地址写进 `.env` 并拉起桌面端 |

`start-frp.cmd` 会自动完成这几件事：

1. 检测樱花 frp 是否安装；没装就打开官方下载页并给出安装步骤
2. 检查账号下有没有指向本机 5080 的隧道；没有就打开隧道页并给出建隧道参数
3. 检查隧道是否在线、是否开了「自动 HTTPS」；没开就提示去开
4. 隧道在线后从樱花日志里取公网地址，确认能打到本机，再写进 `.env` 的 `DIDA_LAN_ADDR`，最后拉起桌面端

需要手动指定地址时可以覆盖：`start-frp.cmd -PublicUrl https://你的隧道地址`。
数据库连接、同步端口等配置在 `pc\DidaLite\.env`（复制 `.env.example` 后修改，该文件不进版本库），首次运行自动建表。
