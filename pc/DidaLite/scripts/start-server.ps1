﻿﻿# 滴答：拉起本地同步服务并给出手机可用的地址
# 用法（在 PowerShell 里）：右键“使用 PowerShell 运行”，或执行
#   powershell -ExecutionPolicy Bypass -File scripts\start-server.ps1
param([int]$Port = 5080)

$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root '.env'
$exe = Join-Path $root 'src\DidaLite.App\bin\Release\net10.0-windows\DidaLite.App.exe'

Write-Host '== 滴答同步服务 ==' -ForegroundColor Cyan

# 1. MySQL
$service = Get-Service MySQL84 -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host '未找到 MySQL84 服务，请先安装并启动 MySQL。' -ForegroundColor Yellow
} elseif ($service.Status -ne 'Running') {
    try {
        Start-Service MySQL84
        Write-Host 'MySQL84 已启动'
    } catch {
        Write-Host 'MySQL84 启动失败，请用管理员身份重跑本脚本。' -ForegroundColor Yellow
    }
}

# 2. 找局域网 IP（连了默认网关的网卡优先）
$conf = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
$ip = ($conf.IPv4Address | Select-Object -First 1).IPAddress
if (-not $ip) {
    $ip = (Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -First 1).IPAddress
}
if (-not $ip) {
    Write-Host '没有找到局域网 IP，请确认电脑已连上 Wi-Fi 或网线。' -ForegroundColor Red
    exit 1
}

$baseUrl = "http://${ip}:$Port"

# 3. 把地址写进 .env，应用启动时读它（保留其它键，按 UTF-8 原样读写，避免中文注释乱码）
$text = if (Test-Path $envPath) { Get-Content -Raw -Encoding UTF8 $envPath } else { '' }
$lines = @($text -split "`r?`n" | Where-Object { $_ -notmatch '^\s*DIDA_LAN_ADDR\s*=' })
while ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = @($lines | Select-Object -First ($lines.Count - 1)) }
$lines += "DIDA_LAN_ADDR=$baseUrl"
[IO.File]::WriteAllLines($envPath, [string[]]$lines, (New-Object Text.UTF8Encoding($false)))

# 4. 防火墙放行（非管理员会失败，只提示，不中断）
$rule = "DidaLite Sync $Port"
if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
    try {
        New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -ErrorAction Stop | Out-Null
        Write-Host "防火墙已放行 TCP $Port"
    } catch {
        Write-Host '防火墙未放行，手机可能连不上。请用管理员 PowerShell 执行：' -ForegroundColor Yellow
        Write-Host "  netsh advfirewall firewall add rule name=`"$rule`" dir=in action=allow protocol=TCP localport=$Port" -ForegroundColor Yellow
    }
}

# 5. 拉起桌面端（同步服务跑在它进程里）
if (-not (Test-Path $exe)) {
    Write-Host "找不到 $exe，请先构建：dotnet build `"$root\DidaLite.slnx`" -c Release" -ForegroundColor Red
    exit 1
}
if (-not (Get-Process DidaLite.App -ErrorAction SilentlyContinue)) {
    Start-Process $exe
    Write-Host '桌面端已启动'
}
Start-Sleep -Seconds 3

# 6. 自检一次（本机回环不需要账号）
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/sync?since=0" -TimeoutSec 5 | Out-Null
    Write-Host '同步服务已就绪' -ForegroundColor Green
} catch {
    Write-Host "同步服务没起来：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '请检查 MySQL84 是否运行、.env 的 DIDA_DB_CONNECTION 是否正确。' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host '手机端填写：' -ForegroundColor Cyan
Write-Host "  地址          ${baseUrl}/sync"
Write-Host '  用户名/密码   在电脑端「设置 → 用户信息」里设置'
Write-Host ''
Write-Host '电脑换网络或重启后，重跑本脚本即可拿到新地址；地址也会显示在「设置」里。'
