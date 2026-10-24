"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const all = (selector) => [...document.querySelectorAll(selector)];
  const titles = {
    overview: ["概览", "运行概览", "查看当前状态，掌握机器人的每一次运行。"],
    persona: ["角色", "角色设定", "管理共用角色、群聊记忆范围，以及待确认的历史角色草稿。"],
    memories: ["记忆", "联系人记忆", "按 QQ 管理长期笔记、近七天原文和公开偏好。"],
    memes: ["表情库", "表情库", "收藏合适的表情，为自然交流增添一点情绪。"],
    schedules: ["定时消息", "定时消息", "一次性或每天发送固定文字和已入库表情。"],
    companion: ["心脑", "心情与日记", "查看她的心情和日记，设置每天整理时间与主动唤醒频率。"],
    settings: ["运行设置", "运行设置", "管理授权范围、回复模式与频率。"]
  };
  const state = {
    token: "", view: "overview", status: null, connected: false, controlBusy: false,
    timer: null, polling: false, session: 0, persona: null, settings: null,
    personaLearningDraft: null, personaLearningGeneration: 0, personaLearningBusy: false, personaLearningConfirming: false,
    personaLearningSourceRequest: 0, personaLearningSourcesLoading: false, personaLearningSources: [], personaLearningExpiryTimer: null,
    dirty: { persona: false, settings: false, public: false, companion: false },
    loading: { persona: false, settings: false, memes: false, companion: false },
    saving: { persona: false, settings: false, memory: false, public: false, history: false, companion: false },
    companion: null, companionRequest: 0,
    userId: null, memoryTab: "notes", memoryGeneration: 0, editingId: null,
    memories: [], history: [], publicLoaded: false, memoriesLoaded: false, historyLoaded: false, historyRequest: 0, memoryRequest: 0,
    memeUrls: new Set(), memeGeneration: 0, memesLoaded: false,
    memeBatchRows: [], memeBatchUrls: new Set(), memeBatchGeneration: 0, memeBatchBusy: false,
    favoriteUrls: new Set(), favoriteGeneration: 0, favoritesLoading: false,
    receivedUrls: new Set(), receivedGeneration: 0, receivedLoading: false,
    receivedSelection: null, receivedPreviewRequest: 0, receivedPreviewReady: false, receivedImporting: false,
    schedules: [], scheduleId: null, scheduleRevision: null, scheduleBusy: false, scheduleGeneration: 0
  };

  const companionPage = window.createCompanionPage({ state, $, api, message, markDirty, confirmDiscard, element, number, date });

  function text(id, value) { $(id).textContent = value == null ? "—" : String(value); }
  function message(id, value = "", type = "") {
    text(id, value);
    $(id).className = `form-message${type ? ` ${type}` : ""}`;
  }
  function notice(value, type = "", source = "action") {
    text("global-message", value);
    $("global-message").hidden = !value;
    $("global-message").className = `notice${type ? ` ${type}` : ""}`;
    $("global-message").dataset.source = source;
  }
  function element(tag, value, className) {
    const result = document.createElement(tag);
    if (value != null) result.textContent = String(value);
    if (className) result.className = className;
    return result;
  }
  function number(value) { return typeof value === "number" && Number.isFinite(value) ? value.toLocaleString("zh-CN") : "—"; }
  function engineName(value) { return value === "deepseek" ? "DeepSeek" : value === "school" ? "学校模型" : value || "—"; }
  function modeName(value) {
    const key = typeof value === "string" ? value.toLowerCase() : value;
    return ({ mention: "被提及或称呼", active: "允许主动回复", manual: "手动模式", 0: "被提及或称呼", 1: "允许主动回复", 2: "手动模式" })[key] ?? "—";
  }
  function date(value) {
    const parsed = new Date(value);
    return value && Number.isFinite(parsed.getTime()) ? parsed.toLocaleString("zh-CN", { hour12: false }) : "时间未提供";
  }
  function safeUrl(value) {
    try { const parsed = new URL(value); return `${parsed.protocol}//${parsed.host}${parsed.pathname}`; }
    catch { return "未提供"; }
  }
  function readToken() { try { return sessionStorage.getItem("qq-admin-token") || ""; } catch { return ""; } }
  function storeToken(token) { try { if (token) sessionStorage.setItem("qq-admin-token", token); else sessionStorage.removeItem("qq-admin-token"); } catch { /* The current page can still hold a token when storage is disabled. */ } }
  function stopPolling() { clearTimeout(state.timer); state.timer = null; }
  function schedulePolling() {
    stopPolling();
    if (state.token && !document.hidden) state.timer = setTimeout(refreshStatus, 5000);
  }
  function updateControls() {
    const runtime = state.status?.runtime;
    const disabled = !state.connected || state.controlBusy;
    $("apply-button").disabled = disabled;
    $("pause-button").disabled = disabled || !runtime?.running || !!runtime.paused;
    $("resume-button").disabled = disabled || !runtime?.running || !runtime.paused;
    $("stop-button").disabled = disabled || !runtime?.running;
    $("refresh-button").disabled = state.polling;
  }
  function resetSessionState() {
    state.session++;
    for (const group of [state.loading, state.saving, state.dirty]) {
      for (const key of Object.keys(group)) group[key] = false;
    }
    state.status = null;
    state.settings = null;
    state.polling = false;
    state.controlBusy = false;
    state.favoritesLoading = false;
    state.userId = null;
    state.editingId = null;
    state.memories = [];
    state.history = [];
    state.publicLoaded = state.memoriesLoaded = state.historyLoaded = false;
    state.memoryRequest++;
    state.historyRequest++;
    for (const id of ["persona-form", "settings-form", "memory-public-pane"]) $(id).reset();
    for (const kind of ["persona", "settings", "public"]) {
      $(`${kind}-fields`).disabled = true;
      $(`${kind}-save`).disabled = true;
      text(`${kind}-dirty`, "未读取");
      $(`${kind}-dirty`).className = "pill neutral";
    }
    for (const id of ["persona-retry", "settings-retry", "memes-refresh", "qq-favorites-refresh"]) $(id).disabled = false;
    $("memory-text").value = "";
    $("memory-user").value = "";
    $("memory-contacts").replaceChildren();
    setMemoryBusy(false);
    $("memory-text").disabled = true;
    $("memory-refresh").disabled = $("history-refresh").disabled = $("history-clear").disabled = $("public-refresh").disabled = true;
    for (const field of $("meme-upload-form").querySelectorAll("input,button")) field.disabled = false;
    text("meme-upload", "导入表情");
    $("qq-face-add").disabled = false;
    renderBackgroundTasks();
  }
  function showLogin(value = "") {
    const batchWasInFlight = state.memeBatchBusy;
    resetSessionState();
    state.personaLearningSourceRequest++;
    state.personaLearningSourcesLoading = false;
    state.personaLearningSources = [];
    state.personaLearningConfirming = false;
    clearPersonaLearning();
    state.persona = null;
    state.memoryGeneration++;
    state.token = "";
    companionPage.reset();
    state.connected = false;
    storeToken("");
    stopPolling();
    revokeMemeUrls();
    resetMemeBatch();
    if (batchWasInFlight) message("meme-batch-message", "连接已更换，上一批导入结果未确认；请刷新清单核对，不会自动重试。", "error");
    revokeFavoriteUrls();
    revokeReceivedUrls();
    state.receivedLoading = false;
    state.receivedImporting = false;
    $("received-images-refresh").disabled = false;
    state.memesLoaded = false;
    $("meme-grid").replaceChildren();
    $("qq-favorites-grid").replaceChildren();
    $("received-images-grid").replaceChildren();
    $("memory-suggestions-list").replaceChildren();
    $("memory-list").replaceChildren();
    $("history-list").replaceChildren();
    state.scheduleGeneration++;
    state.schedules = [];
    $("schedules-list").replaceChildren();
    resetScheduleEditor();
    setScheduleBusy(false);
    resetReceivedSelection();
    $("login-screen").hidden = false;
    $("app-shell").hidden = true;
    $("login-token").value = "";
    message("login-message", value, value ? "error" : "");
    $("login-submit").disabled = false;
    text("login-submit", "连接控制台");
    updateControls();
  }
  async function api(path, options = {}) {
    if (!state.token) throw new Error("请先使用管理令牌连接。");
    const session = state.session;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), options.timeout || 20000);
    try {
      const response = await fetch(path, {
        method: options.method || "GET", signal: controller.signal,
        cache: "no-store", credentials: "omit", redirect: "error",
        headers: { Authorization: `Bearer ${state.token}`, ...(options.body ? { "Content-Type": "application/json" } : {}) },
        ...(options.body ? { body: JSON.stringify(options.body) } : {})
      });
      if (session !== state.session) throw new Error("连接会话已更换，请重试。");
      if (response.status === 401 || response.status === 403) {
        showLogin("令牌无效或已过期，请重新运行 open-panel.cmd 打开当前管理页。");
        throw new Error("管理令牌无效或已过期。");
      }
      if (!response.ok) {
        let error = "操作失败，请稍后重试。";
        try { const body = await response.json(); if (typeof body.error === "string") error = body.error; } catch { /* Never expose raw server responses. */ }
        const failure = new Error(error); failure.status = response.status; throw failure;
      }
      if (options.blob) {
        const blob = await response.blob();
        if (session !== state.session) throw new Error("连接会话已更换，请重试。");
        return blob;
      }
      if (response.status === 204) return null;
      const raw = await response.text();
      if (session !== state.session) throw new Error("连接会话已更换，请重试。");
      if (!raw) return null;
      try { return JSON.parse(raw); } catch { throw new Error("服务返回的数据格式不正确，请重试。"); }
    } catch (error) {
      if (error.name === "AbortError") throw new Error("请求超时，请检查本地服务后重试。");
      if (error instanceof TypeError) throw new Error("无法连接本地服务，请确认机器人管理进程仍在运行。");
      throw error;
    } finally { clearTimeout(timeout); }
  }

  function renderStatus(payload) {
    state.status = payload;
    state.connected = true;
    const runtime = payload?.runtime;
    const policy = runtime?.policy;
    const running = runtime?.running === true;
    const paused = runtime?.paused === true;
    const qq = runtime?.oneBot;
    const label = !runtime ? "正在启动" : !running ? "已停止" : paused ? "已暂停发送" : "监听中";
    text("runtime-state", label);
    text("runtime-badge", !runtime ? "等待" : !running ? "已停止" : paused ? "暂停" : qq?.state === "connected" ? "QQ 已连接" : qq?.state === "offline" ? "QQ 已离线" : qq?.state === "unavailable" ? "QQ 不可用" : qq?.state === "account_changed" ? "账号已变化" : "事件监听");
    $("runtime-badge").className = `pill${!runtime || !running || !qq ? " neutral" : paused || ["unavailable", "offline", "account_changed"].includes(qq.state) ? " warning" : ""}`;
    const qqDetail = ({ connected: "QQ 在线状态及账号最近检查成功", offline: "QQ 当前离线，请在 NapCat 恢复登录后检查", unavailable: "QQ 状态接口不可用，请检查 NapCat 是否在线", account_changed: "QQ 账号与机器人配置不同，发送前校验将拒绝", checking: "正在检查 QQ 在线状态及账号", preview: "本地预览，不向 QQ 发送" })[qq?.state] || "事件监听已启动；QQ 连接尚未单独检查";
    text("runtime-detail", !runtime ? "等待运行实例就绪" : !running ? "可以检查设置或重新启动机器人" : `${paused ? "已暂停发送；" : ""}${qqDetail}${qq?.checkedAt ? `（${date(qq.checkedAt)}）` : ""}`);
    text("runtime-engine", engineName(runtime?.engine));
    text("runtime-model", runtime?.model || "模型信息未提供");
    text("runtime-latency", typeof runtime?.lastReplyMilliseconds === "number" ? `${number(runtime.lastReplyMilliseconds)} ms` : "—");
    text("refresh-time", `更新于 ${new Date().toLocaleTimeString("zh-CN", { hour12: false })}`);
    text("runtime-group", runtime?.groupId);
    text("runtime-mode", modeName(policy?.mode));
    text("runtime-api", runtime?.apiBaseUrl ? safeUrl(runtime.apiBaseUrl) : "—");
    text("runtime-key", runtime?.keyEnvironment || "—");
    text("persona-learning-engine", `草稿使用当前实际运行：${engineName(runtime?.engine)}${runtime?.model ? ` · ${runtime.model}` : " · 模型信息未提供"}`);
    text("settings-running-engine", `当前运行实际引擎：${engineName(runtime?.engine)}${runtime?.model ? ` · ${runtime.model}` : ""}`);
    text("runtime-error", runtime?.lastError || "");
    $("runtime-error").hidden = !runtime?.lastError;
    $("restart-notice").hidden = !payload?.restartRequired;
    $("connection-status").className = "connection";
    text("connection-text", "本地服务已连接");
    if ($("global-message").dataset.source === "status") notice("");
    renderRuntimeRows("group", policy?.groups, false, !!runtime);
    renderRuntimeRows("contact", policy?.privateContacts, true, !!runtime);
    renderGroupActivity(runtime?.recentGroupActivity);
    renderBudget(runtime?.budget);
    renderBackgroundTasks(runtime?.backgroundTasks);
    if (state.settings?.activeGroupId === 0 && !state.dirty.settings) renderActiveGroupOptions(0);
    updateControls();
  }
  function renderRuntimeRows(prefix, values, privateChat, runtimeReady) {
    const rows = $(prefix === "group" ? "group-rows" : "contact-rows");
    rows.replaceChildren();
    const items = Array.isArray(values) ? values : [];
    for (const value of items) {
      const row = element("tr");
      row.append(element("td", privateChat ? value.userId : value.groupId));
      const status = element("td");
      status.append(element("span", value.busy ? "正在处理" : value.pendingActiveMessages > 0 ? "等待主动评估" : "空闲", `pill${value.busy ? "" : " neutral"}`));
      if (!privateChat && value.pendingActiveMessages > 0) status.title = `已合并 ${value.pendingActiveMessages} 条普通消息，最早评估时间：${date(value.activeDueAt)}`;
      row.append(status);
      if (privateChat) row.append(element("td", number(value.pendingMessages)));
      row.append(element("td", privateChat ? number(value.reservedLastHour) :
        `点名 ${number(value.directReservedLastHour)} / 主动 ${number(value.activeReservedLastHour)}`), element("td", number(value.contextCount)));
      if (!privateChat) row.append(element("td", value.activeMessageEvery ?
        `${number(value.activeMessagesSinceOpportunity)} / ${number(value.activeMessageEvery)}` : "—"));
      rows.append(row);
    }
    text(`${prefix}-count`, Array.isArray(values) ? `${items.length} ${privateChat ? "位联系人" : "个群"}` : "—");
    const empty = $(`${prefix}-empty`);
    empty.hidden = items.length > 0;
    empty.textContent = !runtimeReady ? "等待运行实例就绪。" : !Array.isArray(values) ? "当前运行未提供这项状态。" : privateChat ? "当前没有私聊联系人状态。" : "当前没有群聊状态。";
  }
  function renderBackgroundTasks(values) {
    const items = Array.isArray(values) ? values : [];
    const names = { companion: "心脑（日记、心情与主动唤醒）", "onebot-health": "QQ 健康检查", "message-listener": "消息监听", "message-worker": "消息处理" };
    const states = { starting: "正在启动", running: "运行中", failed: "失败", stopped: "已停止" };
    for (const id of ["background-tasks", "companion-background-tasks"]) {
      const container = $(id); container.replaceChildren();
      if (!items.length) { container.append(element("p", "当前运行未提供后台任务状态。", "muted")); continue; }
      for (const item of items) {
        const row = element("p");
        row.append(element("strong", `${names[item.name] || item.name}：`),
          element("span", states[item.state] || "状态未知", `pill${item.state === "failed" ? " warning" : " neutral"}`));
        row.append(element("span", ` · 最近成功：${item.lastSuccessAt ? date(item.lastSuccessAt) : "尚无记录"}${item.lastError ? ` · 最近错误：${item.lastError}` : ""}`, "muted"));
        container.append(row);
      }
    }
  }
  function renderGroupActivity(values) {
    const rows = $("group-activity-rows"); rows.replaceChildren();
    const items = Array.isArray(values) ? values.slice(-12).reverse() : [];
    const triggers = { ambient: "普通群聊", mention: "@ 提及", name: "名字点名", unspecified: "未触发" };
    const outcomes = { queued: "已排入回复", active_pending: "等待空闲或冷却结束", active_waiting: "等待普通消息累计",
      model_skipped: "模型选择安静", api_accepted: "已发送，QQ 已接收", preview: "仅本地预览", failed: "请求失败",
      cancelled: "已取消", stale: "已过期", cooldown: "回复冷却中", active_cooldown: "主动冷却中", hourly_limit: "小时额度已满",
      busy: "当前正在回复", not_a_candidate: "未触发", paused: "已暂停", duplicate: "重复消息", queue_full: "队列已满",
      repeat_meme_suppressed: "已省略重复表情，本轮未发送",
      account_changed_paused: "账号变化，已暂停" };
    for (const item of items) {
      const row = element("tr");
      const kind = (triggers[item.trigger] || "未触发") + (item.containsBotName ? "（含名字）" : "");
      row.append(element("td", date(item.at)), element("td", kind), element("td", outcomes[item.status] || item.status), element("td", item.triggerMessageId));
      rows.append(row);
    }
    $("group-activity-empty").hidden = items.length > 0;
  }
  function renderBudget(budget) {
    const labels = {
      reservedYuan: "累计保守预留 / 元", limitYuan: "预算上限 / 元",
      reservedPoints: "累计保守预留 / 积分", maxPoints: "累计积分上限",
      calls: "已预留调用次数", maxCalls: "累计调用次数上限"
    };
    const list = $("budget-list");
    list.replaceChildren();
    if (budget && typeof budget === "object") {
      for (const [key, label] of Object.entries(labels)) {
        if (!Object.prototype.hasOwnProperty.call(budget, key)) continue;
        const unlimited = budget[key] === null && ["limitYuan", "maxPoints", "maxCalls"].includes(key);
        if (!unlimited && (typeof budget[key] !== "number" || !Number.isFinite(budget[key]))) continue;
        const entry = element("div");
        const value = unlimited ? "未设累计上限" : ["reservedYuan", "limitYuan", "reservedPoints", "maxPoints"].includes(key)
          ? budget[key].toLocaleString("zh-CN", { maximumFractionDigits: 6 }) : number(budget[key]);
        entry.append(element("dt", label), element("dd", value));
        list.append(entry);
      }
    }
    $("budget-empty").hidden = list.children.length > 0;
  }
  async function refreshStatus() {
    stopPolling();
    if (state.polling || !state.token || document.hidden) { schedulePolling(); return; }
    state.polling = true;
    const session = state.session;
    updateControls();
    try {
      const payload = await api("/api/status", { timeout: 12000 });
      if (session !== state.session) return;
      renderStatus(payload);
      if (state.view === "schedules" && !state.scheduleBusy) await loadSchedules(false);
    }
    catch (error) {
      if (session !== state.session) return;
      state.connected = false;
      $("connection-status").className = "connection error";
      text("connection-text", "连接中断");
      notice(`${error.message} 上方状态保留最后一次成功读取的结果。`, "error", "status");
    } finally { if (session === state.session) { state.polling = false; updateControls(); schedulePolling(); } }
  }
  async function control(action) {
    if (action === "stop" && !window.confirm("确定停止机器人？当前回复会结束；之后需要从启动入口重新运行机器人。")) return;
    state.controlBusy = true;
    const session = state.session;
    updateControls();
    const labels = { pause: "正在暂停…", resume: "正在恢复…", apply: "正在请求重载已保存设置…", stop: "正在停止机器人…" };
    notice(labels[action]);
    try {
      await api("/api/control", { method: "POST", body: { action }, timeout: 45000 });
      if (session !== state.session) return;
      notice(({ pause: "已暂停回复。", resume: "已恢复回复。", apply: "重载请求已接受，正在异步应用已保存设置；请查看当前运行状态确认生效。", stop: "停止指令已完成。重新运行请使用本机启动入口。" })[action]);
      await refreshStatus();
    } catch (error) {
      if (session !== state.session) return;
      notice(`${error.message}${action === "stop" ? " 停止指令可能已生效，请检查本机运行窗口。" : action === "apply" ? " 请确认本地服务状态后再试，已保存的设置仍保留。" : ""}`, "error");
    } finally { if (session === state.session) { state.controlBusy = false; updateControls(); } }
  }

  function markDirty(kind, value = true) {
    state.dirty[kind] = value;
    text(`${kind}-dirty`, value ? "未保存" : "已保存");
    $(`${kind}-dirty`).className = `pill ${value ? "warning" : "neutral"}`;
    $(`${kind}-save`).disabled = !value || state.saving[kind];
  }
  function confirmDiscard(kind) { return !state.dirty[kind] || window.confirm("重新读取会放弃这一表单中尚未保存的修改，是否继续？"); }
  function hasUncommittedPageData() {
    return Object.values(state.dirty).some(Boolean) || state.personaLearningDraft || state.personaLearningConfirming ||
      $("memory-text").value.trim() || state.memeBatchBusy ||
      state.memeBatchRows.some(row => ["ready", "failed", "unknown"].includes(row.status)) ||
      all("#meme-grid .meme-card input").some(input => input.dataset.saved !== input.value);
  }
  async function loadPersona(force = false) {
    if (state.loading.persona || state.personaLearningConfirming || (state.persona && !force)) return;
    if (force && !confirmDiscard("persona")) return;
    const session = state.session;
    state.loading.persona = true;
    $("persona-fields").disabled = true;
    $("persona-save").disabled = true;
    $("persona-retry").disabled = true;
    message("persona-message", "正在读取角色设置…");
    try {
      const value = await api("/api/persona");
      if (session !== state.session) return;
      state.persona = value;
      writePersonaForm(value);
      markDirty("persona", false);
      $("persona-fields").disabled = false;
      message("persona-message", "角色设置已读取。");
    } catch (error) { if (session === state.session) message("persona-message", `${error.message} 点击“重新读取”重试。`, "error"); }
    finally { if (session === state.session) { state.loading.persona = false; $("persona-retry").disabled = false; updatePersonaLearningControls(); } }
  }
  function writePersonaForm(value) {
    for (const [id, key] of Object.entries({ "persona-name": "name", "persona-address": "userAddress", "persona-personality": "personality", "persona-style": "speakingStyle", "persona-max": "replyMaxCharacters", "persona-meme": "memeProbabilityPercent" })) $(id).value = value[key] ?? "";
    $("persona-groups").checked = value.usePersonaInGroups === true;
    $("persona-share-memories").checked = value.shareContactMemoriesInGroups === true;
    updateMemeRange();
  }
  function updateMemeRange() { text("persona-meme-output", `${$("persona-meme").value}%`); }
  function checkedText(value, maximum, label, allowLines = false) {
    const result = value.trim();
    const controls = allowLines ? result.replace(/[\r\n\t]/g, "") : result;
    if (!result || result.length > maximum || /[\u0000-\u001f\u007f-\u009f]/.test(controls))
      throw new Error(`${label}需为 1–${maximum} 字，不得包含无效控制字符。`);
    return result;
  }
  async function savePersona(event) {
    event.preventDefault();
    if (!state.persona || state.saving.persona || state.personaLearningConfirming) return;
    const session = state.session;
    let value;
    try {
      value = {
        name: checkedText($("persona-name").value, 16, "角色名字"),
        personality: checkedText($("persona-personality").value, 2000, "性格与人设", true),
        userAddress: checkedText($("persona-address").value, 32, "用户称呼"),
        speakingStyle: checkedText($("persona-style").value, 1000, "说话风格", true),
        replyMaxCharacters: Number($("persona-max").value), memeProbabilityPercent: Number($("persona-meme").value),
        usePersonaInGroups: $("persona-groups").checked, shareContactMemoriesInGroups: $("persona-share-memories").checked
      };
      if (!Number.isInteger(value.replyMaxCharacters) || value.replyMaxCharacters < 40 || value.replyMaxCharacters > 3000)
        throw new Error("回复字数上限需为 40–3000 的整数。");
      if (!Number.isInteger(value.memeProbabilityPercent) || value.memeProbabilityPercent < 0 || value.memeProbabilityPercent > 100)
        throw new Error("表情允许发送比例需为 0–100 的整数。");
    } catch (error) { message("persona-message", error.message, "error"); return; }
    state.saving.persona = true;
    $("persona-fields").disabled = true;
    $("persona-save").disabled = true;
    $("persona-retry").disabled = true;
    updatePersonaLearningControls();
    message("persona-message", "正在保存角色…");
    try {
      await api("/api/persona", { method: "PUT", body: value });
      if (session !== state.session) return;
      state.persona = value;
      clearPersonaLearning();
      message("persona-learning-message", "角色已另行保存；如需从历史整理，请重新生成草稿。");
      markDirty("persona", false);
      message("persona-message", "已保存角色。点击顶部“应用设置”重载。", "success");
      $("restart-notice").hidden = false;
    } catch (error) { if (session === state.session) message("persona-message", error.message, "error"); }
    finally { if (session === state.session) { state.saving.persona = false; $("persona-fields").disabled = false; $("persona-retry").disabled = false; $("persona-save").disabled = !state.dirty.persona; updatePersonaLearningControls(); } }
  }

  function clearPersonaLearning() {
    state.personaLearningGeneration++;
    state.personaLearningDraft = null;
    state.personaLearningBusy = false;
    clearTimeout(state.personaLearningExpiryTimer); state.personaLearningExpiryTimer = null;
    $("persona-learning-form").hidden = true;
    $("persona-learning-base").replaceChildren();
    for (const id of ["persona-draft-name", "persona-draft-personality", "persona-draft-address", "persona-draft-style"]) $(id).value = "";
    updatePersonaLearningControls();
  }
  function updatePersonaLearningControls() {
    const confirming = state.personaLearningConfirming;
    const busy = state.personaLearningBusy;
    const selected = $("persona-learning-user").value;
    const allowed = state.personaLearningSources.some(id => String(id) === selected);
    $("persona-learning-user").disabled = confirming || state.personaLearningSourcesLoading || state.personaLearningSources.length === 0;
    $("persona-learning-refresh").disabled = confirming || busy || state.personaLearningSourcesLoading;
    $("persona-learning-generate").disabled = !state.token || !state.persona || !allowed || busy || confirming || state.loading.persona || state.saving.persona || state.personaLearningSourcesLoading;
    text("persona-learning-generate", busy ? "正在整理，最多约 2 分钟…" : "生成角色草稿");
    $("persona-learning-fields").disabled = confirming;
    $("persona-learning-discard").disabled = confirming;
    $("persona-learning-confirm").disabled = !state.personaLearningDraft || confirming || state.saving.persona || state.loading.persona || state.dirty.persona;
  }
  async function loadPersonaSources() {
    if (state.personaLearningBusy || state.personaLearningConfirming) return;
    const session = state.session;
    const request = ++state.personaLearningSourceRequest;
    const selected = $("persona-learning-user").value;
    state.personaLearningSourcesLoading = true;
    updatePersonaLearningControls();
    try {
      const settings = await api("/api/settings");
      if (session !== state.session || request !== state.personaLearningSourceRequest || state.view !== "persona") return;
      state.personaLearningSources = (settings.allowedPrivateUserIds || []).filter(id => Number.isSafeInteger(id) && id > 0);
      const select = $("persona-learning-user"); select.replaceChildren();
      if (!state.personaLearningSources.length) {
        const option = element("option", "尚未配置私聊白名单"); option.value = ""; select.append(option);
        clearPersonaLearning();
        message("persona-learning-message", "请先在运行设置添加私聊白名单，并留有近七天的对话记录。");
      } else {
        for (const id of state.personaLearningSources) { const option = element("option", `QQ ${id}`); option.value = String(id); select.append(option); }
        select.value = state.personaLearningSources.some(id => String(id) === selected) ? selected : String(state.personaLearningSources[0]);
        if (selected && selected !== select.value) { clearPersonaLearning(); message("persona-learning-message", "来源白名单已变化，旧草稿已丢弃；请重新生成。"); }
      }
    } catch (error) {
      if (session === state.session && request === state.personaLearningSourceRequest) message("persona-learning-message", `${error.message} 点击“刷新联系人”重试。`, "error");
    } finally {
      if (session === state.session && request === state.personaLearningSourceRequest) { state.personaLearningSourcesLoading = false; updatePersonaLearningControls(); }
    }
  }
  function renderPersonaLearning(result) {
    const draft = result.draft;
    if (!result.draftId || !draft?.proposedPersona || !draft?.basePersona || !Number.isFinite(new Date(result.expiresAt).getTime())) throw new Error("角色草稿响应不完整，请重新生成。");
    state.personaLearningDraft = result;
    for (const [id, key] of Object.entries({ "persona-draft-name": "name", "persona-draft-personality": "personality", "persona-draft-address": "userAddress", "persona-draft-style": "speakingStyle" })) $(id).value = draft.proposedPersona[key] ?? "";
    const base = $("persona-learning-base"); base.replaceChildren();
    for (const [label, key] of [["名字", "name"], ["性格与人设", "personality"], ["用户称呼", "userAddress"], ["说话风格", "speakingStyle"]]) {
      const item = element("article", null, "memory-item"); item.append(element("h3", label), element("p", draft.basePersona[key])); base.append(item);
    }
    text("persona-learning-source", `来源：QQ ${draft.userId} · ${draft.sourceCount} 条 · ${date(draft.sourceFrom)} 至 ${date(draft.sourceTo)}；到期：${date(result.expiresAt)}`);
    text("persona-learning-summary", draft.summary || "请检查以下四项草稿，确认后保存。");
    $("persona-learning-form").hidden = false;
    clearTimeout(state.personaLearningExpiryTimer);
    const generation = state.personaLearningGeneration;
    state.personaLearningExpiryTimer = setTimeout(() => {
      if (generation !== state.personaLearningGeneration || state.personaLearningConfirming) return;
      state.personaLearningDraft = null;
      message("persona-learning-message", "草稿已过期，编辑内容仍显示供复制；请重新生成后确认。", "error");
      updatePersonaLearningControls();
    }, Math.max(0, new Date(result.expiresAt).getTime() - Date.now()));
  }
  async function generatePersonaLearning() {
    if ($("persona-learning-generate").disabled) return;
    if (state.personaLearningDraft && !window.confirm("重新生成会替换尚未确认的草稿，上方角色表单保持原样。是否继续？")) return;
    const userId = qqId($("persona-learning-user").value);
    const session = state.session;
    clearPersonaLearning();
    const generation = state.personaLearningGeneration;
    state.personaLearningBusy = true;
    updatePersonaLearningControls();
    message("persona-learning-message", "正在将所选联系人的保留期内历史交给当前模型整理；现有角色未修改，请稍候…");
    try {
      const result = await api("/api/persona-learning/generate", { method: "POST", body: { userId }, timeout: 125000 });
      if (session !== state.session || generation !== state.personaLearningGeneration || state.view !== "persona" || $("persona-learning-user").value !== String(userId)) return;
      renderPersonaLearning(result);
      message("persona-learning-message", state.dirty.persona ? "草稿已生成。上方有未保存的修改，请先保存或保留后重新读取，再重新生成，避免覆盖。" : "草稿已生成，尚未保存。请检查并编辑，点击“确认并保存角色”后生效。");
    } catch (error) {
      if (session === state.session && generation === state.personaLearningGeneration) message("persona-learning-message", `${error.message} 原角色未修改。`, "error");
    } finally {
      if (session === state.session && generation === state.personaLearningGeneration) { state.personaLearningBusy = false; updatePersonaLearningControls(); }
    }
  }
  async function confirmPersonaLearning(event) {
    event.preventDefault();
    const result = state.personaLearningDraft;
    if (!result || state.personaLearningConfirming || state.saving.persona) return;
    if (state.dirty.persona) { message("persona-learning-message", "上方角色表单有未保存的修改，已保留；请先处理，再重新生成草稿。", "error"); return; }
    if (new Date(result.expiresAt).getTime() <= Date.now()) { state.personaLearningDraft = null; updatePersonaLearningControls(); message("persona-learning-message", "草稿已过期，请重新生成。", "error"); return; }
    let body;
    try {
      body = { draftId: result.draftId, name: checkedText($("persona-draft-name").value, 16, "草稿名字"),
        personality: checkedText($("persona-draft-personality").value, 2000, "草稿性格与人设", true),
        userAddress: checkedText($("persona-draft-address").value, 32, "草稿称呼"),
        speakingStyle: checkedText($("persona-draft-style").value, 1000, "草稿说话风格", true) };
    } catch (error) { message("persona-learning-message", error.message, "error"); return; }
    const session = state.session;
    state.personaLearningConfirming = true;
    $("persona-fields").disabled = true; $("persona-save").disabled = true; $("persona-retry").disabled = true;
    updatePersonaLearningControls();
    message("persona-learning-message", "正在保存经你确认的四项角色设定…");
    try {
      const saved = await api("/api/persona-learning/confirm", { method: "POST", body });
      if (session !== state.session) return;
      state.persona = saved; writePersonaForm(saved); markDirty("persona", false);
      clearPersonaLearning();
      message("persona-learning-message", "已保存确认后的角色，字数、表情比例和群聊开关保留。点击顶部“应用设置”重载。", "success");
      message("persona-message", "角色草稿已确认保存，点击顶部“应用设置”重载。", "success");
      $("restart-notice").hidden = false;
    } catch (error) {
      if (session === state.session) {
        if (error.status === 409 || error.status === 410 || error.status === 404) state.personaLearningDraft = null;
        message("persona-learning-message", `${error.message}${error.status === 409 ? " 角色或来源已改变，请重新生成；当前编辑内容保留供复制。" : " 请核对当前角色后再处理；原表单内容保留。"}`, "error");
      }
    } finally {
      if (session === state.session) { state.personaLearningConfirming = false; $("persona-fields").disabled = state.loading.persona; $("persona-retry").disabled = state.loading.persona; $("persona-save").disabled = !state.dirty.persona; updatePersonaLearningControls(); }
    }
  }

  const settingFields = {
    "settings-engine": "engine", "settings-mode": "mode", "settings-active-group": "activeGroupId",
    "settings-private-interval": "privateIntervalSeconds", "settings-private-limit": "privatePerHour",
    "settings-mention-interval": "mentionIntervalSeconds", "settings-mention-limit": "mentionPerHour",
    "settings-active-interval": "activeIntervalSeconds", "settings-active-limit": "activePerHour",
    "settings-active-every": "activeMessageEvery"
  };
  async function loadSettings(force = false) {
    if (state.loading.settings || state.saving.settings || (state.settings && !force)) return;
    if (force && !confirmDiscard("settings")) return;
    const session = state.session;
    state.loading.settings = true;
    $("settings-fields").disabled = true;
    $("settings-save").disabled = true;
    $("settings-retry").disabled = true;
    message("settings-message", "正在读取运行设置…");
    try {
      const value = await api("/api/settings");
      if (session !== state.session) return;
      state.settings = value;
      for (const [id, key] of Object.entries(settingFields)) $(id).value = value[key] ?? "";
      $("settings-groups").value = (value.allowedGroupIds || []).join("\n");
      renderActiveGroupOptions(value.activeGroupId);
      $("settings-contacts").value = (value.allowedPrivateUserIds || []).join("\n");
      $("settings-names").value = (value.botNames || []).join("，");
      text("settings-url", safeUrl(value.oneBotBaseUrl));
      text("settings-port", value.eventPort);
      text("settings-self", value.selfId);
      text("settings-directory", value.memeDirectory);
      $("settings-fields").disabled = false;
      markDirty("settings", false);
      renderContacts();
      message("settings-message", "运行设置已读取。");
    } catch (error) { if (session === state.session) message("settings-message", `${error.message} 点击“重新读取”重试。`, "error"); }
    finally { if (session === state.session) { state.loading.settings = false; $("settings-retry").disabled = false; } }
  }
  function splitValues(value) { return value.split(/[,，、;；\n\r]+/).map(item => item.trim()).filter(Boolean); }
  function qqId(value) {
    if (!/^[1-9]\d*$/.test(String(value).trim())) throw new Error("QQ 号与群号必须为正整数。");
    const parsed = Number(value);
    if (!Number.isSafeInteger(parsed) || parsed <= 0) throw new Error("QQ 号或群号超出可处理范围，请检查输入。");
    return parsed;
  }
  function parseIds(value, min, max, label) {
    const values = splitValues(value).map(qqId);
    if (values.length < min || values.length > max) throw new Error(`${label}需 ${min}–${max} 个号码。`);
    if (new Set(values).size !== values.length) throw new Error(`${label}中有重复号码。`);
    return values;
  }
  function renderActiveGroupOptions(preferred = $("settings-active-group").value) {
    let groups;
    try { groups = parseIds($("settings-groups").value, 1, 8, "群白名单"); }
    catch { groups = []; }
    const select = $("settings-active-group");
    const running = state.status?.runtime?.groupId;
    const desired = Number(preferred === 0 ? 0 : preferred || select.dataset.preferredGroup || state.settings?.activeGroupId || 0);
    select.replaceChildren();
    if (!groups.length) {
      const option = element("option", "请先填写有效的群白名单");
      option.value = "";
      select.append(option);
      return;
    }
    const selected = groups.includes(desired) ? desired : groups.includes(running) ? running : groups[0];
    for (const group of groups) {
      const option = element("option", `${group}${group === running ? "（当前运行）" : ""}`);
      option.value = String(group);
      select.append(option);
    }
    select.value = String(selected);
    select.dataset.preferredGroup = String(selected);
  }
  async function saveSettings(event) {
    event.preventDefault();
    if (!state.settings || state.loading.settings || state.saving.settings) return;
    const session = state.session;
    let value;
    try {
      value = { ...state.settings };
      for (const [id, key] of Object.entries(settingFields)) value[key] = ["engine", "mode"].includes(key) ? $(id).value : Number($(id).value);
      value.allowedGroupIds = parseIds($("settings-groups").value, 1, 8, "群白名单");
      value.allowedPrivateUserIds = parseIds($("settings-contacts").value, 0, 16, "私聊白名单");
      value.activeGroupId = qqId($("settings-active-group").value);
      if (!value.allowedGroupIds.includes(value.activeGroupId)) throw new Error("当前群号必须在群白名单中。");
      if (value.allowedPrivateUserIds.includes(value.selfId)) throw new Error("私聊白名单不能包含机器人自身 QQ。");
      value.botNames = splitValues($("settings-names").value);
      if (!value.botNames.length || value.botNames.length > 4 || value.botNames.some(name => name.length > 16)) throw new Error("机器人称呼需 1–4 个，每个最多 16 字。");
      if (new Set(value.botNames).size !== value.botNames.length) throw new Error("机器人称呼不能重复。");
    } catch (error) { message("settings-message", error.message, "error"); return; }
    state.saving.settings = true;
    $("settings-fields").disabled = true;
    $("settings-save").disabled = true;
    $("settings-retry").disabled = true;
    message("settings-message", "正在保存运行设置…");
    try {
      await api("/api/settings", { method: "PUT", body: value });
      if (session !== state.session) return;
      state.settings = value;
      markDirty("settings", false);
      renderContacts();
      message("settings-message", "设置已保存。点击顶部“应用设置”重载。", "success");
      $("restart-notice").hidden = false;
    } catch (error) { if (session === state.session) message("settings-message", error.message, "error"); }
    finally { if (session === state.session) { state.saving.settings = false; $("settings-fields").disabled = false; $("settings-retry").disabled = false; $("settings-save").disabled = !state.dirty.settings; } }
  }

  function renderContacts() {
    const container = $("memory-contacts");
    container.replaceChildren();
    for (const userId of state.settings?.allowedPrivateUserIds || []) {
      const button = element("button", null, `contact-option${userId === state.userId ? " active" : ""}`);
      button.type = "button";
      button.append(element("span", String(userId).slice(-2), "contact-avatar"), element("span", userId));
      button.setAttribute("aria-pressed", String(userId === state.userId));
      button.addEventListener("click", () => selectUser(userId));
      container.append(button);
    }
  }
  function memoryHasDraft() { return $("memory-text").value.trim() || state.dirty.public; }
  async function selectUser(value) {
    let userId;
    try { userId = qqId(value); } catch (error) { message("memory-selection-message", error.message, "error"); return; }
    if (state.saving.memory || state.saving.public || state.saving.history) { message("memory-selection-message", "正在保存当前联系人，请稍候。", "error"); return; }
    if (userId !== state.userId && memoryHasDraft() && !window.confirm("切换联系人会放弃尚未保存的笔记或公开偏好，是否继续？")) return;
    if (userId === state.userId) { await loadMemoryTab(true); return; }
    state.userId = userId;
    state.memoryGeneration++;
    state.memories = [];
    state.history = [];
    state.memoriesLoaded = false;
    state.historyLoaded = false;
    state.publicLoaded = false;
    state.dirty.public = false;
    $("memory-user").value = userId;
    $("memory-list").replaceChildren();
    $("history-list").replaceChildren();
    $("memory-suggestions-list").replaceChildren();
    text("memory-suggestions-message", "正在读取明确记忆请求…");
    $("public-fields").disabled = true;
    $("public-save").disabled = true;
    $("public-approved").checked = false;
    $("public-address").value = "";
    $("public-tone").value = "neutral";
    $("public-length").value = "normal";
    text("public-dirty", "未读取");
    $("public-dirty").className = "pill neutral";
    resetMemoryEditor();
    $("memory-refresh").disabled = false;
    $("history-refresh").disabled = false;
    $("history-clear").disabled = true;
    $("public-refresh").disabled = false;
    text("memory-list-title", `QQ ${userId} 的长期笔记`);
    text("memory-list-subtitle", "保留到手动删除；群聊是否使用由角色页的记忆共享开关控制。");
    message("memory-selection-message", `当前联系人：${userId}`);
    renderContacts();
    await loadMemoryTab();
  }
  function resetMemoryEditor() {
    state.editingId = null;
    $("memory-text").value = "";
    $("memory-text").disabled = !state.userId;
    $("memory-save").disabled = true;
    $("memory-cancel").hidden = true;
    text("memory-editor-title", "添加长期笔记");
    text("memory-editor-context", state.userId ? `保存到 QQ ${state.userId} · 开启共享后也可用于该成员触发的群聊回复` : "保存前请先选择联系人；群聊使用范围由角色页控制。");
    text("memory-save", "保存笔记");
    message("memory-message");
  }
  async function setMemoryTab(tab) {
    state.memoryTab = tab;
    for (const button of all("[data-memory-tab]")) {
      const selected = button.dataset.memoryTab === tab;
      button.classList.toggle("active", selected);
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
      $(`memory-${button.dataset.memoryTab}-pane`).hidden = !selected;
    }
    await loadMemoryTab();
  }
  async function loadMemoryTab(force = false) {
    if (!state.userId) return;
    if (state.memoryTab === "notes") await loadMemories(force);
    if (state.memoryTab === "history") await loadHistory(force);
    if (state.memoryTab === "public") await loadPublic(force);
  }
  async function loadMemories(force = false) {
    if (state.memoriesLoaded && !force) return;
    const userId = state.userId;
    const generation = state.memoryGeneration;
    const request = ++state.memoryRequest;
    $("memory-refresh").disabled = true;
    message("memory-selection-message", `正在读取 QQ ${userId} 的长期笔记…`);
    try {
      const values = await api(`/api/memories?userId=${userId}`);
      if (generation !== state.memoryGeneration || request !== state.memoryRequest) return;
      state.memories = Array.isArray(values) ? values : [];
      state.memoriesLoaded = true;
      renderMemories();
      await loadMemorySuggestions(userId, generation, request);
      if (generation !== state.memoryGeneration || request !== state.memoryRequest) return;
      message("memory-selection-message", `当前联系人：${userId} · ${state.memories.length} 条长期笔记`);
    } catch (error) {
      if (generation === state.memoryGeneration && request === state.memoryRequest) message("memory-selection-message", `${error.message} 可点击“刷新”重试。`, "error");
    } finally { if (generation === state.memoryGeneration && request === state.memoryRequest) $("memory-refresh").disabled = false; }
  }
  function renderMemories() {
    const container = $("memory-list");
    container.replaceChildren();
    for (const memory of state.memories) {
      const item = element("article", null, "memory-item");
      item.append(element("p", memory.text));
      const footer = element("div", null, "memory-item-footer");
      const stamp = element("time", `更新于 ${date(memory.updatedAt || memory.createdAt)}`);
      if (memory.updatedAt || memory.createdAt) stamp.dateTime = memory.updatedAt || memory.createdAt;
      const buttons = element("div", null, "button-row");
      const edit = element("button", "编辑", "button");
      edit.type = "button";
      edit.addEventListener("click", () => {
        if (state.saving.memory) return;
        if ($("memory-text").value.trim() && !window.confirm("编辑这条笔记会替换输入框里尚未保存的内容，是否继续？")) return;
        state.editingId = memory.id;
        $("memory-text").value = memory.text;
        $("memory-cancel").hidden = false;
        $("memory-save").disabled = false;
        text("memory-editor-title", "编辑长期笔记");
        text("memory-save", "保存修改");
        $("memory-text").focus();
      });
      const remove = element("button", "删除", "button danger-quiet");
      remove.type = "button";
      remove.addEventListener("click", () => deleteMemory(memory));
      buttons.append(edit, remove);
      footer.append(stamp, buttons);
      item.append(footer);
      container.append(item);
    }
    $("memory-empty").hidden = state.memories.length > 0;
    if (!state.memories.length) {
      $("memory-empty").querySelector("h3").textContent = "还没有长期笔记";
      $("memory-empty").querySelector("p").textContent = "在下方手动保存笔记；开启群聊共享后也可用于该成员的群聊回复。";
    }
  }
  async function loadMemorySuggestions(userId, generation, request) {
    $("memory-suggestions-list").replaceChildren();
    message("memory-suggestions-message", "正在读取待确认的明确记忆请求…");
    try {
      const suggestions = await api(`/api/memory-suggestions?userId=${userId}`);
      if (generation !== state.memoryGeneration || request !== state.memoryRequest) return;
      const list = $("memory-suggestions-list"); list.replaceChildren();
      for (const draft of suggestions) {
        const item = element("article", null, "memory-item");
        item.append(element("p", draft.text), element("p", `私聊请求 · ${date(draft.at)}`, "muted"));
        const use = element("button", "编辑并确认保存", "button"); use.type = "button";
        use.addEventListener("click", () => {
          if (state.saving.memory || userId !== state.userId || generation !== state.memoryGeneration || request !== state.memoryRequest) return;
          if ($("memory-text").value.trim() && !window.confirm("使用这条草稿会替换编辑框里尚未保存的内容，是否继续？")) return;
          resetMemoryEditor();
          $("memory-text").value = draft.text;
          $("memory-save").disabled = false;
          message("memory-message", "请核对内容，点击“保存笔记”后才成为长期记忆；群聊使用范围由角色页控制。");
          $("memory-text").focus();
        });
        item.append(use); list.append(item);
      }
      message("memory-suggestions-message", suggestions.length ? `${suggestions.length} 条待确认；未保存的草稿随七天原文过期。` : "暂无明确记忆请求；可私聊发送“请记住：我喜欢简短回答”，再刷新。");
    } catch (error) { if (generation === state.memoryGeneration && request === state.memoryRequest) message("memory-suggestions-message", `${error.message} 点击上方“刷新”重试。`, "error"); }
  }
  function setMemoryBusy(value) {
    state.saving.memory = value;
    $("memory-text").disabled = value;
    $("memory-save").disabled = value || !$("memory-text").value.trim();
    $("memory-cancel").disabled = value;
    $("memory-refresh").disabled = value;
    for (const button of $("memory-list").querySelectorAll("button")) button.disabled = value;
  }
  async function saveMemory(event) {
    event.preventDefault();
    if (!state.userId || state.saving.memory || !$("memory-text").value.trim()) return;
    const session = state.session;
    const editingId = state.editingId;
    const userId = state.userId;
    const content = $("memory-text").value.trim();
    if (content.length > 1000) { message("memory-message", "每条长期笔记最多 1000 字。", "error"); return; }
    setMemoryBusy(true);
    message("memory-message", "正在保存长期笔记…");
    try {
      await api(editingId ? `/api/memories/${encodeURIComponent(editingId)}` : "/api/memories", { method: editingId ? "PUT" : "POST", body: { userId, text: content } });
      if (session !== state.session) return;
      resetMemoryEditor();
      message("memory-message", "长期笔记已保存；开启群聊共享后也可用于该成员触发的群聊回复。", "success");
      await loadMemories(true);
    } catch (error) { if (session === state.session) message("memory-message", error.message, "error"); }
    finally { if (session === state.session) setMemoryBusy(false); }
  }
  async function deleteMemory(memory) {
    if (state.saving.memory || !window.confirm(`确定删除 QQ ${state.userId} 的这条长期笔记？此操作无法撤销。`)) return;
    const session = state.session;
    setMemoryBusy(true);
    message("memory-message", "正在删除笔记…");
    try {
      await api(`/api/memories/${encodeURIComponent(memory.id)}?userId=${state.userId}`, { method: "DELETE" });
      if (session !== state.session) return;
      if (state.editingId === memory.id) resetMemoryEditor();
      message("memory-message", "长期笔记已删除。", "success");
      await loadMemories(true);
    } catch (error) { if (session === state.session) message("memory-message", error.message, "error"); }
    finally { if (session === state.session) setMemoryBusy(false); }
  }
  async function loadHistory(force = false) {
    if (state.saving.history || (state.historyLoaded && !force)) return;
    const userId = state.userId;
    const generation = state.memoryGeneration;
    const request = ++state.historyRequest;
    $("history-refresh").disabled = true;
    $("history-clear").disabled = true;
    message("history-message", "正在读取近 7 天私聊原文…");
    try {
      const values = await api(`/api/history?userId=${userId}`);
      if (generation !== state.memoryGeneration || request !== state.historyRequest) return;
      state.history = Array.isArray(values) ? values : [];
      state.historyLoaded = true;
      const container = $("history-list");
      container.replaceChildren();
      for (const entry of state.history) {
        const item = element("article", null, `history-entry${entry.role === "assistant" ? " assistant" : ""}`);
        const header = element("header");
        header.append(element("strong", entry.role === "assistant" ? "机器人" : `QQ ${userId}`), element("time", date(entry.at)));
        item.append(header, element("p", entry.text));
        container.append(item);
      }
      message("history-message", state.history.length ? `QQ ${userId} · 最近 ${state.history.length} 条私聊原文` : `QQ ${userId} 在保留期内没有私聊原文。`);
    } catch (error) { if (generation === state.memoryGeneration && request === state.historyRequest) message("history-message", `${error.message} 点击“刷新”重试。需要删除损坏原文时可选择“清除此人原文”。`, "error"); }
    finally {
      if (generation === state.memoryGeneration && request === state.historyRequest) {
        $("history-refresh").disabled = false;
        $("history-clear").disabled = !state.userId || state.saving.history;
      }
    }
  }
  async function clearHistory() {
    if (!state.userId || state.saving.history || !window.confirm(`确定清除 QQ ${state.userId} 的全部已保存私聊原文？无法撤销，长期笔记将保留。`)) return;
    const session = state.session;
    state.historyRequest++;
    state.saving.history = true;
    $("history-clear").disabled = true;
    $("history-refresh").disabled = true;
    message("history-message", "正在清除该联系人的私聊原文…");
    try {
      await api(`/api/history?userId=${state.userId}`, { method: "DELETE" });
      if (session !== state.session) return;
      state.history = [];
      $("history-list").replaceChildren();
      state.memoriesLoaded = false;
      state.memoryRequest++;
      $("memory-suggestions-list").replaceChildren();
      message("memory-suggestions-message", "私聊原文已清除；重新读取长期笔记时将同步更新待确认请求。");
      message("history-message", "该联系人的已保存私聊原文已清除，长期笔记已保留。", "success");
    } catch (error) { if (session === state.session) message("history-message", error.message, "error"); }
    finally { if (session === state.session) { state.saving.history = false; $("history-refresh").disabled = false; $("history-clear").disabled = !state.userId; } }
  }
  async function loadPublic(force = false) {
    if (state.publicLoaded && !force) return;
    if (force && !confirmDiscard("public")) return;
    const userId = state.userId;
    const generation = state.memoryGeneration;
    $("public-fields").disabled = true;
    $("public-refresh").disabled = true;
    $("public-save").disabled = true;
    message("public-message", "正在读取公开偏好…");
    try {
      const value = await api(`/api/public-preferences?userId=${userId}`);
      if (generation !== state.memoryGeneration) return;
      $("public-address").value = value?.address || "";
      $("public-tone").value = value?.tone || "neutral";
      $("public-length").value = value?.replyLength || "normal";
      $("public-approved").checked = value?.approved === true;
      $("public-fields").disabled = false;
      state.publicLoaded = true;
      markDirty("public", false);
      message("public-message", `QQ ${userId} · ${value?.approved ? "已批准用于授权群聊" : "尚未批准，不用于群聊"}`);
    } catch (error) { if (generation === state.memoryGeneration) message("public-message", `${error.message} 点击“重新读取”重试。`, "error"); }
    finally { if (generation === state.memoryGeneration) $("public-refresh").disabled = false; }
  }
  async function savePublic(event) {
    event.preventDefault();
    if (!state.userId || state.saving.public || !state.publicLoaded) return;
    const session = state.session;
    const body = { userId: state.userId, address: $("public-address").value.trim(), tone: $("public-tone").value, replyLength: $("public-length").value, approved: $("public-approved").checked };
    state.saving.public = true;
    $("public-fields").disabled = true;
    $("public-save").disabled = true;
    $("public-refresh").disabled = true;
    message("public-message", "正在保存公开偏好…");
    try {
      await api("/api/public-preferences", { method: "PUT", body });
      if (session !== state.session) return;
      markDirty("public", false);
      message("public-message", body.approved ? "公开偏好已保存并批准，可用于该 QQ 触发的授权群回复。" : "公开偏好已保存，尚未批准，不用于群聊。", "success");
    } catch (error) { if (session === state.session) message("public-message", error.message, "error"); }
    finally { if (session === state.session) { state.saving.public = false; $("public-fields").disabled = false; $("public-refresh").disabled = false; $("public-save").disabled = !state.dirty.public; } }
  }

  function revokeMemeUrls() {
    for (const url of state.memeUrls) URL.revokeObjectURL(url);
    state.memeUrls.clear();
    state.memeGeneration++;
  }
  function revokeFavoriteUrls() {
    for (const url of state.favoriteUrls) URL.revokeObjectURL(url);
    state.favoriteUrls.clear();
    state.favoriteGeneration++;
  }
  function revokeReceivedUrls() {
    clearReceivedPreview();
    state.receivedGeneration++;
  }
  function clearReceivedPreview() {
    for (const url of state.receivedUrls) URL.revokeObjectURL(url);
    state.receivedUrls.clear();
    state.receivedPreviewRequest++;
    state.receivedPreviewReady = false;
  }
  function resetReceivedSelection() {
    clearReceivedPreview();
    state.receivedSelection = null;
    $("received-image-form").reset();
    $("received-image-form").hidden = true;
    $("received-image-preview").replaceChildren();
    $("received-image-import").disabled = true;
    $("received-image-cancel").disabled = false;
    text("received-image-import", "导入所选图片");
    for (const field of $("received-image-form").querySelectorAll("input")) field.disabled = false;
    message("received-image-message");
    for (const button of $("received-images-grid").querySelectorAll("button")) button.setAttribute("aria-pressed", "false");
  }
  function tags(value) {
    const values = splitValues(value);
    if (values.length > 8 || values.some(item => item.length > 24 || /[\u0000-\u001f\u007f-\u009f]/.test(item)))
      throw new Error("标签最多 8 个，每个 1–24 字，不得包含控制字符。");
    return [...new Set(values)];
  }
  async function loadMemes(force = false) {
    if (state.loading.memes || (state.memesLoaded && !force)) return;
    if (force && all("#meme-grid .meme-card input").some(input => input.dataset.saved !== input.value) && !window.confirm("刷新会放弃尚未保存的表情标签，是否继续？")) return;
    const session = state.session;
    state.loading.memes = true;
    $("memes-refresh").disabled = true;
    message("memes-message", "正在读取表情库…");
    try {
      const values = await api("/api/memes");
      if (session !== state.session) return;
      revokeMemeUrls();
      const generation = state.memeGeneration;
      const items = Array.isArray(values) ? values : [];
      state.memesLoaded = true;
      $("meme-grid").replaceChildren();
      $("memes-empty").hidden = items.length > 0;
      const previews = [];
      for (const meme of items) {
        const card = element("article", null, "meme-card");
        const preview = element("div", null, "meme-preview");
        preview.append(element("p", "正在载入预览…"));
        card.append(preview, element("h3", meme.id, "meme-id"));
        const input = element("input");
        input.value = Array.isArray(meme.tags) ? meme.tags.join("，") : "";
        input.dataset.saved = input.value;
        input.setAttribute("aria-label", `表情 ${meme.id} 的标签`);
        input.placeholder = "添加标签，用逗号分隔";
        const label = element("label", "标签");
        const button = element("button", "保存标签", "button");
        button.type = "button";
        button.disabled = true;
        const remove = element("button", "删除", "button danger-quiet");
        remove.type = "button";
        remove.setAttribute("aria-label", `删除表情 ${meme.id}`);
        const feedback = element("p", "", "form-message");
        input.addEventListener("input", () => { button.disabled = input.value === input.dataset.saved; });
        button.addEventListener("click", async () => {
          let updated;
          try { updated = tags(input.value); } catch (error) { feedback.textContent = error.message; feedback.className = "form-message error"; return; }
          button.disabled = true;
          remove.disabled = true;
          input.disabled = true;
          button.textContent = "正在保存…";
          feedback.textContent = "正在保存标签…";
          feedback.className = "form-message";
          try {
            await api(`/api/memes/${encodeURIComponent(meme.id)}`, { method: "PUT", body: { tags: updated } });
            input.value = updated.join("，");
            input.dataset.saved = input.value;
            feedback.textContent = "标签已保存，下一轮回复生效";
            feedback.className = "form-message success";
          } catch (error) { feedback.textContent = error.message; feedback.className = "form-message error"; }
          finally { input.disabled = false; button.disabled = input.value === input.dataset.saved; remove.disabled = false; button.textContent = "保存标签"; }
        });
        remove.addEventListener("click", async () => {
          if (remove.disabled || !window.confirm(`确定从表情库删除 ${meme.id}？删除后机器人不再使用此项；本地原图保留。`)) return;
          remove.disabled = true;
          button.disabled = true;
          input.disabled = true;
          remove.textContent = "正在删除…";
          try {
            await api(`/api/memes/${encodeURIComponent(meme.id)}`, { method: "DELETE" });
            const image = preview.querySelector("img");
            if (image && state.memeUrls.delete(image.src)) URL.revokeObjectURL(image.src);
            card.remove();
            const remaining = all("#meme-grid .meme-card").length;
            $("memes-empty").hidden = remaining > 0;
            message("memes-message", `${meme.id} 已移出库，下一轮回复生效；本地原图保留。现有 ${remaining} 项。`, "success");
          } catch (error) { feedback.textContent = error.message; feedback.className = "form-message error"; }
          finally { remove.disabled = false; input.disabled = false; button.disabled = input.value === input.dataset.saved; remove.textContent = "删除"; }
        });
        const actions = element("div", null, "button-row");
        actions.append(button, remove);
        card.append(label, input, actions, feedback);
        $("meme-grid").append(card);
        previews.push({ meme, preview });
      }
      message("memes-message", `共 ${items.length} 张表情 · 点击标签即可编辑`);
      // Four workers bound preview requests even when the library is large.
      const workers = Array.from({ length: Math.min(4, previews.length) }, async () => {
        while (previews.length && generation === state.memeGeneration) {
          const next = previews.shift();
          await loadPreview(next.meme, next.preview, generation);
        }
      });
      await Promise.all(workers);
    } catch (error) { if (session === state.session) message("memes-message", `${error.message} 点击“刷新”重试。`, "error"); }
    finally { if (session === state.session) { state.loading.memes = false; $("memes-refresh").disabled = false; } }
  }
  async function loadPreview(meme, preview, generation) {
    if (Number.isInteger(meme.faceId)) {
      const names = { 0: "微笑", 4: "得意", 5: "流泪", 6: "害羞", 9: "大哭", 12: "调皮", 13: "呲牙", 14: "惊讶", 66: "爱心", 74: "太阳", 76: "赞", 79: "胜利", 99: "鼓掌" };
      const label = element("div", null, "qq-face-preview");
      label.append(element("strong", names[meme.faceId] || "QQ 表情"), element("span", `QQ 原生表情 #${meme.faceId}`));
      preview.replaceChildren(label); return;
    }
    try {
      const parsed = new URL(meme.previewUrl, window.location.href);
      if (parsed.origin !== window.location.origin || !parsed.pathname.startsWith("/api/memes/")) throw new Error("预览地址无效");
      const blob = await api(parsed.pathname + parsed.search, { blob: true });
      if (generation !== state.memeGeneration || !preview.isConnected) return;
      if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(blob.type.toLowerCase())) throw new Error("预览格式不支持");
      const url = URL.createObjectURL(blob);
      state.memeUrls.add(url);
      const image = element("img");
      image.alt = `表情 ${meme.id}`;
      image.loading = "lazy";
      image.src = url;
      preview.replaceChildren(image);
    } catch (error) {
      if (generation !== state.memeGeneration || !preview.isConnected) return;
      const retry = element("button", "重试预览", "button compact");
      retry.type = "button";
      retry.addEventListener("click", () => { preview.replaceChildren(element("p", "正在载入预览…")); loadPreview(meme, preview, generation); });
      retry.title = error.message;
      preview.replaceChildren(retry);
    }
  }
  async function addQqFace(event) {
    event.preventDefault();
    const session = state.session;
    const faceId = Number($("qq-face-id").value);
    let parsedTags;
    try {
      if (!Number.isInteger(faceId) || faceId < 0 || faceId > 65535) throw new Error("QQ 表情编号需为 0–65535 的整数。");
      parsedTags = tags($("qq-face-tags").value);
      if (!parsedTags.length) throw new Error("请为原生表情填写含义标签。");
    } catch (error) { message("qq-face-message", error.message, "error"); return; }
    $("qq-face-add").disabled = true;
    try {
      await api("/api/qq-faces", { method: "POST", body: { faceId, tags: parsedTags } });
      if (session !== state.session) return;
      message("qq-face-message", `qq_face_${faceId} 已收录，下一轮回复生效。`, "success");
      await loadMemes(true);
    } catch (error) { if (session === state.session) message("qq-face-message", error.message, "error"); }
    finally { if (session === state.session) $("qq-face-add").disabled = false; }
  }
  async function loadQqFavorites() {
    if (state.favoritesLoading) return;
    const session = state.session;
    state.favoritesLoading = true;
    $("qq-favorites-refresh").disabled = true;
    message("qq-favorites-message", "正在读取当前机器人 QQ 的收藏表情…");
    revokeFavoriteUrls();
    const generation = state.favoriteGeneration;
    $("qq-favorites-grid").replaceChildren();
    try {
      const items = await api("/api/qq-favorites");
      if (generation !== state.favoriteGeneration) return;
      message("qq-favorites-message", items.length ? `读取到 ${items.length} 项。填写标签后单独导入；尚未加入模型可用库。` : "当前账号没有可读取的收藏表情。先在手机 QQ 收藏后重试。");
      const previews = [];
      for (const item of items) {
        const card = element("article", null, "meme-card");
        const preview = element("div", null, "meme-preview");
        preview.append(element("p", "正在载入收藏预览…"));
        const form = element("form", null, "favorite-form");
        const idInput = element("input"); idInput.value = `qq_fav_${item.id.slice(0, 20)}`;
        idInput.required = true; idInput.maxLength = 48; idInput.setAttribute("aria-label", "收藏表情导入 ID");
        const tagInput = element("input"); tagInput.placeholder = "例如：开心，赞同"; tagInput.required = true;
        tagInput.setAttribute("aria-label", "收藏表情标签");
        const button = element("button", "导入这一张", "button"); button.type = "submit";
        const feedback = element("p", "", "form-message");
        form.append(element("label", "导入 ID"), idInput, element("label", "含义标签"), tagInput, button, feedback);
        form.addEventListener("submit", async event => {
          event.preventDefault();
          let parsedTags;
          try {
            parsedTags = tags(tagInput.value);
            if (!parsedTags.length) throw new Error("请填写含义标签。");
            if (!/^[a-z0-9][a-z0-9_-]{0,47}$/.test(idInput.value) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(idInput.value)) throw new Error("请填写有效表情 ID。");
          } catch (error) { feedback.textContent = error.message; return; }
          button.disabled = true; idInput.disabled = true; tagInput.disabled = true;
          try {
            await api("/api/qq-favorites/import", { method: "POST", body: { favoriteId: item.id, id: idInput.value, tags: parsedTags } });
            feedback.textContent = "已导入，下一轮回复生效。";
            button.textContent = "已导入";
            await loadMemes(true);
          } catch (error) { feedback.textContent = error.message; button.disabled = false; idInput.disabled = false; tagInput.disabled = false; }
        });
        card.append(preview, form); $("qq-favorites-grid").append(card);
        previews.push({ item, preview });
      }
      const workers = Array.from({ length: Math.min(2, previews.length) }, async () => {
        while (previews.length && generation === state.favoriteGeneration) {
          const { item, preview } = previews.shift();
          try {
            const parsed = new URL(item.previewUrl, window.location.href);
            if (parsed.origin !== window.location.origin || !parsed.pathname.startsWith("/api/qq-favorites/")) throw new Error("收藏预览地址无效。");
            const blob = await api(parsed.pathname, { blob: true });
            if (generation !== state.favoriteGeneration) continue;
            if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(blob.type.toLowerCase())) throw new Error("收藏格式不支持。");
            const url = URL.createObjectURL(blob); state.favoriteUrls.add(url);
            const img = element("img"); img.alt = "QQ 收藏表情预览"; img.src = url; preview.replaceChildren(img);
          } catch (error) { if (generation === state.favoriteGeneration) preview.replaceChildren(element("p", `预览失败：${error.message} 可重新读取收藏。`)); }
        }
      });
      await Promise.all(workers);
    } catch (error) { if (session === state.session) message("qq-favorites-message", `${error.message} 请确认 NapCat 在线并登录机器人账号，再重新读取。`, "error"); }
    finally { if (session === state.session) { state.favoritesLoading = false; $("qq-favorites-refresh").disabled = false; } }
  }
  async function loadReceivedImages() {
    if (state.receivedLoading || state.receivedImporting) return;
    state.receivedLoading = true;
    $("received-images-refresh").disabled = true;
    revokeReceivedUrls();
    resetReceivedSelection();
    const generation = state.receivedGeneration;
    $("received-images-grid").replaceChildren();
    message("received-images-message", "正在读取最近收到的图片信息，不会自动下载图片…");
    try {
      const items = await api("/api/received-images");
      if (generation !== state.receivedGeneration) return;
      if (!Array.isArray(items)) throw new Error("图片候选列表格式不正确。");
      for (const item of items) {
        const card = element("article", null, "received-item");
        const detail = element("div");
        detail.append(element("h3", item.isPrivate ? `私聊 · QQ ${item.senderId}` : `群 ${item.groupId} · QQ ${item.senderId}`),
          element("p", `收到于 ${date(item.at)} · 消息 ${item.messageId}`, "muted"));
        if (item.isPrivate) detail.append(element("span", "私聊图片：导入前请确认可公开复用", "pill warning"));
        const choose = element("button", "选择并预览", "button compact");
        choose.type = "button";
        choose.setAttribute("aria-pressed", "false");
        choose.addEventListener("click", () => {
          if (!state.receivedImporting && generation === state.receivedGeneration) selectReceivedImage(item, choose, generation);
        });
        card.append(detail, choose); $("received-images-grid").append(card);
      }
      message("received-images-message", items.length ? `共 ${items.length} 项近期图片，仅显示来源信息。请只选择你确认可复用的一张。` : "暂无可用图片。请从其他 QQ 将收藏表情作为图片发到授权群或机器人私聊，再点击读取；候选 10 分钟后过期。");
    } catch (error) {
      if (generation === state.receivedGeneration) message("received-images-message", `${error.message} 可重新读取；候选过期时请重新发送图片。`, "error");
    } finally {
      if (generation === state.receivedGeneration) { state.receivedLoading = false; $("received-images-refresh").disabled = false; }
    }
  }
  async function selectReceivedImage(item, button, generation) {
    resetReceivedSelection();
    state.receivedSelection = item;
    const request = state.receivedPreviewRequest;
    button.setAttribute("aria-pressed", "true");
    $("received-image-form").hidden = false;
    text("received-image-title", "所选图片 · 请审核后导入");
    text("received-image-source", item.isPrivate ? `来自 QQ ${item.senderId} 的私聊 · ${date(item.at)}` : `来自群 ${item.groupId} · QQ ${item.senderId} · ${date(item.at)}`);
    $("received-image-id").value = `received_${item.id.slice(0, 20)}`;
    $("received-image-preview").append(element("p", "正在下载所选图片预览…"));
    message("received-image-message", "预览成功后，请填写含义标签并确认复用范围。");
    try {
      const parsed = new URL(item.previewUrl, window.location.href);
      if (parsed.origin !== window.location.origin || parsed.pathname !== `/api/received-images/${encodeURIComponent(item.id)}/image` || parsed.search || parsed.hash) throw new Error("图片预览地址无效。");
      const blob = await api(parsed.pathname, { blob: true, timeout: 25000 });
      if (generation !== state.receivedGeneration || request !== state.receivedPreviewRequest) return;
      if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(blob.type.toLowerCase()) || !blob.size || blob.size > 5 * 1024 * 1024) throw new Error("仅支持 5 MiB 以内的 PNG、JPEG、GIF 或 WebP 图片。");
      const url = URL.createObjectURL(blob); state.receivedUrls.add(url);
      const img = element("img"); img.alt = "管理员选中的待导入图片预览";
      await new Promise((resolve, reject) => {
        img.addEventListener("load", resolve, { once: true });
        img.addEventListener("error", () => reject(new Error("图片内容无法显示，请重新选择或使用本地图片导入。")), { once: true });
        img.src = url;
      });
      if (generation !== state.receivedGeneration || request !== state.receivedPreviewRequest) return;
      $("received-image-preview").replaceChildren(img);
      state.receivedPreviewReady = true;
      $("received-image-import").disabled = false;
    } catch (error) {
      if (generation !== state.receivedGeneration || request !== state.receivedPreviewRequest) return;
      clearReceivedPreview();
      $("received-image-preview").replaceChildren(element("p", "预览未成功，不可导入。"));
      message("received-image-message", `${error.message} 候选可能已过期，请重新读取或发送图片。`, "error");
    }
  }
  async function importReceivedImage(event) {
    event.preventDefault();
    if (state.receivedImporting || !state.receivedPreviewReady || !state.receivedSelection) return;
    let parsedTags;
    const id = $("received-image-id").value.trim();
    try {
      if (!/^[a-z0-9][a-z0-9_-]{0,47}$/.test(id) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(id)) throw new Error("请填写有效表情 ID：小写字母或数字开头，后续仅限字母、数字、下划线和短横线，最多 48 位。");
      parsedTags = tags($("received-image-tags").value);
      if (!parsedTags.length) throw new Error("请填写图片的含义标签，模型会据此选择。");
      if (!$("received-image-approved").checked) throw new Error("请先确认图片可在所有授权群和私聊中复用。");
    } catch (error) { message("received-image-message", error.message, "error"); return; }
    const generation = state.receivedGeneration;
    const body = { receivedId: state.receivedSelection.id, id, tags: parsedTags, reuseApproved: true };
    state.receivedImporting = true;
    $("received-images-refresh").disabled = true;
    $("received-image-import").disabled = true;
    $("received-image-cancel").disabled = true;
    for (const field of $("received-image-form").querySelectorAll("input")) field.disabled = true;
    for (const button of $("received-images-grid").querySelectorAll("button")) button.disabled = true;
    message("received-image-message", "正在导入所选图片；完成后下一轮回复生效。");
    let imported = false;
    try {
      await api("/api/received-images/import", { method: "POST", body, timeout: 25000 });
      if (generation !== state.receivedGeneration) return;
      imported = true;
      message("received-image-message", `${id} 已导入，下一轮回复可按标签选择发送。`, "success");
      text("received-image-import", "已导入");
      await loadMemes(true);
    } catch (error) {
      if (generation === state.receivedGeneration) message("received-image-message", `${error.message} 如提示超时或连接中断，请先刷新表情库确认结果，再重试。`, "error");
    } finally {
      if (generation === state.receivedGeneration) {
        state.receivedImporting = false;
        $("received-images-refresh").disabled = false;
        $("received-image-cancel").disabled = false;
        $("received-image-import").disabled = imported || !state.receivedPreviewReady;
        for (const field of $("received-image-form").querySelectorAll("input")) field.disabled = imported;
        for (const button of $("received-images-grid").querySelectorAll("button")) button.disabled = false;
      }
    }
  }
  function fileBase64(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.addEventListener("load", () => { const raw = String(reader.result); resolve(raw.slice(raw.indexOf(",") + 1)); });
      reader.addEventListener("error", () => reject(new Error("无法读取所选文件，请重新选择。")));
      reader.readAsDataURL(file);
    });
  }
  function resetMemeBatch() {
    state.memeBatchGeneration++;
    for (const url of state.memeBatchUrls) URL.revokeObjectURL(url);
    state.memeBatchUrls.clear();
    state.memeBatchRows = [];
    state.memeBatchBusy = false;
    $("meme-batch-files").value = "";
    $("meme-batch-rows").replaceChildren();
    message("meme-batch-message", "每批最多 20 张、总共 20 MiB；标签由你填写，不会自动看图或调用模型。");
    updateMemeBatchControls();
  }
  function updateMemeBatchControls() {
    const busy = state.memeBatchBusy;
    for (const field of $("meme-batch-form").querySelectorAll("input,button")) field.disabled = busy;
    for (const row of state.memeBatchRows) {
      const locked = busy || ["imported", "found", "unknown", "invalid"].includes(row.status);
      row.idInput.disabled = locked;
      row.tagInput.disabled = locked;
    }
    $("meme-batch-import").disabled = busy || !state.memeBatchRows.some(row => ["ready", "failed"].includes(row.status));
    $("meme-batch-check").disabled = busy || !state.memeBatchRows.some(row => row.status === "unknown");
    $("meme-batch-clear").disabled = busy || !state.memeBatchRows.length;
  }
  function setMemeBatchRow(row, status, value) {
    row.status = status;
    row.feedback.textContent = value;
    row.feedback.className = `form-message${status === "imported" ? " success" : ["failed", "invalid", "unknown"].includes(status) ? " error" : ""}`;
  }
  function batchMemeId(file, index, used) {
    let id = file.name.replace(/\.[^.]+$/, "").toLowerCase().replace(/[^a-z0-9_-]+/g, "_").replace(/^[^a-z0-9]+/, "").slice(0, 40);
    if (!id || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(id)) id = `meme_${index + 1}_${crypto.randomUUID().slice(0, 8)}`;
    const stem = id;
    let suffix = 2;
    while (used.has(id)) id = `${stem}_${suffix++}`;
    used.add(id);
    return id;
  }
  async function selectMemeBatch() {
    if (state.memeBatchBusy) return;
    const files = [...$("meme-batch-files").files];
    if (!files.length) return;
    if (files.length > 20 || files.reduce((sum, file) => sum + file.size, 0) > 20 * 1024 * 1024) {
      $("meme-batch-files").value = "";
      message("meme-batch-message", "每批最多 20 张、总共 20 MiB；请减少文件后重选，原有结果保留。", "error"); return;
    }
    if (state.memeBatchRows.length && !window.confirm("更换所选文件会清除当前导入草稿与结果；已成功入库的图片保留。是否继续？")) { $("meme-batch-files").value = ""; return; }
    resetMemeBatch();
    const generation = state.memeBatchGeneration;
    const session = state.session;
    state.memeBatchBusy = true;
    updateMemeBatchControls();
    let existing = [];
    try { existing = await api("/api/memes"); }
    catch { /* The server still rejects any collision if the current library could not be listed. */ }
    if (generation !== state.memeBatchGeneration || session !== state.session) return;
    const used = new Set(existing.map(item => item.id));
    for (let index = 0; index < files.length; index++) {
      const file = files[index];
      const card = element("article", null, "meme-card");
      const row = { clientId: `row-${index + 1}`, file, status: "ready", card };
      card.append(element("h3", file.name, "meme-id"), element("p", `${(file.size / 1024).toFixed(1)} KiB`, "muted"));
      const validFile = file.size > 0 && file.size <= 5 * 1024 * 1024 && ["image/png", "image/jpeg", "image/gif", "image/webp"].includes(file.type.toLowerCase());
      if (validFile) {
        const preview = element("div", null, "meme-preview");
        const image = element("img");
        image.alt = `待导入 ${file.name}`;
        image.src = URL.createObjectURL(file);
        row.previewUrl = image.src;
        state.memeBatchUrls.add(image.src);
        preview.append(image); card.append(preview);
      }
      row.idInput = element("input");
      row.idInput.value = batchMemeId(file, index, used);
      row.idInput.maxLength = 48;
      row.idInput.setAttribute("aria-label", `${file.name} 的批量导入 ID`);
      row.tagInput = element("input");
      row.tagInput.setAttribute("aria-label", `${file.name} 的批量标签`);
      row.tagInput.placeholder = "手填标签，逗号分隔；可留空";
      row.feedback = element("p", "等待导入", "form-message");
      const remove = element("button", "移除此项", "button compact");
      remove.type = "button";
      remove.setAttribute("aria-label", `移除待导入文件 ${file.name}`);
      remove.addEventListener("click", () => {
        if (state.memeBatchBusy) return;
        state.memeBatchRows = state.memeBatchRows.filter(value => value !== row);
        if (row.previewUrl) { URL.revokeObjectURL(row.previewUrl); state.memeBatchUrls.delete(row.previewUrl); }
        card.remove(); updateMemeBatchControls();
      });
      card.append(element("label", "表情 ID"), row.idInput, element("label", "含义标签（可选）"), row.tagInput, row.feedback, remove);
      state.memeBatchRows.push(row);
      $("meme-batch-rows").append(card);
      if (!validFile) setMemeBatchRow(row, "invalid", "文件为空、超过 5 MiB 或格式不支持；请移除此项并重选。");
    }
    state.memeBatchBusy = false;
    message("meme-batch-message", `已选择 ${files.length} 项。请检查每项 ID 和标签；成功项不会重复提交。`);
    updateMemeBatchControls();
  }
  function applyMemeBatchTags() {
    let shared;
    try { shared = tags($("meme-batch-tags").value); }
    catch (error) { message("meme-batch-message", error.message, "error"); return; }
    for (const row of state.memeBatchRows.filter(row => ["ready", "failed"].includes(row.status))) row.tagInput.value = shared.join("，");
    message("meme-batch-message", "通用标签已填入未导入项，仍可逐项修改；尚未入库。");
  }
  async function importMemeBatch(event) {
    event.preventDefault();
    if (state.memeBatchBusy) return;
    const rows = state.memeBatchRows.filter(row => ["ready", "failed"].includes(row.status));
    if (!rows.length) return;
    const used = new Set();
    const submissions = [];
    for (const row of rows) {
      const id = row.idInput.value.trim();
      try {
        if (!/^[a-z0-9][a-z0-9_-]{0,47}$/.test(id) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(id)) throw new Error("ID 需为 1～48 位安全小写英文、数字、下划线或短横线。");
        if (used.has(id)) throw new Error("本批 ID 重复，请改成不同 ID 后重试。");
        used.add(id);
        submissions.push({ row, id, tags: tags(row.tagInput.value) });
      } catch (error) { setMemeBatchRow(row, "failed", error.message); }
    }
    if (!submissions.length) { updateMemeBatchControls(); return; }
    const generation = state.memeBatchGeneration;
    const session = state.session;
    state.memeBatchBusy = true;
    updateMemeBatchControls();
    let submitted = false;
    try {
      const items = [];
      for (const item of submissions) {
        setMemeBatchRow(item.row, "ready", "正在读取图片…");
        items.push({ clientId: item.row.clientId, id: item.id, tags: item.tags, base64: await fileBase64(item.row.file) });
        if (generation !== state.memeBatchGeneration || session !== state.session) return;
      }
      message("meme-batch-message", `正在导入 ${items.length} 项，请等待逐项结果…`);
      submitted = true;
      const response = await api("/api/memes/batch", { method: "POST", body: { items }, timeout: 60000 });
      if (generation !== state.memeBatchGeneration || session !== state.session) return;
      if (!Array.isArray(response?.results)) throw new Error("批量结果格式无法确认。");
      for (const item of submissions) {
        const result = response.results.find(value => value.clientId === item.row.clientId);
        if (result?.status === "imported" && result.id === item.id) setMemeBatchRow(item.row, "imported", "已导入，下一轮回复生效。");
        else if (result?.status === "failed") setMemeBatchRow(item.row, "failed", result.message || "此项失败，请检查后重试。");
        else if (result?.status === "not_attempted") setMemeBatchRow(item.row, "failed", "取消前尚未尝试；可再次导入。");
        else setMemeBatchRow(item.row, "unknown", "结果未确认，请先点击“核对结果”。");
      }
      const imported = state.memeBatchRows.filter(row => row.status === "imported").length;
      const failed = state.memeBatchRows.filter(row => ["failed", "invalid"].includes(row.status)).length;
      const unknown = state.memeBatchRows.filter(row => row.status === "unknown").length;
      message("meme-batch-message", `当前批次：成功 ${imported} 项，失败或待修改 ${failed} 项，结果未知 ${unknown} 项；成功项保留，不会再次提交。`, failed || unknown ? "error" : "success");
      await loadMemes(true);
    } catch (error) {
      if (generation !== state.memeBatchGeneration || session !== state.session) return;
      for (const item of submissions) if (item.row.status !== "imported") setMemeBatchRow(item.row, submitted ? "unknown" : "failed",
        submitted ? "请求中断，结果未知；先核对清单，勿直接重复导入。" : "读取文件失败，此项尚未提交。");
      message("meme-batch-message", submitted ? "本次结果未确认，已成功项保留。请点击“核对结果”后再决定重试。" : error.message, "error");
    } finally {
      if (generation === state.memeBatchGeneration && session === state.session) { state.memeBatchBusy = false; updateMemeBatchControls(); }
    }
  }
  async function checkMemeBatch() {
    if (state.memeBatchBusy) return;
    const generation = state.memeBatchGeneration;
    const session = state.session;
    state.memeBatchBusy = true;
    updateMemeBatchControls();
    try {
      const catalog = await api("/api/memes");
      if (generation !== state.memeBatchGeneration || session !== state.session) return;
      const ids = new Set(catalog.map(item => item.id));
      for (const row of state.memeBatchRows.filter(row => row.status === "unknown")) {
        if (ids.has(row.idInput.value.trim())) setMemeBatchRow(row, "found", "库中已有此 ID，请在上方预览核对；本行不再自动提交。");
        else setMemeBatchRow(row, "ready", "清单中没有此 ID，可再次尝试；服务仍会拒绝覆盖冲突。");
      }
      message("meme-batch-message", "清单已核对；已存在项请预览确认，其余项可以重试。不会自动重试。");
      await loadMemes(true);
    } catch (error) { if (generation === state.memeBatchGeneration && session === state.session) message("meme-batch-message", error.message, "error"); }
    finally { if (generation === state.memeBatchGeneration && session === state.session) { state.memeBatchBusy = false; updateMemeBatchControls(); } }
  }
  async function uploadMeme(event) {
    event.preventDefault();
    const session = state.session;
    const file = $("meme-file").files[0];
    const id = $("meme-id").value.trim();
    let parsedTags;
    try {
      if (!file) throw new Error("请先选择图片文件。");
      if (file.size > 5 * 1024 * 1024) throw new Error("图片不能超过 5 MiB。");
      if (!file.size) throw new Error("图片文件为空，请重新选择。");
      if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(file.type.toLowerCase())) throw new Error("仅支持 PNG、JPEG、GIF 或 WebP 图片。");
      if (!/^[a-z0-9][a-z0-9_-]{0,47}$/.test(id) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(id))
        throw new Error("表情 ID 须以小写字母或数字开头，后续仅限小写字母、数字、下划线和短横线，最多 48 位；不能使用 con、nul 等系统保留名称。");
      parsedTags = tags($("meme-tags").value);
    } catch (error) { message("meme-upload-message", error.message, "error"); return; }
    const fields = [...$("meme-upload-form").querySelectorAll("input,button")];
    for (const field of fields) field.disabled = true;
    message("meme-upload-message", "正在读取图片…");
    text("meme-upload", "正在导入…");
    try {
      const base64 = await fileBase64(file);
      if (session !== state.session) return;
      message("meme-upload-message", "正在导入图片与标签，请稍候…");
      await api("/api/memes", { method: "POST", body: { id, tags: parsedTags, base64 }, timeout: 60000 });
      if (session !== state.session) return;
      $("meme-upload-form").reset();
      message("meme-upload-message", `表情 ${id} 已导入，下一轮回复生效。`, "success");
      await loadMemes(true);
    } catch (error) { if (session === state.session) message("meme-upload-message", error.message, "error"); }
    finally { if (session === state.session) { for (const field of fields) field.disabled = false; text("meme-upload", "导入表情"); } }
  }

  function scheduleDate(value) {
    return value ? new Date(value).toLocaleString("zh-CN", { timeZone: "Asia/Shanghai", hour12: false }) : "—";
  }
  function updateScheduleType() {
    const daily = $("schedule-type").value === "daily";
    $("schedule-once-field").hidden = daily;
    $("schedule-daily-field").hidden = !daily;
    $("schedule-once").required = !daily;
    $("schedule-daily").required = daily;
  }
  function resetScheduleEditor() {
    state.scheduleId = null; state.scheduleRevision = null;
    $("schedule-form").reset();
    text("schedule-editor-heading", "添加定时消息");
    message("schedule-message", "新建任务默认停用；勾选启用后才会到时发送。");
    updateScheduleType();
  }
  function scheduleStatus(value) {
    return ({ claimed: "已预约，等待发送", api_accepted: "QQ 已接收", preview: "仅预览，未发送", delivery_unknown: "投递不确定，请先检查 QQ，勿直接重试",
      failed: "发送失败", cancelled: "已取消", skipped_paused: "暂停时跳过", skipped_expired: "超过发送窗口，已跳过", skipped_offline: "离线时刻，已跳过",
      skipped_busy: "忙碌或达到独立限频，已跳过", skipped_not_authorized: "目标授权已变化，已跳过", account_changed_paused: "账号改变，已暂停" })[value] || value || "尚未执行";
  }
  function preserveScheduleOption(id, value, missingLabel) {
    if (!value) return;
    if (![...$(id).options].some(option => option.value === value)) {
      const option = element("option", missingLabel); option.value = value; option.disabled = true; $(id).append(option);
    }
    $(id).value = value;
  }
  function renderSchedules() {
    const list = $("schedules-list"); list.replaceChildren();
    for (const task of state.schedules) {
      const definition = task.definition;
      const card = element("article", null, "memory-item schedule-item");
      card.append(element("h3", definition.name), element("p", `${definition.enabled ? "已启用" : "已停用"} · ${definition.targetType === "group" ? "群" : "私聊"} ${definition.targetId}`, "muted"),
        element("p", definition.scheduleType === "daily" ? `每天 ${definition.dailyTime}（北京时间）` : `${scheduleDate(definition.onceAt)}（北京时间）`),
        element("p", definition.text || "仅表情"));
      if (definition.memeId) card.append(element("p", `表情：${definition.memeId}`, "muted"));
      card.append(element("p", `${scheduleStatus(task.lastRun?.status)}${task.lastRun ? ` · ${scheduleDate(task.lastRun.dueAt)}` : ""}`, "field-help"));
      if (task.lastRun?.messageId) card.append(element("p", `消息编号：${task.lastRun.messageId}`, "field-help"));
      const row = element("div", null, "button-row");
      const edit = element("button", "编辑", "button compact"); edit.type = "button";
      edit.addEventListener("click", () => editSchedule(task));
      const toggle = element("button", definition.enabled ? "停用" : "启用", "button compact"); toggle.type = "button";
      toggle.addEventListener("click", () => changeSchedule(task, false));
      const remove = element("button", "删除", "button compact danger-quiet"); remove.type = "button";
      remove.addEventListener("click", () => changeSchedule(task, true));
      for (const button of [edit, toggle, remove]) button.disabled = state.scheduleBusy;
      row.append(edit, toggle, remove); card.append(row); list.append(card);
    }
    if (!state.schedules.length) list.append(element("p", "暂无定时消息。可在右侧添加，默认停用。", "empty-copy"));
  }
  async function loadSchedules(options = true) {
    const generation = ++state.scheduleGeneration;
    $("schedules-refresh").disabled = true;
    try {
      const values = await api("/api/schedules");
      if (generation !== state.scheduleGeneration) return;
      if (!Array.isArray(values)) throw new Error("任务列表格式不正确。");
      state.schedules = values; renderSchedules();
      message("schedules-message", `已读取 ${values.length} / 64 项任务。状态每次刷新后更新。`, "success");
      if (options) {
        const [settings, memes] = await Promise.all([api("/api/settings"), api("/api/memes")]);
        if (generation !== state.scheduleGeneration) return;
        const target = $("schedule-target").value, meme = $("schedule-meme").value;
        $("schedule-target").replaceChildren();
        const activeGroup = settings.activeGroupId || state.status?.runtime?.groupId || settings.allowedGroupIds?.[0];
        if (settings.allowedGroupIds?.includes(activeGroup)) {
          const option = element("option", `群 ${activeGroup}`); option.value = `group:${activeGroup}`; $("schedule-target").append(option);
        }
        for (const user of settings.allowedPrivateUserIds || []) {
          const option = element("option", `私聊 ${user}`); option.value = `private:${user}`; $("schedule-target").append(option);
        }
        preserveScheduleOption("schedule-target", target, "原目标已不在当前授权范围，请重新选择");
        $("schedule-meme").replaceChildren(element("option", "不附表情")); $("schedule-meme").options[0].value = "";
        for (const asset of memes) { const option = element("option", `${asset.id} · ${(asset.tags || []).join("、")}`); option.value = asset.id; $("schedule-meme").append(option); }
        preserveScheduleOption("schedule-meme", meme, "原表情已不在表情库，请重新选择");
      }
    } catch (error) { if (generation === state.scheduleGeneration) message("schedules-message", error.message, "error"); }
    finally { if (generation === state.scheduleGeneration) $("schedules-refresh").disabled = false; }
  }
  function editSchedule(task) {
    if (state.scheduleBusy) return;
    state.scheduleId = task.id; state.scheduleRevision = task.revision;
    const definition = task.definition;
    $("schedule-name").value = definition.name;
    preserveScheduleOption("schedule-target", `${definition.targetType}:${definition.targetId}`, "原目标已不在当前授权范围，请重新选择");
    $("schedule-type").value = definition.scheduleType;
    // datetime-local has no zone: format from the specified fixed Shanghai offset, never the browser's local zone.
    $("schedule-once").value = definition.onceAt ? new Date(new Date(definition.onceAt).getTime() + 8 * 3600000).toISOString().slice(0, 16) : "";
    $("schedule-daily").value = definition.dailyTime || "20:00";
    $("schedule-text").value = definition.text;
    $("schedule-meme").value = definition.memeId || "";
    if (definition.memeId) preserveScheduleOption("schedule-meme", definition.memeId, "原表情已不在表情库，请重新选择");
    $("schedule-enabled").checked = definition.enabled;
    text("schedule-editor-heading", `编辑：${definition.name}`);
    message("schedule-message", "保存会取消此任务尚未提交的旧版本。已提交给 QQ 的消息无法撤回。");
    updateScheduleType();
  }
  function setScheduleBusy(value) {
    state.scheduleBusy = value;
    $("schedule-fields").disabled = value; $("schedule-save").disabled = value; $("schedule-cancel").disabled = value;
    renderSchedules();
  }
  async function saveSchedule(event) {
    event.preventDefault(); if (state.scheduleBusy || !$("schedule-form").reportValidity()) return;
    const [targetType, targetId] = $("schedule-target").value.split(":");
    const type = $("schedule-type").value;
    const definition = { name: $("schedule-name").value.trim(), enabled: $("schedule-enabled").checked,
      targetType, targetId: Number(targetId), scheduleType: type, timeZone: "Asia/Shanghai",
      onceAt: type === "once" ? `${$("schedule-once").value}:00+08:00` : null,
      dailyTime: type === "daily" ? $("schedule-daily").value : null,
      text: $("schedule-text").value, memeId: $("schedule-meme").value || null };
    if (!definition.text.trim() && !definition.memeId) { message("schedule-message", "请填写正文或选择一个已入库表情。", "error"); return; }
    const session = state.session; setScheduleBusy(true);
    try {
      await api(state.scheduleId ? `/api/schedules/${state.scheduleId}` : "/api/schedules", {
        method: state.scheduleId ? "PUT" : "POST", body: state.scheduleId ? { revision: state.scheduleRevision, definition } : definition });
      if (session !== state.session) return;
      resetScheduleEditor(); await loadSchedules(false);
      message("schedule-message", "任务已保存并立即生效；不需要应用全部设置。", "success");
    } catch (error) { if (session === state.session) message("schedule-message", error.message, "error"); }
    finally { if (session === state.session) setScheduleBusy(false); }
  }
  async function changeSchedule(task, remove) {
    if (state.scheduleBusy || !window.confirm(remove ? `删除“${task.definition.name}”？未提交的发送会取消。` : `${task.definition.enabled ? "停用" : "启用"}“${task.definition.name}”？`)) return;
    const session = state.session; setScheduleBusy(true);
    try {
      await api(`/api/schedules/${task.id}${remove ? `?revision=${task.revision}` : ""}`, remove
        ? { method: "DELETE" } : { method: "PUT", body: { revision: task.revision, definition: { ...task.definition, enabled: !task.definition.enabled } } });
      if (session !== state.session) return;
      if (state.scheduleId === task.id) resetScheduleEditor();
      await loadSchedules(false);
    } catch (error) { if (session === state.session) message("schedules-message", error.message, "error"); }
    finally { if (session === state.session) setScheduleBusy(false); }
  }

  async function navigate(view) {
    if (!titles[view]) return;
    if (state.view === "persona" && view !== "persona") {
      if (state.personaLearningConfirming) { message("persona-learning-message", "正在确认保存，请等完成后切换页面。"); return; }
      if (state.personaLearningDraft && !window.confirm("离开角色页会丢弃尚未确认的角色草稿，上方主表单修改仍保留。是否继续？")) return;
      clearPersonaLearning();
      state.personaLearningSourceRequest++; state.personaLearningSourcesLoading = false;
      message("persona-learning-message", "上次未确认草稿或尚未完成的结果已丢弃；可选择联系人重新生成。");
    }
    state.view = view;
    for (const item of all("[data-view]")) {
      const active = item.dataset.view === view;
      item.classList.toggle("active", active);
      if (active) item.setAttribute("aria-current", "page"); else item.removeAttribute("aria-current");
    }
    for (const section of all(".view")) section.hidden = section.id !== `view-${view}`;
    text("breadcrumb-title", titles[view][0]);
    text("page-title", titles[view][1]);
    text("page-description", titles[view][2]);
    if (view === "persona") { await loadPersona(); if (state.view === "persona" && state.token) await loadPersonaSources(); }
    if (view === "settings" || view === "memories") await loadSettings();
    if (view === "memes") await loadMemes();
    if (view === "schedules") { updateScheduleType(); await loadSchedules(); }
    if (view === "companion") await companionPage.load();
  }
  async function connect(token) {
    state.token = token.trim();
    if (!state.token) { message("login-message", "请运行 open-panel.cmd 自动连接，或粘贴你安全获取的本机管理令牌。", "error"); return; }
    const session = ++state.session;
    $("login-submit").disabled = true;
    text("login-submit", "正在连接…");
    message("login-message", "正在连接本地管理服务…");
    try {
      const payload = await api("/api/status", { timeout: 12000 });
      if (session !== state.session) return;
      storeToken(state.token);
      $("login-token").value = "";
      $("login-screen").hidden = true;
      $("app-shell").hidden = false;
      renderStatus(payload);
      await navigate(state.view);
      if (session !== state.session) return;
      schedulePolling();
    } catch (error) { if (session === state.session) message("login-message", error.message, "error"); }
    finally { if (session === state.session) { $("login-submit").disabled = false; text("login-submit", "连接控制台"); } }
  }

  $("login-form").addEventListener("submit", event => { event.preventDefault(); connect($("login-token").value); });
  $("logout-button").addEventListener("click", () => {
    if (hasUncommittedPageData() && !window.confirm("退出会丢弃页面中尚未保存的修改和批量导入草稿；若有正在提交的保存或导入，结果可能未知，重新连接后须刷新核对，不会自动重试。是否继续？")) return;
    state.allowPageExit = true;
    showLogin();
    window.location.reload();
  });
  for (const button of all("[data-view]")) button.addEventListener("click", () => navigate(button.dataset.view));
  document.querySelector(".brand").addEventListener("click", event => { event.preventDefault(); navigate("overview"); });
  $("refresh-button").addEventListener("click", refreshStatus);
  $("schedule-type").addEventListener("change", updateScheduleType);
  $("schedule-form").addEventListener("submit", saveSchedule);
  $("schedules-refresh").addEventListener("click", () => loadSchedules());
  $("schedule-cancel").addEventListener("click", resetScheduleEditor);
  $("pause-button").addEventListener("click", () => control("pause"));
  $("resume-button").addEventListener("click", () => control("resume"));
  $("apply-button").addEventListener("click", () => control("apply"));
  $("stop-button").addEventListener("click", () => control("stop"));
  $("persona-form").addEventListener("input", () => {
    markDirty("persona"); message("persona-message", "有未保存的修改。"); updatePersonaLearningControls();
    if (state.personaLearningDraft) message("persona-learning-message", "上方角色表单有未保存的修改，已保留；确认草稿暂不可用，请先处理后重新生成。");
  });
  $("persona-meme").addEventListener("input", updateMemeRange);
  $("persona-form").addEventListener("submit", savePersona);
  $("persona-retry").addEventListener("click", () => loadPersona(true));
  $("persona-learning-refresh").addEventListener("click", loadPersonaSources);
  $("persona-learning-generate").addEventListener("click", generatePersonaLearning);
  $("persona-learning-form").addEventListener("submit", confirmPersonaLearning);
  $("persona-learning-discard").addEventListener("click", () => { if (!state.personaLearningConfirming) { clearPersonaLearning(); message("persona-learning-message", "草稿已丢弃，现有角色和主表单编辑均保留。"); } });
  $("persona-learning-user").addEventListener("change", () => {
    clearPersonaLearning(); message("persona-learning-message", "已切换历史来源，旧草稿或迟到结果已丢弃。点击生成才会调用模型。");
  });
  $("settings-form").addEventListener("input", () => { markDirty("settings"); message("settings-message", "有未保存的修改。"); });
  $("settings-groups").addEventListener("input", () => renderActiveGroupOptions());
  $("settings-active-group").addEventListener("change", () => { $("settings-active-group").dataset.preferredGroup = $("settings-active-group").value; });
  $("settings-form").addEventListener("submit", saveSettings);
  $("settings-retry").addEventListener("click", () => loadSettings(true));
  $("memory-select-form").addEventListener("submit", event => { event.preventDefault(); selectUser($("memory-user").value); });
  $("memory-form").addEventListener("submit", saveMemory);
  $("memory-text").addEventListener("input", () => { $("memory-save").disabled = !$("memory-text").value.trim() || state.saving.memory; message("memory-message", $("memory-text").value.trim() ? "笔记尚未保存。" : ""); });
  $("memory-cancel").addEventListener("click", () => { if (!$("memory-text").value.trim() || window.confirm("放弃这条尚未保存的笔记修改？")) resetMemoryEditor(); });
  $("memory-refresh").addEventListener("click", () => loadMemories(true));
  for (const button of all("[data-memory-tab]")) {
    button.addEventListener("click", () => setMemoryTab(button.dataset.memoryTab));
    button.addEventListener("keydown", event => {
      const keys = ["ArrowLeft", "ArrowRight", "Home", "End"];
      if (!keys.includes(event.key)) return;
      event.preventDefault();
      const tabs = all("[data-memory-tab]");
      const index = tabs.indexOf(button);
      const target = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
      tabs[target].focus();
      setMemoryTab(tabs[target].dataset.memoryTab);
    });
  }
  $("history-refresh").addEventListener("click", () => loadHistory(true));
  $("history-clear").addEventListener("click", clearHistory);
  $("public-refresh").addEventListener("click", () => loadPublic(true));
  $("memory-public-pane").addEventListener("input", () => { markDirty("public"); message("public-message", "公开偏好尚未保存。"); });
  $("memory-public-pane").addEventListener("submit", savePublic);
  $("memes-refresh").addEventListener("click", () => loadMemes(true));
  $("meme-upload-form").addEventListener("submit", uploadMeme);
  $("meme-batch-files").addEventListener("change", selectMemeBatch);
  $("meme-batch-form").addEventListener("submit", importMemeBatch);
  $("meme-batch-apply-tags").addEventListener("click", applyMemeBatchTags);
  $("meme-batch-check").addEventListener("click", checkMemeBatch);
  $("meme-batch-clear").addEventListener("click", () => { if (!state.memeBatchBusy && window.confirm("清空当前批量草稿与结果？已入库图片保留。")) resetMemeBatch(); });
  $("qq-face-form").addEventListener("submit", addQqFace);
  $("qq-favorites-refresh").addEventListener("click", loadQqFavorites);
  $("received-images-refresh").addEventListener("click", loadReceivedImages);
  $("received-image-form").addEventListener("submit", importReceivedImage);
  $("received-image-cancel").addEventListener("click", () => { if (!state.receivedImporting) resetReceivedSelection(); });
  $("meme-file").addEventListener("change", () => {
    const file = $("meme-file").files[0];
    if (file && !$("meme-id").value) {
      let id = file.name.replace(/\.[^.]+$/, "").toLowerCase().replace(/[^a-z0-9_-]/g, "_").replace(/^[^a-z0-9]+/, "").slice(0, 48);
      if (!id || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(id)) id = id ? `meme_${id}` : "meme";
      $("meme-id").value = id;
    }
  });
  document.addEventListener("visibilitychange", () => { if (document.hidden) stopPolling(); else if (state.token && !$("app-shell").hidden) refreshStatus(); });
  window.addEventListener("beforeunload", event => {
    if (!state.allowPageExit && hasUncommittedPageData()) { event.preventDefault(); event.returnValue = ""; }
  });
  window.addEventListener("pagehide", () => { revokeMemeUrls(); revokeFavoriteUrls(); revokeReceivedUrls(); resetMemeBatch(); clearPersonaLearning(); });

  const fragment = new URLSearchParams(window.location.hash.slice(1));
  const fragmentToken = fragment.get("token");
  if (fragmentToken !== null) window.history.replaceState(null, "", window.location.pathname + window.location.search);
  const token = fragmentToken || readToken();
  if (token) connect(token); else showLogin();
})();
