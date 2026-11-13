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

# 2. 组装 stage：发布产物改名 dida.exe，再带上 .env 与数据库脚本
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Rename-Item (Join-Path $stage 'DidaLite.App.exe') 'dida.exe'
Copy-Item (Join-Path $pcRoot '.env') $stage
Copy-Item (Join-Path $pcRoot 'scripts\setup-db.cmd') $stage
Copy-Item (Join-Path $pcRoot 'scripts\setup-db.ps1') $stage

# 3. 编译安装包
dotnet build (Join-Path $PSScriptRoot 'DidaInstaller.wixproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WiX 编译失败' }

Write-Host ('MSI: ' + (Join-Path $PSScriptRoot 'bin\Release\dida.msi')) -ForegroundColor Green
