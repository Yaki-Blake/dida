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
│  └─ scripts\             setup-db（装库/起库）、start-server（拉起同步服务）
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

数据库连接、同步端口等配置在 `pc\DidaLite\.env`（复制 `.env.example` 后修改，该文件不进版本库），首次运行自动建表。
