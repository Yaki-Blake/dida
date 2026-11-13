# 滴答 · 一键上线（桌面端 + 樱花 frp）
# 作用：拉起桌面端（同步服务在它进程里）→ 确保樱花 frp 启动器在跑 → 确认公网地址能通
# 用法：和 dida.exe 放在同一目录，双击同目录的 start-frp.cmd
#   公网地址两种给法，任选其一：
#     1. 同目录（或上级）的 .env 里加一行：DIDA_PUBLIC_URL=https://你的隧道地址
#     2. 命令行传参：start-frp.cmd -PublicUrl https://你的隧道地址
param(
    [string]$PublicUrl,
    [int]$SyncPort = 5080,
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$here = Split-Path -Parent $PSCommandPath
$launcher = 'C:\Program Files\SakuraFrpLauncher\SakuraLauncher.exe'
$logDir = 'C:\ProgramData\SakuraFrpService\Logs'

function Test-Port {
    param([int]$p)
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', $p); return $true } catch { return $false } finally { if ($c) { $c.Close() } }
}

# 从脚本所在目录、exe 子目录、以及往上的各级目录找 .env，读指定键
function Get-EnvValue {
    param([string]$key)
    $candidates = @((Join-Path $here '.env'), (Join-Path $here 'exe\.env'))
    $dir = $here
    for ($i = 0; $i -lt 4 -and $dir; $i++) {
        $candidates += (Join-Path $dir '.env')
        $dir = Split-Path -Parent $dir
    }
    foreach ($path in $candidates) {
        if (-not (Test-Path $path)) { continue }
        $line = (Select-String -Path $path -Pattern "^\s*$key\s*=" | Select-Object -First 1).Line
        if ($line) { return ($line -replace "^\s*$key\s*=", '').Trim() }
    }
    return $null
}

# 公网能不能打到同步服务：/login 回了正常状态码（401/400/200…）就算通。
# 501 是樱花节点对明文 HTTP 的拦截，说明请求没到本机，按不通处理。
function Test-Public {
    try {
        Invoke-WebRequest -Uri "$PublicUrl/login" -Method Post -ContentType 'application/json' `
            -Body '{"name":"","password":""}' -UseBasicParsing -TimeoutSec 6 | Out-Null
        return $true
    } catch {
        $resp = $_.Exception.Response
        if ($resp) { return [int]$resp.StatusCode -ne 501 }
        return $false
    }
}

Write-Host '== 滴答 · 一键上线 ==' -ForegroundColor Cyan

if (-not $PublicUrl) { $PublicUrl = Get-EnvValue 'DIDA_PUBLIC_URL' }
if (-not $PublicUrl) {
    Write-Host '没有公网地址。在 .env 里加一行 DIDA_PUBLIC_URL=https://你的隧道地址，' -ForegroundColor Red
    Write-Host '或者运行 start-frp.cmd -PublicUrl https://你的隧道地址' -ForegroundColor Red
    exit 1
}
$PublicUrl = $PublicUrl.TrimEnd('/')

# 1. 桌面端：同步服务跟着它启动
$appExe = Join-Path $here 'dida.exe'
if (-not (Test-Path $appExe)) { $appExe = Join-Path $here 'exe\dida.exe' }

if (Test-Port $SyncPort) {
    Write-Host "同步服务已在 127.0.0.1:$SyncPort 运行" -ForegroundColor Green
} elseif (-not (Test-Path $appExe)) {
    Write-Host '没找到 dida.exe，请把脚本放到安装目录（和 dida.exe 同一层）再运行。' -ForegroundColor Red
    exit 1
} else {
    Write-Host '正在启动桌面端…'
    Start-Process $appExe
    for ($i = 0; $i -lt 30 -and -not (Test-Port $SyncPort); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Port $SyncPort)) {
        Write-Host '桌面端没起来，同步服务不可用。手动双击 dida.exe 看看有没有报错。' -ForegroundColor Red
        exit 1
    }
    Write-Host "同步服务已就绪（127.0.0.1:$SyncPort）" -ForegroundColor Green
}

# 2. 樱花 frp：启动器会自动打开已经设成自动启动的隧道
if (Test-Public) {
    Write-Host '樱花隧道已在线' -ForegroundColor Green
} elseif (-not (Test-Path $launcher)) {
    Write-Host "没找到樱花启动器：$launcher" -ForegroundColor Red
    exit 1
} else {
    if (Get-Process SakuraLauncher -ErrorAction SilentlyContinue) {
        Write-Host '樱花启动器已开着，等它把隧道连上…' -ForegroundColor Yellow
    } else {
        Write-Host '正在启动樱花 frp 启动器（会自动打开设为自动启动的隧道）…'
        Start-Process $launcher
    }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $ok = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Public) { $ok = $true; break }
        Start-Sleep -Seconds 3
    }

    if (-not $ok) {
        Write-Host '隧道没连上。检查两件事：' -ForegroundColor Red
        Write-Host '  1. 樱花启动器里这条隧道是不是「运行中」，以及是否勾了自动启动；'
        Write-Host "  2. 日志：$logDir"
        exit 1
    }
    Write-Host '樱花隧道已在线' -ForegroundColor Green
}

# 3. 结果
Write-Host ''
Write-Host '== 现在可以用了 ==' -ForegroundColor Cyan
Write-Host "  手机端地址   $PublicUrl"
Write-Host '  账号密码     电脑端「设置 → 用户信息」里那个'
Write-Host '  手机端做法   设置页「登出」→ 改地址 → 重新「登入」→ 回首页点「同步」'
Write-Host ''
Write-Host '注意：隧道在线时，公网任何人都能访问这个地址，请勿外传。'
