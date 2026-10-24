"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");
const web = path.resolve(__dirname, "../../AudioBridge/AdminWeb");

class Element {
  constructor() {
    this.value = ""; this.textContent = ""; this.className = ""; this.dataset = {};
    this.children = []; this.events = {}; this.disabled = false; this.hidden = false;
    this.files = []; this.classList = { toggle() {}, contains: name => this.className.split(" ").includes(name) };
  }
  addEventListener(name, fn) { (this.events[name] ||= []).push(fn); }
  async emit(name) { for (const fn of this.events[name] || []) await fn({ preventDefault() {} }); }
  append(...values) { this.children.push(...values); }
  replaceChildren(...values) { this.children = [...values]; }
  querySelectorAll() { return []; }
  setAttribute() {}
  removeAttribute() {}
  reset() {}
  reportValidity() { return true; }
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function dom() {
  const elements = new Map();
  const $ = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  const element = (tag, value, className) => { const item = new Element(); item.textContent = value ?? ""; item.className = className || ""; return item; };
  return { $, element };
}
function moduleFixture(api) {
  const { $, element } = dom();
  const state = { session: 1, companionRequest: 0, companion: null, loading: { companion: false }, saving: { companion: false }, dirty: { companion: false } };
  const context = vm.createContext({ window: { confirm: () => true } });
  vm.runInContext(fs.readFileSync(path.join(web, "companion.js"), "utf8"), context);
  const page = context.window.createCompanionPage({ state, $, element, api,
    message(id, value) { $(id).textContent = value || ""; },
    markDirty(kind, value = true) { state.dirty[kind] = value; $(`${kind}-save`).disabled = !value || state.saving[kind]; },
    confirmDiscard: () => true, number: String, date: String });
  const expire = () => { state.session++; page.reset(); };
  return { $, state, page, expire };
}
const payload = idleMinutes => ({ settings: { idleMinutes }, contacts: [] });

test("saving locks fields and both reload controls until the submitted settings finish", async () => {
  const pending = deferred(); const calls = [];
  const { $, state, page } = moduleFixture(async (url, options) => { calls.push({ url, options }); return options ? pending.promise : payload(120); });
  await page.load();
  $("companion-idle").value = "120"; await $("companion-form").emit("input");
  const saving = $("companion-form").emit("submit");
  assert.equal($("companion-fields").disabled, true);
  assert.equal($("companion-retry").disabled, true);
  assert.equal($("companion-refresh").disabled, true);
  await page.load(true); await $("companion-form").emit("submit");
  assert.equal(calls.length, 2, "reload and duplicate submit must not race an active save");
  assert.equal(calls[1].options.body.idleMinutes, 120);
  pending.resolve({}); await saving;
  assert.equal(state.companion.settings.idleMinutes, 120);
  assert.equal(state.dirty.companion, false);
  assert.equal($("companion-fields").disabled, false);
  assert.equal($("companion-save").disabled, true);
});

test("failed save keeps the edit dirty and enables an explicit retry", async () => {
  const { $, state, page } = moduleFixture(async (url, options) => { if (options) throw new Error("synthetic failure"); return payload(120); });
  await page.load(); $("companion-idle").value = "180"; await $("companion-form").emit("input");
  await $("companion-form").emit("submit");
  assert.equal(state.dirty.companion, true);
  assert.equal($("companion-save").disabled, false);
  assert.equal($("companion-fields").disabled, false);
  assert.match($("companion-message").textContent, /synthetic failure/);
});

test("reset permits a fresh read while an old session read is still pending", async () => {
  const old = deferred(), current = deferred(); let reads = 0;
  const fixture = moduleFixture(() => ++reads === 1 ? old.promise : current.promise);
  const first = fixture.page.load(); fixture.expire(); const second = fixture.page.load();
  old.resolve(payload(777)); await first;
  assert.equal(fixture.state.loading.companion, true, "old finally must not unlock current read");
  assert.equal(fixture.$("companion-fields").disabled, true);
  assert.equal(fixture.state.companion, null);
  current.resolve(payload(180)); await second;
  assert.equal(reads, 2);
  assert.equal(fixture.$("companion-idle").value, 180);
  assert.equal(fixture.state.loading.companion, false);
});

test("late save completion cannot clear a new session edit or unlock its pending read", async () => {
  const save = deferred(), fresh = deferred(); let reads = 0;
  const fixture = moduleFixture((url, options) => options ? save.promise : ++reads === 1 ? payload(120) : fresh.promise);
  await fixture.page.load(); await fixture.$("companion-form").emit("input");
  const saving = fixture.$("companion-form").emit("submit");
  fixture.expire(); const loading = fixture.page.load();
  save.resolve({}); await saving;
  assert.equal(fixture.state.companion, null);
  assert.equal(fixture.state.loading.companion, true);
  assert.equal(fixture.$("companion-fields").disabled, true);
  fresh.resolve(payload(180)); await loading;
  await fixture.$("companion-form").emit("input");
  assert.equal(fixture.state.dirty.companion, true);
});

async function flushUntil(predicate) {
  for (let i = 0; i < 40; i++) { if (predicate()) return; await new Promise(resolve => setImmediate(resolve)); }
  assert.ok(predicate(), "page did not reach expected state");
}
function response(body, status = 200) {
  return { status, ok: status < 400, text: async () => JSON.stringify(body), json: async () => body };
}
function appFixture(fetch) {
  const { $ , element } = dom();
  const nav = ["overview", "companion", "settings"].map(view => { const item = new Element(); item.dataset.view = view; return item; });
  const window = { location: { hash: "", pathname: "/", search: "" }, history: { replaceState() {} }, addEventListener() {}, confirm: () => true };
  const context = vm.createContext({ window, document: {
    hidden: false, getElementById: $, createElement: element,
    querySelectorAll: selector => selector === "[data-view]" ? nav : [], querySelector: () => new Element(), addEventListener() {}
  }, fetch, AbortController, URL, URLSearchParams,
  sessionStorage: { getItem: () => "", setItem() {}, removeItem() {} },
  setTimeout: () => 1, clearTimeout() {} });
  for (const file of ["companion.js", "app.js"]) vm.runInContext(fs.readFileSync(path.join(web, file), "utf8"), context);
  return { $, nav, async connect() { $("login-token").value = "synthetic-token"; await $("login-form").emit("submit"); await flushUntil(() => $("app-shell").hidden === false); } };
}

test("real console 401 path resets heart page loading and reconnects without a full reload", async () => {
  let reads = 0;
  const app = appFixture(async url => url === "/api/status" ? response({ runtime: { running: true } }) : response(payload(180), ++reads === 1 ? 401 : 200));
  await app.connect(); await app.nav[1].emit("click");
  assert.equal(app.$("login-screen").hidden, false);
  await app.connect();
  await flushUntil(() => !app.$("companion-fields").disabled);
  assert.equal(reads, 2);
  assert.equal(app.$("companion-idle").value, 180);
  assert.equal(app.$("companion-retry").disabled, false);
});

test("real console also clears a failed settings load on authentication expiry", async () => {
  let reads = 0;
  const app = appFixture(async url => url === "/api/status" ? response({ runtime: { running: true } }) : response({ allowedGroupIds: [], allowedPrivateUserIds: [] }, ++reads === 1 ? 403 : 200));
  await app.connect(); await app.nav[2].emit("click");
  assert.equal(app.$("login-screen").hidden, false);
  await app.connect(); await flushUntil(() => !app.$("settings-fields").disabled);
  assert.equal(reads, 2);
  assert.equal(app.$("settings-retry").disabled, false);
});

test("background task failures and success times render on overview and heart pages", async () => {
  const app = appFixture(async () => response({ runtime: { running: true, backgroundTasks: [
    { name: "companion", state: "failed", lastSuccessAt: "2026-10-24T02:00:00Z", lastError: "ChatApiException" }
  ] } }));
  await app.connect();
  for (const id of ["background-tasks", "companion-background-tasks"]) {
    const row = app.$(id).children[0];
    assert.match(row.children[0].textContent, /心脑/);
    assert.equal(row.children[1].textContent, "失败");
    assert.match(row.children[2].textContent, /最近成功：.*ChatApiException/);
  }
});

test("an old settings request cannot unlock the new session request after a status 401", async () => {
  const old = deferred(), current = deferred(); let statusReads = 0, settingsReads = 0;
  const app = appFixture(async url => {
    if (url === "/api/status") return response({ runtime: { running: true } }, ++statusReads === 2 ? 401 : 200);
    return ++settingsReads === 1 ? old.promise : current.promise;
  });
  await app.connect();
  const previousRead = app.nav[2].emit("click");
  await app.$("refresh-button").emit("click");
  assert.equal(app.$("login-screen").hidden, false);
  await app.connect(); await flushUntil(() => settingsReads === 2);
  old.resolve(response({ allowedGroupIds: [], allowedPrivateUserIds: [], engine: "OLD_SESSION" }));
  await previousRead;
  assert.equal(app.$("settings-fields").disabled, true);
  assert.equal(app.$("settings-retry").disabled, true);
  current.resolve(response({ allowedGroupIds: [], allowedPrivateUserIds: [], engine: "school" }));
  await flushUntil(() => !app.$("settings-fields").disabled);
  assert.equal(app.$("settings-engine").value, "school");
});
