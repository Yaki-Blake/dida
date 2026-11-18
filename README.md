# 滴答（Dida）

电脑端（Windows / WPF）+ 手机端（Android）双端日程软件：待办、便签、闹钟、课表一处管理，两端自动同步。提醒在设备本地排程，断网也会准时响。

## 快速上手

### 1. 下载安装

到 [Releases](https://github.com/Yaki-Blake/dida/releases) 下载最新版：

| 文件 | 装在哪 | 说明 |
| --- | --- | --- |
| `dida.msi` | Windows | 双击安装，装完在系统托盘运行 |
| `dida.apk` | Android | 直接安装（需允许「安装未知应用」） |
| `dida-uninstall.cmd` | Windows | 卸载后清理残留注册信息，右键「以管理员身份运行」；加 `-CheckOnly` 参数只查看不动 |

### 2. 打通同步（约 1 分钟）

1. **电脑端**：打开滴答 → 设置 → 同步服务器。默认已填好公共服务器地址，**自己起一个用户名和密码**点登录即可——首次登录会自动注册这个账号，之后两端都用同一组。
2. **手机端**：打开滴答 → 设置 → 填同一个服务器地址、同一组用户名密码 → 「检查连接」确认通过。
3. **手机端**主界面点「同步」。之后日常改动两端点同步即可合流。

> 想用自己的服务器或完全离线单机用，见下方「同步模式」。

### 3. 日常使用

- **电脑端**：日视图安排待办、月/年视图总览、「专注」计时、课表页看课。到点的提醒从托盘弹出。
- **手机端**：主界面三个按钮——「仅上传」把本地改动推上去、「仅下载」拉取电脑端的改动、「同步」双向一次到位。便签、闹钟在顶部标签页里看和改。
- **互相删除也是同步的**：在手机系统日历里删掉一条日程，回滴答点「同步」，电脑端那条会一起消失；反过来电脑端删掉的任务，手机下次下载时同样删掉。
- **课表**：电脑端支持从教务系统一键导入课表（需要能访问学校教务系统），导入后按周显示。
- **手机日历联动**（手机端设置页）：
  - 「从系统日历读入任务」：把手机日历里已有的日程导入滴答，随同步上云；
  - 「写入日历」：把电脑端同步下来的任务写进手机系统日历，让系统日历 App 也能看到。

## 各端功能一览

| 功能 | 电脑端 | 手机端 |
| --- | --- | --- |
| 待办 / 提醒 | ✔（日视图、月/年视图、专注） | ✔（提醒与全屏闹钟） |
| 便签 | ✔ | ✔（可查看、编辑，双向同步） |
| 闹钟 | ✔ | ✔（手机上停用/删除闹钟，电脑端列表跟着刷新） |
| 课表 | ✔（教务导入） | —（随同步可见） |
| 系统日历读入 / 写出 | — | ✔ |

## 数据与隐私

- 不上传任何数据到第三方；数据只存在你的设备和**你自己登录的那台同步服务器**上，密码以 PBKDF2 派生值存储，明文不落库。
- 电脑端数据目录：`%LOCALAPPDATA%\DidaLite`（本地库 + `.env` 配置）。
- 手机端数据在应用私有目录，卸载即清空；需要连残留注册一起清就用 `dida-uninstall.cmd`。

## 同步模式

| 模式 | 适合 | 怎么配 |
| --- | --- | --- |
| 公共服务器（默认） | 普通用户，出门在外也能同步 | 什么都不用改，两端填同一账号即可 |
| 自建服务器 | 想完全自己掌控数据 | 一台 Linux 服务器，按 [`docs/deploy/README.md`](docs/deploy/README.md) 跑 `install.sh`，再把两端的服务器地址改成你的域名 |
| 本机 / 局域网 | 只在家里、宿舍同步 | 电脑端「本机」模式自己起服务，手机填电脑的局域网地址（`scripts/` 里的 `setup-db`、`start-server` 是这套玩法的辅助脚本） |

## 从源码构建

```
# 电脑端（.NET 10）
dotnet publish pc\DidaLite\src\DidaLite.App\DidaLite.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64

# 电脑端单元测试
dotnet test pc\DidaLite\DidaLite.slnx

# 手机端（Kotlin / Gradle，Android Studio 直接打开 android\ 目录也行）
cd android && gradle assembleDebug

# 手机端单元测试（工程路径含中文时用这个包装脚本跑）
python android\run_tests.py

# MSI 安装包（WiX 由 NuGet 还原，无需全局安装）
powershell -ExecutionPolicy Bypass -File pc\DidaLite\installer\build-msi.ps1
```

## 目录结构

```
dida
├─ android\                     手机端（Gradle 工程，包名 com.didalite.android）
│  ├─ app\src\main\...\         界面、本地库（SQLite）、同步、日历读写、提醒排程
│  ├─ app\src\test\...\         单元测试（日历删除回流等核心逻辑）
│  └─ run_tests.py              中文路径下跑单测的辅助脚本
├─ pc\DidaLite\
│  ├─ src\DidaLite.App          WPF 界面、托盘、提醒、设置
│  ├─ src\DidaLite.Core         模型、重复规则、提醒推导
│  ├─ src\DidaLite.Data         本地库与同步存储（远程权威库 / 本机模式）
│  ├─ src\DidaLite.Sync         Linux 服务端（自建同步服务器用）
│  ├─ src\DidaLite.Tests        集成测试
│  ├─ installer\                MSI 打包（build-msi.ps1 一键出包）
│  └─ scripts\                  setup-db / start-server / start-frp（本机模式辅助脚本）
└─ docs\
   ├─ design\                   开发计划书、UI 草图、设计令牌
   └─ deploy\                   Linux 自建服务器部署脚本（install.sh / systemd / nginx 示例）
```

## 版本说明

- 端与端版本号独立：电脑端当前 0.3.9，手机端当前 0.1.9，两边的改动在同步协议上互通。
- 手机端删除日程能同步删除电脑端，需要手机端 ≥ 0.1.9。
