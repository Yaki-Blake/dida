# 滴答 · 一键上线（樱花 frp + 桌面端）
# 流程：检测樱花 frp → 检查隧道 / 自动 HTTPS → 等隧道在线 → 取公网地址 → 写进 dida 的 .env → 起桌面端
# 用法：和 dida.exe 放在同一目录，双击 start-frp.cmd
#   手动指定地址：start-frp.cmd -PublicUrl https://你的隧道地址
param(
    [string]$PublicUrl,
    [int]$SyncPort = 5080,
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$here = Split-Path -Parent $PSCommandPath
$sakuraConfig = 'C:\ProgramData\SakuraFrpService\config.json'
$logDir = 'C:\ProgramData\SakuraFrpService\Logs'

$homePage = 'https://www.natfrp.com/'
$downloadPage = 'https://www.natfrp.com/tunnel/download'
$tunnelPage = 'https://www.natfrp.com/tunnel/'
$docPage = 'https://doc.natfrp.com/'

function Test-Port {
    param([int]$p)
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', $p); return $true } catch { return $false } finally { if ($c) { $c.Close() } }
}

# 公网能不能打到同步服务：/login 回了正常状态码（401/400/200…）就算通。
# 501 是樱花节点对明文 HTTP 的拦截，说明请求没到本机，按不通处理。
function Test-Public {
    param([string]$Url)
    try {
        Invoke-WebRequest -Uri "$Url/login" -Method Post -ContentType 'application/json' `
            -Body '{"name":"","password":""}' -UseBasicParsing -TimeoutSec 6 | Out-Null
        return $true
    } catch {
        $resp = $_.Exception.Response
        if ($resp) { return [int]$resp.StatusCode -ne 501 }
        return $false
    }
}

function Get-EnvValue {
    param([string]$path, [string]$key)
    if (-not (Test-Path $path)) { return $null }
    $line = (Select-String -Path $path -Pattern "^\s*$key\s*=" | Select-Object -First 1).Line
    if ($line) { return ($line -replace "^\s*$key\s*=", '').Trim() }
    return $null
}

function Set-EnvValue {
    param([string]$path, [string]$key, [string]$value)
    if (-not (Test-Path $path)) {
        [IO.File]::WriteAllText($path, "$key=$value`r`n", (New-Object Text.UTF8Encoding($false)))
        return
    }
    $lines = [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$key\s*=") { $lines[$i] = "$key=$value"; $found = $true }
    }
    if (-not $found) { $lines += "$key=$value" }
    [IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))
}

# 启动器可能装在默认目录、自定义目录，或只剩系统服务；逐个找
function Find-Launcher {
    foreach ($p in @(
        'C:\Program Files\SakuraFrpLauncher\SakuraLauncher.exe',
        'C:\Program Files (x86)\SakuraFrpLauncher\SakuraLauncher.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\SakuraFrpLauncher\SakuraLauncher.exe'),
        (Join-Path $env:LOCALAPPDATA 'SakuraFrpLauncher\SakuraLauncher.exe')
    )) {
        if (Test-Path $p) { return $p }
    }

    foreach ($root in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )) {
        foreach ($item in @(Get-ItemProperty $root -ErrorAction SilentlyContinue)) {
            if ($item.DisplayName -match 'SakuraFrp' -and $item.InstallLocation) {
                $p = Join-Path $item.InstallLocation 'SakuraLauncher.exe'
                if (Test-Path $p) { return $p }
            }
        }
    }

    $svc = Get-CimInstance Win32_Service -Filter "Name='SakuraFrpService'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.PathName) {
        $exe = $svc.PathName -replace '^"([^"]+)".*$', '$1'
        $p = Join-Path (Split-Path $exe -Parent) 'SakuraLauncher.exe'
        if (Test-Path $p) { return $p }
    }

    return $null
}

function Get-SakuraTunnels {
    param([string]$token)
    try {
        return @(Invoke-RestMethod -Uri "https://api.natfrp.com/v4/tunnels?token=$token" -TimeoutSec 8)
    } catch {
        return $null
    }
}

# 樱花把「使用 >>域名:端口<< 连接你的隧道」打在日志里，从最新的日志往回找。
# 日志被服务进程占着，必须用 ReadWrite 共享方式打开，否则会报「文件被另一个进程占用」。
function Find-TunnelAddress {
    param([string]$port)
    $logs = @(Get-ChildItem $logDir -Filter 'SakuraFrpService.*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 3)
    foreach ($log in $logs) {
        try {
            $fs = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
            $text = $sr.ReadToEnd()
            $sr.Close()
            $fs.Close()
        } catch { continue }

        $hits = [regex]::Matches($text, '使用 >>(.+?)<< 连接你的隧道')
        for ($i = $hits.Count - 1; $i -ge 0; $i--) {
            if ($hits[$i].Groups[1].Value.EndsWith(":$port")) { return $hits[$i].Groups[1].Value }
        }
    }
    return $null
}

function Find-AppExe {
    foreach ($p in @((Join-Path $here 'dida.exe'), (Join-Path $here 'exe\dida.exe'))) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Show-CreateTunnelHelp {
    Write-Host ''
    Write-Host '  在打开的页面里照这样建一条隧道：' -ForegroundColor Yellow
    Write-Host '    · 隧道类型    TCP'
    Write-Host '    · 本地地址    127.0.0.1'
    Write-Host "    · 本地端口    $SyncPort"
    Write-Host '    · 节点        任意一个延迟低的'
    Write-Host '    · 高级设置    打开「自动 HTTPS」（手机 App 才能用 https 访问）'
    Write-Host ''
    Write-Host "  建好后在樱花启动器里点「启动」，再重新运行本脚本。"
    Write-Host "  图文教程：$docPage"
}

Write-Host '== 滴答 · 一键上线 ==' -ForegroundColor Cyan

$appExe = Find-AppExe
if (-not $appExe) {
    Write-Host '没找到 dida.exe，请把脚本放到安装目录（和 dida.exe 同一层）再运行。' -ForegroundColor Red
    exit 1
}
$envFile = Join-Path (Split-Path -Parent $appExe) '.env'

# ---------------- 确定公网地址 ----------------
if ($PublicUrl) {
    Write-Host "使用指定地址：$PublicUrl"
} else {
    # 1. 樱花 frp 装没装
    Write-Host '[1/4] 检测樱花 frp…'
    $launcher = Find-Launcher
    if (-not $launcher) {
        Write-Host '  没检测到樱花 frp 启动器。' -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  官方不提供免登录的下载直链，这一步需要手动装一次：' -ForegroundColor Yellow
        Write-Host "    1. 打开下载页（已帮你打开浏览器）：$downloadPage"
        Write-Host '    2. 登录樱花账号；没有就先去官网注册（需实名认证）'
        Write-Host '    3. 点「下载」拿到安装包并安装；页面若给的是「复制命令」，'
        Write-Host '       就用管理员身份打开 PowerShell，粘贴后回车执行'
        Write-Host '    4. 打开启动器登录一次，再重新运行本脚本'
        Write-Host ''
        Write-Host "  官网：$homePage"
        Write-Host "  文档：$docPage"
        Start-Process $downloadPage
        exit 1
    }
    Write-Host "  已安装：$launcher"

    # 2. 有没有建好的隧道（指向本机同步端口）
    Write-Host '[2/4] 检查隧道…'
    if (-not (Test-Path $sakuraConfig)) {
        Write-Host '  樱花还没初始化，先打开启动器登录一次。' -ForegroundColor Yellow
        Start-Process $launcher
        exit 1
    }
    $token = (Get-Content $sakuraConfig -Raw | ConvertFrom-Json).token
    if (-not $token) {
        Write-Host '  樱花启动器还没登录，打开它登录一次再重新运行本脚本。' -ForegroundColor Yellow
        Start-Process $launcher
        exit 1
    }

    $tunnels = Get-SakuraTunnels $token
    if ($null -eq $tunnels) {
        Write-Host '  查不到隧道列表（登录凭证可能失效，或网络不通）。' -ForegroundColor Yellow
        Write-Host '  打开樱花启动器重新登录一次，再运行本脚本。' -ForegroundColor Yellow
        Start-Process $launcher
        exit 1
    }
    if ($tunnels.Count -eq 0) {
        Write-Host '  账号下还没有隧道。' -ForegroundColor Yellow
        Show-CreateTunnelHelp
        Start-Process $tunnelPage
        exit 1
    }

    $tunnel = $tunnels | Where-Object { $_.local_port -eq $SyncPort } | Select-Object -First 1
    if (-not $tunnel) {
        Write-Host "  有 $($tunnels.Count) 条隧道，但没有一条指向本机端口 $SyncPort 的。" -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  任选一种处理：' -ForegroundColor Yellow
        Write-Host "    · 把现有隧道的「本地端口」改成 $SyncPort"
        Write-Host '    · 或者按下面的参数新建一条'
        Show-CreateTunnelHelp
        Start-Process $tunnelPage
        exit 1
    }
    Write-Host "  找到隧道：$($tunnel.name)（本地 $SyncPort → 公网端口 $($tunnel.remote)）"

    # 自动 HTTPS：隧道没开的话，手机 App 访问不了
    if ($tunnel.extra -notmatch 'auto_https') {
        Write-Host '  这条隧道没开「自动 HTTPS」，手机端会连不上。' -ForegroundColor Yellow
        Write-Host '  去隧道编辑页 → 高级设置 → 打开「自动 HTTPS」→ 保存后重启隧道。' -ForegroundColor Yellow
        Start-Process $tunnelPage
        exit 1
    }

    # 3. 隧道是不是开着的
    Write-Host '[3/4] 检查隧道是否在线…'
    if (-not $tunnel.online) {
        Write-Host '  不在线，启动樱花启动器…'
        if (-not (Get-Process SakuraLauncher -ErrorAction SilentlyContinue)) { Start-Process $launcher }
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $now = Get-SakuraTunnels $token | Where-Object { $_.id -eq $tunnel.id } | Select-Object -First 1
            if ($now -and $now.online) { $tunnel = $now; break }
        }
        if (-not $tunnel.online) {
            Write-Host '  等了很久还是不在线。' -ForegroundColor Red
            Write-Host '  打开樱花启动器，确认这条隧道在「运行中」；没开就点「启动」再重跑本脚本。' -ForegroundColor Yellow
            Write-Host "  日志：$logDir"
            Start-Process $launcher
            exit 1
        }
    }
    Write-Host '  在线'

    # 4. 拿到公网地址并确认能打到本机
    Write-Host '[4/4] 读取公网地址…'
    $candidates = @()
    $fromLog = Find-TunnelAddress $tunnel.remote
    if ($fromLog) { $candidates += $fromLog }
    $lastAddr = Get-EnvValue $envFile 'DIDA_LAN_ADDR'
    if ($lastAddr) { $candidates += ($lastAddr -replace '^https?://', '').TrimEnd('/') }

    $address = $null
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        foreach ($scheme in @('https', 'http')) {
            if (Test-Public "$scheme`://$c") { $PublicUrl = "$scheme`://$c"; $address = $c; break }
        }
        if ($PublicUrl) { break }
    }

    if (-not $PublicUrl) {
        if ($candidates.Count -eq 0) {
            Write-Host '  没能从樱花日志里读到隧道地址。' -ForegroundColor Red
        } else {
            Write-Host "  隧道在线，但地址 $($candidates -join ' / ') 打不通本机同步服务。" -ForegroundColor Red
        }
        Write-Host '  请确认：' -ForegroundColor Yellow
        Write-Host "    · 隧道「本地端口」是 $SyncPort"
        Write-Host '    · 隧道已开「自动 HTTPS」'
        Write-Host '    · 桌面端已启动（下面会尝试拉起）'
        Write-Host '  也可以手动看启动器首页的隧道地址，然后：start-frp.cmd -PublicUrl https://域名:端口'
        Start-Process $launcher
        exit 1
    }
    Write-Host "  可用：$PublicUrl"
}

# ---------------- 写进 dida 的配置 ----------------
$oldAddr = Get-EnvValue $envFile 'DIDA_LAN_ADDR'
$PublicUrl = $PublicUrl.TrimEnd('/')
if ($oldAddr -ne $PublicUrl) {
    Set-EnvValue $envFile 'DIDA_LAN_ADDR' $PublicUrl
    Write-Host "已把地址写入 $envFile" -ForegroundColor Green
} else {
    Write-Host '桌面端配置里已经是这个地址'
}

# ---------------- 拉起桌面端 ----------------
$running = Get-Process dida -ErrorAction SilentlyContinue
if ($running) {
    if ($oldAddr -ne $PublicUrl) {
        Write-Host '地址变了，重启桌面端让它生效…'
        $running | Stop-Process -Force
        for ($i = 0; $i -lt 15 -and (Test-Port $SyncPort); $i++) { Start-Sleep -Seconds 1 }
        Start-Process $appExe
        for ($i = 0; $i -lt 30 -and -not (Test-Port $SyncPort); $i++) { Start-Sleep -Seconds 1 }
    }
} elseif (Test-Port $SyncPort) {
    Write-Host "同步服务已在 127.0.0.1:$SyncPort 运行"
} else {
    Write-Host '启动桌面端…'
    Start-Process $appExe
    for ($i = 0; $i -lt 30 -and -not (Test-Port $SyncPort); $i++) { Start-Sleep -Seconds 1 }
}

if (-not (Test-Port $SyncPort)) {
    Write-Host '桌面端没起来，同步服务不可用。手动双击 dida.exe 看有没有报错。' -ForegroundColor Red
    exit 1
}

# ---------------- 结果 ----------------
Write-Host ''
Write-Host '== 现在可以用了 ==' -ForegroundColor Cyan
Write-Host "  手机端地址   $PublicUrl"
Write-Host '  账号密码     电脑端「设置 → 用户信息」里那个'
Write-Host '  手机端做法   设置页「登出」→ 改地址 → 重新「登入」→ 回首页点「同步」'
Write-Host ''
Write-Host '注意：隧道在线时，公网任何人都能访问这个地址，请勿外传。'
