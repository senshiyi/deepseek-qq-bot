"use strict";

// This page shares authentication and request ownership with the main console.
window.createCompanionPage = ({ state, $, api, message, markDirty, confirmDiscard, element, number, date }) => {
  function writeForm(value) {
    $("companion-audit").checked = value.audit !== false;
    $("companion-enabled").checked = value.enabled === true;
    $("companion-qzone").checked = value.qzone === true;
    $("companion-hour").value = value.auditHour ?? 4;
    $("companion-idle").value = value.idleMinutes ?? 120;
    $("companion-cooldown").value = value.cooldownMinutes ?? 120;
    $("companion-limit").value = value.dailyLimit ?? 4;
  }
  function readForm() {
    return { audit: $("companion-audit").checked, enabled: $("companion-enabled").checked, qzone: $("companion-qzone").checked,
      auditHour: Number($("companion-hour").value), idleMinutes: Number($("companion-idle").value),
      cooldownMinutes: Number($("companion-cooldown").value), dailyLimit: Number($("companion-limit").value) };
  }
  function updateControls() {
    const busy = state.loading.companion || state.saving.companion;
    $("companion-fields").disabled = busy || !state.companion;
    $("companion-save").disabled = busy || !state.companion || !state.dirty.companion;
    $("companion-retry").disabled = busy;
    $("companion-refresh").disabled = busy;
    for (const button of $("companion-list").querySelectorAll("button")) button.disabled = busy;
  }
  function reset() {
    state.companionRequest++;
    state.companion = null;
    state.loading.companion = false;
    state.saving.companion = false;
    markDirty("companion", false);
    $("companion-form").reset();
    $("companion-list").replaceChildren();
    $("companion-contact").replaceChildren();
    $("companion-levels").hidden = true;
    state.companionContact = "all";
    state.companionLevel = "all";
    $("companion-empty").hidden = false;
    $("companion-dirty").textContent = "未读取";
    message("companion-message");
    updateControls();
  }
  async function load(force = false) {
    if (state.loading.companion || state.saving.companion) return;
    if (force && !confirmDiscard("companion")) return;
    if (state.companion && !force) return;
    const session = state.session, request = ++state.companionRequest;
    state.loading.companion = true;
    updateControls();
    message("companion-message", "正在读取心情与日记…");
    try {
      const value = await api("/api/companion");
      if (session !== state.session || request !== state.companionRequest) return;
      state.companion = value;
      writeForm(value.settings || {});
      render(value);
      markDirty("companion", false);
      message("companion-message", "已读取；设置保存后立即生效。");
    } catch (error) {
      if (session === state.session && request === state.companionRequest)
        message("companion-message", `${error.message} 点击“重新读取”重试。`, "error");
    } finally {
      if (session === state.session && request === state.companionRequest) {
        state.loading.companion = false;
        updateControls();
      }
    }
  }
  async function save(event) {
    event.preventDefault();
    if (!state.companion || state.loading.companion || state.saving.companion || !$("companion-form").reportValidity()) return;
    const session = state.session, request = ++state.companionRequest;
    const settings = readForm();
    state.saving.companion = true;
    updateControls();
    message("companion-message", "正在保存…");
    try {
      await api("/api/companion-settings", { method: "PUT", body: settings });
      if (session !== state.session || request !== state.companionRequest) return;
      state.companion.settings = settings;
      markDirty("companion", false);
      message("companion-message", "已保存并立即生效；不需要应用设置。", "success");
    } catch (error) {
      if (session === state.session && request === state.companionRequest) message("companion-message", error.message, "error");
    } finally {
      if (session === state.session && request === state.companionRequest) {
        state.saving.companion = false;
        updateControls();
      }
    }
  }
  const LEVELS = ["normal", "important", "vital"];
  // 日记筛选：先按联系人，再按重要性；两者都在本地过滤已加载的数据，不重新请求。
  function render(value) {
    const contacts = Array.isArray(value.contacts) ? value.contacts : [];
    const select = $("companion-contact");
    const chosen = contacts.some(item => String(item.userId) === state.companionContact) ? state.companionContact : "all";
    state.companionContact = chosen;
    select.replaceChildren(element("option", "全部联系人"));
    select.firstChild.value = "all";
    for (const item of contacts) {
      const option = element("option", `QQ ${item.userId}`);
      option.value = String(item.userId);
      select.append(option);
    }
    select.value = chosen;
    select.disabled = contacts.length === 0;
    const shown = chosen === "all" ? contacts : contacts.filter(item => String(item.userId) === chosen);
    const counts = { normal: 0, important: 0, vital: 0 };
    for (const item of shown)
      for (const entry of Array.isArray(item.entries) ? item.entries : [])
        if (LEVELS.includes(entry.level)) counts[entry.level] += 1;
    const total = counts.normal + counts.important + counts.vital;
    const level = total > 0 && (state.companionLevel === "all" || counts[state.companionLevel] > 0) ? state.companionLevel : "all";
    state.companionLevel = level;
    const chips = $("companion-levels");
    chips.hidden = total === 0;
    for (const button of chips.querySelectorAll("[data-companion-level]")) {
      const key = button.getAttribute("data-companion-level");
      const label = key === "all" ? "全部" : key === "normal" ? "普通" : key === "important" ? "重要" : "非常重要";
      button.textContent = `${label} ${key === "all" ? total : counts[key]}`;
      const active = key === level;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
    }
    const list = $("companion-list");
    list.replaceChildren();
    $("companion-empty").hidden = contacts.length > 0;
    for (const item of shown) {
      const card = element("article", null, "panel");
      const heading = element("div", null, "panel-heading");
      const summary = element("div");
      const entries = Array.isArray(item.entries) ? item.entries : [];
      const own = { normal: 0, important: 0, vital: 0 };
      for (const entry of entries) if (LEVELS.includes(entry.level)) own[entry.level] += 1;
      summary.append(element("h3", `QQ ${item.userId}`), element("p", item.mood ? `当前心情：${item.mood}` : "当前心情：未记录", "muted"),
        element("p", `日记 ${entries.length} 条 · 普通 ${own.normal} / 重要 ${own.important} / 非常重要 ${own.vital}`, "field-help"));
      heading.append(summary); card.append(heading);
      const details = element("dl", null, "connection-details");
      for (const [label, shown] of [["上次整理", item.lastAuditAt ? date(item.lastAuditAt) : "未整理"],
        ["上次主动", item.lastWakeupAt ? date(item.lastWakeupAt) : "未主动"], ["今天已主动", `${number(item.wakeupsToday)} 次`]]) {
        const wrap = element("div"); wrap.append(element("dt", label), element("dd", shown)); details.append(wrap);
      }
      card.append(details);
      // 好感度 / 占有欲：进度条显示当前值，右边可直接改数字保存；日记整理也会按当天感受微调。
      const meters = element("div", null, "companion-meters");
      // 好感度 -1000～1000（负数=厌烦），占有欲 0～100；进度条可以直接拖，右边也能输入精确值。
      for (const [key, label, value, minimum, maximum] of [
        ["affection", "好感度", item.affection, -1000, 1000],
        ["possession", "占有欲", item.possession, 0, 100]
      ]) {
        const current = Math.min(maximum, Math.max(minimum, Number(value) || 0));
        const row = element("div", null, "companion-meter");
        const tag = element("span", `${label} ${current}`, "companion-meter-label");
        const slider = element("input", null, "companion-meter-slider");
        slider.type = "range"; slider.min = String(minimum); slider.max = String(maximum); slider.step = "1";
        slider.value = String(current); slider.setAttribute("aria-label", `${label}进度（${minimum}～${maximum}）`); slider.dataset.meter = key;
        const number = element("input", null, "companion-meter-number");
        number.type = "number"; number.min = String(minimum); number.max = String(maximum); number.step = "1";
        number.value = String(current); number.setAttribute("aria-label", `${label}（${minimum}～${maximum}）`); number.dataset.meter = key;
        slider.addEventListener("input", () => {
          number.value = slider.value;
          tag.textContent = `${label} ${slider.value}`;
        });
        number.addEventListener("input", () => {
          const typed = Math.min(maximum, Math.max(minimum, Number(number.value) || 0));
          slider.value = String(typed);
          tag.textContent = `${label} ${typed}`;
        });
        row.append(tag, slider, number);
        meters.append(row);
      }
      const saveMeters = element("button", "保存数值", "button compact"); saveMeters.type = "button";
      saveMeters.addEventListener("click", () => saveMetersFor(item.userId, card));
      meters.append(saveMeters);
      card.append(meters);
      const visible = level === "all" ? entries : entries.filter(entry => entry.level === level);
      if (!entries.length) card.append(element("p", "还没有日记条目；到整理时间后由模型写入。", "field-help"));
      else if (!visible.length) card.append(element("p", `没有「${level === "normal" ? "普通" : level === "important" ? "重要" : "非常重要"}」的日记条目。`, "field-help"));
      for (const entry of visible) {
        const row = element("div", null, "memory-item"), body = element("div");
        body.append(element("p", `[${entry.levelLabel}] ${entry.text}`));
        const foot = `记录 ${date(entry.createdAt)} · ${entry.expiresAt ? `遗忘于 ${date(entry.expiresAt)}` : "永不遗忘"}`;
        body.append(element("p", foot + (entry.mood ? ` · 当时心情：${entry.mood}` : "") + (entry.unsaid ? ` · 没说出口：${entry.unsaid}` : ""), "field-help"));
        const remove = element("button", "删除", "button compact danger-quiet"); remove.type = "button";
        remove.addEventListener("click", () => removeEntry(item.userId, entry)); row.append(body, remove); card.append(row);
      }
      list.append(card);
    }
  }
  async function saveMetersFor(userId, card) {
    if (state.loading.companion || state.saving.companion) return;
    const values = {};
    for (const input of card.querySelectorAll('input[data-meter][type="number"]')) values[input.dataset.meter] = Number(input.value);
    if (!Number.isInteger(values.affection) || !Number.isInteger(values.possession) ||
      values.affection < -1000 || values.affection > 1000 || values.possession < 0 || values.possession > 100) {
      message("companion-message", "好感度须为 -1000～1000，占有欲须为 0～100 的整数。", "error");
      return;
    }
    state.saving.companion = true; updateControls();
    message("companion-message", "正在保存数值…");
    try {
      const saved = await api(`/api/companion/${userId}/meters`, { method: "PUT", body: values });
      const item = (state.companion?.contacts || []).find(contact => contact.userId === userId);
      if (item) { item.affection = saved.affection; item.possession = saved.possession; }
      if (state.companion) render(state.companion);
      message("companion-message", `已保存：好感度 ${saved.affection}，占有欲 ${saved.possession}。`, "success");
    } catch (error) {
      message("companion-message", error.message, "error");
    } finally {
      state.saving.companion = false; updateControls();
    }
  }

  async function removeEntry(userId, entry) {
    if (state.loading.companion || state.saving.companion || !confirmDiscard("companion") || !window.confirm("删除这条日记？删除后不再参与回复。")) return;
    const session = state.session, request = ++state.companionRequest;
    state.saving.companion = true; updateControls();
    try {
      await api(`/api/companion/${userId}/entries/${entry.id}`, { method: "DELETE" });
      if (session !== state.session || request !== state.companionRequest) return;
      state.companion = null; markDirty("companion", false);
      state.saving.companion = false;
      await load();
    } catch (error) {
      if (session === state.session && request === state.companionRequest) message("companion-message", error.message, "error");
    } finally {
      if (session === state.session && request === state.companionRequest) { state.saving.companion = false; updateControls(); }
    }
  }
  $("companion-contact").addEventListener("change", () => {
    if (!state.companion) return;
    state.companionContact = $("companion-contact").value || "all";
    state.companionLevel = "all";
    render(state.companion);
  });
  for (const button of $("companion-levels").querySelectorAll("[data-companion-level]")) {
    button.addEventListener("click", () => {
      if (!state.companion) return;
      state.companionLevel = button.getAttribute("data-companion-level") || "all";
      render(state.companion);
    });
  }
  $("companion-form").addEventListener("input", () => {
    if (state.loading.companion || state.saving.companion) return;
    markDirty("companion"); message("companion-message", "有未保存的修改。");
  });
  $("companion-form").addEventListener("submit", save);
  $("companion-refresh").addEventListener("click", () => load(true));
  $("companion-retry").addEventListener("click", () => load(true));
  return { load, reset };
};
