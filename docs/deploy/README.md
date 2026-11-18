# 滴答同步服务 · 服务器部署包

把待办数据放到你自己的服务器上，手机在任何网络下都能连，不需要电脑一直开机。

包的定位是"和现有服务和平共处"：**独立目录、独立系统用户、独立端口、独立 systemd 单元**，
不碰 nginx / apache、不占 80 和 443、不改任何已有配置。

---

## 一、包里的东西

```
dida-sync-deploy/
├── app/                     ← 程序本体（linux-x64，自包含，服务器不用装 .NET）
│   └── dida-sync
├── preflight.sh             ← 只读预检，先跑这个
├── install.sh               ← 安装 / 升级
├── uninstall.sh             ← 卸载
├── dida-sync.service        ← systemd 单元
├── nginx-dida.conf.example  ← 可选：想上 HTTPS 时参考，不自动应用
└── README.md
```

安装后服务器上会多出这些东西，仅此而已：

| 路径 | 作用 |
|---|---|
| `/opt/dida-sync` | 程序（升级时保留上一版为 `/opt/dida-sync.prev`） |
| `/var/lib/dida-sync/dida.db` | 数据（SQLite，WAL 模式） |
| `/etc/dida-sync.env` | 配置（端口、数据目录） |
| `/etc/systemd/system/dida-sync.service` | 开机自启与守护 |
| 系统用户 `dida-sync` | 无权登录，只用来跑这个服务 |

---

## 二、部署（4 步）

### 0. 先确认系统镜像能跑（买机器/重装时就要看）

这个包是**自包含**发布，服务器不用装 .NET，但对系统有三个硬要求：

| 要求 | 说明 |
|---|---|
| x86_64 架构 | ARM 机型（如阿里云 g8y/c8y）跑不了，需要另编一份 arm64 |
| glibc ≥ 2.28 | 低于这个版本，程序启动直接报 `GLIBC_2.28 not found` |
| 有 systemd | 单元文件托管用，纯容器镜像没有 |

**能直接用的镜像**：Alibaba Cloud Linux 3、Ubuntu 20.04 / 22.04 / 24.04、Debian 11 / 12、Rocky 8+ / Alma 8+、CentOS Stream 8/9。

**不能用的镜像**（买之前避开）：

| 镜像 | 原因 |
|---|---|
| **CentOS 7** | glibc 2.17，太旧 |
| **Alibaba Cloud Linux 2** | 同 CentOS 7，glibc 2.17 |
| Alpine | 用 musl 而不是 glibc |

如果机器已经买成上面这三种，两条路：**重置系统盘换成 Alibaba Cloud Linux 3 / Ubuntu 22.04**（最省事，阿里云控制台就能操作），或者告诉我，我另出一份 Dockerfile 用容器跑。

`preflight.sh` 会自动判断这三条并直接告诉你「✅ 可以直接装」还是「❌ 装不了 + 原因」，不用自己比对。

### 1. 先跑预检（只读）

```bash
bash preflight.sh
```

它会打印发行版、glibc、内存、端口占用、现有 nginx 站点、防火墙状态。
把输出发回来，我帮你确认端口和暴露方式。

### 2. 上传

```bash
# 在你自己的电脑上执行
scp -r dida-sync-deploy 用户名@服务器IP:~/
```

### 3. 安装

```bash
ssh 用户名@服务器IP
cd ~/dida-sync-deploy
sudo bash install.sh                 # 默认端口 5081
# 想换端口：sudo PORT=5090 bash install.sh
```

### 4. 设置同步账号

```bash
sudo -u dida-sync /opt/dida-sync/dida-sync set-user 你的用户名 你的密码
```

设完不用重启。改密码重跑这条即可（旧 token 会失效，手机需要重新登录）。

---

## 三、验证

在服务器上：

```bash
curl http://127.0.0.1:5081/api/health
# {"ok":true,"backend":"sqlite","users_configured":true,"max_rev":0,...}

systemctl status dida-sync --no-pager
journalctl -u dida-sync -n 30 --no-pager
```

在自己电脑上（确认公网可达）：

```bash
curl http://服务器公网IP:5081/api/health
```

不通的话，九成是**云厂商安全组**没放行（脚本改不了它）。阿里云的控制台路径是：

> ECS → 实例 → 点进实例 → 安全组 → 配置规则 → **入方向** → 手动添加
> 端口范围 `5081/5081`，授权对象 `0.0.0.0/0`（想更严就填自己常用的公网 IP）

安全组之外还有一层系统防火墙，CentOS 系镜像常见：

```bash
sudo firewall-cmd --list-ports                    # 看有没有放行
sudo firewall-cmd --add-port=5081/tcp --permanent  # 放行（只加这一条，不动别的规则）
sudo firewall-cmd --reload
```

注意 `--reload` 会重载全部规则，但**不会**影响 80/443 上已有的网站。

**手机端**：地址填 `http://服务器公网IP:5081/sync`，用户名密码填第 4 步那组。

> 用 IP + 非标准端口访问**不需要 ICP 备案**；一旦想用域名走 80/443，就必须备案（阿里云会拦未备案域名）。

---

## 四、让电脑也连上服务器

手机连服务器只要填地址；电脑要改一行配置——因为电脑默认自己就是服务端。

打开 `%LOCALAPPDATA%\DidaLite\.env`（桌面端首次运行会自动生成），把这三行填上：

```ini
DIDA_REMOTE_URL=http://服务器公网IP:5081
DIDA_REMOTE_USER=你的用户名
DIDA_REMOTE_PASSWORD=你的密码
```

重启桌面端即可。之后：

- 电脑不再占用 5080，手机也不用再连电脑，两边统一连服务器；
- 第一次切换会把本地待办全量推上去（改动量小，几秒完成）；
- 密码在服务器上改了之后，桌面端会拿 401 自动重登一次，不用同步改 `.env`；
- 想把电脑切回单机：把 `DIDA_REMOTE_URL` 留空再重启，本地那份照样在。

**已知缺口**：切到服务器后，手机端的「课表」页读不到内容——课表接口还只在桌面端 5080 上，
没有搬进服务器版。待办、闹钟、日历不受影响。

---

## 五、确认没影响你原来的网站

部署前后各跑一次，对比输出即可自证：

```bash
# 站点还活着，状态码和之前一致
curl -s -o /dev/null -w '%{http_code}\n' https://你的域名/

# 80/443 的监听者没变（应该还是 nginx）
ss -ltnp | grep -E ':(80|443) '

# nginx 配置与进程都没被重启过
systemctl show nginx -p ActiveEnterTimestamp
ls -l /etc/nginx/sites-enabled /etc/nginx/conf.d
```

`install.sh` 只对 `dida-sync` 这一个单元执行 `systemctl`，不会 `daemon-reload` 之外的任何全局动作，
也不会 `ufw` 里加规则（它只把命令打印出来给你自己决定）。

---

## 六、日常运维

```bash
# 状态与日志
systemctl status dida-sync --no-pager
journalctl -u dida-sync -f

# 重启 / 停止
sudo systemctl restart dida-sync
sudo systemctl stop dida-sync

# 改配置
sudo nano /etc/dida-sync.env      # DIDA_SYNC_PORT / DIDA_DATA_DIR / DIDA_TRUST_PROXY
sudo systemctl restart dida-sync

# 查看账号与数据量
sudo -u dida-sync /opt/dida-sync/dida-sync info
```

### 备份（建议加进 crontab，每天一次）

```bash
# 需要 sqlite3 命令：apt install -y sqlite3
sudo -u dida-sync sqlite3 /var/lib/dida-sync/dida.db ".backup /var/lib/dida-sync/backup-$(date +%F).db"
```

恢复：停服务 → 用备份覆盖 `dida.db`（同时删掉 `dida.db-wal`、`dida.db-shm`）→ 启服务。

### 升级

把新的 `app/` 覆盖上传后重跑 `sudo bash install.sh`。配置不覆盖，数据不动，
旧版本留在 `/opt/dida-sync.prev`。

### 回滚

```bash
sudo systemctl stop dida-sync
sudo rm -rf /opt/dida-sync
sudo mv /opt/dida-sync.prev /opt/dida-sync
sudo systemctl start dida-sync
```

### 卸载

```bash
sudo bash uninstall.sh              # 删程序，数据保留
sudo bash uninstall.sh --purge      # 连数据一起删
```

---

## 七、要不要上 HTTPS

| 做法 | 代价 | 适用 |
|---|---|---|
| `http://IP:5081` 直连 | token 明文传输，公网可被嗅探 | 自己短期内测，别长期用 |
| nginx 反代 + 域名证书 | 域名要备案（大陆服务器），要改一块 nginx 配置 | 长期用，推荐 |
| 只在自己的内网/VPN 里用 | 无需证书 | 只在校园网/家里用 |

要上 nginx 的话，`nginx-dida.conf.example` 里有现成的一块，复制修改变量后先 `nginx -t`
再 `systemctl reload nginx`（reload 不会断开现有连接）。用上反代后记得在
`/etc/dida-sync.env` 里打开 `DIDA_TRUST_PROXY=1`。

---

## 八、常见问题

**Q：手机连不上，服务器本机 curl 却正常？**
云厂商安全组没放行端口。去控制台加入站规则 TCP 5081。

**Q：启动报 `GLIBC_2.28 not found`？**
系统太旧（CentOS 7 / Alibaba Cloud Linux 2 都是 glibc 2.17）。换镜像或用 Docker，改配置是解决不了的。

**Q：启动报 `Exec format error`？**
买成 ARM 机型了。这个包只编了 x86_64，告诉我我另出一份 arm64。

**Q：2G 内存的机器会不会拖垮网站？**
这个服务实测空载约 25MB（常驻内存随请求数略涨），单元里写了 `MemoryMax=256M`，超了只会重启它自己。

**Q：能换成 MySQL 吗？**
能，但没必要。`/etc/dida-sync.env` 里填 `DIDA_DB_CONNECTION` 即可，
那台机器上已有 MySQL 的话注意别把默认库名写成现有的库。

**Q：端口选哪个？**
默认 5081，避开桌面端用的 5080。用非 80/443 端口一般不需要备案。

**Q：数据存在哪？能拷回电脑吗？**
`/var/lib/dida-sync/dida.db`，标准 SQLite，拷回电脑用任意 SQLite 工具都能打开。

---

## 九、这一版验证过什么

在 Ubuntu 24.04（WSL，systemd 环境，glibc 2.39）上实际跑过完整流程：

- `install.sh` 全新安装 → systemd `enabled` + `active`，健康检查返回 `ok:true`
- 无 token 访问 `/sync` → 401；错误密码 → 401；连续 9 次错误 → 429
- 登录拿 token（43 位）→ 推送 → `accepted rev=1` → 拉取一致
- 旧 `updated_at` 的改动 → `rejected reason=stale` 并带回服务端版本
- 重启进程 → `max_rev` 与数据都在（SQLite WAL）
- 非 JSON 请求头 / 空请求体 → 400；2MB 请求体 → 413
- 数据文件权限 600（`UMask=0077`），空载内存约 25MB
- `uninstall.sh --purge --purge-user` → 目录、配置、单元、用户、端口全部清空，无残留

电脑端接服务器（`SyncClient`）也实测过，不是纸上推演：

- 首次同步自动 `/login` 换 token，第二次复用同一个 token（不重复登录）
- 服务器上改了密码导致 token 失效 → 自动重登一次并继续，不会卡在 401
- 从单机切到服务器：游标归零、本地全量重传一次；切回来同理
- Windows 开着系统代理时，连内网/环回地址不再被代理接走（之前会被代理返回 502）

**未验证**：真实云服务器的安全组、公网链路、你自己的域名/证书。

---

## 十、归档说明（2026-11-17）

这 6 个文件原在 `打包/dida-sync-deploy/`，随 `app/`（linux-x64 自包含发布，107 MB）一起作为部署包分发。
现在搬到了 `源码/docs/deploy/`，**`app/` 不再保存**——它是 `dotnet publish` 的产物，随时可重建，占空间且会过期。

### 重新生成 app/

```bash
cd 源码/pc/DidaLite
dotnet publish src/DidaLite.Sync/DidaLite.Sync.csproj \
    -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

然后把 `publish/linux-x64/` 的内容作为 `app/` 放到这 6 个文件旁边，就成了完整部署包：

```bash
# 目录结构：dida-sync-deploy/{app/, *.sh, *.service, *.example, README.md}
mkdir -p dida-sync-deploy
cp -a 源码/docs/deploy/. dida-sync-deploy/
cp -a 源码/pc/DidaLite/publish/linux-x64 dida-sync-deploy/app
tar -czf dida-sync-deploy.tar.gz dida-sync-deploy
```

### 备注

- 上面「四、部署」里的 `scp -r dida-sync-deploy ...` 传的是**整个目录**，压缩包只是备份，不必非要打 tar。
- `dida-sync.service` 里的 `Documentation=file:/opt/dida-sync/README.md` 指向的是一份不存在的文件
  （`install.sh` 只把 `app/` 拷进 `/opt`）。不影响运行，只是 `systemctl status` 里那个链接点不开。
