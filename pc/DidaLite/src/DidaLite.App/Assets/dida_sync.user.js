// ==UserScript==
// @name         dida 课表自动同步（默认浏览器）
// @namespace    dida-lite-jwxt
// @version      1.0.1
// @description  用默认浏览器里已登录的教务账号，把课表自动同步进本机 dida；不读密码、不导出 Cookie。装上后打开课表页即自动完成，点 dida 的「一键同步」即可全程无感。
// @author       YakiBlake
// @match        *://jwxt.gdufe.edu.cn/*
// @match        *://*.gdufe.edu.cn/*
// @require      http://127.0.0.1:5080/gdufe_export.js
// @connect      127.0.0.1
// @grant        GM_xmlhttpRequest
// @grant        GM_getValue
// @grant        GM_setValue
// @run-at       document-idle
// ==/UserScript==

(function () {
  'use strict';

  var API = 'http://127.0.0.1:5080';
  var THROTTLE_MS = 10 * 60 * 1000;   // 同一课表页 10 分钟内不重复自动同步

  function toast(msg, ok) {
    var old = document.getElementById('dida-auto-toast');
    if (old) old.remove();
    var el = document.createElement('div');
    el.id = 'dida-auto-toast';
    el.setAttribute('style', [
      'position:fixed', 'bottom:20px', 'right:20px', 'z-index:2147483647',
      'max-width:80vw', 'padding:10px 14px', 'border-radius:10px',
      'box-shadow:0 6px 24px rgba(0,0,0,.18)', 'white-space:pre-wrap',
      'font:14px/1.5 -apple-system,Segoe UI,Microsoft YaHei,sans-serif',
      'background:' + (ok ? '#16a34a' : '#dc2626'), 'color:#fff'
    ].join(';') + ';');
    el.textContent = msg;
    document.body.appendChild(el);
    setTimeout(function () { if (el.parentNode) el.remove(); }, ok ? 6000 : 15000);
  }

  // GM_xmlhttpRequest 走扩展通道，不受页面 CORS / CSP / 私有网络预检限制
  function gm(opts) {
    return new Promise(function (resolve, reject) {
      GM_xmlhttpRequest(Object.assign({}, opts, {
        timeout: opts.timeout || 15000,
        onload: resolve,
        onerror: function () { reject(new Error('连不上 dida（' + API + '），请确认 dida 正在运行')); },
        ontimeout: function () { reject(new Error('dida 响应超时')); }
      }));
    });
  }

  function decode(buf) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(buf); }
    catch (e) { return new TextDecoder('gb18030').decode(buf); }
  }

  async function getConfig() {
    var r = await gm({ method: 'GET', url: API + '/api/schedule/sync-config' });
    return JSON.parse(r.responseText);
  }

  function downloadIcs(ics) {
    try {
      var a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([ics], { type: 'text/calendar;charset=utf-8' }));
      a.download = '课表_同步到dida.ics';
      document.body.appendChild(a); a.click(); a.remove();
      return true;
    } catch (e) { return false; }
  }

  async function getHtml(cfg) {
    // 课表页直接读 DOM，零请求：WebVPN / 登录态变化 / 网络抖动都不影响
    var table = document.querySelector('#kbtable');
    var raw = table ? (table.outerHTML || table.textContent || '') : '';
    if (raw && /kbcontent|星期|节次|第\s*[1-9一二三四五六]/.test(raw)) return table.outerHTML || raw;
    var sel = document.querySelector('#xnxq01id');
    var term = (sel && sel.value) || cfg.term;
    var kbUrl = cfg.kbPath;
    if (location.pathname && location.pathname.indexOf('/jsxsd/') < 0 &&
        /xskb|kbtable|kb/i.test(location.pathname)) kbUrl = location.pathname;
    var resp;
    try {
      resp = await fetch(kbUrl, {
        method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: 'xnxq01id=' + encodeURIComponent(term) + '&zc='
      });
    } catch (e) {
      throw new Error('取教务课表失败（' + ((e && e.message) || e) + '）：' +
        '请确认已登录、校外先连学校 VPN，并停留在「我的课表 → 学期理论课表」页面');
    }
    if (!resp.ok) throw new Error('教务系统返回 HTTP ' + resp.status + '（请先登录，再打开学期理论课表）');
    var html = decode(await resp.arrayBuffer());
    if (html.indexOf('kbtable') < 0)
      throw new Error('没拿到课表，请确认已登录教务系统（校外先连学校 VPN）');
    return html;
  }

  async function sync(force) {
    var ics = null;
    try {
      if (typeof globalThis.GDUFE !== 'object')
        throw new Error('解析核心未加载：请确认 dida 正在运行，然后刷新本页');
      var cfg = await getConfig();
      var html = await getHtml(cfg);
      var result = globalThis.GDUFE.convert(html, {
        term: cfg.term, monday: cfg.monday,
        exclude: cfg.exclude || '', calname: '课表'
      });
      ics = result.ics;
      var sent = await gm({
        method: 'POST',
        url: API + '/api/schedule/import?name=' + encodeURIComponent('默认浏览器同步.ics') + '&source=browser',
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        data: result.ics
      });
      var json = JSON.parse(sent.responseText);
      if (sent.status < 200 || sent.status >= 300 || !json.ok)
        throw new Error(json.message || ('dida 返回 HTTP ' + sent.status));
      GM_setValue('dida-last-sync', Date.now());
      toast('已自动同步到 dida：' + json.events + ' 节课 · ' + json.courses + ' 门课 ✓', true);
    } catch (e) {
      var why = (e && e.message) || String(e);
      var saved = ics ? downloadIcs(ics) : false;
      toast('同步失败：' + why +
        (saved ? '\n已自动下载「课表_同步到dida.ics」，可在 dida 点「导入课表」选它。' : '') +
        '\n助手页：' + API + '/dida-sync', false);
    }
  }

  // 右下角常驻按钮：随时手动重同步 / 强制刷新
  var btn = document.createElement('button');
  btn.textContent = '同步到 dida';
  btn.title = '把当前课表同步进本机 dida（点 dida 的「一键同步」会自动完成，无需点这里）';
  btn.setAttribute('style', [
    'position:fixed', 'bottom:20px', 'right:20px', 'z-index:2147483646',
    'background:#16a34a', 'color:#fff', 'border:0', 'border-radius:999px',
    'padding:9px 15px', 'font:13px/1.2 -apple-system,Segoe UI,Microsoft YaHei,sans-serif',
    'box-shadow:0 4px 16px rgba(0,0,0,.22)', 'cursor:pointer', 'opacity:.86'
  ].join(';') + ';');
  btn.onclick = function () { sync(true); };
  document.body.appendChild(btn);

  // 课表页打开即自动同步（节流，避免同一页反复导入）
  if (document.querySelector('#kbtable') && Date.now() - (GM_getValue('dida-last-sync', 0) || 0) > THROTTLE_MS) {
    setTimeout(function () { sync(false); }, 800);
  }
})();
