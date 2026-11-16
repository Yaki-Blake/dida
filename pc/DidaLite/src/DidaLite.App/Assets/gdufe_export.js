/* gdufe_export.js
 * 广东财经大学（强智 /jsxsd/）学期理论课表 -> iCalendar(.ics)
 * 纯 JavaScript，零第三方依赖。同一份核心供「浏览器书签」「油猴脚本」「Node 对拍测试」共用。
 *
 * 与 gdufe_schedule.py 的解析规则逐条对齐，产物字节级一致（含 UID / DTSTAMP）。
 * 安全边界：只在浏览器已登录的同源页面内运行，不读密码、不导出 Cookie、不请求任何第三方。
 */
(function (root, factory) {
  var api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.GDUFE = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // ---- 与 Python 版一致的常量 ----
  var PERIODS = {
    1: ['08:30', '09:15'], 2: ['09:15', '10:00'],
    3: ['10:20', '11:05'], 4: ['11:05', '11:50'],
    5: ['14:00', '14:45'], 6: ['14:45', '15:30'],
    7: ['15:50', '16:35'], 8: ['16:35', '17:20'],
    9: ['18:30', '19:15'], 10: ['19:15', '20:00'],
    11: ['20:20', '21:05'], 12: ['21:05', '21:50']
  };
  var ROW_PERIOD = {
    '第一二节': [1, 2], '第三四节': [3, 4], '第五六节': [5, 6],
    '第七八节': [7, 8], '第九十节': [9, 10], '第十一十二节': [11, 12]
  };
  var WEEK_CHARS = '一二三四五六日天';
  var ROOM_HINT = /教|楼|室|机房|实验|场|中心|馆|\d/;
  var SEP_RE = /^[-—=]{5,}$/;
  var PERIOD_RE = /\[?\s*(\d{1,2})\s*[-~]\s*(\d{1,2})\s*\]?\s*节/;
  var DEFAULT_CONFIG = { term: '2026-2027-1', monday: '2026-09-07', exclude: '', calname: '课表' };
  var CONF_KEY = 'gdufe-schedule-config-v1';
  var KB_PATH = '/jsxsd/xskb/xskb_list.do';

  // ---- 小工具 ----
  function clean(s) { return s.replace(/\s+/g, ' ').trim(); }
  function pad(n, w) { var s = String(n); while (s.length < w) s = '0' + s; return s; }
  function byteLen(s) { return new TextEncoder().encode(s).length; }
  function attrOf(attrs, name) {
    var m = new RegExp(name + '\\s*=\\s*([\'"])(.*?)\\1', 'i').exec(attrs);
    return m ? m[2] : '';
  }
  function decodeEntities(s) {
    return s.replace(/&(#x?[0-9a-fA-F]+|nbsp|amp|lt|gt|quot|apos);/g, function (all, key) {
      if (key === 'nbsp') return ' ';
      if (key === 'amp') return '&';
      if (key === 'lt') return '<';
      if (key === 'gt') return '>';
      if (key === 'quot') return '"';
      if (key === 'apos') return "'";
      var n = key[1] === 'x' || key[1] === 'X'
        ? parseInt(key.slice(2), 16) : parseInt(key.slice(1), 10);
      return isNaN(n) ? all : String.fromCodePoint(n);
    });
  }

  // ---- MD5（对 UTF-8 字节），用于生成与 Python 版完全相同的稳定 UID ----
  function md5(bytes) {
    var K = new Array(64), S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21], i;
    for (i = 0; i < 64; i++) K[i] = Math.floor(Math.abs(Math.sin(i + 1)) * 4294967296);
    var len = bytes.length;
    var total = (((len + 8) >> 6) + 1) << 6;
    var buf = new Uint8Array(total);
    buf.set(bytes, 0);
    buf[len] = 0x80;
    var dv = new DataView(buf.buffer);
    dv.setUint32(total - 8, (len * 8) >>> 0, true);
    dv.setUint32(total - 4, Math.floor(len * 8 / 4294967296), true);
    var a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
    for (var off = 0; off < total; off += 64) {
      var M = new Array(16);
      for (i = 0; i < 16; i++) M[i] = dv.getUint32(off + i * 4, true);
      var A = a0, B = b0, C = c0, D = d0;
      for (i = 0; i < 64; i++) {
        var F, g;
        if (i < 16) { F = (B & C) | (~B & D); g = i; }
        else if (i < 32) { F = (D & B) | (~D & C); g = (5 * i + 1) % 16; }
        else if (i < 48) { F = B ^ C ^ D; g = (3 * i + 5) % 16; }
        else { F = C ^ (B | ~D); g = (7 * i) % 16; }
        var sum = (A + F + K[i] + M[g]) | 0;
        var rot = S[(i >> 4) * 4 + (i % 4)];
        var rotated = (sum << rot) | (sum >>> (32 - rot));
        var tmp = D; D = C; C = B; B = (B + rotated) | 0; A = tmp;
      }
      a0 = (a0 + A) | 0; b0 = (b0 + B) | 0; c0 = (c0 + C) | 0; d0 = (d0 + D) | 0;
    }
    var out = '';
    [a0, b0, c0, d0].forEach(function (n) {
      for (var j = 0; j < 4; j++) {
        var b = (n >>> (j * 8)) & 0xff;
        out += (b < 16 ? '0' : '') + b.toString(16);
      }
    });
    return out;
  }
  function md5hex(s) { return md5(new TextEncoder().encode(s)); }

  // ---- HTML -> 表格 token 结构（与 Python KBParser 状态机一一对应）----
  function htmlToTables(html) {
    var TAG_RE = /<(\/?)([a-zA-Z][\w:.-]*)([^>]*?)\/?>/g;
    var tables = [], stack = [], active = null, font = null, line = [], m, pos = 0;

    function flush() {
      var t = clean(line.join(''));
      line = [];
      if (!t || !active) return;
      active.lines.push(t);
      active.tokens.push([SEP_RE.test(t) ? 'sep' : 'text', t]);
    }
    function pushData(raw) {
      if (!raw || active === null) return;
      var s = decodeEntities(raw);
      if (font) font[1].push(s); else line.push(s);
    }
    function newPart() { return { lines: [], tokens: [] }; }

    while ((m = TAG_RE.exec(html)) !== null) {
      pushData(html.slice(pos, m.index));
      pos = TAG_RE.lastIndex;
      var closing = m[1] === '/', tag = m[2].toLowerCase(), attrs = m[3] || '';
      if (tag === 'table') {
        if (!closing) stack.push({ rows: [], row: null, cell: null });
        else if (stack.length) { tables.push(stack.pop().rows); active = null; }
        continue;
      }
      if (!stack.length) continue;
      var cur = stack[stack.length - 1];
      if (tag === 'tr') {
        if (!closing) cur.row = [];
        else if (cur.row !== null) { cur.rows.push(cur.row); cur.row = null; }
      } else if (tag === 'td' || tag === 'th') {
        if (!closing) {
          cur.cell = { detail: newPart(), simple: newPart(), plain: newPart() };
          active = cur.cell.plain; font = null; line = [];
        } else {
          flush();
          var c = cur.cell;
          if (!c) continue;
          var chosen = c.detail.tokens.length ? c.detail
            : (c.simple.tokens.length ? c.simple : c.plain);
          if (cur.row === null) cur.row = [];
          cur.row.push({ text: chosen.lines.join('\n'), tokens: chosen.tokens });
          cur.cell = null; active = null;
        }
      } else if (tag === 'div' && cur.cell !== null) {
        if (!closing) {
          var cls = attrOf(attrs, 'class');
          var kind = (cls.indexOf('kbcontent') >= 0 && cls.indexOf('kbcontent1') < 0) ? 'detail'
            : (cls.indexOf('kbcontent1') >= 0 ? 'simple' : 'plain');
          active = cur.cell[kind]; font = null; line = [];
        } else {
          flush();
          active = cur.cell ? cur.cell.plain : null;
        }
      } else if (tag === 'font' && active !== null) {
        if (!closing) { flush(); font = [attrOf(attrs, 'title'), []]; }
        else if (font) {
          var t = clean(font[1].join(''));
          if (t) { active.lines.push(t); active.tokens.push([font[0], t]); }
          font = null;
        }
      } else if (tag === 'br') {
        flush();
      }
    }
    pushData(html.slice(pos));
    return tables;
  }

  function pickKBTable(html) {
    var tables = htmlToTables(html), best = null, bestScore = -1;
    tables.forEach(function (rows) {
      if (!rows.length) return;
      var flat = rows.map(function (r) { return r.map(function (c) { return c.text; }).join(' '); }).join(' ');
      var score = rows.length * 2;
      if (/星期|周[一二三四五六日天]|Mon|Tue/i.test(flat)) score += 20;
      if (flat.indexOf('节') >= 0) score += 10;
      if (/周次|\d{1,2}\s*[-~,]\s*\d{1,2}\s*[（(]/.test(flat)) score += 30;
      if (html.indexOf('kbtable') >= 0) score += 5;
      if (score > bestScore) { best = rows; bestScore = score; }
    });
    if (!best) throw new Error('页面里没找到课表。可能是登录态失效、返回了登录页，或当前不在课表页面。');
    return best;
  }

  function columnToDay(rows) {
    for (var r = 0; r < Math.min(3, rows.length); r++) {
      var mapping = {};
      rows[r].forEach(function (cell, idx) {
        for (var n = 1; n <= 7; n++) {
          var ch = WEEK_CHARS[n - 1];
          if (new RegExp('星期\\s*' + ch + '|周\\s*' + ch).test(cell.text)) { mapping[idx] = n; break; }
        }
      });
      if (Object.keys(mapping).length >= 5) return mapping;
    }
    var fallback = {}; for (var i = 1; i <= 7; i++) fallback[i] = i;
    return fallback;
  }

  function parseWeeksExpr(expr) {
    var parity = expr.indexOf('单') >= 0 ? '单' : (expr.indexOf('双') >= 0 ? '双' : '');
    var s = expr.replace(/[（(][^）)]*[）)]/g, '').replace(/周/g, '').replace(/第/g, '').trim();
    var weeks = new Set();
    s.split(/[,，;；\s]+/).forEach(function (part) {
      if (!part) return;
      var m = /^(\d{1,2})\s*[-~—]\s*(\d{1,2})$/.exec(part);
      if (m) { for (var w = +m[1]; w <= +m[2]; w++) weeks.add(w); }
      else if (/^\d{1,2}$/.test(part)) weeks.add(+part);
    });
    if (parity) {
      var filtered = new Set();
      weeks.forEach(function (w) { if (parity === '单' ? w % 2 === 1 : w % 2 === 0) filtered.add(w); });
      weeks = filtered;
    }
    return weeks;
  }

  function parseBlock(block, day, fallback, cid) {
    var name = '', teacher = '', room = '', weeksExpr = '';
    var p0 = fallback[0], p1 = fallback[1];
    var tagged = block.some(function (t) { return t[0] !== 'text' && t[0] !== 'sep'; });
    var leftovers = [];
    block.forEach(function (tok) {
      var kind = tok[0], t = tok[1], m;
      if (kind === '老师') teacher = t;
      else if (kind === '教室') room = t;
      else if (kind.indexOf('周次') >= 0 || (t.indexOf('周') >= 0 && /\d/.test(t) && t.indexOf('节') < 0)) weeksExpr = t;
      else if ((m = PERIOD_RE.exec(t))) { p0 = +m[1]; p1 = +m[2]; }
      else if (!name) name = t;
      else leftovers.push(t);
    });
    if (!tagged) {
      leftovers.forEach(function (t) {
        if (!room && ROOM_HINT.test(t)) room = t;
        else if (!teacher && /^[\u4e00-\u9fa5·]{2,6}$/.test(t)) teacher = t;
      });
    }
    var weeks = weeksExpr ? parseWeeksExpr(weeksExpr) : new Set();
    if (!name || !weeks.size) return null;
    return {
      cid: cid, name: name, teacher: teacher, room: room || '见教务系统',
      day: day, weeks: weeks, weeksExpr: weeksExpr, p0: p0, p1: p1
    };
  }

  function parseCell(cell, day, fallback, cidStart) {
    var blocks = [], cur = [];
    cell.tokens.forEach(function (tok) {
      if (tok[0] === 'sep' || SEP_RE.test(tok[1])) {
        if (cur.length) { blocks.push(cur); cur = []; }
      } else cur.push(tok);
    });
    if (cur.length) blocks.push(cur);
    var out = [];
    blocks.forEach(function (b, i) {
      var c = parseBlock(b, day, fallback, cidStart + i);
      if (c) out.push(c);
    });
    return out;
  }

  function parseRows(rows) {
    var courses = [], warnings = [], dayOfCol = columnToDay(rows);
    rows.forEach(function (row) {
      var head = row.length ? row[0].text.trim() : '';
      var fp = ROW_PERIOD[head];
      if (!fp) {
        var m = /(\d{1,2})\s*[-~]\s*(\d{1,2})\s*节/.exec(head);
        fp = m ? [+m[1], +m[2]] : [1, 2];
      }
      row.forEach(function (cell, col) {
        var day = dayOfCol[col];
        if (day === undefined || col === 0) return;
        parseCell(cell, day, fp, courses.length).forEach(function (c) {
          courses.push(c); c.cid = courses.length - 1;
          if (!PERIODS[c.p0] || !PERIODS[c.p1]) warnings.push('课程「' + c.name + '」节次 ' + c.p0 + '-' + c.p1 + ' 不在作息表中');
        });
      });
    });
    return { courses: courses, warnings: warnings };
  }

  // ---- 日期 / ICS ----
  function parseDate(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s);
    if (!m) throw new Error('日期格式应为 YYYY-MM-DD：' + s);
    return { y: +m[1], m: +m[2], d: +m[3] };
  }
  function addDays(base, n) {
    var dt = new Date(Date.UTC(base.y, base.m - 1, base.d) + n * 86400000);
    return { y: dt.getUTCFullYear(), m: dt.getUTCMonth() + 1, d: dt.getUTCDate() };
  }
  function utcStamp(d, hm) {
    var p = hm.split(':');
    var dt = new Date(Date.UTC(d.y, d.m - 1, d.d, +p[0] - 8, +p[1], 0));
    return pad(dt.getUTCFullYear(), 4) + pad(dt.getUTCMonth() + 1, 2) + pad(dt.getUTCDate(), 2)
      + 'T' + pad(dt.getUTCHours(), 2) + pad(dt.getUTCMinutes(), 2) + '00Z';
  }
  function expand(courses, monday, skipWeeks, excludeDates) {
    var events = [];
    courses.forEach(function (c) {
      Array.from(c.weeks).sort(function (a, b) { return a - b; }).forEach(function (w) {
        if (skipWeeks.has(w)) return;
        var d = addDays(monday, (w - 1) * 7 + c.day - 1);
        var key = pad(d.y, 4) + '-' + pad(d.m, 2) + '-' + pad(d.d, 2);
        if (excludeDates.has(key) || !PERIODS[c.p0] || !PERIODS[c.p1]) return;
        events.push(Object.assign({}, c, {
          week: w, date: d,
          start: utcStamp(d, PERIODS[c.p0][0]),
          end: utcStamp(d, PERIODS[c.p1][1])
        }));
      });
    });
    return events;
  }
  function icsEscape(s) {
    return s.replace(/\\/g, '\\\\').replace(/;/g, '\\;').replace(/,/g, '\\,').replace(/\n/g, '\\n');
  }
  function fold(line) {
    if (byteLen(line) <= 75) return [line];
    var out = [], cur = '', limit = 75;
    for (var ch of line) {
      if (byteLen(cur + ch) > limit) { out.push(cur); cur = ' '; limit = 74; }
      cur += ch;
    }
    if (cur) out.push(cur);
    return out;
  }
  function stableUid(e) {
    var seed = [e.name, e.day, e.p0, e.room, e.cid, e.week].join('|');
    return md5hex(seed).slice(0, 12) + '-w' + e.week + '@gdufe.local';
  }
  function buildICS(events, monday, calname) {
    var stamp = utcStamp(monday, '00:00');
    var raw = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//GDUFE Schedule//CN',
      'CALSCALE:GREGORIAN', 'METHOD:PUBLISH', 'X-WR-CALNAME:' + icsEscape(calname || '课表'),
      'X-WR-TIMEZONE:Asia/Shanghai'];
    events.forEach(function (e) {
      var desc = '第' + e.week + '教学周 · 周次 ' + e.weeksExpr;
      if (e.teacher) desc += ' · ' + e.teacher;
      raw.push('BEGIN:VEVENT', 'UID:' + stableUid(e), 'DTSTAMP:' + stamp,
        'DTSTART:' + e.start, 'DTEND:' + e.end,
        'SUMMARY:' + icsEscape(e.name), 'LOCATION:' + icsEscape(e.room),
        'DESCRIPTION:' + icsEscape(desc),
        'BEGIN:VALARM', 'ACTION:DISPLAY', 'TRIGGER:-PT15M',
        'DESCRIPTION:' + icsEscape(e.name + ' 即将开始'),
        'END:VALARM', 'END:VEVENT');
    });
    raw.push('END:VCALENDAR');
    var folded = [];
    raw.forEach(function (l) { fold(l).forEach(function (f) { folded.push(f); }); });
    return folded.join('\r\n') + '\r\n';
  }

  // 核心入口：HTML 字符串 -> ICS 字符串
  function convert(html, config) {
    var cfg = Object.assign({}, DEFAULT_CONFIG, config || {});
    var monday = parseDate(cfg.monday);
    if (new Date(Date.UTC(monday.y, monday.m - 1, monday.d)).getUTCDay() !== 1)
      throw new Error('第 1 周周一必须是星期一，当前填的是 ' + cfg.monday);
    var parsed = parseRows(pickKBTable(html));
    if (!parsed.courses.length) throw new Error('解析到 0 门课，请确认页面是「学期理论课表」。');
    var skip = new Set((cfg.skipWeeks || '').split(/[,，\s]+/).filter(Boolean).map(Number));
    var excl = new Set((cfg.exclude || '').split(/[,，\s]+/).filter(Boolean));
    var events = expand(parsed.courses, monday, skip, excl);
    return {
      ics: buildICS(events, monday, cfg.calname),
      courses: parsed.courses, events: events, warnings: parsed.warnings, config: cfg
    };
  }

  // ---- 以下仅浏览器端使用 ----
  function getConfig() {
    try { return Object.assign({}, DEFAULT_CONFIG, JSON.parse(localStorage.getItem(CONF_KEY) || '{}')); }
    catch (e) { return Object.assign({}, DEFAULT_CONFIG); }
  }
  function saveConfig(cfg) {
    try { localStorage.setItem(CONF_KEY, JSON.stringify(cfg)); } catch (e) { /* 隐私模式下忽略 */ }
  }
  function decodeText(buf) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(buf); }
    catch (e) { return new TextDecoder('gb18030').decode(buf); }
  }
  function currentTerm() {
    var sel = document.querySelector('#xnxq01id');
    return (sel && sel.value) || getConfig().term;
  }
  // 优先直接读当前页的课表；否则在教务系统同源内自动 POST 取课表页（浏览器自带会话，无需 Cookie）
  async function getScheduleHTML() {
    var table = document.querySelector('#kbtable');
    if (table && table.textContent && table.textContent.indexOf('星期') >= 0) return table.outerHTML;
    var host = location.hostname.toLowerCase();
    if (!(host === 'jwxt.gdufe.edu.cn' || /gdufe\.edu\.cn$/.test(host)))
      throw new Error('请先登录教务系统（jwxt.gdufe.edu.cn），或在已打开的课表页上使用本书签。');
    var term = currentTerm();
    var resp = await fetch(KB_PATH, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: 'xnxq01id=' + encodeURIComponent(term) + '&zc='
    });
    if (!resp.ok) throw new Error('请求课表页失败：HTTP ' + resp.status);
    var html = decodeText(await resp.arrayBuffer());
    if (html.indexOf('kbtable') < 0)
      throw new Error('拿到的页面不含课表，登录态可能已过期，请重新登录教务系统后重试。');
    return html;
  }
  function download(ics, term) {
    var blob = new Blob([ics], { type: 'text/calendar;charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url; a.download = '课表_' + term + '.ics';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
  }
  function panelStyle(el, css) { el.setAttribute('style', css); return el; }
  function removeUI() {
    ['gdufe-exp-panel', 'gdufe-exp-toast'].forEach(function (id) {
      var n = document.getElementById(id); if (n) n.remove();
    });
  }
  function toast(count, onSettings) {
    removeUI();
    var t = panelStyle(document.createElement('div'),
      'position:fixed;top:14px;right:14px;z-index:2147483646;background:#fff;color:#1f2937;' +
      'border:1px solid #d1d5db;border-radius:10px;box-shadow:0 6px 24px rgba(0,0,0,.16);' +
      'padding:10px 14px;font:14px/1.5 -apple-system,Segoe UI,Microsoft YaHei,sans-serif;display:flex;' +
      'align-items:center;gap:12px;max-width:90vw');
    t.id = 'gdufe-exp-toast';
    var span = document.createElement('span');
    span.textContent = '已导出 ' + count + ' 个日历事件 ✓';
    t.appendChild(span);
    var b = panelStyle(document.createElement('button'),
      'border:0;background:#2563eb;color:#fff;border-radius:6px;padding:4px 10px;cursor:pointer;font-size:13px');
    b.textContent = '设置';
    b.onclick = onSettings;
    t.appendChild(b);
    var x = panelStyle(document.createElement('button'),
      'border:0;background:transparent;color:#6b7280;cursor:pointer;font-size:16px;line-height:1');
    x.textContent = '×'; x.onclick = function () { t.remove(); };
    t.appendChild(x);
    document.body.appendChild(t);
    setTimeout(function () { if (t.parentNode) t.remove(); }, 12000);
  }
  function showPanel(html, cfg, onDone) {
    removeUI();
    var preview;
    try { preview = convert(html, cfg); }
    catch (e) { alert('课表导出失败：' + e.message); return; }
    var p = panelStyle(document.createElement('div'),
      'position:fixed;top:14px;right:14px;z-index:2147483647;width:320px;max-width:92vw;' +
      'background:#fff;color:#1f2937;border:1px solid #d1d5db;border-radius:12px;' +
      'box-shadow:0 10px 36px rgba(0,0,0,.2);padding:16px;' +
      'font:14px/1.5 -apple-system,Segoe UI,Microsoft YaHei,sans-serif');
    p.id = 'gdufe-exp-panel';
    function row(label, input) {
      var l = panelStyle(document.createElement('label'), 'display:block;margin:10px 0 4px;color:#374151;font-size:13px');
      l.textContent = label; p.appendChild(l);
      panelStyle(input, 'width:100%;box-sizing:border-box;border:1px solid #d1d5db;border-radius:6px;' +
        'padding:6px 8px;font-size:14px;color:#111827;background:#fff');
      p.appendChild(input);
    }
    var h = panelStyle(document.createElement('div'), 'font-weight:600;font-size:16px;margin-bottom:2px');
    h.textContent = '课表 → 日历';
    p.appendChild(h);
    var info = panelStyle(document.createElement('div'), 'color:#6b7280;font-size:12px');
    info.textContent = '解析到 ' + preview.courses.length + ' 个课程块 / ' + preview.events.length + ' 个事件';
    p.appendChild(info);
    var iTerm = document.createElement('input'); iTerm.value = cfg.term;
    var iMon = document.createElement('input'); iMon.type = 'date'; iMon.value = cfg.monday;
    var iExc = document.createElement('input'); iExc.value = cfg.exclude; iExc.placeholder = '如 2026-10-01,2026-10-02';
    var iName = document.createElement('input'); iName.value = cfg.calname;
    row('学期（用于文件名）', iTerm);
    row('第 1 教学周的周一', iMon);
    row('排除的日期（停课/假期，可留空）', iExc);
    row('日历名称', iName);
    var hint = panelStyle(document.createElement('div'), 'color:#9ca3af;font-size:11px;margin-top:10px');
    hint.textContent = '设置保存在本机浏览器，以后点书签直接下载。数据不离开当前页面。';
    p.appendChild(hint);
    var bar = panelStyle(document.createElement('div'), 'display:flex;gap:8px;margin-top:14px');
    var go = panelStyle(document.createElement('button'),
      'flex:1;border:0;background:#2563eb;color:#fff;border-radius:6px;padding:8px;cursor:pointer;font-size:14px');
    go.textContent = '生成并下载 .ics';
    go.onclick = function () {
      var next = { term: iTerm.value.trim(), monday: iMon.value, exclude: iExc.value.trim(), calname: iName.value.trim() || '课表' };
      var result;
      try { result = convert(html, next); }
      catch (e) { alert('导出失败：' + e.message); return; }
      saveConfig(next);
      download(result.ics, next.term);
      p.remove();
      onDone(result.events.length, function () { showPanel(html, next, onDone); });
    };
    var cancel = panelStyle(document.createElement('button'),
      'border:0;background:#f3f4f6;color:#374151;border-radius:6px;padding:8px 12px;cursor:pointer;font-size:14px');
    cancel.textContent = '取消'; cancel.onclick = function () { p.remove(); };
    bar.appendChild(go); bar.appendChild(cancel); p.appendChild(bar);
    document.body.appendChild(p);
  }
  async function main() {
    if (typeof document === 'undefined') throw new Error('main() 只能在浏览器中运行');
    var html = await getScheduleHTML();
    var cfg = getConfig();
    var first = true;
    try { first = !localStorage.getItem(CONF_KEY); } catch (e) { /* ignore */ }
    if (first) { showPanel(html, cfg, function (n, settings) { toast(n, settings); }); return; }
    var result = convert(html, cfg);
    download(result.ics, cfg.term);
    toast(result.events.length, function () { showPanel(html, cfg, function (n, s) { toast(n, s); }); });
  }

  return {
    PERIODS: PERIODS, parseWeeksExpr: parseWeeksExpr, htmlToTables: htmlToTables,
    pickKBTable: pickKBTable, parseRows: parseRows, expand: expand, fold: fold,
    md5hex: md5hex, buildICS: buildICS, convert: convert, main: main,
    DEFAULT_CONFIG: DEFAULT_CONFIG
  };
});
