#!/usr/bin/env bash
# 滴答同步服务 · 安装 / 升级
#
# 只做这几件事，绝不碰服务器上已有的任何服务：
#   1. 建一个系统用户 dida-sync（不给登录 shell）
#   2. 把程序放到 /opt/dida-sync
#   3. 建数据目录 /var/lib/dida-sync
#   4. 写一个独立的 systemd 单元 dida-sync.service 并启动
#   5. 写配置文件 /etc/dida-sync.env（已存在则保留，升级不会覆盖）
#
# 不会做：不改 nginx / apache 配置、不占 80 和 443、不重启其他任何服务、不动 iptables 规则。

set -euo pipefail

PORT="${PORT:-5081}"
APP_DIR=/opt/dida-sync
DATA_DIR=/var/lib/dida-sync
ENV_FILE=/etc/dida-sync.env
UNIT_FILE=/etc/systemd/system/dida-sync.service
SVC_USER=dida-sync
SVC_NAME=dida-sync

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

info()  { printf '\033[36m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[33m!!\033[0m %s\n' "$1"; }
fail()  { printf '\033[31mxx\033[0m %s\n' "$1"; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "需要 root 权限，请用：sudo bash install.sh"
# Windows 上打的包会丢失可执行位（NTFS 无此位），解压后先补回来，再做存在性检查
chmod +x "$HERE/app/dida-sync" "$HERE/app/createdump" 2>/dev/null || true
[ -x "$HERE/app/dida-sync" ] || fail "找不到 app/dida-sync，请确认整个目录一起上传"

# 端口被别的程序占着就停下，绝不抢端口
if command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | grep -qE "[:.]${PORT} "; then
  if systemctl is-active --quiet "$SVC_NAME" 2>/dev/null; then
    info "端口 $PORT 由现有的 $SVC_NAME 占用，按升级处理"
  else
    fail "端口 $PORT 已被其他程序占用。换端口：sudo PORT=5090 bash install.sh"
  fi
fi

# 已安装：先停服务再覆盖程序文件（数据在 /var/lib/dida-sync，不受影响）
if systemctl is-active --quiet "$SVC_NAME" 2>/dev/null; then
  info "停止旧进程"
  systemctl stop "$SVC_NAME"
fi

info "创建系统用户 $SVC_USER"
if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  useradd --system --no-create-home --home-dir "$APP_DIR" --shell /usr/sbin/nologin "$SVC_USER"
fi

info "部署程序到 $APP_DIR"
mkdir -p "$APP_DIR"
# 保留上一版，出问题可以立刻回退
if [ -x "$APP_DIR/dida-sync" ]; then
  rm -rf "${APP_DIR}.prev"
  cp -a "$APP_DIR" "${APP_DIR}.prev"
fi
find "$APP_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "$HERE/app/." "$APP_DIR/"
chown -R root:root "$APP_DIR"
chmod 755 "$APP_DIR/dida-sync"

info "准备数据目录 $DATA_DIR"
mkdir -p "$DATA_DIR"
chown "$SVC_USER:$SVC_USER" "$DATA_DIR"
chmod 750 "$DATA_DIR"

if [ -f "$ENV_FILE" ]; then
  info "保留已有配置 $ENV_FILE（升级不覆盖）"
else
  info "写入配置 $ENV_FILE"
  cat > "$ENV_FILE" <<EOF
# dida-sync 的配置。改完执行：systemctl restart dida-sync
DIDA_SYNC_PORT=$PORT
DIDA_DATA_DIR=$DATA_DIR

# 放在 nginx / caddy 后面时打开，限流才能按真实 IP 算
# DIDA_TRUST_PROXY=1

# 多机共用一个 MySQL 时才需要，默认用本机 SQLite，留空即可
# DIDA_DB_CONNECTION=server=127.0.0.1;port=3306;database=ticktick;user=dida;password=改成你的
EOF
  chmod 640 "$ENV_FILE"
  chown root:"$SVC_USER" "$ENV_FILE"
fi

info "安装 systemd 单元"
install -m 644 "$HERE/dida-sync.service" "$UNIT_FILE"
systemctl daemon-reload
systemctl enable "$SVC_NAME" >/dev/null 2>&1 || true
systemctl restart "$SVC_NAME"

info "等待服务就绪"
ok=0
for _ in $(seq 1 30); do
  if curl -fsS --max-time 2 "http://127.0.0.1:${PORT}/api/health" >/dev/null 2>&1; then ok=1; break; fi
  sleep 1
done

if [ "$ok" -eq 1 ]; then
  echo
  info "服务已启动：http://127.0.0.1:${PORT}/api/health"
  curl -s "http://127.0.0.1:${PORT}/api/health"; echo
else
  warn "健康检查没通过，看日志：journalctl -u $SVC_NAME -n 50 --no-pager"
fi

echo
info "还需要你做两件事"
echo "  1) 设置同步账号（手机登录用这一组）："
echo "       sudo -u $SVC_USER $APP_DIR/dida-sync set-user 你的用户名 你的密码"
echo "     设完不用重启，手机直接登录即可。"
echo
echo "  2) 放行端口（二选一，取决于你怎么暴露）："
echo "     · 云服务器：在控制台「安全组」里放行 TCP $PORT（这一步脚本做不了）"
echo "     · 本机防火墙：sudo ufw allow $PORT/tcp comment 'dida-sync'"
echo
echo "  手机端地址填：http://你的服务器公网IP:$PORT/sync"
echo
echo "本次改动范围："
echo "  · 新增 $APP_DIR、$DATA_DIR、$ENV_FILE、$UNIT_FILE"
echo "  · 新增系统用户 $SVC_USER"
echo "  · 未修改 nginx / apache 配置，未占用 80/443，未重启其他服务"
