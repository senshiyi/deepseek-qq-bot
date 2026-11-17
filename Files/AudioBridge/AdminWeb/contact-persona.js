"use strict";

// 联系人专属人格：从私聊白名单里选人，留空的项目沿用全局角色。
window.createContactPersonaPage = ({ $, api, message }) => {
  const fields = { name: "contact-persona-name", userAddress: "contact-persona-address",
    personality: "contact-persona-personality", speakingStyle: "contact-persona-style" };
  let sources = [];
  let loading = false;

  function contactId() {
    const value = Number($("contact-persona-user").value);
    if (!Number.isInteger(value) || value <= 0) throw new Error("请先选择一位私聊白名单联系人。");
    return value;
  }

  function write(persona) {
    for (const [key, id] of Object.entries(fields)) $(id).value = persona?.[key] ?? "";
  }

  async function loadSources() {
    if (loading) return;
    loading = true;
    const select = $("contact-persona-user");
    const selected = select.value;
    try {
      const settings = await api("/api/settings");
      sources = (settings.allowedPrivateUserIds || []).filter(id => Number.isSafeInteger(id) && id > 0);
      const options = sources.map(id => {
        const option = document.createElement("option");
        option.value = String(id); option.textContent = "QQ " + id;
        return option;
      });
      if (!options.length) {
        const option = document.createElement("option");
        option.value = ""; option.textContent = "尚未配置私聊白名单";
        options.push(option);
      }
      select.replaceChildren(...options);
      select.value = sources.some(id => String(id) === selected) ? selected : (sources.length ? String(sources[0]) : "");
    } catch (error) {
      select.replaceChildren(Object.assign(document.createElement("option"), { value: "", textContent: "读取私聊白名单失败" }));
      message("contact-persona-message", error.message, "error");
    } finally { loading = false; }
  }

  async function load() {
    let userId;
    try { userId = contactId(); } catch (error) { message("contact-persona-message", error.message, "error"); return; }
    message("contact-persona-message", "正在读取…");
    try {
      const value = await api("/api/contact-personas?userId=" + userId);
      write(value.persona);
      message("contact-persona-message", value.persona
        ? "已读取专属人格；留空的项目沿用全局角色。"
        : "这位联系人还没有专属人格，正在沿用全局角色。", "success");
    } catch (error) { message("contact-persona-message", error.message, "error"); }
  }

  async function save(event) {
    event.preventDefault();
    let userId;
    try {
      userId = contactId();
      const body = { userId };
      for (const [key, id] of Object.entries(fields)) body[key] = $(id).value.trim();
      if (!Object.values(fields).some(id => $(id).value.trim()))
        throw new Error("至少填写一项；全部留空就等于沿用全局人格。");
      message("contact-persona-message", "正在保存…");
      await api("/api/contact-personas", { method: "PUT", body });
      message("contact-persona-message", `已保存 QQ ${userId} 的专属人格；点“应用设置”或重启后聊天回复生效，唤醒和空间评论立即生效。`, "success");
    } catch (error) { message("contact-persona-message", error.message, "error"); }
  }

  async function clear() {
    let userId;
    try { userId = contactId(); } catch (error) { message("contact-persona-message", error.message, "error"); return; }
    if (!window.confirm(`清除 QQ ${userId} 的专属人格？清除后他会回到全局角色。`)) return;
    try {
      await api("/api/contact-personas/" + userId, { method: "DELETE" });
      write(null);
      message("contact-persona-message", "已清除，这位联系人回到全局角色；应用设置后聊天回复生效。", "success");
      await loadSources();
      await load();
    } catch (error) { message("contact-persona-message", error.message, "error"); }
  }

  $("contact-persona-form").addEventListener("submit", save);
  $("contact-persona-load").addEventListener("click", async () => { await loadSources(); await load(); });
  $("contact-persona-clear").addEventListener("click", clear);
  $("contact-persona-user").addEventListener("change", load);
  // 只有连上控制台、真正打开角色页时才读一次；页面刚加载时还没有令牌，读了只会报错。
  let ready = false;
  async function bringUp() {
    if (ready) return;
    ready = true;
    await loadSources();
    await load();
  }
  return { bringUp, load, loadSources };
};
