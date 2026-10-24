"use strict";

// This page shares authentication and request ownership with the main console.
window.createCompanionPage = ({ state, $, api, message, markDirty, confirmDiscard, element, number, date }) => {
  function writeForm(value) {
    $("companion-audit").checked = value.audit !== false;
    $("companion-enabled").checked = value.enabled === true;
    $("companion-hour").value = value.auditHour ?? 4;
    $("companion-idle").value = value.idleMinutes ?? 120;
    $("companion-cooldown").value = value.cooldownMinutes ?? 120;
    $("companion-limit").value = value.dailyLimit ?? 4;
  }
  function readForm() {
    return { audit: $("companion-audit").checked, enabled: $("companion-enabled").checked,
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
  function render(value) {
    const list = $("companion-list");
    list.replaceChildren();
    const contacts = Array.isArray(value.contacts) ? value.contacts : [];
    $("companion-empty").hidden = contacts.length > 0;
    for (const item of contacts) {
      const card = element("article", null, "panel");
      const heading = element("div", null, "panel-heading");
      const summary = element("div");
      summary.append(element("h3", `QQ ${item.userId}`), element("p", item.mood ? `当前心情：${item.mood}` : "当前心情：未记录", "muted"));
      heading.append(summary); card.append(heading);
      const details = element("dl", null, "connection-details");
      for (const [label, shown] of [["上次整理", item.lastAuditAt ? date(item.lastAuditAt) : "未整理"],
        ["上次主动", item.lastWakeupAt ? date(item.lastWakeupAt) : "未主动"], ["今天已主动", `${number(item.wakeupsToday)} 次`]]) {
        const wrap = element("div"); wrap.append(element("dt", label), element("dd", shown)); details.append(wrap);
      }
      card.append(details);
      const entries = Array.isArray(item.entries) ? item.entries : [];
      if (!entries.length) card.append(element("p", "还没有日记条目；到整理时间后由模型写入。", "field-help"));
      for (const entry of entries) {
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
  $("companion-form").addEventListener("input", () => {
    if (state.loading.companion || state.saving.companion) return;
    markDirty("companion"); message("companion-message", "有未保存的修改。");
  });
  $("companion-form").addEventListener("submit", save);
  $("companion-refresh").addEventListener("click", () => load(true));
  $("companion-retry").addEventListener("click", () => load(true));
  return { load, reset };
};
