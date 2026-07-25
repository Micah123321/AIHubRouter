const state = {
  dashboard: null,
  selectedGroupId: null,
  sortField: "weightedScore",
  sortDescending: true,
  clearPassword: false,
  clearToken: false,
  draftKeyIds: new Set(),
  draftBlacklistIds: new Set(),
  settingsHydrated: false,
  requestInFlight: false
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers: {
      "X-AIHub-Web": "1",
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(options.headers || {})
    }
  });

  if (response.status === 401 && path !== "/api/auth/login") {
    showLogin();
    throw new Error("登录已失效。");
  }

  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(body.error || "请求失败。");
  }
  return body;
}

function showLogin() {
  $("#appShell").hidden = true;
  $("#loginView").hidden = false;
  window.setTimeout(() => $("#webPassword").focus(), 0);
}

function showApp() {
  $("#loginView").hidden = true;
  $("#appShell").hidden = false;
}

function applyTheme(theme) {
  document.documentElement.removeAttribute("data-theme");
  if (theme === "light" || theme === "dark") {
    document.documentElement.dataset.theme = theme;
  }
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function enumValue(value) {
  return String(value || "").replace(/^./, char => char.toLowerCase());
}

function formatNumber(value, digits = 4) {
  return Number.isFinite(value) ? value.toFixed(digits).replace(/\.?0+$/, "") : "-";
}

function formatPercent(value) {
  return Number.isFinite(value) ? `${(value * 100).toFixed(1)}%` : "-";
}

function formatScore(value) {
  if (!Number.isFinite(value)) return "-";
  return `${value > 0 ? "+" : ""}${value.toFixed(4)}`;
}

function parsePriceMultiplier(selector) {
  const rawValue = $(selector).value.trim().replace(",", ".");
  const value = rawValue === "" ? Number.NaN : Number(rawValue);
  return Number.isFinite(value) && value >= 0 ? value : Number.NaN;
}

function readPriceRange() {
  const minimum = parsePriceMultiplier("#minimumPriceMultiplier");
  const maximum = parsePriceMultiplier("#maximumPriceMultiplier");
  if (!Number.isFinite(minimum) || !Number.isFinite(maximum) || minimum > maximum) {
    throw new Error("价格范围必须是非负有限数值，且最小值不能大于最大值。");
  }
  return { minimum, maximum };
}

function formatDate(value) {
  if (!value) return "-";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "-" : new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
    hour12: false
  }).format(date);
}

function hydrateSettings(settings, force = false) {
  if (state.settingsHydrated && !force) return;
  $("#baseUrl").value = settings.baseUrl;
  $("#visitSite").href = settings.baseUrl;
  $("#email").value = settings.email;
  $("#password").value = "";
  $("#bearerToken").value = "";
  $("#groupStickiness").value = settings.groupStickiness;
  $("#minimumPriceMultiplier").value = settings.minimumPriceMultiplier;
  $("#maximumPriceMultiplier").value = settings.maximumPriceMultiplier;
  $("#pollingInterval").value = settings.pollingIntervalSeconds;
  $("#persistCredentials").checked = settings.persistCredentials;
  $("#themeSelect").value = enumValue(settings.themeMode);
  const mode = enumValue(settings.routingMode);
  const modeInput = $(`input[name="routingMode"][value="${mode}"]`);
  if (modeInput) modeInput.checked = true;
  state.clearPassword = false;
  state.clearToken = false;
  state.draftKeyIds = new Set(settings.selectedKeyIds);
  state.draftBlacklistIds = new Set(settings.blacklistedGroupIds);
  state.settingsHydrated = true;
  applyTheme($("#themeSelect").value);
  updateCredentialState(settings);
  updateSecretControls(settings);
}

function updateCredentialState(settings) {
  const parts = [];
  if (settings.hasPassword && !state.clearPassword) parts.push("密码已配置");
  if (settings.hasBearerToken && !state.clearToken) parts.push("Token 已配置");
  $("#credentialState").textContent = parts.length ? parts.join(" · ") : "未配置认证";
  $("#persistCredentials").disabled = !settings.canPersistCredentials;
  $("#persistCredentials").title = settings.canPersistCredentials ? "" : settings.credentialProtection;
}

function updateSecretControls(settings) {
  $("#clearPassword").disabled = !settings.hasPassword && !$("#password").value;
  $("#clearToken").disabled = !settings.hasBearerToken && !$("#bearerToken").value;
  $("#password").closest(".secret-field").classList.toggle("secret-cleared", state.clearPassword);
  $("#bearerToken").closest(".secret-field").classList.toggle("secret-cleared", state.clearToken);
}

function renderGroups(groups) {
  $("#groupCount").textContent = `${groups.length} 个分组`;
  $("#groupRows").innerHTML = groups.length ? groups.map(group => `
    <tr>
      <td><input type="checkbox" class="blacklist-check" data-id="${group.id}" aria-label="禁用 ${escapeHtml(group.name)}" ${state.draftBlacklistIds.has(group.id) ? "checked" : ""}></td>
      <td>${group.id}</td>
      <td>${escapeHtml(group.name)}</td>
      <td>${escapeHtml(group.platform)}</td>
      <td><span class="state-badge ${group.status === "active" ? "available" : "error"}">${escapeHtml(group.status)}</span></td>
    </tr>`).join("") : '<tr><td class="empty-state" colspan="5">刷新后显示分组</td></tr>';
}

function sortedProviders(providers) {
  const direction = state.sortDescending ? -1 : 1;
  return [...providers].sort((a, b) => {
    const left = a[state.sortField];
    const right = b[state.sortField];
    if (!Number.isFinite(left) && !Number.isFinite(right)) return (a.groupId ?? 0) - (b.groupId ?? 0);
    if (!Number.isFinite(left)) return 1;
    if (!Number.isFinite(right)) return -1;
    return (left - right) * direction || (a.groupId ?? 0) - (b.groupId ?? 0);
  });
}

function stateClass(value) {
  return ({ "推荐": "recommended", "可用": "available", "警告": "warning", "异常": "error", "停用": "error", "黑名单": "blacklisted" })[value] || "";
}

function renderProviders(providers) {
  const rows = sortedProviders(providers);
  if (state.selectedGroupId && !rows.some(row => row.groupId === state.selectedGroupId && row.canManualRoute)) {
    state.selectedGroupId = null;
  }
  $("#providerCount").textContent = `${providers.length} 个方案`;
  $("#providerRows").innerHTML = rows.length ? rows.map(provider => `
    <tr class="provider-row ${state.selectedGroupId === provider.groupId ? "selected" : ""}" data-group-id="${provider.groupId ?? ""}">
      <td><input type="radio" name="providerSelection" value="${provider.groupId ?? ""}" aria-label="选择 ${escapeHtml(provider.plan)}" ${state.selectedGroupId === provider.groupId ? "checked" : ""} ${provider.canManualRoute ? "" : "disabled"}></td>
      <td>${provider.groupId ?? "-"}</td>
      <td title="${escapeHtml(provider.plan)}">${escapeHtml(provider.plan || "-")}</td>
      <td>${Number.isFinite(provider.multiplier) ? `${formatNumber(provider.multiplier)}x` : "-"}</td>
      <td>${Number.isFinite(provider.latency) ? `${provider.latency.toFixed(0)} ms` : "-"}</td>
      <td>${Number.isFinite(provider.confidence) ? `${formatPercent(provider.confidence)} / ${provider.sampleCount}` : "-"}</td>
      <td>${formatScore(provider.weightedScore)}</td>
      <td><span class="state-badge ${stateClass(provider.state)}">${escapeHtml(provider.state)}</span></td>
      <td>${formatDate(provider.checkedAt)}</td>
    </tr>`).join("") : '<tr><td class="empty-state" colspan="9">刷新后显示方案</td></tr>';
  $("#manualButton").disabled = state.dashboard?.isBusy || !state.selectedGroupId;
  updateSortIndicators();
}

function renderKeys(keys) {
  $("#keyCount").textContent = `${keys.length} 个 Key`;
  $("#keyRows").innerHTML = keys.length ? keys.map(key => `
    <tr>
      <td><input type="checkbox" class="key-check" data-id="${key.id}" aria-label="路由 ${escapeHtml(key.name)}" ${state.draftKeyIds.has(key.id) ? "checked" : ""}></td>
      <td>${key.id}</td>
      <td>${escapeHtml(key.name)}</td>
      <td><span class="state-badge ${key.status === "active" ? "available" : "error"}">${escapeHtml(key.status)}</span></td>
      <td>${key.groupId ?? "-"}</td>
      <td>${escapeHtml(key.groupName)}</td>
    </tr>`).join("") : '<tr><td class="empty-state" colspan="6">刷新后显示 Key</td></tr>';
}

function updateSortIndicators() {
  $$(".sort-button").forEach(button => {
    button.querySelector("span").textContent = button.dataset.sort === state.sortField
      ? (state.sortDescending ? "↓" : "↑") : "";
  });
}

function renderDashboard(dashboard, syncSettings = false) {
  state.dashboard = dashboard;
  hydrateSettings(dashboard.settings, syncSettings);
  $("#connectionSummary").textContent = dashboard.connectionSummary;
  $("#candidateSummary").textContent = dashboard.candidateSummary;
  $("#autoRouting").checked = dashboard.autoRouting;
  $("#busyIndicator").hidden = !dashboard.isBusy && !state.requestInFlight;
  $$("#refreshButton, #dryRunButton, #routeButton, #saveButton").forEach(button => {
    button.disabled = dashboard.isBusy || state.requestInFlight;
  });
  $("#manualButton").disabled = dashboard.isBusy || state.requestInFlight || !state.selectedGroupId;
  renderGroups(dashboard.groups);
  renderProviders(dashboard.providers);
  renderKeys(dashboard.keys);
  $("#statusText").textContent = dashboard.status;
  $("#statusDot").className = `status-dot ${dashboard.statusKind}`;
  $("#lastUpdated").textContent = dashboard.lastUpdatedAt ? `更新于 ${formatDate(dashboard.lastUpdatedAt)}` : "";
  updateCredentialState(dashboard.settings);
  updateSecretControls(dashboard.settings);
}

function settingsPayload() {
  const routingMode = $('input[name="routingMode"]:checked')?.value || "balanced";
  const password = $("#password").value;
  const bearerToken = $("#bearerToken").value;
  const priceRange = readPriceRange();
  return {
    baseUrl: $("#baseUrl").value.trim(),
    email: $("#email").value.trim(),
    password: password ? password : null,
    bearerToken: bearerToken ? bearerToken : null,
    clearPassword: state.clearPassword,
    clearBearerToken: state.clearToken,
    routingMode,
    groupStickiness: Number.parseFloat($("#groupStickiness").value),
    minimumPriceMultiplier: priceRange.minimum,
    maximumPriceMultiplier: priceRange.maximum,
    pollingIntervalSeconds: Number.parseInt($("#pollingInterval").value, 10),
    persistCredentials: $("#persistCredentials").checked,
    themeMode: $("#themeSelect").value,
    selectedKeyIds: [...state.draftKeyIds],
    blacklistedGroupIds: [...state.draftBlacklistIds]
  };
}

async function runAction(path, options = {}) {
  if (state.requestInFlight) return;
  state.requestInFlight = true;
  if (state.dashboard) renderDashboard(state.dashboard);
  try {
    const dashboard = await api(path, { method: "POST", ...options });
    renderDashboard(dashboard);
  } catch (error) {
    showStatusError(error.message);
  } finally {
    state.requestInFlight = false;
    if (state.dashboard) renderDashboard(state.dashboard);
  }
}

function showStatusError(message) {
  $("#statusText").textContent = message;
  $("#statusDot").className = "status-dot error";
}

async function loadDashboard() {
  if (state.requestInFlight || $("#appShell").hidden) return;
  try {
    renderDashboard(await api("/api/dashboard"));
  } catch (error) {
    if (!$("#appShell").hidden) showStatusError(error.message);
  }
}

$("#loginForm").addEventListener("submit", async event => {
  event.preventDefault();
  $("#loginError").textContent = "";
  try {
    await api("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ password: $("#webPassword").value })
    });
    $("#webPassword").value = "";
    showApp();
    renderDashboard(await api("/api/dashboard"), true);
  } catch (error) {
    $("#loginError").textContent = error.message;
  }
});

$("#toggleLoginPassword").addEventListener("click", () => {
  $("#webPassword").type = $("#webPassword").type === "password" ? "text" : "password";
});

$("#logoutButton").addEventListener("click", async () => {
  await api("/api/auth/logout", { method: "POST" }).catch(() => {});
  showLogin();
});

$("#saveButton").addEventListener("click", async () => {
  if (state.requestInFlight) return;
  state.requestInFlight = true;
  try {
    const dashboard = await api("/api/settings", { method: "PUT", body: JSON.stringify(settingsPayload()) });
    state.settingsHydrated = false;
    renderDashboard(dashboard, true);
  } catch (error) {
    showStatusError(error.message);
  } finally {
    state.requestInFlight = false;
    if (state.dashboard) renderDashboard(state.dashboard);
  }
});

$("#refreshButton").addEventListener("click", () => runAction("/api/actions/refresh"));
$("#dryRunButton").addEventListener("click", () => runAction("/api/actions/dry-run"));
$("#routeButton").addEventListener("click", () => runAction("/api/actions/route"));
$("#manualButton").addEventListener("click", () => {
  if (!state.selectedGroupId) return;
  runAction("/api/actions/manual-route", { body: JSON.stringify({ groupId: state.selectedGroupId }) });
});

$("#autoRouting").addEventListener("change", async event => {
  if (state.requestInFlight) return;
  state.requestInFlight = true;
  try {
    const dashboard = await api("/api/auto-routing", {
      method: "PUT",
      body: JSON.stringify({ enabled: event.target.checked })
    });
    renderDashboard(dashboard);
  } catch (error) {
    event.target.checked = !event.target.checked;
    showStatusError(error.message);
  } finally {
    state.requestInFlight = false;
    if (state.dashboard) renderDashboard(state.dashboard);
  }
});

$("#themeSelect").addEventListener("change", event => applyTheme(event.target.value));
$("#baseUrl").addEventListener("input", event => { $("#visitSite").href = event.target.value; });
$("#password").addEventListener("input", () => {
  state.clearPassword = false;
  updateSecretControls(state.dashboard.settings);
});
$("#bearerToken").addEventListener("input", () => {
  state.clearToken = false;
  updateSecretControls(state.dashboard.settings);
});
$("#clearPassword").addEventListener("click", () => {
  $("#password").value = "";
  state.clearPassword = true;
  updateCredentialState(state.dashboard.settings);
  updateSecretControls(state.dashboard.settings);
});
$("#clearToken").addEventListener("click", () => {
  $("#bearerToken").value = "";
  state.clearToken = true;
  updateCredentialState(state.dashboard.settings);
  updateSecretControls(state.dashboard.settings);
});

$("#groupRows").addEventListener("change", event => {
  if (!event.target.classList.contains("blacklist-check")) return;
  const id = Number(event.target.dataset.id);
  event.target.checked ? state.draftBlacklistIds.add(id) : state.draftBlacklistIds.delete(id);
});

$("#keyRows").addEventListener("change", event => {
  if (!event.target.classList.contains("key-check")) return;
  const id = Number(event.target.dataset.id);
  event.target.checked ? state.draftKeyIds.add(id) : state.draftKeyIds.delete(id);
});

$("#providerRows").addEventListener("change", event => {
  if (event.target.name !== "providerSelection") return;
  state.selectedGroupId = Number(event.target.value);
  renderProviders(state.dashboard.providers);
});

$("#providerRows").addEventListener("click", event => {
  const row = event.target.closest(".provider-row");
  const radio = row?.querySelector('input[type="radio"]');
  if (!radio || radio.disabled || event.target === radio) return;
  radio.checked = true;
  radio.dispatchEvent(new Event("change", { bubbles: true }));
});

$$(".sort-button").forEach(button => button.addEventListener("click", () => {
  if (state.sortField === button.dataset.sort) {
    state.sortDescending = !state.sortDescending;
  } else {
    state.sortField = button.dataset.sort;
    state.sortDescending = ["confidence", "weightedScore"].includes(state.sortField);
  }
  renderProviders(state.dashboard?.providers || []);
}));

async function initialize() {
  try {
    const auth = await api("/api/auth/status");
    if (!auth.authenticated) {
      showLogin();
      return;
    }
    showApp();
    renderDashboard(await api("/api/dashboard"), true);
  } catch {
    showLogin();
  }
}

initialize();
window.setInterval(loadDashboard, 3000);
