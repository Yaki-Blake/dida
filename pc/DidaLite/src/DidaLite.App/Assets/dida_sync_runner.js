/* dida_sync_runner.js
 * 在「教务系统页面」里执行：读课表 HTML -> ICS -> POST 回本机 dida(5080)。
 * 由 /dida-sync 助手页把本文件源码 + gdufe_export.js + 配置一起打包成书签 URL，
 * 因此这里必须自包含，不能引用外部变量或外部文件。
 *
 * 边界：只查当前登录账号自己的课表，不读密码、不导出 Cookie、不请求第三方域名。
 *
 * 抗拦截设计（针对 Chrome 的本地网络访问限制 / CSP / WebVPN）：
 *   1. 课表优先直接读当前页 #kbtable，零请求，不依赖教务系统二次 POST；
 *   2. 跨源 POST 被浏览器拦下时，改用 postMessage 交给 127.0.0.1 的助手页同源中转；
 *   3. 中转也不成功时自动下载 .ics，用户用 dida「导入课表」选文件即可；
 *   4. 每一步失败都给中文、可操作的提示，不再抛裸的 "Failed to fetch"。
 */
async function didaSyncRunner(CORE, CFG) {
  'use strict';

  function toast(msg, ok) {
    try {
      var old = document.getElementById('dida-sync-toast');
      if (old) old.remove();
      var el = document.createElement('div');
      el.id = 'dida-sync-toast';
      el.setAttribute('style', [
        'position:fixed', 'top:14px', 'right:14px', 'z-index:2147483647',
        'max-width:82vw', 'padding:10px 14px', 'border-radius:10px',
        'box-shadow:0 6px 24px rgba(0,0,0,.18)', 'white-space:pre-wrap',
        'font:14px/1.5 -apple-system,Segoe UI,Microsoft YaHei,sans-serif',
        'background:' + (ok ? '#16a34a' : '#dc2626'), 'color:#fff'
      ].join(';') + ';');
      el.textContent = msg;
      document.body.appendChild(el);
      setTimeout(function () { if (el.parentNode) el.remove(); }, ok ? 6000 : 15000);
    } catch (e) {
      if (!ok) alert(msg);
    }
  }

  function decode(buf) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(buf); }
    catch (e) { return new TextDecoder('gb18030').decode(buf); }
  }

  // 兜底：把 ICS 存成文件，用户点 dida 的「导入课表」选它即可，完全绕开浏览器网络限制
  function downloadIcs(ics) {
    try {
      if (typeof Blob === 'undefined' || typeof URL === 'undefined' ||
          !URL.createObjectURL || !document.createElement) return false;
      var blob = new Blob([ics], { type: 'text/calendar;charset=utf-8' });
      var href = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = href;
      a.download = '课表_同步到dida.ics';
      document.body.appendChild(a);
      if (a.click) a.click();
      if (a.remove) a.remove();
      setTimeout(function () { URL.revokeObjectURL(href); }, 30000);
      return true;
    } catch (e) { return false; }
  }

  // 中转：教务页 -> 助手页(127.0.0.1:5080) 走 postMessage，助手页与 5080 同源，
  // 它的 POST 不受跨源 / 私有网络限制。优先复用 window.opener（助手页打开的课表页），
  // 否则自己开一个助手页窗口；定时重发直到助手页回 ack，避免加载时序问题。
  function relayThroughHelper(ics) {
    return new Promise(function (resolve) {
      var origin = String(CFG.api || '').replace(/\/+$/, '');
      var targets = [];
      try {
        if (typeof window === 'undefined') { resolve(false); return; }
        if (window.opener && window.opener !== window) targets.push(window.opener);
        if (window.open) {
          var opened = window.open(origin + '/dida-sync', 'dida-sync-helper');
          if (opened && targets.indexOf(opened) < 0) targets.push(opened);
        }
      } catch (e) { /* 弹窗被拦就交给下面的下载兜底 */ }

      if (!targets.length || typeof window.addEventListener !== 'function' ||
          typeof setInterval !== 'function') { resolve(false); return; }

      var settled = false, timer = null, tries = 0;
      function finish(ok, message) {
        if (settled) return;
        settled = true;
        if (timer) clearInterval(timer);
        window.removeEventListener('message', onMessage);
        resolve(ok ? (message || true) : false);
      }
      function onMessage(ev) {
        var d = ev && ev.data;
        if (!d || d.type !== 'dida-sync-relay-result') return;
        finish(!!d.ok, d.message);
      }
      window.addEventListener('message', onMessage);
      function ping() {
        if (settled) return;
        if (tries++ > 24) { finish(false); return; }
        for (var i = 0; i < targets.length; i++) {
          try { targets[i].postMessage({ type: 'dida-sync-relay', ics: ics }, origin); }
          catch (e) { /* 某个窗口已关闭，忽略 */ }
        }
      }
      timer = setInterval(ping, 250);
      ping();
      setTimeout(function () { finish(false); }, 7000);
    });
  }

  try {
    // 1) 加载解析核心（UMD 会挂到 globalThis.GDUFE）
    if (typeof globalThis.GDUFE !== 'object') (new Function(CORE))();
    if (typeof globalThis.GDUFE !== 'object')
      throw new Error('解析核心加载失败：页面 CSP 可能拦截了内联脚本，请改用助手页里的油猴脚本');

    // 2) 课表 HTML：只要当前页有像样的 #kbtable 就直接读，不再额外请求教务系统
    var html = '';
    var table = document.querySelector('#kbtable');
    var raw = table ? (table.outerHTML || table.textContent || '') : '';
    if (raw && /kbcontent|星期|节次|第\s*[1-9一二三四五六]/.test(raw)) {
      html = table.outerHTML || raw;
    } else {
      var kbUrl = CFG.kbPath;
      // WebVPN / 反向代理路径下，绝对路径 /jsxsd/... 不存在，改用当前页地址
      if (typeof location !== 'undefined' && location.pathname &&
          location.pathname.indexOf('/jsxsd/') < 0 && /xskb|kbtable|kb/i.test(location.pathname))
        kbUrl = location.pathname;
      var sel = document.querySelector('#xnxq01id');
      var term = (sel && sel.value) || CFG.term;
      var resp;
      try {
        resp = await fetch(kbUrl, {
          method: 'POST', credentials: 'same-origin',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: 'xnxq01id=' + encodeURIComponent(term) + '&zc='
        });
      } catch (e) {
        throw new Error('取教务课表失败（' + ((e && e.message) || e) + '）：' +
          '请确认已登录、校外先连学校 VPN，并手动打开「我的课表 → 学期理论课表」，' +
          '看到完整课表后再点同步');
      }
      if (!resp.ok) throw new Error('教务系统返回 HTTP ' + resp.status + '（请先登录，再打开学期理论课表）');
      html = decode(await resp.arrayBuffer());
      if (html.indexOf('kbtable') < 0)
        throw new Error('没拿到课表：请确认已登录教务系统，并停留在「学期理论课表」页面');
    }

    // 3) 转成 ICS（与本地 Python / CDP 同步同一份解析核心，产物字节级一致）
    var result = globalThis.GDUFE.convert(html, {
      term: CFG.term, monday: CFG.monday,
      exclude: CFG.exclude || '', calname: '课表'
    });

    // 4) 送回 dida 本机 5080；服务端已放行 CORS 与 Private Network 预检
    var url = CFG.api + '/api/schedule/import?name=' +
      encodeURIComponent('默认浏览器同步.ics') + '&source=browser';
    var json;
    try {
      var sent = await fetch(url, {
        method: 'POST', mode: 'cors', credentials: 'omit',
        headers: { 'Content-Type': 'text/plain' }, body: result.ics
      });
      if (!sent.ok) throw new Error('HTTP ' + sent.status);
      json = await sent.json();
      if (!json.ok) throw new Error(json.message || 'dida 导入失败');
    } catch (e) {
      var why = (e && e.message) || String(e);
      var relayed = await relayThroughHelper(result.ics);
      if (relayed) {
        toast('已同步到 dida ✓（经同步助手中转）\n课表页若没刷新，回 dida 点一下「一键同步」', true);
        return { ok: true, relayed: true };
      }
      var saved = downloadIcs(result.ics);
      throw new Error('浏览器拦下了到 dida 的本机请求（' + why + '）。' +
        (saved ? '已自动下载「课表_同步到dida.ics」，请在 dida 点「导入课表」选它；' : '') +
        '想全自动请安装助手页里的油猴脚本（走扩展通道，不受此限制）。' +
        '助手页：' + CFG.api + '/dida-sync');
    }

    toast('已同步到 dida：' + json.events + ' 节课 · ' + json.courses + ' 门课 ✓', true);
    return { ok: true, events: json.events, courses: json.courses };
  } catch (e) {
    var why = (e && e.message) || String(e);
    toast('同步失败：' + why + '\n助手页：' + CFG.api + '/dida-sync', false);
    return { ok: false, message: why };
  }
}

if (typeof module !== 'undefined' && module.exports) module.exports = didaSyncRunner;
