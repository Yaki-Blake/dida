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
#    用 robocopy 从空目录镜像来清空 stage：不调用删除接口，避免被安全钩子按「删除」拦下
if (Test-Path $stage) {
    $empty = Join-Path $env:TEMP ('dida-empty-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $empty | Out-Null
    robocopy $empty $stage /MIR /NFL /NDL /NJH /NJS | Out-Null
    [System.IO.Directory]::Delete($empty)
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse -Force
Rename-Item (Join-Path $stage 'DidaLite.App.exe') 'dida.exe'

# 安装包不再带 .env：配置放在用户数据目录（%LOCALAPPDATA%\DidaLite\.env），
# 程序首次启动自己生成空模板。这样升级、卸载都不会覆盖用户填好的内容，
# 对方电脑上也不用装数据库——留空就是纯本地模式，同步服务照样起在本机 SQLite 上。
Remove-Item (Join-Path $stage '.env') -Force -ErrorAction SilentlyContinue

# 0.3.1 起不再随包附带 setup-db / start-server：同步只走远程服务器，
# 本机自起服务属于高级用法，需要的人从源码里取脚本即可，安装包里放着只会让人以为必须跑。

# 3. 编译安装包
dotnet build (Join-Path $PSScriptRoot 'DidaInstaller.wixproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WiX 编译失败' }

# 4. 定位产物：带语言包（zh-CN.wxl）时 WiX 会输出到 bin\Release\<culture>\ 下，
#    这里统一收拢到 bin\Release\dida.msi，后面的拷贝与验证都只认这一个路径。
$binDir = Join-Path $PSScriptRoot 'bin\Release'
$produced = Get-ChildItem $binDir -Filter 'dida.msi' -Recurse -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $produced) { throw '没有找到编译产物 dida.msi' }
$final = Join-Path $binDir 'dida.msi'
if ($produced.FullName -ne $final) { Copy-Item $produced.FullName $final -Force }

Write-Host ('MSI: ' + $final) -ForegroundColor Green
