#!/usr/bin/env bash
# 滴答同步服务 · 只读预检
# 这个脚本不写任何文件、不改任何配置、不启停任何服务，只把部署前需要知道的信息打出来。
# 用法：bash preflight.sh

set -u

line() { printf '\n\033[36m── %s ──\033[0m\n' "$1"; }

line "系统与运行时"
[ -f /etc/os-release ] && . /etc/os-release && echo "发行版 : ${PRETTY_NAME:-未知}"
echo "内核   : $(uname -srm)"
ARCH=$(uname -m)
echo "架构   : $ARCH"

RUNTIME_OK=yes
RUNTIME_NOTE=""

LDD_OUT=$(ldd --version 2>/dev/null | head -1)
GLIBC=""
case "$LDD_OUT" in
  # 只认真正的 glibc 自述，Git Bash / musl 等别的 ldd 也报版本号，不能照单全收
  *GLIBC*|*"GNU libc"*|*glibc*) GLIBC=$(printf '%s\n' "$LDD_OUT" | grep -oE '[0-9]+\.[0-9]+$' | tail -1) ;;
esac
if [ -z "$GLIBC" ]; then
  echo "glibc  : 未检测到${LDD_OUT:+（ldd 报告：$LDD_OUT）}"
  RUNTIME_OK=no
  RUNTIME_NOTE="不是 glibc 系统（Alpine 之类用 musl），本包跑不了，需换镜像或上 Docker"
else
  echo "glibc  : $GLIBC"
  # 自包含包要 glibc ≥ 2.28（Ubuntu 20.04+ / Debian 10+ / CentOS 8+ / Alibaba Cloud Linux 3）
  if [ "$(printf '%s\n2.28\n' "$GLIBC" | sort -V | head -1)" = "2.28" ]; then
    echo "         ≥ 2.28，满足要求"
  else
    echo "         < 2.28，跑不起来"
    RUNTIME_OK=no
    RUNTIME_NOTE="glibc 太旧（CentOS 7 / Alibaba Cloud Linux 2 就是这个版本），必须换镜像或上 Docker"
  fi
fi

case "$ARCH" in
  x86_64|amd64) : ;;
  *) RUNTIME_OK=no; RUNTIME_NOTE="架构是 $ARCH，这个包只编了 x86_64" ;;
esac

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
  echo "systemd: 没有（容器环境？）—— 本包靠 systemd 托管，容器里要自己写启动命令"
  RUNTIME_OK=no
  [ -n "$RUNTIME_NOTE" ] || RUNTIME_NOTE="没有 systemd，无法用本包的单元文件托管"
fi

line "资源"
free -m 2>/dev/null | awk 'NR==1{print "         "$0} NR==2{print "内存    "$0}'
df -h /opt /var 2>/dev/null | awk 'NR>1{print "磁盘    "$5" 已用  "$4" 可用  "$6}'
echo "CPU    : $(nproc) 核"

line "端口占用（只查 5080/5081/8443）"
if command -v ss >/dev/null 2>&1; then
  out=$(ss -ltn 2>/dev/null | grep -E ':(5080|5081|8443) ')
  [ -n "$out" ] && echo "$out" || echo "5080 / 5081 / 8443 都空闲"
else
  echo "没有 ss 命令，跳过"
fi

line "现有 Web 服务（只读，不会改动）"
if command -v ss >/dev/null 2>&1; then
  ss -ltn 2>/dev/null | grep -E ':(80|443) ' || echo "80 / 443 当前没有监听"
fi
for s in nginx apache2 httpd caddy; do
  if systemctl list-unit-files 2>/dev/null | grep -q "^${s}.service"; then
    echo "$s    : $(systemctl is-active "$s" 2>/dev/null)"
  fi
done
[ -d /etc/nginx/sites-enabled ] && { echo "nginx 站点:"; ls /etc/nginx/sites-enabled | sed 's/^/         /'; }
[ -d /etc/nginx/conf.d ] && { echo "nginx conf.d:"; ls /etc/nginx/conf.d | sed 's/^/         /'; }

line "是否已装过"
[ -d /opt/dida-sync ] && echo "程序目录：已存在" || echo "程序目录：不存在（全新安装）"
[ -d /var/lib/dida-sync ] && echo "数据目录：已存在" || echo "数据目录：不存在"
systemctl is-enabled dida-sync 2>/dev/null | sed 's/^/开机自启：/'
systemctl is-active dida-sync 2>/dev/null | sed 's/^/运行状态：/'

line "防火墙"
if command -v ufw >/dev/null 2>&1; then
  ufw status 2>/dev/null | head -8
elif command -v firewall-cmd >/dev/null 2>&1; then
  echo "firewalld: $(firewall-cmd --state 2>/dev/null)"
  firewall-cmd --list-ports 2>/dev/null | sed 's/^/已放行端口：/'
else
  echo "未见 ufw / firewalld（可能直接用云厂商安全组）"
fi

line "权限与会话"
echo "当前用户：$(id -un) (uid=$(id -u))"
if [ "$(id -u)" -eq 0 ]; then
  echo "当前是 root"
elif sudo -n true 2>/dev/null; then
  echo "sudo：免密可用"
else
  echo "sudo：需要输入密码"
fi

line "能不能装"
if [ "$RUNTIME_OK" = "yes" ]; then
  echo "✅ 这台机器可以直接装：bash install.sh"
  echo "   装完记得去云控制台安全组放行 TCP 5081（阿里云/腾讯云都是安全组说了算，本机防火墙另说）"
else
  echo "❌ 这台机器装不了：$RUNTIME_NOTE"
  echo "   两条路：① 重装系统为 Alibaba Cloud Linux 3 / Ubuntu 22.04 或更新；"
  echo "           ② 保留当前系统，改用 Docker 跑（我可以另出一份 Dockerfile）"
fi

line "结论"
echo "把上面这段输出发回来，我按实际情况给你确定的端口和暴露方式。"
