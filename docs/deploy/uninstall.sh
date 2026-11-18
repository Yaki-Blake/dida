#!/usr/bin/env bash
# 滴答同步服务 · 卸载
#   sudo bash uninstall.sh            只删程序和 systemd 单元，数据保留
#   sudo bash uninstall.sh --purge    连数据目录一起删（不可恢复）
#   sudo bash uninstall.sh --purge --purge-user   再删掉系统用户
#
# 不会碰 nginx / apache / 80 / 443 / 其他任何服务。

set -euo pipefail

APP_DIR=/opt/dida-sync
DATA_DIR=/var/lib/dida-sync
ENV_FILE=/etc/dida-sync.env
UNIT_FILE=/etc/systemd/system/dida-sync.service
SVC_USER=dida-sync
SVC_NAME=dida-sync

info() { printf '\033[36m==>\033[0m %s\n' "$1"; }
warn() { printf '\033[33m!!\033[0m %s\n' "$1"; }
[ "$(id -u)" -eq 0 ] || { echo "需要 root 权限，请用：sudo bash uninstall.sh"; exit 1; }

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "dida-sync"; then
  info "本机防火墙里有 dida-sync 规则，如需删除：sudo ufw delete allow 5081/tcp"
fi

if [ -f "$UNIT_FILE" ]; then
  info "停止并禁用 $SVC_NAME"
  systemctl stop "$SVC_NAME" 2>/dev/null || true
  systemctl disable "$SVC_NAME" 2>/dev/null || true
fi

# 兜底：手工启动过、或 systemd 没把进程收走时，按可执行文件路径精确清理。
# 只匹配 /opt/dida-sync/dida-sync 这一个路径，不会碰到服务器上其他任何进程。
if pgrep -f "^$APP_DIR/dida-sync" >/dev/null 2>&1; then
  info "仍有 dida-sync 进程在跑，按路径清理"
  pkill -f "^$APP_DIR/dida-sync" 2>/dev/null || true
  sleep 1
fi

info "删除 systemd 单元"
rm -f "$UNIT_FILE"
systemctl daemon-reload

info "删除程序目录 $APP_DIR（含上一版备份）"
rm -rf "$APP_DIR" "${APP_DIR}.prev"

if [ "${1:-}" = "--purge" ] || [ "${2:-}" = "--purge" ]; then
  info "删除数据目录 $DATA_DIR"
  rm -rf "$DATA_DIR"
  info "删除配置 $ENV_FILE"
  rm -f "$ENV_FILE"
else
  info "数据保留在 $DATA_DIR（要一起删：加 --purge）"
fi

if [ "${1:-}" = "--purge-user" ] || [ "${2:-}" = "--purge-user" ] || [ "${3:-}" = "--purge-user" ]; then
  if id -u "$SVC_USER" >/dev/null 2>&1; then
    # 该用户只用来跑这个服务，先确保它名下的进程都退出，再删用户
    pkill -u "$SVC_USER" 2>/dev/null || true
    sleep 1
    if userdel "$SVC_USER" 2>/dev/null; then
      info "已删除系统用户 $SVC_USER"
    else
      warn "系统用户 $SVC_USER 没删掉（可能还有进程占用），稍后可手动执行：userdel $SVC_USER"
    fi
  fi
  getent group "$SVC_USER" >/dev/null 2>&1 && groupdel "$SVC_USER" 2>/dev/null || true
fi

echo
if pgrep -f "^$APP_DIR/dida-sync" >/dev/null 2>&1; then
  warn "还有 dida-sync 进程在运行，请检查：pgrep -af dida-sync"
else
  info "已卸载干净：进程、单元、目录都已清理。nginx / apache 与 80/443 全程未动。"
fi
