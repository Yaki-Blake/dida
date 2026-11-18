# 滴答 · 源码改完，把新版本更新到本地安装目录（默认 D:\Dida）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File sync-to-d.ps1
#   powershell -ExecutionPolicy Bypass -File sync-to-d.ps1 -Launch
#   powershell -ExecutionPolicy Bypass -File sync-to-d.ps1 -SkipPublish
#   powershell -ExecutionPolicy Bypass -File sync-to-d.ps1 -Clean     # 同时删掉目标里多出来的旧文件
#
# 只覆盖程序文件；数据（.env / dida.db）在 %LOCALAPPDATA%\DidaLite，不在这个目录，不受影响。

param(
    [string]$Target = 'D:\Dida',
    [switch]$SkipPublish,
    [switch]$Clean,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$pcRoot  = $PSScriptRoot
$publish = Join-Path $pcRoot 'publish\win-x64'
$project = Join-Path $pcRoot 'src\DidaLite.App\DidaLite.App.csproj'

if (-not (Test-Path $Target)) { throw "目标目录不存在：$Target" }
if (-not (Test-Path (Join-Path $Target 'dida.exe'))) {
    Write-Host "注意：$Target 里原本没有 dida.exe，先确认这是安装位置而不是别的目录" -ForegroundColor Yellow
}

# 1. 发布自包含版本
if (-not $SkipPublish) {
    Write-Host '==> 编译发布（self-contained win-x64）' -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
} elseif (-not (Test-Path $publish)) {
    throw "没有可复用的发布产物：$publish"
}

# 2. 关闭正在运行的实例（先礼后兵，避免丢未落盘的数据）
$proc = Get-Process dida -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "==> 关闭正在运行的 dida.exe（PID $(($proc | Select-Object -ExpandProperty Id) -join ','))" -ForegroundColor Cyan
    $proc | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    $proc = Get-Process dida -ErrorAction SilentlyContinue
    if ($proc) { $proc | Stop-Process -Force; Start-Sleep -Milliseconds 600 }
}

# 3. 同步文件：默认只覆盖、不删除；-Clean 时镜像（顺带清掉旧版遗留的 setup-db 等）
$mode = if ($Clean) { '/MIR' } else { '/E' }
$roboArgs = @(
    $publish, $Target, $mode,
    '/XF', '.env', 'schedule.json', 'schedule_notes.json',
    '/NFL', '/NDL', '/NJH', '/NJS', '/R:1', '/W:1'
)
Write-Host "==> 同步到 $Target（$mode）" -ForegroundColor Cyan
& robocopy @roboArgs | Out-Null
$code = $LASTEXITCODE
if ($code -ge 8) { throw "robocopy 失败，退出码 $code" }

# 4. 发布产物叫 DidaLite.App.exe，安装目录里统一是 dida.exe
$appExe = Join-Path $Target 'DidaLite.App.exe'
if (Test-Path $appExe) { Copy-Item $appExe (Join-Path $Target 'dida.exe') -Force }

# 5. 结果
$files = Get-ChildItem $Target -Recurse -File
$exe = Get-Item (Join-Path $Target 'dida.exe')
$appDll = Get-Item (Join-Path $Target 'DidaLite.App.dll')

Write-Host ''
Write-Host '更新完成' -ForegroundColor Green
Write-Host ("  目录      {0}" -f $Target)
Write-Host ("  文件数    {0}" -f $files.Count)
Write-Host ("  dida.exe  {0}  {1:yyyy-MM-dd HH:mm:ss}" -f $exe.Length, $exe.LastWriteTime)
Write-Host ("  App.dll   {0}  {1:yyyy-MM-dd HH:mm:ss}" -f $appDll.Length, $appDll.LastWriteTime)
Write-Host ("  数据目录  {0}" -f (Join-Path $env:LOCALAPPDATA 'DidaLite'))

if ($Launch) {
    Write-Host '==> 启动' -ForegroundColor Cyan
    Start-Process (Join-Path $Target 'dida.exe')
}
