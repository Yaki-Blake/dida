# 滴答 · 环境自检脚本
# 作用：检测 MySQL，没装就下载安装（ZIP 便携版），装了但没启动就拉起，最后给出本机和手机可用的地址
# 用法：双击同目录 setup-db.cmd，或
#   powershell -ExecutionPolicy Bypass -File setup-db.ps1
param(
    [int]$Port = 3306,
    [int]$SyncPort = 5080,
    [string]$Database,
    [string]$RootPassword,
    [string]$InstallDir = 'C:\DidaMySQL'
)

$ErrorActionPreference = 'Stop'
$MySQLVersion = '8.4.7'

function Test-Port {
    param([int]$p)
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', $p); return $true } catch { return $false } finally { if ($c) { $c.Close() } }
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-MySqlService {
    Get-Service -Name 'MySQL84', 'MySQL80', 'MySQL8', 'MySQL', 'DidaMySQL' -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Get-LanIp {
    $conf = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        Select-Object -First 1
    $ip = ($conf.IPv4Address | Select-Object -First 1).IPAddress
    if (-not $ip) {
        $ip = (Get-NetIPAddress -AddressFamily IPv4 |
            Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
            Select-Object -First 1).IPAddress
    }
    $ip
}

# 1. 配置：优先读软件目录（或其上级）的 .env
$here = Split-Path -Parent $PSCommandPath
$envPath = $null
$dir = $here
for ($i = 0; $i -lt 4 -and $dir; $i++) {
    if (Test-Path (Join-Path $dir '.env')) { $envPath = Join-Path $dir '.env'; break }
    $dir = Split-Path -Parent $dir
}
if ($envPath) {
    $line = (Select-String -Path $envPath -Pattern '^\s*DIDA_DB_CONNECTION\s*=' | Select-Object -First 1).Line
    if ($line) {
        $conn = $line -replace '^\s*DIDA_DB_CONNECTION\s*=', ''
        if (-not $Database -and $conn -match '(?i)(^|;)\s*database\s*=\s*([^;]+)') { $Database = $matches[2].Trim() }
        if (-not $RootPassword -and $conn -match '(?i)(^|;)\s*password\s*=\s*([^;]+)') { $RootPassword = $matches[2].Trim() }
    }
}
if (-not $Database) { $Database = 'ticktick' }
if (-not $RootPassword) { $RootPassword = 'change-me' }   # 默认占位值，实际以 .env 或 -RootPassword 为准

Write-Host '== 滴答 · 环境自检 ==' -ForegroundColor Cyan
if ($envPath) { Write-Host "配置文件：$envPath" }

# 2. MySQL：已监听就跳过
if (Test-Port $Port) {
    Write-Host "MySQL 已在 127.0.0.1:$Port 运行" -ForegroundColor Green
} else {
    if (-not (Test-Admin)) {
        Write-Host '启动或安装 MySQL 需要管理员权限，正在提权重跑…' -ForegroundColor Yellow
        $psArgs = "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" " +
                  "-Port $Port -SyncPort $SyncPort -Database `"$Database`" " +
                  "-RootPassword `"$RootPassword`" -InstallDir `"$InstallDir`""
        Start-Process powershell -Verb RunAs -ArgumentList $psArgs | Out-Null
        exit 0
    }

    $svc = Get-MySqlService
    if ($svc) {
        Write-Host "找到服务 $($svc.Name)（当前 $($svc.Status)），正在启动…" -ForegroundColor Yellow
        try {
            if ($svc.Status -ne 'Running') { Start-Service $svc.Name }
            for ($i = 0; $i -lt 30 -and -not (Test-Port $Port); $i++) { Start-Sleep -Seconds 1 }
        } catch {
            Write-Host "服务启动失败：$($_.Exception.Message)" -ForegroundColor Red
        }
        if (-not (Test-Port $Port)) {
            Write-Host 'MySQL 服务没能起来，请到「服务」里手动查看错误日志，或重装 MySQL。' -ForegroundColor Red
            exit 1
        }
        Write-Host "MySQL 已启动（服务 $($svc.Name)）" -ForegroundColor Green
    } else {
        # 3. 没装：下载 ZIP 便携版并初始化
        Write-Host '未发现 MySQL 服务，开始下载安装…' -ForegroundColor Yellow
        $zipName = "mysql-$MySQLVersion-winx64.zip"
        $url = "https://dev.mysql.com/get/Downloads/MySQL-$MySQLVersion/$zipName"
        $zip = Join-Path $env:TEMP $zipName
        if (-not (Test-Path $zip)) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Write-Host "下载 $url（约 230 MB，请稍等）"
            try {
                Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
            } catch {
                Write-Host "下载失败：$($_.Exception.Message)" -ForegroundColor Red
                Write-Host "请手动下载后重跑本脚本：$url" -ForegroundColor Yellow
                Read-Host '按回车退出'
                exit 1
            }
        }

        Write-Host "解压到 $InstallDir"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $tmp = Join-Path $env:TEMP 'dida-mysql-extract'
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
        $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        Copy-Item (Join-Path $inner.FullName '*') $InstallDir -Recurse -Force
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

        if (Test-Path (Join-Path $InstallDir 'data')) {
            Write-Host "$InstallDir\data 已存在，为避免覆盖数据已停止。请换一个 -InstallDir 或先备份/清空该目录。" -ForegroundColor Red
            exit 1
        }

        $ini = @"
[mysqld]
basedir=$InstallDir
datadir=$InstallDir\data
port=$Port
character-set-server=utf8mb4
collation-server=utf8mb4_0900_ai_ci
default-time-zone='+08:00'
[client]
port=$Port
default-character-set=utf8mb4
"@
        [IO.File]::WriteAllText((Join-Path $InstallDir 'my.ini'), $ini, (New-Object Text.UTF8Encoding($false)))

        $mysqld = Join-Path $InstallDir 'bin\mysqld.exe'
        $mysql = Join-Path $InstallDir 'bin\mysql.exe'
        $iniPath = Join-Path $InstallDir 'my.ini'

        Write-Host '初始化数据目录…'
        & $mysqld "--defaults-file=$iniPath" --initialize-insecure --console
        if ($LASTEXITCODE -ne 0) {
            Write-Host '初始化失败，请检查磁盘空间与目录权限。' -ForegroundColor Red
            exit 1
        }

        Write-Host '注册并启动服务 DidaMySQL…'
        & $mysqld "--defaults-file=$iniPath" --install 'DidaMySQL'
        Start-Service DidaMySQL
        for ($i = 0; $i -lt 30 -and -not (Test-Port $Port); $i++) { Start-Sleep -Seconds 1 }
        if (-not (Test-Port $Port)) {
            Write-Host '服务已注册但端口没有起来，请查看 Windows 服务 DidaMySQL。' -ForegroundColor Red
            exit 1
        }

        Write-Host '设置 root 密码并创建数据库…'
        $sql = "ALTER USER 'root'@'localhost' IDENTIFIED BY '$RootPassword'; " +
               "CREATE DATABASE IF NOT EXISTS $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
        & $mysql "--protocol=tcp" "-P" "$Port" "-u" "root" "--skip-password" "-e" $sql
        if ($LASTEXITCODE -ne 0) {
            Write-Host '设置密码或建库失败，可手动执行：' -ForegroundColor Yellow
            Write-Host "  $mysql -P $Port -u root --skip-password -e `"$sql`""
        }
        Write-Host 'MySQL 安装完成' -ForegroundColor Green
    }
}

# 4. 防火墙放行同步端口（需要管理员；非管理员查不到规则，静默跳过）
$rule = "DidaLite Sync $SyncPort"
if (Test-Admin) {
    if (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue) {
        Write-Host "防火墙已放行 TCP $SyncPort"
    } else {
        try {
            New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $SyncPort -Action Allow -ErrorAction Stop | Out-Null
            Write-Host "防火墙已放行 TCP $SyncPort"
        } catch {
            Write-Host "防火墙放行失败，手机可能连不上。用管理员 PowerShell 执行：" -ForegroundColor Yellow
            Write-Host "  netsh advfirewall firewall add rule name=`"$rule`" dir=in action=allow protocol=TCP localport=$SyncPort"
        }
    }
}

# 5. 桌面端没跑就拉起来（同步服务在它进程里）
$appExe = Join-Path $here 'dida.exe'
if (-not (Test-Path $appExe)) { $appExe = Join-Path $here 'DidaLite.App.exe' }
if (-not (Test-Port $SyncPort)) {
    if (Test-Path $appExe) {
        Start-Process $appExe
        for ($i = 0; $i -lt 15 -and -not (Test-Port $SyncPort); $i++) { Start-Sleep -Seconds 1 }
        Write-Host '桌面端已启动'
    } else {
        Write-Host "同目录没有 dida.exe，请先启动滴答桌面端，同步服务随它启动。" -ForegroundColor Yellow
    }
}

# 6. 输出地址
$ip = Get-LanIp
Write-Host ''
Write-Host '== 可用的本地地址 ==' -ForegroundColor Cyan
Write-Host "  MySQL        127.0.0.1:$Port   库 $Database / 用户 root"
Write-Host "  本机同步     http://127.0.0.1:$SyncPort/sync"
if ($ip) {
    Write-Host "  手机端填     http://${ip}:$SyncPort" -ForegroundColor Green
    Write-Host '               （同一 Wi-Fi 下手机直接填这个地址）'
} else {
    Write-Host '没有找到局域网 IP，请确认电脑已连上 Wi-Fi 或网线。' -ForegroundColor Yellow
}
Write-Host '  账号密码     在电脑端「设置 → 用户信息」里设置'
Write-Host ''
Write-Host '换网络或重启电脑后，重跑本脚本即可拿到新地址。'
