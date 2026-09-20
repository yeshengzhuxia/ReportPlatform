const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const clone = value => JSON.parse(JSON.stringify(value));
const operators = { contains: '包含', eq: '等于', ne: '不等于', gt: '大于', gte: '大于等于', lt: '小于', lte: '小于等于', empty: '为空' };

export function templateOptions(templates, selected = '', disableInvalid = true) {
  return '<option value="">自定义条件</option>' + ['preset', 'personal'].map(scope => {
    const items = templates.filter(t => t.scope === scope);
    return items.length ? `<optgroup label="${scope === 'preset' ? '报表预设' : '我的模板'}">${items.map(t => `<option value="${esc(t.id)}" ${t.id === selected ? 'selected' : ''} ${disableInvalid && t.invalidReason ? 'disabled' : ''}>${esc(t.name)}${t.isDefault ? '（默认）' : ''}${t.invalidReason ? '（字段已变更）' : ''}</option>`).join('')}</optgroup>` : '';
  }).join('');
}

function conditionRows(host, fields, initial, changed) {
  let rows = clone(initial || []);
  const draw = () => {
    host.innerHTML = rows.length ? rows.map((row, index) => `<div class="filter" data-row="${index}"><select data-key="field" aria-label="筛选字段">${!fields.some(f => f.key === row.field) ? `<option value="${esc(row.field)}">已失效：${esc(row.field)}</option>` : ''}${fields.map(f => `<option value="${esc(f.key)}" ${f.key === row.field ? 'selected' : ''}>${esc(f.label || f.key)}</option>`).join('')}</select><select data-key="op" aria-label="比较方式">${Object.entries(operators).map(([key, text]) => `<option value="${key}" ${key === row.op ? 'selected' : ''}>${text}</option>`).join('')}</select><input data-key="value" aria-label="筛选值" placeholder="输入筛选值" maxlength="1000" value="${esc(row.value)}" ${row.op === 'empty' ? 'disabled' : ''}><button type="button" data-remove="${index}" aria-label="删除条件">×</button></div>`).join('') : '<div class="filter-dialog-empty">暂无条件，将查询当前组织的全部数据。</div>';
    host.querySelectorAll('[data-key]').forEach(el => {
      const change = () => {
        rows[Number(el.closest('[data-row]').dataset.row)][el.dataset.key] = el.value;
        if (el.dataset.key === 'op') draw();
        changed();
      };
      if (el.tagName === 'INPUT') el.oninput = change; else el.onchange = change;
    });
    host.querySelectorAll('[data-remove]').forEach(button => button.onclick = () => { rows.splice(Number(button.dataset.remove), 1); draw(); changed(); });
  };
  draw();
  return {
    set(value) { rows = clone(value); draw(); },
    add() { if (!fields.length) throw Error('请先配置报表字段。'); if (rows.length >= 50) throw Error('最多添加 50 个条件。'); rows.push({ field: fields[0].key, op: 'contains', value: '' }); draw(); changed(); },
    value() {
      if (rows.some(row => !fields.some(f => f.key === row.field) || !operators[row.op])) throw Error('条件中的字段或比较方式已失效，请修改后再应用。');
      return clone(rows.map(row => ({ ...row, value: row.op === 'empty' ? '' : row.value ?? '' })));
    }
  };
}

export function editFilterDialog(options) {
  return new Promise(resolve => {
    let templates = [...(options.templates || [])], selected = options.templateId || '', busy = false;
    const presetMode = options.name !== undefined;
    const dialog = document.createElement('dialog'); dialog.className = 'filter-dialog';
    dialog.setAttribute('aria-label', options.title || '过滤条件');
    dialog.innerHTML = `<form><div class="dialog-head"><h2>${esc(options.title || '过滤条件')}</h2><button type="button" data-close aria-label="关闭">×</button></div><fieldset><div class="filter-template-tools">${presetMode ? `<label>模板名称<input data-name value="${esc(options.name)}" maxlength="80" required placeholder="例如：未完成工单"></label>` : `<label>选择模板<select data-template aria-label="选择过滤模板"></select></label>${options.onDelete ? '<button type="button" data-delete-template>删除个人模板</button>' : ''}`}</div><div class="filter-dialog-toolbar"><small>以下条件同时满足 · ${esc(options.organization || '当前登录组织')}</small><div><button type="button" data-clear>清空条件</button><button type="button" data-add>＋ 添加条件</button></div></div><div class="filter-dialog-rows"></div>${!presetMode && options.onSave ? '<div class="filter-save-template"><input data-name maxlength="80" placeholder="输入名称，保存为我的模板" aria-label="个人模板名称"><button type="button" data-save-template>保存为我的模板</button><small>仅当前用户和账套可见，保存后下次仍可使用。</small></div>' : ''}</fieldset><p class="error" role="alert" data-error></p><div class="dialog-actions"><button type="button" data-close>取消</button><button type="submit" class="primary">${presetMode ? '保存模板' : '应用条件'}</button></div></form>`;
    document.body.append(dialog);
    const $ = selector => dialog.querySelector(selector);
    const error = text => $('[data-error]').textContent = text;
    const updateTemplates = () => {
      if (presetMode) return;
      $('[data-template]').innerHTML = templateOptions(templates, selected, false);
      const current = templates.find(t => t.id === selected);
      if ($('[data-delete-template]')) $('[data-delete-template]').disabled = current?.scope !== 'personal';
    };
    const rows = conditionRows($('.filter-dialog-rows'), options.fields, options.filters, () => { selected = ''; updateTemplates(); error(''); });
    const close = value => { if (busy) return; dialog.close(); dialog.remove(); resolve(value); };
    const perform = async action => {
      if (busy) return;
      busy = true; $('fieldset').disabled = true; dialog.querySelectorAll('.dialog-actions button, [data-close]').forEach(b => b.disabled = true); error('');
      try { await action(); } catch (ex) { error(ex.message); }
      finally { busy = false; $('fieldset').disabled = false; dialog.querySelectorAll('.dialog-actions button, [data-close]').forEach(b => b.disabled = false); updateTemplates(); }
    };
    dialog.querySelectorAll('[data-close]').forEach(b => b.onclick = () => close(null));
    dialog.oncancel = event => { event.preventDefault(); close(null); };
    $('[data-add]').onclick = () => { try { rows.add(); } catch (ex) { error(ex.message); } };
    $('[data-clear]').onclick = () => { rows.set([]); selected = ''; updateTemplates(); error(''); };
    if (!presetMode) {
      updateTemplates();
      $('[data-template]').onchange = event => {
        selected = event.target.value;
        const template = templates.find(t => t.id === selected);
        if (template) rows.set(template.filters);
        error(template?.invalidReason || ''); updateTemplates();
      };
      if (options.onSave) $('[data-save-template]').onclick = () => perform(async () => {
        const name = $('[data-name]').value.trim(); if (!name) throw Error('请填写个人模板名称。');
        const saved = await options.onSave(name, rows.value()); templates.push(saved); selected = saved.id;
        $('[data-name]').value = ''; updateTemplates();
      });
      if (options.onDelete) $('[data-delete-template]').onclick = () => perform(async () => {
        const template = templates.find(t => t.id === selected);
        if (template?.scope === 'personal' && await options.onDelete(template)) { templates = templates.filter(t => t.id !== selected); selected = ''; updateTemplates(); }
      });
    }
    $('form').onsubmit = event => {
      event.preventDefault(); if (busy) return;
      try {
        const name = presetMode ? $('[data-name]').value.trim() : undefined;
        if (presetMode && !name) throw Error('请填写模板名称。');
        close({ name, filters: rows.value(), templateId: selected });
      } catch (ex) { error(ex.message); }
    };
    dialog.showModal();
  });
}

export function mountTemplateSettings(host, { templates = [], defaultId = '', getFields, confirmDelete }) {
  let presets = clone(templates), selected = defaultId;
  const draw = () => {
    host.innerHTML = `<div class="preset-settings-heading"><div><h2>过滤模板与默认条件</h2><small>报表预设对有查看权限的用户可见；默认模板在打开报表时自动带入。</small></div><button type="button" data-new-preset>＋ 新增模板</button></div><label class="preset-default">默认模板<select data-default aria-label="报表默认模板"><option value="">不设默认（无过滤条件）</option>${presets.map(t => `<option value="${esc(t.id)}" ${t.id === selected ? 'selected' : ''}>${esc(t.name)}</option>`).join('')}</select></label><div class="preset-list">${presets.map(t => `<div class="preset-item"><span title="${esc(t.name)}">${esc(t.name)}</span><small>${t.filters.length} 个条件</small><button type="button" data-edit-preset="${esc(t.id)}">编辑</button><button type="button" data-delete-preset="${esc(t.id)}">删除</button></div>`).join('') || '<small>先配置字段，再添加常用过滤模板。</small>'}</div><p class="error" data-preset-error></p>`;
    host.querySelector('[data-default]').onchange = event => selected = event.target.value;
    const edit = async id => {
      const template = presets.find(t => t.id === id);
      const fields = getFields().filter(f => f.key?.trim());
      if (!fields.length) { host.querySelector('[data-preset-error]').textContent = '请先配置报表字段。'; return; }
      if (!template && presets.length >= 30) { host.querySelector('[data-preset-error]').textContent = '每个报表最多配置 30 个预设模板。'; return; }
      const result = await editFilterDialog({ title: template ? '编辑报表预设' : '新增报表预设', name: template?.name || '', fields, filters: template?.filters || [] });
      if (!result) return;
      if (presets.some(t => t.id !== id && t.name.toLowerCase() === result.name.toLowerCase())) { host.querySelector('[data-preset-error]').textContent = '模板名称不能重复。'; return; }
      const saved = { id: id || globalThis.crypto?.randomUUID?.() || 'template-' + Date.now().toString(36) + Math.random().toString(36).slice(2), name: result.name, filters: result.filters };
      if (template) presets = presets.map(t => t.id === id ? saved : t); else presets.push(saved);
      draw();
    };
    host.querySelector('[data-new-preset]').onclick = () => edit();
    host.querySelectorAll('[data-edit-preset]').forEach(button => button.onclick = () => edit(button.dataset.editPreset));
    host.querySelectorAll('[data-delete-preset]').forEach(button => button.onclick = async () => {
      if (!await confirmDelete()) return;
      presets = presets.filter(t => t.id !== button.dataset.deletePreset);
      if (selected === button.dataset.deletePreset) selected = '';
      draw();
    });
  };
  draw();
  return { value: () => ({ filterTemplates: clone(presets), defaultFilterTemplateId: selected }) };
}
