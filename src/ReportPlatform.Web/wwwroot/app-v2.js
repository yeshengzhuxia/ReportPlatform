import { editFilterDialog, mountTemplateSettings, templateOptions } from './filter-editor.js?v=20260920-templates';
const $ = (selector, root = document) => root.querySelector(selector);
const app = $('#app');
let state, view = 'reports', currentReport, filters = [];
let personalTemplates = [], selectedTemplateId = '', templatesReady = Promise.resolve(), reportRequestVersion = 0;
const copyFilters = value => JSON.parse(JSON.stringify(value || []));
let queryState = { page: 1, pageSize: 50, total: 0, queried: false };
let sidebarCollapsed = window.matchMedia('(max-width: 800px)').matches;
try { const stored = localStorage.getItem('report-platform:sidebar-collapsed'); if (stored !== null) sidebarCollapsed = stored === 'true'; } catch {}

const names = { reports: '报表中心', connections: '数据源管理', accounts: '账套管理', users: '用户管理', roles: '角色与权限', audit: '查询审计' };
const icons = { reports: '▦', connections: '▤', accounts: '▣', users: '♙', roles: '◇', audit: '◷' };
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
const reportIcon = (report, fallback = '▦') => report.icon
  ? `<img class="report-icon-image" src="${esc(report.icon)}" alt="">`
  : esc(fallback);

function toast(message) {
  $('#toast').textContent = message;
  $('#toast').style.display = 'block';
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => $('#toast').style.display = 'none', 4200);
}

async function api(path, method = 'GET', data) {
  const response = await fetch('/api' + path, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'ReportPlatform' },
    body: data === undefined ? undefined : JSON.stringify(data)
  });
  const result = await response.json();
  if (!response.ok) {
    if (response.status === 401 && path !== '/login') login();
    throw Error(result.error || '请求失败');
  }
  return result;
}

function ask(title, detail, confirmText = '确认', danger = false) {
  return new Promise(resolve => {
    const dialog = document.createElement('dialog');
    dialog.className = 'confirm-dialog';
    dialog.innerHTML = `<div class="dialog-head"><h2>${esc(title)}</h2><button type="button" aria-label="关闭">×</button></div><p>${esc(detail)}</p><div class="dialog-actions"><button type="button" data-cancel>取消</button><button type="button" class="${danger ? 'danger primary' : 'primary'}" data-ok>${esc(confirmText)}</button></div>`;
    document.body.append(dialog);
    const close = value => { dialog.close(); dialog.remove(); resolve(value); };
    $('[aria-label="关闭"]', dialog).onclick = () => close(false);
    $('[data-cancel]', dialog).onclick = () => close(false);
    $('[data-ok]', dialog).onclick = () => close(true);
    dialog.oncancel = () => close(false);
    dialog.showModal();
  });
}

function login() {
  state = null;
  app.innerHTML = `<div class="login"><section class="login-art"><div class="brand"><img src="/favicon.svg" alt="">浅猪</div><div><small>REPORT WORKSPACE</small><h1>让每一次查询<br>都有据可依。</h1><p>连接业务数据，以账套划分组织范围。让合适的人，看见合适的数据。</p></div><small>报表查询 · 权限管理 · 操作留痕</small></section><section class="login-form"><form id="login"><h1>登录工作台</h1><p>使用您的平台账号，进入报表工作空间。</p><label>登录账号<input name="username" required autocomplete="username"></label><label>密码<input name="password" type="password" required autocomplete="current-password"></label><div id="accounts"></div><p class="error" id="error"></p><button class="primary">验证账号</button></form></section></div>`;
  let accountStep = false;
  $('#login').onsubmit = async event => {
    event.preventDefault();
    const button = $('button', event.target); button.disabled = true;
    try {
      const request = Object.fromEntries(new FormData(event.target));
      const result = await api('/login', 'POST', request);
      if (result.accounts) {
        if (!result.accounts.length) throw Error('暂无可登录账套，请联系管理员。');
        $('#accounts').innerHTML = `<label>选择账套<select name="accountId">${result.accounts.map(account => `<option value="${esc(account.id)}">${esc(account.name)}</option>`).join('')}</select></label>`;
        accountStep = true; button.textContent = '登录账套';
      } else await boot();
    } catch (error) { $('#error').textContent = error.message; }
    finally { button.disabled = false; }
  };
  $('#login').querySelectorAll('input').forEach(input => input.oninput = () => {
    if (accountStep) { $('#accounts').innerHTML = ''; accountStep = false; $('#login button').textContent = '验证账号'; }
  });
}

async function boot() {
  try { state = await api('/bootstrap'); shell(); render(); }
  catch (error) { if (!state) login(); else toast(error.message); }
}

function shell() {
  app.innerHTML = `<div class="layout"><aside class="sidebar" id="sidebar"><div class="brand"><img src="/favicon.svg" alt="浅猪"><div class="brand-name">浅猪<small>REPORT WORKSPACE</small></div><button class="sidebar-close" id="sidebar-close" aria-label="关闭菜单">×</button></div><div class="sidebar-scroll" id="sidebar-navigation"></div><div class="foot">组织隔离 · 精细授权<br>SQL Server 报表工作台</div></aside><button class="sidebar-scrim" id="sidebar-scrim" aria-label="关闭侧边菜单" tabindex="-1"></button><main><header class="top"><div class="top-left"><button id="menu-toggle" aria-controls="sidebar">☰</button><span class="breadcrumb">工作空间 / <b id="breadcrumb">报表中心</b></span></div><div class="right"><span class="chip">组织 · ${esc(state.account.name)}</span><span class="user-name">${esc(state.user.name)}</span><button id="change-password">修改密码</button><button id="logout">退出登录</button></div></header><div class="content" id="content"></div></main></div>`;
  $('#menu-toggle').onclick = () => setSidebarCollapsed(!sidebarCollapsed);
  $('#sidebar-close').onclick = $('#sidebar-scrim').onclick = () => setSidebarCollapsed(true);
  setSidebarCollapsed(sidebarCollapsed, false);
  drawSidebar();
  $('#logout').onclick = async () => {
    if (!await ask('退出登录', '确定退出当前账套并返回登录页吗？', '退出登录', true)) return;
    try { await api('/logout', 'POST', {}); login(); } catch (error) { toast(error.message); }
  };
  $('#change-password').onclick = changePassword;
}

function setSidebarCollapsed(collapsed, persist = true) {
  sidebarCollapsed = collapsed;
  $('.layout').classList.toggle('sidebar-collapsed', collapsed);
  const button = $('#menu-toggle');
  button.setAttribute('aria-expanded', String(!collapsed));
  button.title = collapsed ? '展开左侧菜单' : '收起左侧菜单';
  button.setAttribute('aria-label', button.title);
  if (persist) try { localStorage.setItem('report-platform:sidebar-collapsed', String(collapsed)); } catch {}
}

function drawSidebar() {
  const host = $('#sidebar-navigation');
  const opened = new Map([...host.querySelectorAll('.report-menu-group')].map(group => [group.dataset.group, group.open]));
  const groups = new Map();
  // Bootstrap already excludes disabled and unauthorized reports on the server.
  for (const report of state.reports.filter(report => report.showInMenu && report.actions.includes('view'))) {
    const name = report.category?.trim() || '业务报表';
    if (!groups.has(name)) groups.set(name, []);
    groups.get(name).push(report);
  }
  host.innerHTML = `<nav class="nav" aria-label="平台功能">${Object.keys(names).filter(key => state.admin || key === 'reports').map(key => `<button data-nav="${key}" title="${names[key]}" aria-label="${names[key]}"><span class="nav-icon" aria-hidden="true">${icons[key]}</span><span class="nav-label">${names[key]}</span></button>`).join('')}</nav>${groups.size ? `<div class="sidebar-section-title">快捷报表</div><nav class="nav report-menu" aria-label="报表快捷菜单">${[...groups].sort(([a], [b]) => a.localeCompare(b, 'zh-CN')).map(([name, reports]) => `<details class="report-menu-group" data-group="${esc(name)}" ${opened.get(name) !== false ? 'open' : ''}><summary title="${esc(name)}" aria-label="${esc(name)}"><span class="group-initial" aria-hidden="true">${esc([...name][0])}</span><span class="nav-label">${esc(name)}</span><span class="group-count">${reports.length}</span></summary>${reports.map(report => `<button data-menu-report="${esc(report.id)}" title="${esc(name + ' / ' + report.name)}" aria-label="${esc(report.name)}"><span class="nav-icon report-initial" aria-hidden="true">${reportIcon(report, [...report.name][0] || '▦')}</span><span class="nav-label">${esc(report.name)}</span></button>`).join('')}</details>`).join('')}</nav>` : ''}`;
  host.querySelectorAll('[data-nav]').forEach(button => button.onclick = () => {
    view = button.dataset.nav; currentReport = null; render();
    if (window.matchMedia('(max-width: 800px)').matches) setSidebarCollapsed(true, false);
  });
  host.querySelectorAll('[data-menu-report]').forEach(button => button.onclick = () => {
    openReport(button.dataset.menuReport);
    if (window.matchMedia('(max-width: 800px)').matches) setSidebarCollapsed(true, false);
  });
  updateNavigation();
}

function updateNavigation() {
  document.querySelectorAll('[data-nav]').forEach(button => {
    const active = !currentReport && (button.dataset.nav === view || view === 'report-config' && button.dataset.nav === 'reports');
    button.classList.toggle('active', active);
    if (active) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
  });
  document.querySelectorAll('[data-menu-report]').forEach(button => {
    const active = button.dataset.menuReport === currentReport?.id;
    button.classList.toggle('active', active);
    if (active) { button.setAttribute('aria-current', 'page'); button.closest('details').open = true; }
    else button.removeAttribute('aria-current');
  });
  $('#breadcrumb').textContent = currentReport?.name || (view === 'report-config' ? '报表中心 / 管理全部报表' : names[view]);
}

function heading(title, subtitle, action = '') { return `<div class="heading"><div><h1>${title}</h1><p>${subtitle}</p></div>${action}</div>`; }

async function render() {
  updateNavigation();
  try {
    if (view === 'reports') return reports();
    if (view === 'report-config') return await manage('reports');
    if (view === 'audit') return auditPage();
    return manage(view);
  } catch (error) { toast(error.message); }
}

function reports() {
  leaveReportView();
  view = 'reports'; currentReport = null; updateNavigation();
  const content = $('#content');
  content.innerHTML = heading('报表中心', '在当前组织内查看、筛选、分页浏览和导出业务数据。', state.admin ? '<button class="primary" id="new-report">＋ 新建报表</button>' : '') + `<div class="card compact-toolbar"><input id="search" class="grow" placeholder="搜索报表名称或说明" aria-label="搜索报表"><span class="muted">${state.reports.length} 个可用报表</span>${state.admin ? '<button id="configure">管理全部报表</button>' : ''}</div><div id="report-list"></div>`;
  const draw = () => {
    const term = $('#search').value.toLowerCase();
    const reports = state.reports.filter(report => `${report.name} ${report.description || ''}`.toLowerCase().includes(term));
    $('#report-list').innerHTML = reports.length ? `<div class="report-grid">${reports.map(report => `<article class="report-card"><div class="icon" aria-hidden="true">${reportIcon(report)}</div><div class="report-card-info"><h2 title="${esc(report.name)}">${esc(report.name)}</h2><p title="${esc(report.description || '')}">${esc(report.category || '业务报表')} · ${report.fields.length} 个字段${report.description ? ` · ${esc(report.description)}` : ''}</p></div><div class="report-card-actions"><button data-open="${esc(report.id)}">打开 →</button>${report.actions.includes('edit') ? `<button data-edit="${esc(report.id)}">设置</button>` : ''}</div></article>`).join('')}</div>` : `<div class="card empty"><div class="icon">▦</div><h2>${term ? '未找到匹配的报表' : '还没有可用报表'}</h2><p>${state.admin ? '先添加数据源，再创建报表并配置字段与角色权限。' : '请联系管理员为您的角色分配报表权限。'}</p></div>`;
    document.querySelectorAll('[data-open]').forEach(button => button.onclick = () => openReport(button.dataset.open));
    document.querySelectorAll('[data-edit]').forEach(button => button.onclick = async () => edit('reports', await api('/admin/reports/' + button.dataset.edit)));
  };
  draw(); $('#search').oninput = draw;
  if (state.admin) { $('#new-report').onclick = () => edit('reports'); $('#configure').onclick = () => { view = 'report-config'; render(); }; }
}

function leaveReportView() {
  reportRequestVersion++;
  $('#content')?.classList.remove('report-view');
  $('.layout')?.classList.remove('report-mode');
}

function allFilterTemplates() {
  return [...(currentReport.filterTemplates || []).map(t => ({ ...t, id: 'preset:' + t.id, scope: 'preset', isDefault: t.id === currentReport.defaultFilterTemplateId })), ...personalTemplates];
}

function updateReportToolbar() {
  const selector = $('#report-template'); if (!selector) return;
  const templates = allFilterTemplates();
  selector.innerHTML = templateOptions(templates, selectedTemplateId);
  const selected = templates.find(t => t.id === selectedTemplateId);
  $('#edit-filters').textContent = `过滤条件 (${filters.length})`;
  $('#filter-summary').textContent = `${selected ? selected.name + ' · ' : ''}${filters.length ? filters.length + ' 个条件同时满足' : '当前组织的全部数据'}`;
  $('#filter-summary').title = filters.map(f => `${currentReport.fields.find(field => field.key === f.field)?.label || f.field}：${f.value ?? ''}`).join('；');
}

function applyReportFilters(value, templateId = '') {
  filters = copyFilters(value); selectedTemplateId = templateId;
  reportRequestVersion++; queryState = { ...queryState, page: 1, total: 0, queried: false };
  $('#results').className = 'empty'; $('#results').innerHTML = '<p>条件已就绪，点击「查询数据」查看结果。</p>';
  $('#pager').innerHTML = ''; $('#result-meta').textContent = '等待查询'; updateReportToolbar();
}

function openReport(id) {
  currentReport = state.reports.find(report => report.id === id);
  if (!currentReport) { toast('报表已停用或您没有查看权限。'); reports(); return; }
  const report = currentReport;
  view = 'reports'; updateNavigation(); reportRequestVersion++;
  personalTemplates = []; queryState = { page: 1, pageSize: 50, total: 0, queried: false };
  const defaultTemplate = (report.filterTemplates || []).find(t => t.id === report.defaultFilterTemplateId);
  filters = copyFilters(defaultTemplate?.filters); selectedTemplateId = defaultTemplate ? 'preset:' + defaultTemplate.id : '';
  $('#content').classList.add('report-view'); $('.layout').classList.add('report-mode');
  $('#content').innerHTML = `<section class="card report-commandbar"><div class="report-command-row"><button id="back" class="report-back" title="返回报表中心" aria-label="返回报表中心">←</button><div class="report-identity"><h1 title="${esc(report.name)}">${esc(report.name)}</h1><small title="${esc(report.description || '')}">组织：${esc(state.account.name)}</small></div><div class="report-query-actions"><select id="report-template" aria-label="过滤模板"></select><button id="edit-filters">过滤条件</button><button id="reset">${defaultTemplate ? '恢复默认' : '清空条件'}</button><button id="query" class="primary" ${report.actions.includes('query') ? '' : 'disabled'}>查询数据</button>${report.actions.includes('export') ? '<button id="export">↓ 导出 CSV</button>' : ''}${report.actions.includes('edit') ? '<button id="report-settings">设置</button>' : ''}</div></div><div id="filter-summary" class="report-filter-summary"></div></section><section class="card result-card"><div class="results-head"><h2>查询结果</h2><small id="result-meta">等待查询</small></div><div id="results" class="empty"><p>条件已就绪，点击「查询数据」查看结果。</p></div><div id="pager"></div></section>`;
  $('#back').onclick = reports;
  $('#reset').onclick = () => applyReportFilters(defaultTemplate?.filters, defaultTemplate ? 'preset:' + defaultTemplate.id : '');
  $('#query').onclick = () => runReport(false, 1);
  if ($('#export')) $('#export').onclick = () => runReport(true);
  if ($('#report-settings')) $('#report-settings').onclick = async () => {
    try { await edit('reports', await api('/admin/reports/' + report.id)); } catch (ex) { toast(ex.message); }
  };
  $('#report-template').onchange = event => {
    const template = allFilterTemplates().find(t => t.id === event.target.value);
    applyReportFilters(template?.filters || filters, template?.id || '');
  };
  $('#edit-filters').onclick = async () => {
    await templatesReady; if (currentReport !== report) return;
    const result = await editFilterDialog({
      fields: report.fields, filters, templates: allFilterTemplates(), templateId: selectedTemplateId, organization: '当前组织：' + state.account.name,
      onSave: report.actions.includes('query') ? async (name, draft) => {
        const saved = await api(`/reports/${report.id}/filter-templates`, 'POST', { name, filters: draft });
        const template = { ...saved, id: 'personal:' + saved.id, scope: 'personal' };
        personalTemplates.push(template); updateReportToolbar(); toast('个人模板已保存。'); return template;
      } : undefined,
      onDelete: async template => {
        if (!await ask('删除个人模板', `确定删除“${template.name}”吗？`, '删除', true)) return false;
        await api(`/reports/${report.id}/filter-templates/${encodeURIComponent(template.id.slice(9))}`, 'DELETE');
        personalTemplates = personalTemplates.filter(t => t.id !== template.id);
        if (selectedTemplateId === template.id) selectedTemplateId = '';
        updateReportToolbar(); return true;
      }
    });
    if (result && currentReport === report) applyReportFilters(result.filters, result.templateId);
  };
  updateReportToolbar();
  templatesReady = api(`/reports/${report.id}/filter-templates`).then(result => {
    if (currentReport !== report) return;
    personalTemplates = result.templates.map(t => ({ ...t, id: 'personal:' + t.id, scope: 'personal' })); updateReportToolbar();
  }).catch(ex => { if (currentReport === report) toast('个人模板加载失败：' + ex.message); });
}

function table(columns, rows) { return `<div class="table-wrap"><table><thead><tr>${columns.map(column => `<th>${esc(column.label || column.key)}</th>`).join('')}</tr></thead><tbody>${rows.map(row => `<tr>${columns.map(column => `<td>${esc(row[column.key] ?? '—')}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`; }

function drawPager() {
  const host = $('#pager');
  if (!queryState.queried) { host.innerHTML = ''; return; }
  const pages = Math.max(1, Math.ceil(queryState.total / queryState.pageSize));
  host.innerHTML = `<div class="pager"><label>每页<select id="page-size">${[20, 50, 100, 200, 500, 1000].map(size => `<option value="${size}" ${size === queryState.pageSize ? 'selected' : ''}>${size}</option>`).join('')}</select>条</label><span class="muted">共 ${queryState.total.toLocaleString()} 条 · 第 ${queryState.page} / ${pages} 页</span><span class="grow"></span><button id="prev-page" ${queryState.page <= 1 ? 'disabled' : ''}>上一页</button><button id="next-page" ${queryState.page >= pages ? 'disabled' : ''}>下一页</button></div>`;
  $('#page-size').onchange = event => { queryState.pageSize = Number(event.target.value); runReport(false, 1); };
  $('#prev-page').onclick = () => runReport(false, queryState.page - 1);
  $('#next-page').onclick = () => runReport(false, queryState.page + 1);
}

async function runReport(exporting, page = queryState.page) {
  const button = $(exporting ? '#export' : '#query'); button.disabled = true;
  const report = currentReport, requestFilters = copyFilters(filters);
  const version = exporting ? reportRequestVersion : ++reportRequestVersion;
  try {
    if (exporting) {
      const response = await fetch(`/api/reports/${report.id}/export`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'ReportPlatform' }, body: JSON.stringify({ filters: requestFilters }) });
      if (!response.ok) throw Error((await response.json()).error || '导出失败');
      const url = URL.createObjectURL(await response.blob()); const link = document.createElement('a');
      link.href = url; link.download = `${report.name}.csv`; link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000); toast('已开始下载全部筛选结果。');
    } else {
      const result = await api(`/reports/${report.id}/query`, 'POST', { filters: requestFilters, page, pageSize: queryState.pageSize });
      if (version !== reportRequestVersion || currentReport !== report) return;
      queryState = { page: result.page, pageSize: result.pageSize, total: result.total, queried: true };
      $('#results').className = ''; $('#results').innerHTML = result.rows.length ? table(currentReport.fields, result.rows) : '<div class="empty">没有符合条件的数据</div>';
      $('#result-meta').textContent = `${result.total.toLocaleString()} 条结果 · 本页 ${result.rows.length} 条 · ${result.duration} ms`;
      drawPager();
    }
  } catch (error) {
    if (!exporting && (version !== reportRequestVersion || currentReport !== report)) return;
    toast(error.message);
    if (!exporting) { $('#results').className = ''; $('#results').innerHTML = `<p class="error" role="alert">${esc(error.message)}</p>`; $('#result-meta').textContent = '查询失败'; $('#pager').innerHTML = ''; }
  } finally { button.disabled = false; }
}

async function changePassword() {
  const dialog = document.createElement('dialog');
  dialog.innerHTML = `<form id="password-form"><div class="dialog-head"><h2>修改密码</h2><button type="button" id="close">×</button></div><p class="note">新密码至少 5 位。修改后会退出所有已登录设备，请用新密码重新登录。</p><label>当前密码<input name="currentPassword" type="password" required autocomplete="current-password"></label><label>新密码<input name="newPassword" type="password" required minlength="5" maxlength="256" autocomplete="new-password"></label><label>确认新密码<input name="confirmPassword" type="password" required minlength="5" maxlength="256" autocomplete="new-password"></label><p id="password-error" class="error"></p><div class="dialog-actions"><button type="button" id="cancel">取消</button><button class="primary">保存新密码</button></div></form>`;
  document.body.append(dialog); dialog.showModal();
  const close = () => { dialog.close(); dialog.remove(); };
  $('#close', dialog).onclick = close; $('#cancel', dialog).onclick = close; dialog.oncancel = close;
  $('#password-form', dialog).onsubmit = async event => {
    event.preventDefault(); const data = Object.fromEntries(new FormData(event.target));
    if (data.newPassword !== data.confirmPassword) { $('#password-error', dialog).textContent = '两次输入的新密码不一致。'; return; }
    const button = $('button.primary', dialog); button.disabled = true;
    try { await api('/password', 'POST', data); close(); toast('密码已修改，请使用新密码重新登录。'); login(); }
    catch (error) { $('#password-error', dialog).textContent = error.message; }
    finally { button.disabled = false; }
  };
}

async function manage(type) {
  leaveReportView();
  const list = await api('/admin/' + type);
  $('#content').innerHTML = heading(type === 'reports' ? '报表配置' : names[type], { reports: '维护 SQL、字段名称和发布状态。', users: '管理账号、角色及可访问组织。', roles: '为每个角色配置每张报表的独立操作权限。', accounts: '多个组织可访问相同数据库，并使用组织 ID 限制数据。', connections: '集中维护 SQL Server 连接，密码加密保存。' }[type], '<button class="primary" id="new">＋ 新建</button>') + `<div class="card">${list.length ? `<div class="table-wrap"><table><thead><tr><th>名称</th><th>配置信息</th><th>状态</th><th>操作</th></tr></thead><tbody>${list.map(item => `<tr><td><b>${esc(item.name)}</b></td><td>${esc(type === 'connections' ? `${item.host} / ${item.database}` : type === 'accounts' ? `组织编码：${item.orgId}` : type === 'users' ? item.username : type === 'roles' ? (item.admin ? '所有管理权限' : `${Object.keys(item.permissions || {}).length} 个报表授权`) : item.category || '业务报表')}</td><td><span class="chip">${item.enabled === false ? '已停用' : '已配置'}</span></td><td><button data-edit="${esc(item.id)}">编辑</button>${type === 'connections' ? `<button data-test="${esc(item.id)}">测试连接</button>` : ''}<button class="danger" data-delete="${esc(item.id)}">删除</button></td></tr>`).join('')}</tbody></table></div>` : '<div class="empty"><h2>暂无配置</h2><p>点击右上角「新建」开始配置。</p></div>'}</div>`;
  if (type === 'reports') {
    $('#content').insertAdjacentHTML('afterbegin', '<button class="back" id="manage-back">← 返回报表中心</button>');
    $('#manage-back').onclick = reports;
  }
  $('#new').onclick = () => edit(type);
  document.querySelectorAll('[data-edit]').forEach(button => button.onclick = () => edit(type, list.find(item => item.id === button.dataset.edit)));
  document.querySelectorAll('[data-test]').forEach(button => button.onclick = async () => { button.disabled = true; try { toast((await api(`/admin/connections/${button.dataset.test}/test`, 'POST', {})).message); } catch (error) { toast(error.message); } finally { button.disabled = false; } });
  document.querySelectorAll('[data-delete]').forEach(button => button.onclick = async () => { if (!await ask('删除配置', '删除后无法恢复。确定继续吗？', '删除', true)) return; try { await api(`/admin/${type}/${button.dataset.delete}`, 'DELETE'); toast('已删除配置'); await refresh(); render(); } catch (error) { toast(error.message); } });
}

const input = (label, key, value = '', type = 'text', constraints = '') => `<label>${label}<input name="${key}" type="${type}" value="${esc(value)}" ${type === 'password' ? 'autocomplete="new-password"' : ''} ${constraints} ${label === '登录账号' ? 'required minlength="2" maxlength="80" pattern="[A-Za-z0-9_.@\\-]{2,80}" title="请输入 2–80 位英文字母、数字或 _ . @ -，不能包含中文或空格"' : ''}>${label === '登录账号' ? '<small>例如 zhangsan、user_01；中文姓名请填写在“名称”中。</small>' : ''}</label>`;
const checked = (key, label, value) => `<label class="inline-check"><input type="checkbox" name="${key}" ${value ? 'checked' : ''}>${label}</label>`;

async function prepareReportIcon(file) {
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) throw Error('图标仅支持 PNG、JPG 或 WebP 图片。');
  if (file.size > 5 * 1024 * 1024) throw Error('原图不能超过 5 MB。');
  const bitmap = await createImageBitmap(file);
  if (!bitmap.width || !bitmap.height) { bitmap.close?.(); throw Error('无法读取该图片，请换一张图片。'); }
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
  const context = canvas.getContext('2d');
  const scale = Math.min(64 / bitmap.width, 64 / bitmap.height);
  const width = Math.max(1, Math.round(bitmap.width * scale));
  const height = Math.max(1, Math.round(bitmap.height * scale));
  context.drawImage(bitmap, Math.round((64 - width) / 2), Math.round((64 - height) / 2), width, height);
  bitmap.close?.();
  const result = canvas.toDataURL('image/webp', .9);
  if (!result.startsWith('data:image/webp;base64,')) throw Error('图片处理失败，请换一张图片。');
  return result;
}

async function edit(type, item = {}) {
  try {
    const deps = {};
    let reportIconValue = item.icon || '';
    if (type === 'users') [deps.roles, deps.accounts] = await Promise.all([api('/admin/roles'), api('/admin/accounts')]);
    if (type === 'roles') deps.reports = await api('/admin/reports');
    if (type === 'reports') deps.connections = state.admin ? await api('/admin/connections') : [{ id: item.connectionId, name: '当前数据源' }];
    let extra = '';
    if (type === 'connections') extra = input('服务器地址', 'host', item.host) + input('端口', 'port', item.port || 1433, 'number') + input('数据库名', 'database', item.database) + input('数据库账号（仅授予 SELECT）', 'username', item.username) + input(item.id ? '新密码（留空保留）' : '数据库密码', 'password', '', 'password') + checked('trustCertificate', '信任内网自签证书', item.trustCertificate);
    if (type === 'accounts') extra = input('组织编码', 'orgId', item.orgId) + checked('enabled', '启用组织', item.enabled !== false);
    if (type === 'users') extra = input('登录账号', 'username', item.username) + input(item.id ? '重置密码（留空保留，至少 5 位）' : '初始密码（至少 5 位）', 'password', '', 'password', 'minlength="5" maxlength="256"') + `<div class="full">角色<div class="checks">${deps.roles.map(role => checked(`role:${role.id}`, esc(role.name), item.roleIds?.includes(role.id))).join('')}</div>可登录组织<div class="checks">${deps.accounts.map(account => checked(`account:${account.id}`, esc(account.name), item.accountIds?.includes(account.id))).join('')}</div></div>` + checked('enabled', '启用用户', item.enabled !== false);
    if (type === 'roles') extra = `<div class="full">${item.admin ? '<p class="note">内置管理员拥有全部权限。</p>' : `<p class="note">查看控制报表可见性；查询、导出、配置分别授权。SQL 配置权限只应授予可信人员。</p><div class="table-wrap"><table><thead><tr><th>报表</th><th>查看</th><th>查询</th><th>导出</th><th>配置 SQL / 字段</th></tr></thead><tbody>${deps.reports.map(report => `<tr><td>${esc(report.name)}</td>${['view', 'query', 'export', 'edit'].map(action => `<td><input type="checkbox" name="perm:${report.id}:${action}" ${item.permissions?.[report.id]?.includes(action) ? 'checked' : ''}></td>`).join('')}</tr>`).join('')}</tbody></table></div>`}</div>`;
    if (type === 'reports') extra = input('分组', 'category', item.category || '业务报表') + `<label>数据源<select name="connectionId">${deps.connections.map(source => `<option value="${esc(source.id)}" ${source.id === item.connectionId ? 'selected' : ''}>${esc(source.name)}</option>`).join('')}</select></label>` + input('说明', 'description', item.description) + input('组织字段列名（可留空）', 'orgColumn', item.orgColumn ?? '') + `<div class="note full org-column-note">填写 TenantId 等 SQL 返回列名时按当前账套过滤；留空则显示所有组织的数据。</div><div class="full report-icon-setting"><label>报表图标（可选）</label><div class="report-icon-editor"><div id="report-icon-preview" class="report-icon-preview" aria-hidden="true">${reportIcon(item)}</div><label class="report-icon-upload">选择图片<input id="report-icon-file" type="file" accept="image/png,image/jpeg,image/webp"></label><button type="button" id="remove-report-icon">移除图标</button></div><small>用于左侧快捷菜单和报表卡片。支持 PNG、JPG、WebP，上传后自动缩放。</small></div><label class="full">SQL 查询<textarea name="sql" class="sql" spellcheck="false" placeholder="SELECT TenantId, MoCode AS 工单编号 FROM dbo.IcsMo">${esc(item.sql || '')}</textarea></label><div class="note full">使用单条 SELECT，不带分号、注释或 ORDER BY。点击“获取字段”只读取 SQL 的字段结构，不读取业务记录；带 AS 的别名会自动写入显示名称，其他字段需您补充名称。</div><div class="full"><div class="results-head"><h2>字段与显示名称</h2><div class="toolbar"><button type="button" id="fetch-fields">获取字段</button><button type="button" id="add-field">＋ 添加字段</button></div></div><div id="field-rows"></div></div>` + checked('enabled', '发布并启用报表', item.enabled !== false);
    if (type === 'reports') extra += `<div class="full menu-setting">${checked('showInMenu', '显示在左侧菜单', item.showInMenu === true)}<small>按上方“分组”归类；仅有查看权限的用户可见，查询与导出沿用角色按钮权限。</small></div>`;
    if (type === 'reports') extra += '<section class="full preset-settings" id="report-filter-templates"></section>';
    const dialog = document.createElement('dialog');
    dialog.innerHTML = `<form id="editor"><div class="dialog-head"><h2>${item.id ? '编辑' : '新建'}${type === 'reports' ? '报表' : names[type].replace('管理', '')}</h2><button type="button" id="close">×</button></div><div class="form-grid">${input('名称', 'name', item.name)}${extra}</div><p class="error" id="form-error"></p><div class="dialog-actions"><button type="button" id="cancel">取消</button><button class="primary" type="submit">保存配置</button></div></form>`;
    document.body.append(dialog); dialog.showModal();
    const close = () => { dialog.close(); dialog.remove(); };
    $('#close', dialog).onclick = close; $('#cancel', dialog).onclick = close; dialog.oncancel = close;
    const fields = (item.fields || [{ key: '', label: '' }]).map(field => ({ ...field }));
    const templateSettings = type === 'reports' ? mountTemplateSettings($('#report-filter-templates', dialog), {
      templates: item.filterTemplates || [], defaultId: item.defaultFilterTemplateId || '', getFields: () => fields,
      confirmDelete: () => ask('删除报表预设', '删除的预设将在保存报表配置后生效。确定继续吗？', '删除', true)
    }) : null;
    const drawFields = () => {
      $('#field-rows', dialog).innerHTML = fields.map((field, index) => `<div class="field-row"><input data-field="${index}" data-key="key" value="${esc(field.key)}" maxlength="128" placeholder="SQL 返回列名 / 别名" aria-label="字段名"><input data-field="${index}" data-key="label" value="${esc(field.label)}" placeholder="显示名称（没有 AS 时填写）" aria-label="显示名称"><button type="button" data-del="${index}">×</button></div>`).join('');
      $('#field-rows', dialog).querySelectorAll('[data-field]').forEach(element => element.oninput = () => fields[element.dataset.field][element.dataset.key] = element.value);
      $('#field-rows', dialog).querySelectorAll('[data-del]').forEach(button => button.onclick = () => { fields.splice(Number(button.dataset.del), 1); drawFields(); });
    };
    if (type === 'reports') {
      drawFields();
      const drawReportIcon = () => $('#report-icon-preview', dialog).innerHTML = reportIcon({ icon: reportIconValue });
      $('#report-icon-file', dialog).onchange = async event => {
        const file = event.target.files?.[0]; if (!file) return;
        $('#form-error', dialog).textContent = '';
        try { reportIconValue = await prepareReportIcon(file); drawReportIcon(); }
        catch (error) { $('#form-error', dialog).textContent = error.message; }
        finally { event.target.value = ''; }
      };
      $('#remove-report-icon', dialog).onclick = () => { reportIconValue = ''; drawReportIcon(); };
      $('#add-field', dialog).onclick = () => { fields.push({ key: '', label: '' }); drawFields(); };
      $('#fetch-fields', dialog).onclick = async event => {
        const form = $('#editor', dialog); const button = event.currentTarget; button.disabled = true; $('#form-error', dialog).textContent = '';
        try {
          const result = await api('/admin/reports/preview-fields', 'POST', { connectionId: form.elements.connectionId.value, sql: form.elements.sql.value });
          fields.splice(0, fields.length, ...result.fields.map(field => ({ key: field.key, label: field.label || '' }))); drawFields();
          const aliases = result.fields.filter(field => field.label).length; toast(`已获取 ${fields.length} 个字段，其中 ${aliases} 个已带入 AS 别名。`);
        } catch (error) { $('#form-error', dialog).textContent = error.message; }
        finally { button.disabled = false; }
      };
    }
    $('#editor', dialog).onsubmit = async event => {
      event.preventDefault(); const form = event.target; const data = { ...item, ...Object.fromEntries(new FormData(form)) };
      for (const key of ['enabled', 'trustCertificate', 'showInMenu']) if (form.elements[key]) data[key] = form.elements[key].checked;
      if (type === 'users') { data.roleIds = deps.roles.filter(role => form.elements[`role:${role.id}`].checked).map(role => role.id); data.accountIds = deps.accounts.filter(account => form.elements[`account:${account.id}`].checked).map(account => account.id); }
      if (type === 'roles') { data.permissions = {}; for (const report of deps.reports) { const actions = ['view', 'query', 'export', 'edit'].filter(action => form.elements[`perm:${report.id}:${action}`]?.checked); if (actions.length) data.permissions[report.id] = actions; } }
      if (type === 'reports') { data.fields = fields; data.icon = reportIconValue; Object.assign(data, templateSettings.value()); }
      Object.keys(data).filter(key => key.includes(':')).forEach(key => delete data[key]);
      const button = $('[type="submit"]', dialog); button.disabled = true;
      try {
        const reopen = type === 'reports' && currentReport?.id === item.id;
        await api(`/admin/${type}${item.id ? '/' + item.id : ''}`, item.id ? 'PUT' : 'POST', data);
        close(); toast('配置已保存'); await refresh(); if (reopen) openReport(item.id); else render();
      }
      catch (error) { $('#form-error', dialog).textContent = error.message; }
      finally { button.disabled = false; }
    };
  } catch (error) { toast(error.message); }
}

async function refresh() { state = await api('/bootstrap'); drawSidebar(); }

async function auditPage() {
  leaveReportView();
  const today = new Date(Date.now() + 28800000).toISOString().slice(0, 10);
  $('#content').innerHTML = heading('查询审计', '按北京时间统计每日报表使用情况，包含成功与失败的查询。') + `<div class="card compact-toolbar"><label>统计日期<input type="date" id="date" value="${today}"></label><button id="load-audit">刷新统计</button></div><div id="audit-data"></div>`;
  const load = async () => { try { const result = await api('/audit?date=' + $('#date').value); $('#audit-data').innerHTML = `<div class="metrics"><div class="metric">查询次数<strong>${result.summary.reduce((sum, item) => sum + item.queries, 0)}</strong></div><div class="metric">导出次数<strong>${result.summary.reduce((sum, item) => sum + item.exports, 0)}</strong></div><div class="metric">失败次数<strong>${result.summary.reduce((sum, item) => sum + item.failures, 0)}</strong></div></div><div class="card"><h2>用户 × 报表 × 组织</h2>${result.summary.length ? table([{ key: 'username', label: '用户' }, { key: 'reportName', label: '报表' }, { key: 'accountId', label: '组织 ID' }, { key: 'queries', label: '查询次数' }, { key: 'exports', label: '导出次数' }, { key: 'failures', label: '失败次数' }], result.summary) : '<p>当天暂无查询记录。</p>'}</div>`; } catch (error) { toast(error.message); } };
  $('#load-audit').onclick = load; $('#date').onchange = load; await load();
}

boot();
