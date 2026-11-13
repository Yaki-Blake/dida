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
   ├─ design\              开发计划书、UI 草图、设计令牌
   └─ screenshots\         运行截图
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
| `start-frp.cmd` | 一键上线：启动桌面端 + 樱花 frp 隧道，手机走公网地址同步 |

`start-frp.cmd` 需要知道自己的公网地址，两种给法任选其一：

1. 在 `.env` 里加一行 `DIDA_PUBLIC_URL=https://你的隧道地址`
2. 命令行传入：`start-frp.cmd -PublicUrl https://你的隧道地址`

它会先确认桌面端在监听 5080，再拉起樱花 frp 启动器（隧道要在启动器里设为自动启动），最后探测这个公网地址是否真能打到同步服务。

数据库连接、同步端口等配置在 `pc\DidaLite\.env`（复制 `.env.example` 后修改，该文件不进版本库），首次运行自动建表。
