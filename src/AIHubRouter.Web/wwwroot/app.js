const state = {
  dashboard: null,
  selectedGroupId: null,
  sortField: "weightedScore",
  sortDescending: true,
  clearPassword: false,
  clearToken: false,
  draftKeyIds: new Set(),
  draftLunaKeyIds: new Set(),
  draftBlacklistIds: new Set(),
  draftDetectorBindings: new Map(),
  detectorCredentialKeyIds: new Set(),
  detectorCredentialClears: new Set(),
  reliabilityDraftChanged: false,
  settingsHydrated: false,
  credentialDraftChanged: false,
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
    throw new Error(body.error || body.dashboard?.status || "请求失败。");
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

function readBoundedNumber(selector, minimum, maximum, label) {
  const value = Number.parseFloat($(selector).value);
  if (!Number.isFinite(value) || value < minimum || value > maximum) {
    throw new Error(`${label}必须在 ${minimum} 到 ${maximum} 之间。`);
  }
  return value;
}

function readBoundedInteger(selector, minimum, maximum, label) {
  const value = Number($(selector).value);
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${label}必须是 ${minimum} 到 ${maximum} 之间的整数。`);
  }
  return value;
}

function readRequiredText(selector, label) {
  const value = $(selector).value.trim();
  if (!value) throw new Error(`${label}不能为空。`);
  return value;
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
  $("#confidenceImpact").value = settings.confidenceImpact;
  $("#minimumConfidence").value = settings.minimumConfidence;
  $("#providerSeriesWeight").value = settings.providerSeriesWeight;
  $("#providerSeriesCacheSeconds").value = settings.providerSeriesCacheSeconds;
  $("#providerSeriesRange").value = settings.providerSeriesRange;
  $("#providerSeriesTimezone").value = settings.providerSeriesTimezone;
  $("#reliabilityDetectionEnabled").checked = settings.reliabilityDetectionEnabled !== false;
  $("#reliabilityQuarantineHours").value = settings.reliabilityQuarantineHours ?? 24;
  $("#pollingInterval").value = settings.pollingIntervalSeconds;
  $("#persistCredentials").checked = settings.persistCredentials;
  $("#themeSelect").value = enumValue(settings.themeMode);
  const mode = enumValue(settings.routingMode);
  const modeInput = $(`input[name="routingMode"][value="${mode}"]`);
  if (modeInput) modeInput.checked = true;
  state.clearPassword = false;
  state.clearToken = false;
  state.credentialDraftChanged = false;
  state.draftKeyIds = new Set(settings.selectedKeyIds || []);
  state.draftLunaKeyIds = new Set(settings.lunaSelectedKeyIds || []);
  for (const id of state.draftKeyIds) state.draftLunaKeyIds.delete(id);
  state.draftBlacklistIds = new Set(settings.blacklistedGroupIds);
  state.draftDetectorBindings = new Map((settings.detectorBindings || []).map(binding => [
    binding.keyId,
    {
      keyId: binding.keyId,
      baseUrl: binding.baseUrl || "",
      models: [...(binding.models || [])],
      enabled: binding.enabled !== false
    }
  ]));
  state.detectorCredentialKeyIds = new Set(settings.detectorCredentialKeyIds || []);
  state.detectorCredentialClears = new Set();
  state.reliabilityDraftChanged = false;
  state.settingsHydrated = true;
  applyTheme($("#themeSelect").value);
  updateCredentialState(settings);
  updateSecretControls(settings);
}

function updateCredentialState(settings) {
  const parts = [];
  if (settings.hasPassword && !state.clearPassword) parts.push("密码已配置");
  if (settings.hasBearerToken && !state.clearToken) parts.push("Token 已配置");
  if (settings.credentialsUnavailable) parts.push("已有认证待解密");
  else if (!settings.canPersistCredentials) parts.push("加密保存不可用");
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
  return ({ "推荐": "recommended", "可用": "available", "警告": "warning", "价格范围外": "warning", "异常": "error", "停用": "error", "黑名单": "blacklisted", "掺水隔离": "error" })[value] || "";
}

function reliabilityLabel(state) {
  return ({
    passed: "可靠性通过", Passed: "可靠性通过",
    quarantined: "掺水隔离", Quarantined: "掺水隔离",
    unavailable: "检测不可用", Unavailable: "检测不可用",
    evidenceInsufficient: "证据不足", EvidenceInsufficient: "证据不足",
    unconfigured: "未配置检测", Unconfigured: "未配置检测"
  })[state] || "未检测";
}

function reliabilityMarkup(state, until, models) {
  if (!state || state === "unconfigured" || state === "Unconfigured") return "";
  const label = reliabilityLabel(state);
  const modelText = Array.isArray(models) && models.length ? ` · ${models.join("/")}` : "";
  const isPassed = state === "passed" || state === "Passed";
  const isQuarantined = state === "quarantined" || state === "Quarantined";
  const isUnavailable = state === "unavailable" || state === "Unavailable";
  const untilText = isQuarantined && until ? ` · 至 ${formatDate(until)}` : "";
  const className = isPassed ? "available" : isQuarantined || isUnavailable ? "error" : "warning";
  return `<div class="reliability-note ${className}">${escapeHtml(label + modelText + untilText)}</div>`;
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
      <td>${formatPercent(provider.cacheHitRate)}</td>
      <td>${formatScore(provider.weightedScore)}</td>
      <td><span class="state-badge ${stateClass(provider.state)}">${escapeHtml(provider.state)}</span>${reliabilityMarkup(provider.reliabilityState, provider.reliabilityQuarantinedUntil, provider.reliabilityModels)}</td>
      <td>${formatDate(provider.checkedAt)}</td>
    </tr>`).join("") : '<tr><td class="empty-state" colspan="10">刷新后显示方案</td></tr>';
  $("#manualButton").disabled = state.dashboard?.isBusy || !state.selectedGroupId;
  updateSortIndicators();
}

function renderLunaRoute(route, configuredKeyCount) {
  const resolved = route || {
    configured: configuredKeyCount > 0,
    hasRun: false,
    healthAvailable: false,
    hasTarget: false,
    healthMessage: configuredKeyCount > 0
      ? `已配置 ${configuredKeyCount} 个 Luna Key，尚未运行。`
      : "未配置 Luna Key。",
    filteredGroupCount: 0,
    selectedKeyCount: configuredKeyCount,
    groupId: null,
    plan: null,
    multiplier: null,
    latency: null,
    decisionReason: "未运行"
  };
  let stateLabel = "未配置";
  let stateKey = "unconfigured";
  let badgeClass = "";
  if (resolved.configured && !resolved.hasRun) {
    stateLabel = "未运行";
    stateKey = "pending";
    badgeClass = "warning";
  } else if (resolved.configured && !resolved.healthAvailable) {
    stateLabel = "不可访问";
    stateKey = "unavailable";
    badgeClass = "error";
  } else if (resolved.configured && !resolved.hasTarget) {
    stateLabel = "无可用候选";
    stateKey = "no-target";
    badgeClass = "warning";
  } else if (resolved.configured) {
    stateLabel = "可用";
    stateKey = "available";
    badgeClass = "available";
  }

  const panel = $("#lunaRoutePanel");
  panel.dataset.state = stateKey;
  const stateBadge = $("#lunaRouteState");
  stateBadge.className = `state-badge ${badgeClass}`;
  stateBadge.textContent = stateLabel;
  $("#lunaHealthMessage").textContent = resolved.healthMessage || "暂无 Luna 健康信息。";
  $("#lunaRouteGroup").textContent = resolved.groupId ?? "-";
  $("#lunaRoutePlan").textContent = resolved.plan || "-";
  $("#lunaRouteMultiplier").textContent = Number.isFinite(resolved.multiplier)
    ? `${formatNumber(resolved.multiplier)}x` : "-";
  $("#lunaRouteLatency").textContent = Number.isFinite(resolved.latency)
    ? `${resolved.latency.toFixed(0)} ms` : "-";
  $("#lunaRouteFiltered").textContent = resolved.hasRun
    ? `${resolved.filteredGroupCount} 个` : "-";
  $("#lunaRouteKeys").textContent = resolved.configured
    ? `${resolved.selectedKeyCount} 个` : "-";
}

function renderKeys(keys) {
  $("#keyCount").textContent = `${keys.length} 个 Key`;
  $("#keyRows").innerHTML = keys.length ? keys.map(key => `
    <tr>
      <td><input type="checkbox" class="key-check" data-id="${key.id}" aria-label="主路由 ${escapeHtml(key.name)}" ${state.draftKeyIds.has(key.id) ? "checked" : ""}></td>
      <td><input type="checkbox" class="luna-key-check" data-id="${key.id}" aria-label="Luna 路由 ${escapeHtml(key.name)}" ${state.draftLunaKeyIds.has(key.id) ? "checked" : ""}></td>
      <td>${key.id}</td>
      <td>${escapeHtml(key.name)}</td>
      <td><span class="state-badge ${key.status === "active" ? "available" : "error"}">${escapeHtml(key.status)}</span>${reliabilityMarkup(key.reliabilityState, key.reliabilityQuarantinedUntil, key.reliabilityModels)}</td>
      <td>${key.groupId ?? "-"}</td>
      <td>${escapeHtml(key.groupName)}</td>
    </tr>`).join("") : '<tr><td class="empty-state" colspan="7">刷新后显示 Key</td></tr>';
}

function reliabilityPhaseLabel(value) {
  return ({
    disabled: "已关闭", idle: "尚未运行", queued: "已排队", running: "运行中",
    completed: "已完成", completedWithWarnings: "完成（有未确认项）",
    failed: "失败", cancelled: "已取消"
  })[enumValue(value)] || "尚未运行";
}

function reliabilityTriggerLabel(value) {
  return ({
    startup: "启动检查", scheduled: "每小时复检", manual: "手动检测", refresh: "刷新唤醒",
    configurationChanged: "配置变更", keyGroupChanged: "新渠道", routingCycle: "路由周期"
  })[enumValue(value)] || "-";
}

function reliabilitySkipReasonLabel(value) {
  return ({
    notDue: "未到一小时", missingGroup: "未分配渠道", missingBinding: "未配置检测",
    missingCredential: "未配置凭据", noModels: "未选择模型"
  })[enumValue(value)] || "";
}

function capabilityStatusLabel(value) {
  return ({
    missing: "无健康样本（仍检测）", unknown: "健康状态未知（仍检测）",
    failed: "健康样本失败（仍检测）", healthy: "健康样本正常"
  })[enumValue(value)] || "";
}

function probeFamilyLabel(value) {
  return ({
    process: "过程", network: "网络", juice: "Juice", identity: "身份",
    coverage: "覆盖率", fingerprint: "指纹", verdict: "结论"
  })[enumValue(value)] || String(value || "-");
}

function probeStageLabel(value) {
  return ({
    queued: "排队", running: "运行中", completed: "完成", failed: "失败",
    cancelled: "取消", skipped: "跳过"
  })[enumValue(value)] || String(value || "-");
}

function eventTypeLabel(value) {
  return ({
    runQueued: "轮次排队", runStarted: "轮次开始", probeQueued: "探针排队",
    probeStarted: "探针开始", probeCompleted: "探针完成", probeFailed: "探针失败",
    probeCancelled: "探针取消", probeSkipped: "探针跳过", quarantineApplied: "已隔离",
    quarantineRejected: "模拟隔离", runCompleted: "轮次完成", runFailed: "轮次失败",
    runCancelled: "轮次取消"
  })[enumValue(value)] || String(value || "-");
}

function reliabilityStateBadge(value) {
  const normalized = enumValue(value);
  const label = reliabilityLabel(value);
  const className = normalized === "passed" || normalized === "completed"
    ? "available"
    : normalized === "quarantined" || normalized === "unavailable" || normalized === "failed"
      ? "error"
      : normalized === "unconfigured" ? "" : "warning";
  return `<span class="state-badge ${className}">${escapeHtml(label)}</span>`;
}

function detectorConfigKeys(dashboard) {
  const keys = new Map();
  for (const key of dashboard.keys || []) {
    keys.set(key.id, { id: key.id, name: key.name, groupId: key.groupId });
  }
  for (const key of dashboard.reliability?.keys || []) {
    keys.set(key.keyId, { id: key.keyId, name: key.keyName, groupId: key.groupId });
  }
  for (const probe of dashboard.reliability?.runtime?.probes || []) {
    keys.set(probe.keyId, { id: probe.keyId, name: probe.keyName, groupId: probe.groupId });
  }
  for (const binding of dashboard.settings.detectorBindings || []) {
    if (!keys.has(binding.keyId)) {
      keys.set(binding.keyId, { id: binding.keyId, name: `Key ${binding.keyId}`, groupId: null });
    }
  }
  return [...keys.values()].sort((left, right) => left.id - right.id);
}

function renderReliabilityConfig(dashboard) {
  const rowsElement = $("#reliabilityConfigRows");
  if (state.reliabilityDraftChanged && rowsElement.children.length > 0) return;
  const keys = detectorConfigKeys(dashboard);
  const summaryByKey = new Map((dashboard.reliability?.keys || []).map(item => [item.keyId, item]));
  $("#reliabilityConfigMeta").textContent = `${keys.length} 个 Key`;
  rowsElement.innerHTML = keys.length ? keys.map(key => {
    const binding = state.draftDetectorBindings.get(key.id) || {
      keyId: key.id, baseUrl: "", models: [], enabled: false
    };
    const models = new Set(binding.models.length
      ? binding.models.map(enumValue)
      : ["sol", "terra", "luna"]);
    const hasCredential = state.detectorCredentialKeyIds.has(key.id);
    const pendingClear = state.detectorCredentialClears.has(key.id);
    const credentialState = pendingClear ? "待清除" : hasCredential ? "已保存" : "未配置";
    const summary = summaryByKey.get(key.id);
    const status = summary?.status || (binding.enabled || hasCredential ? "evidenceInsufficient" : "unconfigured");
    return `<tr data-key-id="${key.id}">
      <td><input type="checkbox" class="detector-enabled" aria-label="启用 ${escapeHtml(key.name)} 检测" ${binding.enabled ? "checked" : ""}></td>
      <td><strong>${escapeHtml(key.name || `Key ${key.id}`)}</strong><div class="metric-line">ID ${key.id}</div></td>
      <td>${key.groupId ?? "-"}</td>
      <td><input class="detector-base-url" type="url" inputmode="url" autocomplete="off" value="${escapeHtml(binding.baseUrl)}" aria-label="${escapeHtml(key.name)} 检测地址"></td>
      <td><div class="reliability-models">
        ${["sol", "terra", "luna"].map(model => `<label><input type="checkbox" class="detector-model" value="${model}" ${models.has(model) ? "checked" : ""}><span>${model}</span></label>`).join("")}
      </div></td>
      <td><div class="detector-secret-field ${pendingClear ? "pending-clear" : ""}">
        <input class="detector-secret-input" type="password" autocomplete="new-password" placeholder="${credentialState}" aria-label="${escapeHtml(key.name)} 检测凭据">
        <button class="icon-button detector-secret-clear" type="button" title="${pendingClear ? "撤销清除" : "清除检测凭据"}" aria-label="${pendingClear ? "撤销清除" : "清除"} ${escapeHtml(key.name)} 检测凭据" aria-pressed="${pendingClear}">×</button>
        <span class="detector-secret-state">${credentialState}</span>
      </div></td>
      <td>${reliabilityStateBadge(status)}${summary?.lastCheckedAt ? `<div class="metric-line">${formatDate(summary.lastCheckedAt)}</div>` : ""}</td>
    </tr>`;
  }).join("") : '<tr><td class="empty-state" colspan="7">刷新后显示 Key</td></tr>';
}

function syncDetectorCredentialRow(row) {
  const keyId = Number(row?.dataset.keyId);
  const input = row?.querySelector(".detector-secret-input");
  const field = row?.querySelector(".detector-secret-field");
  const button = row?.querySelector(".detector-secret-clear");
  const stateLabel = row?.querySelector(".detector-secret-state");
  if (!(keyId > 0) || !input || !field || !button || !stateLabel) return;

  const pendingClear = state.detectorCredentialClears.has(keyId);
  const pendingReplace = !pendingClear && input.value.trim().length > 0;
  const hasSavedCredential = state.detectorCredentialKeyIds.has(keyId);
  const label = pendingClear ? "待清除" : pendingReplace ? "待替换" :
    hasSavedCredential ? "已保存" : "未配置";
  field.classList.toggle("pending-clear", pendingClear);
  button.title = pendingClear ? "撤销清除" : "清除检测凭据";
  button.setAttribute("aria-label", `${pendingClear ? "撤销清除" : "清除"} 检测凭据`);
  button.setAttribute("aria-pressed", String(pendingClear));
  stateLabel.textContent = label;
}

function networkMetric(summary) {
  if (!summary) return "-";
  const errors = (summary.errorCategories || [])
    .map(item => `${item.category}:${item.count}`)
    .join(" / ");
  return `任务 ${summary.logicalCompleted ?? 0}/${summary.logicalTasks ?? 0} · 成功 ${summary.successful ?? 0} · HTTP ${summary.httpAttempts ?? 0} · 重试 ${summary.retries ?? 0}` +
    (errors ? ` · ${errors}` : "");
}

function evidenceMetric(summary) {
  if (!summary) return "-";
  const juiceState = ({
    pass: "通过", mismatch: "不一致", insufficient: "证据不足", possible_non_gpt: "疑似非GPT"
  })[summary.juiceState] || "未知";
  const fingerprintState = summary.fingerprintState === "strong_match"
    ? `强指向 ${summary.fingerprintModel || "已知型号"}`
    : summary.fingerprintEnabled ? "证据不明确" : "未启用";
  return `Juice ${juiceState} ${summary.juiceValidCompleted ?? 0}` +
    ` · 输出 ${summary.outputExact ?? 0}/${summary.outputRequests ?? 0}` +
    ` · 覆盖 ${summary.coverageRequests ?? 0}` +
    ` · 指纹 ${fingerprintState}`;
}

function renderReliabilityProbes(runtime) {
  const probes = runtime?.probes || [];
  $("#reliabilityProbeMeta").textContent = `${probes.length} 项`;
  $("#reliabilityProbeRows").innerHTML = probes.length ? probes.map(probe => {
    const stage = enumValue(probe.stage);
    const stageClass = stage === "completed" ? "available" : stage === "failed" ? "error" : "warning";
    const status = probe.status ? reliabilityLabel(probe.status) : "-";
    const verdict = probe.verdict && enumValue(probe.verdict) !== "evidenceInsufficient" ? ` · ${probe.verdict}` : "";
    const error = probe.errorCategory && enumValue(probe.errorCategory) !== "none" ? ` · ${probe.errorCategory}` : "";
    const skipReason = reliabilitySkipReasonLabel(probe.skipReason);
    const capability = capabilityStatusLabel(probe.capabilityStatus);
    const outcome = [status + verdict + error, skipReason].filter(Boolean).join(" · ");
    return `<tr>
      <td>${escapeHtml(probe.keyName || `Key ${probe.keyId}`)}<div class="metric-line">${probe.keyId} / ${probe.groupId ?? "-"}</div></td>
      <td>${escapeHtml(probe.model || "-")}</td>
      <td>${escapeHtml(probeFamilyLabel(probe.family))}</td>
      <td><span class="state-badge ${stageClass}">${escapeHtml(probeStageLabel(probe.stage))}</span></td>
      <td>${escapeHtml(outcome || "-")}</td>
      <td><div class="metric-line">${escapeHtml(networkMetric(probe.network))}</div></td>
      <td><div class="metric-line">${escapeHtml(evidenceMetric(probe.evidence))}</div>${capability ? `<div class="reliability-note ${enumValue(probe.capabilityStatus) === "healthy" ? "available" : "warning"}">${escapeHtml(capability)}</div>` : ""}</td>
      <td>${formatDate(probe.completedAt || probe.startedAt || probe.queuedAt)}${probe.nextCheckAt ? `<div class="metric-line">下次 ${formatDate(probe.nextCheckAt)}</div>` : ""}</td>
    </tr>`;
  }).join("") : '<tr><td class="empty-state" colspan="8">等待检测</td></tr>';
}

function renderReliabilityTimeline(runtime) {
  const allEvents = runtime?.events || [];
  const events = [...allEvents].sort((a, b) => b.sequence - a.sequence).slice(0, 120);
  $("#reliabilityTimelineMeta").textContent = `${allEvents.length} 条事件${runtime?.timelineTruncated ? " · 已裁剪" : ""}`;
  $("#reliabilityTimelineRows").innerHTML = events.length ? events.map(item => {
    const status = item.status ? reliabilityLabel(item.status) : "-";
    const verdict = item.verdict && enumValue(item.verdict) !== "evidenceInsufficient" ? ` · ${item.verdict}` : "";
    const error = item.errorCategory && enumValue(item.errorCategory) !== "none" ? item.errorCategory : "";
    const isolation = item.quarantinedUntil ? `至 ${formatDate(item.quarantinedUntil)}` : "";
    const skipReason = reliabilitySkipReasonLabel(item.skipReason);
    const capability = capabilityStatusLabel(item.capabilityStatus);
    const nextCheck = item.nextCheckAt ? `下次 ${formatDate(item.nextCheckAt)}` : "";
    return `<tr>
      <td>#${item.sequence}</td>
      <td>${formatDate(item.occurredAt)}</td>
      <td>${escapeHtml(item.keyName || (item.keyId ? `Key ${item.keyId}` : "轮次"))}<div class="metric-line">${item.keyId ?? "-"} / ${item.groupId ?? "-"}</div></td>
      <td>${escapeHtml(item.model || "-")}</td>
      <td>${escapeHtml(probeFamilyLabel(item.family))}</td>
      <td>${escapeHtml(eventTypeLabel(item.eventType))}</td>
      <td>${escapeHtml(status + verdict)}</td>
      <td>${escapeHtml([skipReason, capability, error, isolation, nextCheck].filter(Boolean).join(" · ") || "-")}</td>
    </tr>`;
  }).join("") : '<tr><td class="empty-state" colspan="8">等待检测</td></tr>';
}

function renderReliability(dashboard) {
  const reliability = dashboard.reliability;
  const runtime = reliability?.runtime;
  const phase = enumValue(runtime?.phase || (dashboard.settings.reliabilityDetectionEnabled ? "idle" : "disabled"));
  $("#reliabilityRuntime").dataset.phase = phase;
  $("#reliabilityPhase").textContent = reliabilityPhaseLabel(phase);
  $("#reliabilityTrigger").textContent = reliabilityTriggerLabel(runtime?.trigger);
  $("#reliabilityProgress").textContent = `${runtime?.completedProbeCount ?? 0} / ${runtime?.totalProbeCount ?? 0}`;
  $("#reliabilityFailures").textContent = String(runtime?.failedProbeCount ?? 0);
  $("#reliabilityRunId").textContent = runtime?.runId ? runtime.runId.slice(0, 12) : "-";
  $("#reliabilityNextCheck").textContent = formatDate(runtime?.nextCheckAt);
  $("#reliabilitySchedule").textContent = dashboard.settings.reliabilityDetectionEnabled
    ? `每小时 · ${runtime?.selectedKeyCount ?? 0} 个 Key`
    : "已关闭";
  $("#reliabilityCheckButton").disabled = !dashboard.settings.reliabilityDetectionEnabled || state.requestInFlight;
  renderReliabilityConfig(dashboard);
  renderReliabilityProbes(runtime);
  renderReliabilityTimeline(runtime);
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
  renderLunaRoute(dashboard.lunaRoute, (dashboard.settings.lunaSelectedKeyIds || []).length);
  $("#autoRouting").checked = dashboard.autoRouting;
  $("#busyIndicator").hidden = !dashboard.isBusy && !state.requestInFlight;
  $$("#refreshButton, #dryRunButton, #routeButton, #saveButton").forEach(button => {
    button.disabled = dashboard.isBusy || state.requestInFlight;
  });
  $("#manualButton").disabled = dashboard.isBusy || state.requestInFlight || !state.selectedGroupId;
  renderGroups(dashboard.groups);
  renderProviders(dashboard.providers);
  renderKeys(dashboard.keys);
  renderReliability(dashboard);
  const providerReferenceMessage = [
    dashboard.providerSeriesStatus?.message,
    dashboard.providerCacheHitRateStatus?.message
  ].filter(Boolean).join(" · ");
  $("#statusText").textContent = providerReferenceMessage
    ? `${dashboard.status} · ${providerReferenceMessage}`
    : dashboard.status;
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
  const detectorBindings = [...state.draftDetectorBindings.values()]
    .filter(binding => binding.baseUrl || binding.enabled)
    .map(binding => {
      if (binding.enabled && !binding.baseUrl) {
        throw new Error(`Key ${binding.keyId} 的检测地址不能为空。`);
      }
      if (binding.models.length === 0) {
        throw new Error(`Key ${binding.keyId} 至少选择一个检测模型。`);
      }
      return binding;
    });
  const detectorApiKeys = {};
  $$("#reliabilityConfigRows .detector-secret-input").forEach(input => {
    const keyId = Number(input.closest("tr")?.dataset.keyId);
    if (keyId > 0 && input.value) detectorApiKeys[keyId] = input.value;
  });
  for (const keyId of state.detectorCredentialClears) detectorApiKeys[keyId] = "";
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
    confidenceImpact: readBoundedNumber("#confidenceImpact", 0, 2, "置信度影响强度"),
    minimumConfidence: readBoundedNumber("#minimumConfidence", 0, 1, "最低置信度"),
    providerSeriesWeight: readBoundedNumber("#providerSeriesWeight", 0, 1, "供应商序列权重"),
    providerSeriesCacheSeconds: readBoundedInteger("#providerSeriesCacheSeconds", 30, 3600, "供应商序列响应缓存"),
    providerSeriesRange: readRequiredText("#providerSeriesRange", "供应商序列范围"),
    providerSeriesTimezone: readRequiredText("#providerSeriesTimezone", "供应商序列时区"),
    pollingIntervalSeconds: Number.parseInt($("#pollingInterval").value, 10),
    persistCredentials: $("#persistCredentials").checked,
    themeMode: $("#themeSelect").value,
    selectedKeyIds: [...state.draftKeyIds],
    blacklistedGroupIds: [...state.draftBlacklistIds],
    lunaSelectedKeyIds: [...state.draftLunaKeyIds],
    reliabilityDetectionEnabled: $("#reliabilityDetectionEnabled").checked,
    reliabilityDetectionIntervalSeconds: 3600,
    reliabilityQuarantineHours: readBoundedInteger("#reliabilityQuarantineHours", 1, 168, "可靠性隔离时长"),
    detectorBindings,
    detectorApiKeys
  };
}

async function runAction(path, options = {}) {
  if (state.requestInFlight) return;
  state.requestInFlight = true;
  if (state.dashboard) renderDashboard(state.dashboard);
  try {
    const response = await api(path, { method: "POST", ...options });
    renderDashboard(response.dashboard || response);
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
  const persistCredentials = $("#persistCredentials");
  const autoEnabledCredentialPersistence =
    state.credentialDraftChanged && !persistCredentials.checked;
  if (autoEnabledCredentialPersistence && persistCredentials.disabled) {
    showStatusError("当前环境没有可用的安全凭据存储，无法保存认证。请配置 AIHUB_ROUTER_MASTER_KEY。");
    return;
  }

  if (autoEnabledCredentialPersistence) persistCredentials.checked = true;
  state.requestInFlight = true;
  try {
    const dashboard = await api("/api/settings", { method: "PUT", body: JSON.stringify(settingsPayload()) });
    if (autoEnabledCredentialPersistence) {
      dashboard.status = "配置已保存，认证已启用安全持久化。";
    }
    state.settingsHydrated = false;
    renderDashboard(dashboard, true);
  } catch (error) {
    if (autoEnabledCredentialPersistence) persistCredentials.checked = false;
    showStatusError(error.message);
  } finally {
    state.requestInFlight = false;
    if (state.dashboard) renderDashboard(state.dashboard);
  }
});

$("#refreshButton").addEventListener("click", () => runAction("/api/actions/refresh"));
$("#reliabilityCheckButton").addEventListener("click", () => runAction("/api/actions/reliability-check"));
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
$("#email").addEventListener("input", () => { state.credentialDraftChanged = true; });
$("#password").addEventListener("input", () => {
  state.credentialDraftChanged = true;
  state.clearPassword = false;
  updateSecretControls(state.dashboard.settings);
});
$("#bearerToken").addEventListener("input", () => {
  state.credentialDraftChanged = true;
  state.clearToken = false;
  updateSecretControls(state.dashboard.settings);
});
$("#clearPassword").addEventListener("click", () => {
  $("#password").value = "";
  state.credentialDraftChanged = true;
  state.clearPassword = true;
  updateCredentialState(state.dashboard.settings);
  updateSecretControls(state.dashboard.settings);
});
$("#clearToken").addEventListener("click", () => {
  $("#bearerToken").value = "";
  state.credentialDraftChanged = true;
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
  if (!event.target.classList.contains("key-check") &&
      !event.target.classList.contains("luna-key-check")) return;
  const id = Number(event.target.dataset.id);
  const isLuna = event.target.classList.contains("luna-key-check");
  const selected = isLuna ? state.draftLunaKeyIds : state.draftKeyIds;
  const other = isLuna ? state.draftKeyIds : state.draftLunaKeyIds;
  event.target.checked ? selected.add(id) : selected.delete(id);
  if (event.target.checked) {
    other.delete(id);
    const otherInput = $("#keyRows input." +
      (isLuna ? "key-check" : "luna-key-check") +
      '[data-id="' + id + '"]');
    if (otherInput) otherInput.checked = false;
  }
});

function updateDetectorBindingFromRow(row) {
  const keyId = Number(row?.dataset.keyId);
  if (!(keyId > 0)) return;
  const models = [...row.querySelectorAll(".detector-model:checked")].map(input => input.value);
  state.draftDetectorBindings.set(keyId, {
    keyId,
    baseUrl: row.querySelector(".detector-base-url").value.trim().replace(/\/$/, ""),
    models,
    enabled: row.querySelector(".detector-enabled").checked
  });
  state.reliabilityDraftChanged = true;
}

$("#reliabilityConfigRows").addEventListener("change", event => {
  if (!event.target.matches(".detector-enabled, .detector-model")) return;
  updateDetectorBindingFromRow(event.target.closest("tr"));
});

$("#reliabilityConfigRows").addEventListener("input", event => {
  const row = event.target.closest("tr");
  if (event.target.classList.contains("detector-base-url")) {
    updateDetectorBindingFromRow(row);
    return;
  }
  if (event.target.classList.contains("detector-secret-input")) {
    const keyId = Number(row?.dataset.keyId);
    state.detectorCredentialClears.delete(keyId);
    syncDetectorCredentialRow(row);
    state.reliabilityDraftChanged = true;
    state.credentialDraftChanged = true;
  }
});

$("#reliabilityConfigRows").addEventListener("click", event => {
  const button = event.target.closest(".detector-secret-clear");
  if (!button) return;
  const row = button.closest("tr");
  const keyId = Number(row?.dataset.keyId);
  const input = row?.querySelector(".detector-secret-input");
  if (!(keyId > 0) || !input) return;
  input.value = "";
  if (state.detectorCredentialClears.has(keyId)) {
    state.detectorCredentialClears.delete(keyId);
  } else {
    state.detectorCredentialClears.add(keyId);
  }
  syncDetectorCredentialRow(row);
  state.reliabilityDraftChanged = true;
  state.credentialDraftChanged = true;
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
