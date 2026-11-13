-- 滴答双端 · 服务端（MySQL 8.4）表结构
-- 字段与客户端 SQLite 的 task 表对齐；客户端专有的 dirty 不同步，服务端独有 received_at
-- 执行：mysql -h 127.0.0.1 -P 3306 -u root -p < schema.sql

CREATE DATABASE IF NOT EXISTS ticktick
  DEFAULT CHARACTER SET utf8mb4
  COLLATE utf8mb4_0900_ai_ci;

USE ticktick;

CREATE TABLE IF NOT EXISTS task (
  id          CHAR(26)     NOT NULL COMMENT 'ULID，双端生成，全局唯一',
  title       VARCHAR(255) NOT NULL,
  note        TEXT         NULL,
  location    VARCHAR(255) NULL COMMENT '地点，可为空',
  start_at    BIGINT       NULL COMMENT 'UTC 毫秒，起始时间',
  due_at      BIGINT       NULL COMMENT 'UTC 毫秒，截止时间',
  all_day     TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '全天事件，只取日期',
  remind_at   BIGINT       NULL COMMENT 'UTC 毫秒，为空则用 start_at / due_at',
  repeat_rule VARCHAR(32)  NULL COMMENT 'none|daily|weekly|monthly|yearly|weekday|every:N',
  done        TINYINT(1)   NOT NULL DEFAULT 0,
  deleted     TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '墓碑标记',
  updated_at  BIGINT       NOT NULL COMMENT 'UTC 毫秒，冲突裁决依据',
  received_at BIGINT       NOT NULL COMMENT '服务端收到时刻，客户端时钟偏差过大时用它兜底',
  rev         BIGINT       NOT NULL COMMENT '服务端单调递增版本号',
  PRIMARY KEY (id),
  KEY idx_rev (rev),
  KEY idx_start (start_at),
  KEY idx_due (due_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS sync_meta (
  k VARCHAR(32) PRIMARY KEY,
  v BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO sync_meta (k, v) VALUES ('rev_seq', 0);

-- 同步账号：只有一个用户，手机上用「用户名 + 密码」换 token
CREATE TABLE IF NOT EXISTS app_user (
  id         TINYINT     NOT NULL PRIMARY KEY COMMENT '单用户，固定为 1',
  name       VARCHAR(64) NOT NULL,
  salt       VARCHAR(32) NOT NULL,
  hash       VARCHAR(64) NOT NULL COMMENT 'PBKDF2-SHA256 派生值，Base64',
  token      VARCHAR(64) NULL COMMENT '手机登录后换到的凭证',
  updated_at BIGINT      NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
