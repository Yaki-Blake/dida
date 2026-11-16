# 滴答 · 生成 MSI 安装包
# 用法：powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1
#      加 -SkipPublish 可复用 publish\win-x64 里已有的发布产物
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$pcRoot = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $PSScriptRoot 'stage'
$publish = Join-Path $pcRoot 'publish\win-x64'

# 1. 发布自包含版本
if (-not $SkipPublish) {
    dotnet publish (Join-Path $pcRoot 'src\DidaLite.App\DidaLite.App.csproj') `
        -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
}

# 2. 组装 stage：发布产物改名 dida.exe，再带上配置模板与数据库脚本
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Rename-Item (Join-Path $stage 'DidaLite.App.exe') 'dida.exe'

# 安装包内不放真实数据库口令：只写一份空白模板，用户需要双端同步时自行填写。
# 留空时程序以纯本地模式运行（EnvFile 取不到连接串 → SyncServer 不启动），不会报错。
$envTemplate = @'
# 滴答 · 同步数据库配置（安装包内为空白模板，不含任何口令）
# 需要用手机双端同步时，把下面一行换成自己的 MySQL 连接串，例如：
# DIDA_DB_CONNECTION=server=127.0.0.1;port=3306;database=ticktick;user=root;password=你的密码
# 留空 = 纯本地模式，所有数据只存在本机。
DIDA_DB_CONNECTION=

# 局域网访问地址（手机在同一 WiFi 下连电脑时填，可留空）
DIDA_LAN_ADDR=
'@
$envTemplate | Set-Content -Path (Join-Path $stage '.env') -Encoding utf8

Copy-Item (Join-Path $pcRoot 'scripts\setup-db.cmd') $stage
Copy-Item (Join-Path $pcRoot 'scripts\setup-db.ps1') $stage

# 3. 编译安装包
dotnet build (Join-Path $PSScriptRoot 'DidaInstaller.wixproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WiX 编译失败' }

Write-Host ('MSI: ' + (Join-Path $PSScriptRoot 'bin\Release\dida.msi')) -ForegroundColor Green
