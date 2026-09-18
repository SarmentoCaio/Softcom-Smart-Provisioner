(() => {
  const state = {
    bootstrap: null,
    accessMode: "online",
    onlineConnected: false,
    environment: "aws1",
    databases: [],
    database: "",
    companies: [],
    company: null,
    oauthClients: [],
    oauthClient: null,
    androidDevices: [],
    androidSerial: "",
    androidSerials: [],
    androidSelectionInitialized: false,
    multiDevice: false,
    androidTargets: {},
    provisioningJobs: {},
    generatedUrl: "",
    module: "smart_pdv",
    useSelfHost: false,
    selfHostBaseUrl: "",
    deviceMode: "existing",
    fiscalSeries: [],
    seriesExpanded: false,
    databaseSuggestionIndex: -1,
    oauthSuggestionIndex: -1,
    logs: [],
    updateAvailable: false,
    updateRequired: false,
    latestVersion: "",
    hasSavedTefToken: false,
    tefSettingsInitialized: false,
    busy: new Set()
  };

  const $ = (id) => document.getElementById(id);
  const send = (action, payload = {}) => window.chrome?.webview?.postMessage({ action, payload });
  const databasePrefix = "softcoms_softcomshop_";
  let pendingProvisionConfirmation = null;

  function toast(message, type = "info") {
    const host = $("toast-host");
    const el = document.createElement("div");
    el.className = `toast ${type}`;
    el.textContent = message;
    host.appendChild(el);
    setTimeout(() => el.remove(), 4200);
  }

  function showInlineError(id, message) {
    const el = $(id);
    if (!el) {
      toast(message, "error");
      return;
    }
    el.textContent = message || "Ocorreu um erro.";
    el.classList.remove("hidden");
  }

  function clearInlineError(id) {
    const el = $(id);
    if (!el) return;
    el.textContent = "";
    el.classList.add("hidden");
  }

  function selectedProvisionDevicesLabel() {
    const selected = new Set(state.androidSerials || []);
    const names = state.androidDevices
      .filter(device => selected.has(device.serial))
      .map(device => device.friendlyName || device.model || device.serial)
      .filter(Boolean);
    if (!names.length) return "Nenhum selecionado";
    if (names.length <= 2) return `${names.length} · ${names.join(" + ")}`;
    return `${names.length} · ${names.slice(0, 2).join(" + ")} +${names.length - 2}`;
  }

  function openProvisionConfirmation(options) {
    const modal = $("provision-confirm-modal");
    if (!modal) return;
    pendingProvisionConfirmation = options.onConfirm;
    $("provision-confirm-title").textContent = options.title || "Confirmar provisionamento";
    $("provision-confirm-description").textContent = options.description || "Confira os dados antes de iniciar.";
    $("provision-confirm-module").textContent = options.moduleLabel || "Smart";
    $("provision-confirm-devices").textContent = selectedProvisionDevicesLabel();
    $("provision-confirm-access").textContent = options.accessLabel || "—";
    $("provision-confirm-data").textContent = options.clearData ? "Serão limpos" : "Serão preservados";
    modal.classList.remove("hidden");
    setTimeout(() => $("provision-confirm-submit")?.focus(), 0);
  }

  function closeProvisionConfirmation() {
    $("provision-confirm-modal")?.classList.add("hidden");
    pendingProvisionConfirmation = null;
  }

  function acceptProvisionConfirmation() {
    const action = pendingProvisionConfirmation;
    closeProvisionConfirmation();
    if (typeof action === "function") action();
  }

  function setBusy(key, active) {
    if (active) state.busy.add(key); else state.busy.delete(key);
    const map = {
      databases: $("load-databases"),
      android: $("refresh-android"),
      vpn: $("connect-vpn"),
      docker: $("connect-docker"),
      validation: $("validate-link"),
      provision: $("prepare-smart"),
      createDevice: $("create-device"),
      online: $("use-database"),
      update: $("check-update"),
      updateInstall: $("install-update")
    };
    const el = map[key];
    if (el) {
      el.disabled = active;
      el.classList.toggle("busy", active);
      if (key === "validation") el.textContent = active ? "Validando..." : "Validar";
      if (key === "provision") el.textContent = active ? "Provisionando..." : "Provisionar selecionados";
      if (key === "createDevice") el.textContent = active ? "Criando..." : "Criar dispositivo";
      if (key === "online") el.textContent = active ? "Conectando..." : (state.accessMode === "online" ? "Conectar" : "Usar");
      if (key === "update") el.textContent = active ? "Verificando..." : "Verificar agora";
      if (key === "updateInstall") el.textContent = active ? "Atualizando..." : "Atualizar agora";
    }

    if (key === "createDevice") {
      const modalButton = $("selfhost-create-confirm");
      if (modalButton) {
        modalButton.disabled = active;
        modalButton.classList.toggle("busy", active);
        modalButton.textContent = active ? "Criando..." : "Criar";
      }
    }

    if (key === "series") {
      ["reload-series", "save-nfce-series", "save-nfe-series"].forEach(id => {
        const button = $(id);
        if (!button) return;
        button.disabled = active;
        button.classList.toggle("busy", active);
      });
    }
  }

  function configureNavigation() {
    document.querySelectorAll(".nav-item").forEach(btn => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".nav-item").forEach(x => x.classList.remove("active"));
        btn.classList.add("active");
        const page = btn.dataset.page;
        document.querySelectorAll(".page").forEach(x => x.classList.remove("active"));
        $(`page-${page}`).classList.add("active");
        const titles = {
          provision: ["Preparar dispositivo Smart", "Use o modo Online sem VPN ou o acesso direto ao banco para provisionar o Smart."],
          logs: ["Logs", "Acompanhe VPN, banco, ADB e preparação sem janelas de console."],
          settings: ["Configurações", "Clientes salvos, atualizações e opções avançadas."]
        };
        $("page-title").textContent = titles[page][0];
        $("page-subtitle").textContent = titles[page][1];
      });
    });
  }

  function configureTheme() {
    $("theme-toggle").addEventListener("click", () => {
      document.documentElement.classList.toggle("light");
      $("theme-toggle").textContent = document.documentElement.classList.contains("light") ? "☾" : "☼";
    });
  }

  function renderBootstrap() {
    const b = state.bootstrap;
    if (!b) return;

    state.logs = b.logs || state.logs || [];
    renderLogs();

    $("app-version").textContent = `v${b.app.version}`;
    state.environment = b.settings.lastEnvironment || "aws1";
    state.accessMode = b.settings.accessMode === "docker" || b.settings.accessMode === "database" ? "docker" : "online";
    if (state.accessMode === "online") {
      state.databases = rememberedOnlineClients();
      const lastOnline = b.settings.lastOnlineClient || b.settings.lastDatabase || "";
      if (lastOnline) {
        state.database = normalizeDatabaseName(lastOnline);
        $("database-input").value = displayDatabaseName(lastOnline);
      }
    } else {
      state.database = "";
      $("database-input").value = "";
    }
    $("smart-package").value = b.settings.smartPackageName || "";
    state.hasSavedTefToken = !!b.smartTef?.hasSavedToken;
    if (!state.tefSettingsInitialized) {
      $("tef-device-name").value = b.settings.smartTefDeviceName || "SMART 1";
      $("tef-cnpj").value = b.settings.smartTefCnpj || "";
      $("tef-empresa-id").value = b.settings.smartTefEmpresaId || "";
      $("save-tef-configuration").checked = b.settings.saveSmartTefConfiguration === true;
      state.tefSettingsInitialized = true;
    }
    $("tef-token").placeholder = state.hasSavedTefToken
      ? "Token salvo com proteção local"
      : "Token do Smart TEF";
    if ($("update-channel")) $("update-channel").value = b.settings.updateChannel === "beta" ? "beta" : "stable";
    if ($("update-auto-check")) $("update-auto-check").checked = b.settings.autoCheckUpdates !== false;
    if ($("update-auto-install")) $("update-auto-install").checked = b.settings.autoInstallUpdates !== false;
    if ($("update-stable-url")) $("update-stable-url").value = b.settings.stableManifestUrl || "";
    if ($("update-beta-url")) $("update-beta-url").value = b.settings.betaManifestUrl || "";
    const activeManifest = (b.settings.updateChannel === "beta" ? b.settings.betaManifestUrl : b.settings.stableManifestUrl) || "";
    if ($("update-status-badge") && !state.updateAvailable) {
      $("update-status-badge").textContent = activeManifest ? "Configurado" : "Não configurado";
      $("update-status-badge").className = activeManifest ? "ok" : "warn";
    }
    state.selfHostBaseUrl = b.selfHost?.defaultBaseUrl || "";
    if ($("selfhost-base-url")) $("selfhost-base-url").value = state.selfHostBaseUrl;
    if ($("selfhost-status")) {
      const sh = b.selfHost || {};
      if (sh.installed) {
        $("selfhost-status").textContent = `SelfHost ${sh.version || "?"} · ${sh.generation || "versão identificada"}`;
        $("selfhost-status").title = `${sh.installPath || ""}${sh.configDescription ? ` · ${sh.configDescription}` : ""}`;
      } else {
        $("selfhost-status").textContent = "SelfHost não localizado.";
      }
    }
    const cap = b.capabilities;
    const dbOk = !!cap.databaseCredentials;
    $("db-credential-status").textContent = dbOk ? "Credenciais OK" : "Sem credenciais";
    $("db-credential-status").className = `mini-status ${dbOk ? "ok" : "warn"}`;

    $("setting-db-status").textContent = dbOk ? "Configurado" : "Pendente";
    $("setting-db-status").className = dbOk ? "ok" : "warn";
    $("setting-vpn-status").textContent = cap.vpnCredentials ? "Configurado" : "Pendente";
    $("setting-vpn-status").className = cap.vpnCredentials ? "ok" : "warn";
    $("setting-api-status").textContent = cap.apiCredentials ? "Configurado" : "Pendente";
    $("setting-api-status").className = cap.apiCredentials ? "ok" : "warn";

    $("setting-adb-status").textContent = cap.adbAvailable ? "Disponível" : "Não localizado";
    $("setting-adb-status").className = cap.adbAvailable ? "ok" : "bad";
    $("setting-scrcpy-status").textContent = cap.scrcpyAvailable ? "Disponível" : "Não localizado";
    $("setting-scrcpy-status").className = cap.scrcpyAvailable ? "ok" : "bad";
    $("setting-openvpn-status").textContent = cap.openVpn ? "Localizado" : "Não localizado";
    $("setting-openvpn-status").className = cap.openVpn ? "ok" : "warn";
    $("vpn-profile").textContent = cap.vpnProfile || "Nenhum perfil localizado";

    const essentials = cap.adbAvailable && cap.scrcpyAvailable;
    $("global-status-dot").className = `status-dot ${essentials ? "ok" : "error"}`;
    $("global-status").textContent = essentials ? "Componentes locais OK" : "Verificar instalação";
    $("global-substatus").textContent = state.accessMode === "online" ? "Modo Online" : "Banco via Docker isolado";
    renderAccessMode();
    renderSavedOnlineClients();
  }

  function renderAccessMode() {
    const online = state.accessMode === "online";
    const docker = state.accessMode === "docker";
    document.querySelectorAll("#access-mode-switch button").forEach(btn => {
      btn.classList.toggle("active", btn.dataset.access === state.accessMode);
    });
    $("load-databases")?.classList.add("hidden");
    $("connect-vpn")?.classList.add("hidden");
    $("connect-docker")?.classList.add("hidden");
    $("use-database").textContent = online ? "Conectar" : "Localizar";
    $("db-credential-status").textContent = online
      ? (state.onlineConnected ? "Online conectado" : "Online")
      : (state.bootstrap?.capabilities?.dockerAvailable ? "Docker disponível" : "Docker ausente");
    $("db-credential-status").className = `mini-status ${online ? (state.onlineConnected ? "ok" : "") : (state.bootstrap?.capabilities?.dockerAvailable ? "ok" : "warn")}`;
    $("client-helper").textContent = online
      ? "Informe o cliente. O acesso ocorre pela sessão WEB do Softcomshop."
      : "Digite o cliente; o Provisioner procura automaticamente nos dois ambientes AWS pelo Docker.";
    $("access-mode-helper").textContent = online
      ? "Usa a sessão Web do Softcomshop."
      : "Busca o cliente nas duas AWS automaticamente.";
    $("open-client-site").disabled = !state.database;
    $("global-substatus").textContent = online
      ? (state.onlineConnected ? "Softcomshop Online conectado" : "Modo Online")
      : (state.bootstrap?.capabilities?.dockerAvailable ? "Banco via Docker isolado" : "Docker Desktop não localizado");
    renderCompanyPreview();
    renderModuleMode();
    updateActions();
  }

  function displayDatabaseName(database) {
    return String(database || "").replace(/^softcoms_softcomshop_/i, "");
  }

  function normalizeDatabaseName(value) {
    const typed = String(value || "").trim();
    if (!typed) return "";
    return /^softcoms_softcomshop_/i.test(typed) ? typed : `${databasePrefix}${typed}`;
  }

  function rememberedOnlineClients() {
    const settings = state.bootstrap?.settings || {};
    const recent = Array.isArray(settings.recentOnlineClients) ? settings.recentOnlineClients : [];
    const last = settings.lastOnlineClient || settings.lastDatabase || "";
    const values = [...recent];
    if (last) values.unshift(last);
    const seen = new Set();
    return values
      .map(normalizeDatabaseName)
      .filter(Boolean)
      .filter(value => {
        const key = value.toLowerCase();
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      });
  }

  function renderSavedOnlineClients() {
    const host = $("saved-online-clients");
    if (!host) return;

    const clients = rememberedOnlineClients();
    if (!clients.length) {
      host.innerHTML = `<div class="saved-client-empty">Nenhum cliente Online salvo.</div>`;
      return;
    }

    host.innerHTML = clients.map(client => {
      const display = displayDatabaseName(client);
      return `
        <div class="saved-client-row">
          <div class="saved-client-copy">
            <strong>${escapeHtml(display)}</strong>
            <small>Salvo localmente para pesquisa rápida</small>
          </div>
          <button type="button" class="ghost compact saved-client-remove" data-database="${escapeAttr(client)}">Excluir</button>
        </div>`;
    }).join("");

    host.querySelectorAll(".saved-client-remove").forEach(button => {
      button.addEventListener("click", () => {
        const database = button.dataset.database || "";
        const display = displayDatabaseName(database);
        if (!database || !confirm(`Remover ${display} da lista de clientes salvos neste computador?`)) return;
        send("removeOnlineClient", { database });
      });
    });
  }

  function resetDatabaseDependents() {
    state.company = null;
    state.oauthClient = null;
    state.companies = [];
    state.oauthClients = [];
    state.fiscalSeries = [];
    renderCompanies();
    renderCompanyPreview();
    renderOauthClients();
    renderOauthPreview();
    renderFiscalSeries();
    setSetupCompact(false);
    updateActions();
  }

  function setSetupCompact(compact) {
    const grid = $("setup-grid");
    if (!grid) return;
    grid.classList.toggle("compact", !!compact);
    $("expand-setup")?.classList.toggle("hidden", !compact);
    const companySummary = $("setup-company-summary");
    companySummary?.classList.toggle("hidden", !compact);
    if (companySummary) {
      companySummary.textContent = compact && state.company
        ? `Empresa: ${state.company.name}`
        : "";
    }
    const headings = grid.querySelectorAll("h2");
    if (headings[0]) headings[0].textContent = compact && state.database
      ? `Softcomshop · ${displayDatabaseName(state.database)}`
      : "Softcomshop";
    if (headings[1]) headings[1].textContent = compact && state.company
      ? state.company.name
      : "Empresa do dispositivo";
  }

  function useTypedDatabase() {
    const input = $("database-input");
    const database = normalizeDatabaseName(input.value);
    state.database = database;
    input.value = displayDatabaseName(database);
    resetDatabaseDependents();
    $("database-error").classList.add("hidden");
    if (database) {
      if (state.accessMode === "online") send("connectOnline", { database: state.database, accessMode: state.accessMode });
      else send("resolveDockerClient", { database: state.database, accessMode: "docker" });
    }
  }

  function getFilteredDatabases() {
    const query = $("database-input").value.trim().toLowerCase();
    const items = state.databases
      .map(db => ({ raw: db, name: displayDatabaseName(db) }))
      .filter(item => !query || item.name.toLowerCase().includes(query))
      .sort((a, b) => {
        const aStarts = query && a.name.toLowerCase().startsWith(query) ? 0 : 1;
        const bStarts = query && b.name.toLowerCase().startsWith(query) ? 0 : 1;
        return aStarts - bStarts || a.name.localeCompare(b.name, "pt-BR", { sensitivity: "base" });
      });
    return items.slice(0, 15);
  }

  function hideDatabaseSuggestions() {
    const host = $("database-suggestions");
    host.classList.add("hidden");
    $("database-input").setAttribute("aria-expanded", "false");
    state.databaseSuggestionIndex = -1;
  }

  function renderDatabaseSuggestions(forceOpen = false) {
    const host = $("database-suggestions");
    const input = $("database-input");
    const items = getFilteredDatabases();

    if (!state.databases.length) {
      hideDatabaseSuggestions();
      return;
    }

    if (!items.length) {
      host.innerHTML = `<div class="database-suggestion-empty">Nenhum cliente encontrado para <strong>${escapeHtml(input.value.trim())}</strong>.</div>`;
    } else {
      host.innerHTML = items.map((item, idx) => `
        <button type="button" class="database-suggestion ${idx === state.databaseSuggestionIndex ? "active" : ""}" data-database="${escapeAttr(item.raw)}" role="option">
          <span class="database-suggestion-icon">▤</span>
          <span class="database-suggestion-copy">
            <strong>${escapeHtml(item.name)}</strong>
            <small>Softcomshop</small>
          </span>
        </button>
      `).join("");
    }

    host.querySelectorAll(".database-suggestion").forEach(el => {
      el.addEventListener("mousedown", e => e.preventDefault());
      el.addEventListener("click", () => selectDatabaseSuggestion(el.dataset.database));
    });

    if (forceOpen || document.activeElement === input) {
      host.classList.remove("hidden");
      input.setAttribute("aria-expanded", "true");
    }
  }

  function selectDatabaseSuggestion(database) {
    if (!database) return;
    state.database = normalizeDatabaseName(database);
    $("database-input").value = displayDatabaseName(database);
    resetDatabaseDependents();
    $("database-error").classList.add("hidden");
    hideDatabaseSuggestions();
    loadCompanies();
  }

  function renderDatabases() {
    const input = $("database-input");
    if (!state.database) {
      if (state.environment === "aws2") {
        state.database = normalizeDatabaseName("jormungandr");
        input.value = "jormungandr";
      } else {
        const last = state.bootstrap?.settings?.lastDatabase || "";
        if (last) {
          state.database = normalizeDatabaseName(last);
          input.value = displayDatabaseName(last);
          loadCompanies();
        }
      }
    } else {
      input.value = displayDatabaseName(state.database);
    }
    renderDatabaseSuggestions(false);
  }

  function renderCompanies() {
    const select = $("company-select");
    select.disabled = state.companies.length === 0;
    select.innerHTML = `<option value="">Selecione a empresa...</option>`;
    state.companies.forEach(company => {
      const opt = document.createElement("option");
      opt.value = String(company.id);
      opt.textContent = company.displayName || company.name;
      select.appendChild(opt);
    });
  }

  function renderCompanyPreview() {
    const el = $("company-preview");
    const c = state.company;
    if (!c) {
      el.className = "company-preview empty";
      el.innerHTML = `<div class="company-avatar">—</div><div><strong>Nenhuma empresa selecionada</strong><span>${state.accessMode === "online" ? "Os dados serão lidos da sessão Web do Softcomshop." : (state.accessMode === "docker" ? "Os dados serão lidos do banco pelo túnel Docker isolado." : "Os dados serão lidos diretamente do banco pela VPN local.")}</span></div>`;
      return;
    }
    const initials = (c.name || "E").trim().slice(0, 2).toUpperCase();
    el.className = "company-preview";
    el.innerHTML = `<div class="company-avatar">${escapeHtml(initials)}</div><div><strong>${escapeHtml(c.name)}</strong><span>${escapeHtml(c.cnpj || "CNPJ não identificado")}</span></div>`;
  }

  function getFilteredOauthClients() {
    const input = $("oauth-input");
    const query = String(input?.value || "").trim().toLowerCase();
    return state.oauthClients
      .map((item, idx) => ({ item, idx }))
      .filter(({ item }) => !query || String(item.name || "").toLowerCase().includes(query))
      .sort((a, b) => {
        const aName = String(a.item.name || "");
        const bName = String(b.item.name || "");
        const aStarts = query && aName.toLowerCase().startsWith(query) ? 0 : 1;
        const bStarts = query && bName.toLowerCase().startsWith(query) ? 0 : 1;
        return aStarts - bStarts || aName.localeCompare(bName, "pt-BR", { sensitivity: "base" });
      })
      .slice(0, 30);
  }

  function hideOauthSuggestions() {
    const host = $("oauth-suggestions");
    const input = $("oauth-input");
    if (!host || !input) return;
    host.classList.add("hidden");
    input.setAttribute("aria-expanded", "false");
    state.oauthSuggestionIndex = -1;
  }

  function renderOauthSuggestions(forceOpen = false) {
    const host = $("oauth-suggestions");
    const input = $("oauth-input");
    if (!host || !input || input.disabled || !state.oauthClients.length) {
      hideOauthSuggestions();
      return;
    }

    const items = getFilteredOauthClients();
    if (!items.length) {
      host.innerHTML = `<div class="database-suggestion-empty">Nenhum dispositivo encontrado para <strong>${escapeHtml(input.value.trim())}</strong>.</div>`;
    } else {
      host.innerHTML = items.map(({ item, idx }, suggestionIndex) => {
        const status = item.deviceId ? "vinculado" : "disponível";
        const detail = item.deviceId ? `${status} · ${escapeHtml(item.deviceId)}` : status;
        return `
          <button type="button" class="database-suggestion device-suggestion ${suggestionIndex === state.oauthSuggestionIndex ? "active" : ""}" data-index="${idx}" role="option">
            <span class="device-suggestion-line">
              <strong>${escapeHtml(item.name || "Sem nome")}</strong>
              <small> · ${detail}</small>
            </span>
          </button>`;
      }).join("");
    }

    host.querySelectorAll(".database-suggestion").forEach(el => {
      el.addEventListener("mousedown", e => e.preventDefault());
      el.addEventListener("click", () => selectOauthSuggestion(Number(el.dataset.index)));
    });

    if (forceOpen || document.activeElement === input) {
      host.classList.remove("hidden");
      input.setAttribute("aria-expanded", "true");
    }
  }

  function selectOauthSuggestion(index) {
    if (!Number.isInteger(index) || !state.oauthClients[index]) return;
    state.oauthClient = state.oauthClients[index];
    state.fiscalSeries = [];
    clearInlineError("series-error");
    const select = $("oauth-select");
    if (select) select.value = String(index);
    const input = $("oauth-input");
    if (input) input.value = state.oauthClient.name || "";
    if (state.androidSerials[0]) state.androidTargets[state.androidSerials[0]] = state.oauthClient.clientId;
    hideOauthSuggestions();
    renderOauthPreview();
    renderAndroid();
    loadFiscalSeries();
  }

  function renderOauthClients() {
    const select = $("oauth-select");
    const input = $("oauth-input");
    if (select) {
      select.disabled = state.oauthClients.length === 0;
      select.innerHTML = `<option value="">Selecione um dispositivo...</option>`;
      state.oauthClients.forEach((item, idx) => {
        const opt = document.createElement("option");
        opt.value = String(idx);
        const status = item.deviceId ? "vinculado" : "disponível";
        opt.textContent = `${item.name} · ${status}`;
        select.appendChild(opt);
      });
    }

    if (input) {
      input.disabled = state.oauthClients.length === 0;
      input.placeholder = state.oauthClients.length
        ? `Digite o nome do dispositivo (${state.oauthClients.length} disponível(is))`
        : (state.company ? "Nenhum dispositivo localizado" : "Selecione a empresa primeiro");

      if (state.oauthClient) {
        const idx = state.oauthClients.findIndex(x => x.clientId === state.oauthClient.clientId);
        if (idx >= 0) {
          if (select) select.value = String(idx);
          input.value = state.oauthClients[idx].name || "";
        } else if (document.activeElement !== input) {
          input.value = "";
        }
      } else if (document.activeElement !== input) {
        input.value = "";
      }
    }

    renderOauthSuggestions(false);
  }

  function renderOauthPreview(evaluateCurrentLink = true) {
    const el = $("oauth-preview");
    const o = state.oauthClient;
    if (!o) {
      el.className = "oauth-preview empty";
      el.innerHTML = `<div><strong>Selecione um dispositivo</strong><span>O vínculo atual será comparado com o Android escolhido.</span></div>`;
      updateActions();
      return;
    }
    const linked = !!o.deviceId;
    const detail = linked ? `device_id: ${o.deviceId}` : "Sem device_id atual";
    el.className = "oauth-preview";
    el.innerHTML = `<div><strong>${escapeHtml(o.name)}</strong><span>${escapeHtml(detail)}</span></div>`;
    renderFiscalSeries();
    updateActions();
    if (evaluateCurrentLink) evaluate();
  }

  function renderDeviceMode() {
    const selfHost = isSelfHostMode();
    if (selfHost && state.deviceMode !== "existing") state.deviceMode = "existing";
    const isNew = !selfHost && state.deviceMode === "new";
    document.querySelectorAll('input[name="device-mode"]').forEach(input => {
      const label = input.closest(".choice");
      if (label) label.classList.toggle("selected", input.value === state.deviceMode);
    });
    $("device-mode-grid")?.classList.toggle("hidden", selfHost);
    $("new-device-fields").classList.toggle("hidden", !isNew);
    $("existing-device-fields").classList.toggle("hidden", isNew);
    $("selfhost-new-device")?.classList.toggle("hidden", !selfHost);
    updateActions();
  }

  function openSelfHostCreateModal() {
    if (!isSelfHostMode()) return;
    const modal = $("selfhost-create-modal");
    const input = $("selfhost-create-name");
    if (!modal || !input) return;
    const modernSelfHost = state.bootstrap?.selfHost?.generation === "SelfHost 4.1+";
    input.value = "";
    $("selfhost-create-series").value = "";
    $("selfhost-create-number").value = "1";
    $("selfhost-create-nfe-series").value = "";
    $("selfhost-create-nfe-number").value = "1";
    $("selfhost-create-nfe-block")?.classList.toggle("hidden", !modernSelfHost);
    if ($("selfhost-create-primary-tag")) {
      $("selfhost-create-primary-tag").textContent = modernSelfHost ? "preferencial" : "SelfHost 4.0";
    }
    if ($("selfhost-create-helper")) {
      $("selfhost-create-helper").textContent = modernSelfHost
        ? "A NFC-e é usada quando informada. Deixe-a vazia para usar a NF-e como fallback; somente a série escolhida é enviada ao SelfHost."
        : "No SelfHost 4.0, o cadastro administrativo utiliza uma única série NFC-e e sua próxima numeração.";
    }
    clearInlineError("selfhost-create-error");
    modal.classList.remove("hidden");
    setTimeout(() => input.focus(), 0);
  }

  function closeSelfHostCreateModal() {
    $("selfhost-create-modal")?.classList.add("hidden");
  }

  function createSelfHostDevice() {
    clearInlineError("selfhost-create-error");
    const modernSelfHost = state.bootstrap?.selfHost?.generation === "SelfHost 4.1+";
    const name = $("selfhost-create-name")?.value.trim() || "";
    const seriesText = $("selfhost-create-series")?.value.trim() || "";
    const numberText = $("selfhost-create-number")?.value.trim() || "";
    const nfeSeriesText = modernSelfHost ? ($("selfhost-create-nfe-series")?.value.trim() || "") : "";
    const nfeNumberText = modernSelfHost ? ($("selfhost-create-nfe-number")?.value.trim() || "") : "";
    const series = Number(seriesText);
    const initialNumber = Number(numberText);
    const nfeSeries = Number(nfeSeriesText);
    const nfeInitialNumber = Number(nfeNumberText);

    if (!state.database || !state.company || !name) {
      showInlineError("selfhost-create-error", "Selecione cliente e empresa e informe o nome do dispositivo.");
      return;
    }
    const normalizedName = `SELFHOST_${name.replace(/^(SELFHOST_)+/i, "").trim()}`.toLowerCase();
    if (state.oauthClients.some(x => {
      const currentName = String(x.name || "").trim().toLowerCase();
      return currentName === name.toLowerCase() || currentName === normalizedName;
    })) {
      showInlineError("selfhost-create-error", `Já existe um cadastro chamado ${name} na lista atual.`);
      return;
    }
    if (seriesText) {
      if (!/^\d+$/.test(seriesText) || !Number.isInteger(series) || series < 0) {
        showInlineError("selfhost-create-error", "Informe uma série NFC-e válida.");
        return;
      }
      if (!/^\d+$/.test(numberText) || !Number.isInteger(initialNumber) || initialNumber < 1) {
        showInlineError("selfhost-create-error", "Informe uma numeração inicial NFC-e válida.");
        return;
      }
    } else if (modernSelfHost) {
      if (!/^\d+$/.test(nfeSeriesText) || !Number.isInteger(nfeSeries) || nfeSeries < 0) {
        showInlineError("selfhost-create-error", "Informe a série NFC-e ou, como alternativa, uma série NF-e válida.");
        return;
      }
      if (!/^\d+$/.test(nfeNumberText) || !Number.isInteger(nfeInitialNumber) || nfeInitialNumber < 1) {
        showInlineError("selfhost-create-error", "Informe uma numeração inicial NF-e válida.");
        return;
      }
    } else {
      showInlineError("selfhost-create-error", "Informe uma série NFC-e válida.");
      return;
    }
    send("createOauthClient", {
      accessMode: state.accessMode,
      environment: state.environment,
      database: state.database,
      companyId: state.company.id,
      name,
      series: seriesText,
      initialNumber: numberText,
      nfeSeries: nfeSeriesText,
      nfeInitialNumber: nfeSeriesText ? nfeNumberText : "",
      module: state.module,
      useSelfHost: true
    });
  }

  function loadFiscalSeries() {
    if (isTefMode() || !state.database || !state.company || !state.oauthClient) {
      state.fiscalSeries = [];
      renderFiscalSeries();
      return;
    }
    send("loadFiscalSeries", {
      accessMode: state.accessMode,
      environment: state.environment,
      database: state.database,
      companyId: state.company.id,
      clientId: state.oauthClient.clientId
    });
  }

  function seriesItems(type) {
    const normalized = type === "nfce" ? "NFCe" : "NFe";
    return (state.fiscalSeries || []).filter(x => x.documentType === normalized);
  }

  function renderSeriesType(type) {
    const select = $(`${type}-record`);
    if (!select) return;
    const items = seriesItems(type);
    const previous = select.value;
    select.innerHTML = `<option value="">Nova série</option>` + items.map(item =>
      `<option value="${item.id}">Série ${escapeHtml(item.series)} · nº ${item.initialNumber}${item.environment === 1 ? " · Produção" : " · Homologação"}</option>`
    ).join("");

    if (previous && items.some(x => String(x.id) === previous)) select.value = previous;
    else if (items.length) select.value = String(items[0].id);
    else select.value = "";

    applySeriesRecord(type);
  }

  function applySeriesRecord(type) {
    const id = $(`${type}-record`).value;
    const item = seriesItems(type).find(x => String(x.id) === String(id));
    $(`${type}-series`).value = item?.series ?? "";
    $(`${type}-number`).value = item?.initialNumber ?? 1;
    $(`${type}-environment`).value = String(item?.environment ?? 2);
  }

  function updateSeriesAccordion() {
    const panel = $("fiscal-series-panel");
    const content = $("series-content");
    const toggle = $("toggle-series");
    if (!panel || !content || !toggle) return;
    panel.classList.toggle("expanded", state.seriesExpanded);
    content.classList.toggle("collapsed", !state.seriesExpanded);
    toggle.setAttribute("aria-expanded", state.seriesExpanded ? "true" : "false");
  }

  function updateSeriesSummary() {
    const summary = $("series-summary");
    if (!summary) return;
    const nfce = seriesItems("nfce");
    const nfe = seriesItems("nfe");
    const nfceCurrent = nfce[0];
    const nfeCurrent = nfe[0];
    const parts = [];
    if (nfceCurrent) parts.push(`NFC-e ${escapeHtml(nfceCurrent.series)} · nº ${nfceCurrent.initialNumber}`);
    else parts.push("NFC-e sem série");
    if (nfeCurrent) parts.push(`NF-e ${escapeHtml(nfeCurrent.series)} · nº ${nfeCurrent.initialNumber}`);
    else parts.push("NF-e sem série");
    summary.innerHTML = parts.join(" &nbsp;•&nbsp; ");
  }

  function renderFiscalSeries() {
    const panel = $("fiscal-series-panel");
    if (!panel) return;
    const visible = !isTefMode() && state.deviceMode === "existing" && !!state.oauthClient;
    panel.classList.toggle("hidden", !visible);
    if (!visible) return;
    renderSeriesType("nfce");
    renderSeriesType("nfe");
    updateSeriesSummary();
    updateSeriesAccordion();
  }

  function saveFiscalSeries(type) {
    clearInlineError("series-error");
    if (!state.database || !state.company || !state.oauthClient) {
      showInlineError("series-error", "Selecione cliente, empresa e dispositivo Softcomshop.");
      return;
    }

    const series = $(`${type}-series`).value.trim();
    const initialNumber = Number($(`${type}-number`).value);
    const fiscalEnvironment = Number($(`${type}-environment`).value);
    const idText = $(`${type}-record`).value;
    if (!/^\d+$/.test(series) || !Number.isInteger(initialNumber) || initialNumber < 1) {
      showInlineError("series-error", "Informe a série e um número inicial válido.");
      return;
    }
    if (![1, 2].includes(fiscalEnvironment)) {
      showInlineError("series-error", "Selecione Produção ou Homologação.");
      return;
    }

    const selected = seriesItems(type).find(x => String(x.id) === String(idText));
    const selectedSeries = String(selected?.series ?? "").trim();
    const changingSeries = !!selected && selectedSeries !== series;
    const effectiveId = !changingSeries && idText ? Number(idText) : null;
    const label = type === "nfce" ? "NFC-e" : "NF-e";
    const action = changingSeries ? `alterar a série ${selectedSeries} para ${series}` : `salvar série ${series}`;

    if (!confirm(`${label}: ${action}, número inicial ${initialNumber}, vinculada ao dispositivo ${state.oauthClient.name}?`)) return;
    send("saveFiscalSeries", {
      accessMode: state.accessMode,
      environment: state.environment,
      database: state.database,
      companyId: state.company.id,
      clientId: state.oauthClient.clientId,
      documentType: type,
      id: effectiveId,
      series,
      initialNumber,
      fiscalEnvironment
    });
  }

  function renderAndroid() {
    const picker = $("android-picker");
    const online = state.androidDevices.filter(x => x.isOnline);
    $("standard-device-config")?.classList.toggle("hidden", isTefMode() || state.multiDevice);
    picker.classList.toggle("single", !state.multiDevice);
    $("android-multi-actions")?.classList.toggle("hidden", !state.multiDevice);
    if ($("prepare-smart")) $("prepare-smart").textContent = state.multiDevice ? "Provisionar selecionados" : "Provisionar";

    if (!online.length) {
      picker.innerHTML = `<div class="empty-state">Nenhum Android online no ADB.</div>`;
      state.androidSerial = "";
      state.androidSerials = [];
      state.androidSelectionInitialized = false;
      updateActions();
      return;
    }

    const onlineSerials = new Set(online.map(x => x.serial));
    state.androidSerials = state.androidSerials.filter(x => onlineSerials.has(x));
    if (!state.multiDevice && state.androidSerials.length > 1) state.androidSerials = [state.androidSerials[0]];
    if (!state.androidSelectionInitialized) {
      state.androidSerials = [online[0].serial];
      state.androidSelectionInitialized = true;
    }
    state.androidSerial = state.androidSerials[0] || "";

    assignProvisioningTargets();
    picker.innerHTML = online.map(d => `
      <div class="android-option ${state.androidSerials.includes(d.serial) ? "selected" : ""}" data-serial="${escapeAttr(d.serial)}">
        <div class="android-radio"></div>
        <div>
          <strong>${escapeHtml(d.friendlyName || "Dispositivo não identificado")}</strong>
          <span>${escapeHtml(d.serial)} · Modelo Android: ${escapeHtml(d.model || "?")} · Android ${escapeHtml(d.androidVersion || "?")} (SDK ${escapeHtml(d.androidSdk || "?")})</span>
          <span>Smart ${escapeHtml(d.smartVersion || "não detectado")} · Package: ${escapeHtml(d.smartPackage || "não detectado")} · ${escapeHtml(d.resolution || "resolução não lida")}</span>
          ${state.multiDevice && state.androidSerials.includes(d.serial) && !isTefMode() && state.oauthClients.length ? `<label class="android-target-label">Cadastro para este Android
            <select class="android-target" data-target-serial="${escapeAttr(d.serial)}">
              <option value="">Selecione...</option>
              ${state.oauthClients.map(x => `<option value="${escapeAttr(x.clientId)}" ${state.androidTargets[d.serial] === x.clientId ? "selected" : ""}>${escapeHtml(x.name)}</option>`).join("")}
            </select></label>` : ""}
        </div>
        <div class="device-meta">
          <b>${d.isAmbiguousIdentity ? "VALIDAR" : "CONECTADO"}</b>
          <span>${d.confirmedSmartDeviceId
            ? `Smart ID ${escapeHtml(d.confirmedSmartDeviceId)}`
            : escapeHtml(d.identificationStatus || "Não identificado")}</span>
        </div>
      </div>
    `).join("");

    picker.querySelectorAll(".android-option").forEach(el => {
      el.addEventListener("click", () => {
        const serial = el.dataset.serial;
        state.androidSerials = state.multiDevice
          ? (state.androidSerials.includes(serial)
              ? state.androidSerials.filter(x => x !== serial)
              : [...state.androidSerials, serial])
          : [serial];
        state.androidSerial = state.androidSerials[0] || "";
        renderAndroid();
        evaluate();
      });
    });
    picker.querySelectorAll(".android-target").forEach(select => {
      select.addEventListener("click", e => e.stopPropagation());
      select.addEventListener("change", e => {
        e.stopPropagation();
        state.androidTargets[select.dataset.targetSerial] = select.value;
        updateActions();
      });
    });

    if ($("android-selected-count")) {
      const count = state.androidSerials.length;
      $("android-selected-count").textContent = `Selecionados: ${count} dispositivo${count === 1 ? "" : "s"}`;
    }

    updateActions();
  }

  function assignProvisioningTargets() {
    const validIds = new Set(state.oauthClients.map(x => x.clientId));
    Object.keys(state.androidTargets).forEach(serial => {
      if (!validIds.has(state.androidTargets[serial])) delete state.androidTargets[serial];
    });
    if (!state.multiDevice) {
      const serial = state.androidSerials[0];
      if (serial && state.oauthClient) state.androidTargets[serial] = state.oauthClient.clientId;
      return;
    }
    // No modo multidispositivo nunca herda nem escolhe automaticamente o cadastro
    // global. Cada relacao ADB -> cadastro deve ser informada explicitamente na lista.
  }

  function provisioningTargetsPayload() {
    return Object.fromEntries(state.androidSerials.map(serial => {
      const clientId = state.androidTargets[serial];
      return [serial, state.oauthClients.find(x => x.clientId === clientId) || null];
    }));
  }

  function provisioningTargetsValid() {
    if (isTefMode()) return true;
    if (!state.multiDevice) return state.androidSerials.length === 1 && !!state.oauthClient;
    assignProvisioningTargets();
    if (state.androidSerials.length < 2) return false;
    const targets = state.androidSerials.map(x => state.androidTargets[x]).filter(Boolean);
    return targets.length === state.androidSerials.length && new Set(targets).size === targets.length;
  }

  function renderProvisioningJobs() {
    const host = $("provisioning-jobs");
    const list = $("provisioning-job-list");
    if (!host || !list) return;
    const jobs = Object.values(state.provisioningJobs);
    host.classList.toggle("hidden", !jobs.length);
    const statusNames = ["Aguardando", "Em execução", "Concluído", "Falhou", "Cancelado"];
    const classNames = ["pending", "running", "success", "failed", "canceled"];
    list.innerHTML = jobs.map(job => {
      const logs = (job.logs || []).slice(-5).map(x => `${x.stage}: ${x.message}`).join("\n");
      return `<div class="provisioning-job ${classNames[job.status] || "pending"}">
        <div><strong>${escapeHtml(job.friendlyName || job.serial)}</strong><span>${escapeHtml(job.serial)} · ${escapeHtml(job.smartPackageName || "package não detectado")}${job.targetDeviceName ? ` · ${escapeHtml(job.targetDeviceName)}` : ""}</span></div>
        <div><strong>${statusNames[job.status] || job.status}</strong><button type="button" class="ghost compact cancel-job" data-serial="${escapeAttr(job.serial)}" ${job.status !== 0 && job.status !== 1 ? "disabled" : ""}>Cancelar</button></div>
        <small>Etapa: ${escapeHtml(job.stage || "Aguardando")} · ${Math.round((job.elapsedMilliseconds || 0) / 1000)}s${job.error ? ` · ${escapeHtml(job.error)}` : ""}</small>
        <div class="job-log">${escapeHtml(logs)}</div>
      </div>`;
    }).join("");
    list.querySelectorAll(".cancel-job").forEach(button => button.addEventListener("click", () => send("cancelProvisioningJob", { serial: button.dataset.serial })));
  }

  function renderValidation(payload) {
    const card = $("validation-card");
    card.className = `validation-card ${payload.status || "neutral"}`;
    const icons = { success: "✓", ready: "→", warning: "!", danger: "×" };
    card.innerHTML = `
      <div class="validation-icon">${icons[payload.status] || "?"}</div>
      <div><strong>${escapeHtml(payload.title || "Validação")}</strong><span>${escapeHtml(payload.detail || "")}</span></div>
    `;
  }

  function renderLogs() {
    const host = $("log-list");
    if (!host) return;
    const items = state.logs || [];
    if (!items.length) {
      host.innerHTML = `<div class="empty-state">Nenhum evento registrado nesta sessão.</div>`;
      return;
    }
    host.innerHTML = items.slice(-500).map(item => {
      const level = String(item.level || "INFO").toUpperCase();
      const rowClass = level === "ERROR" ? "error" : (level === "WARN" ? "warn" : "");
      return `<div class="log-row ${rowClass}">
        <span class="log-time">${escapeHtml(item.time || "")}</span>
        <span class="log-level">${escapeHtml(level)}</span>
        <span class="log-source">${escapeHtml(item.source || "APP")}</span>
        <span class="log-message">${escapeHtml(item.message || "")}</span>
      </div>`;
    }).join("");
    host.scrollTop = host.scrollHeight;
  }

  function isTefMode() {
    return state.module === "smart_tef";
  }

  function moduleRequiresSelfHost() {
    return state.module === "smart_comanda" || state.module === "smart_autopagamento";
  }

  function isSelfHostMode() {
    return !isTefMode() && (moduleRequiresSelfHost() || state.useSelfHost);
  }

  function getSelfHostBaseUrl() {
    return ($("selfhost-base-url")?.value || state.selfHostBaseUrl || "").trim();
  }

  function getTefPayload() {
    const token = $("tef-token").value.trim();
    return {
      tefDeviceName: $("tef-device-name").value.trim(),
      tefCnpj: $("tef-cnpj").value.trim(),
      tefEmpresaId: $("tef-empresa-id").value.trim(),
      tefToken: token,
      useSavedTefToken: !token && state.hasSavedTefToken,
      saveTefConfiguration: $("save-tef-configuration").checked
    };
  }

  function tefFieldsValid() {
    const x = getTefPayload();
    return !!(x.tefDeviceName && x.tefCnpj && x.tefEmpresaId && (x.tefToken || x.useSavedTefToken));
  }

  function renderModuleMode() {
    const tef = isTefMode();
    const selfHostRequired = moduleRequiresSelfHost();
    const selfHost = isSelfHostMode();
    $("standard-device-config").classList.toggle("hidden", tef || state.multiDevice);
    $("link-source-config")?.classList.toggle("hidden", tef);
    const selfHostCheck = $("use-selfhost-check");
    if (selfHostCheck) {
      selfHostCheck.checked = selfHost;
      selfHostCheck.disabled = selfHostRequired;
    }
    if ($("link-source-hint")) {
      $("link-source-hint").textContent = selfHostRequired
        ? "Smart Comanda e Smart Autopagamento exigem vínculo pelo SelfHost."
        : (selfHost
          ? "O dispositivo será vinculado pelo SelfHost. Depois, o módulo pode ser alterado no próprio Smart, inclusive para Comanda."
          : "O dispositivo será vinculado pelo Softcomshop. Se pretende alternar depois para Comanda, escolha SelfHost.");
    }
    $("selfhost-config")?.classList.toggle("hidden", !selfHost);
    $("fiscal-series-panel")?.classList.toggle("hidden", selfHost);
    if (selfHost && state.deviceMode === "new") {
      state.deviceMode = "existing";
      const existing = document.querySelector('input[name="device-mode"][value="existing"]');
      if (existing) existing.checked = true;
    }
    const deviceLabel = $("device-source-label");
    if (deviceLabel) deviceLabel.textContent = selfHost ? "Dispositivo SelfHost" : "Dispositivo Softcomshop";
    if (selfHost && $("selfhost-status")) {
      const base = getSelfHostBaseUrl();
      const sh = state.bootstrap?.selfHost || {};
      const version = sh.installed ? `SelfHost ${sh.version || "?"} · ${sh.generation || ""}` : "SelfHost não localizado";
      const legacy = sh.generation === "SelfHost 4.0";
      const mapping = legacy
        ? "Config2.json · um dispositivo filho diferente por Android"
        : "um dispositivo filho diferente por Android";
      $("selfhost-status").textContent = base ? `${version} · ${mapping} · ${base}` : `${version} · ${mapping} · IP local automático`;
    }
    $("tef-config").classList.toggle("hidden", !tef);
    $("validate-link").classList.toggle("hidden", tef);
    if (tef) {
      $("url-box").classList.add("hidden");
      $("preparation-description").innerHTML = `O <strong>Smart TEF</strong> possui fluxo próprio. O Provisioner percorre Iniciar Configuração → Smart TEF → Avançar, informa o Nome do dispositivo, abre Digitar dados manualmente, preenche CNPJ, Empresa ID e Token e valida a chegada à tela de login.`;
      renderValidation({
        status: state.androidSerial && tefFieldsValid() ? "ready" : "neutral",
        title: state.androidSerial ? "Smart TEF pronto para configuração" : "Selecione um Android",
        detail: state.androidSerial ? "Os dados manuais do Smart TEF serão preenchidos automaticamente." : "Conecte ou selecione o aparelho que receberá a configuração."
      });
    } else {
      $("preparation-description").innerHTML = selfHost
        ? `O <strong>${$("module-select").selectedOptions[0]?.textContent || "Smart"}</strong> será vinculado pelo SelfHost. Assim o mesmo dispositivo poderá trocar de módulo no próprio Smart, inclusive para Comanda, sem refazer o vínculo. O SelfHost deve estar previamente configurado e com o serviço ativo.`
        : (state.accessMode === "online"
          ? `Use <strong>Validar</strong> para conferir o cenário ou <strong>Preparar Smart</strong> para criar/reutilizar o vínculo pela sessão Web, abrir o APK e confirmar o dispositivo no Softcomshop — sem VPN/MySQL.`
          : `Use <strong>Validar</strong> para conferir o cenário ou <strong>Preparar Smart</strong> para executar de fato: abrir o APK, informar a URL pelo ADB e confirmar o vínculo no banco.`);
      if (state.oauthClient && state.androidSerial) evaluate();
      else renderValidation({ status: "neutral", title: selfHost ? "Selecione um dispositivo SelfHost e um Android" : "Selecione um dispositivo WEB e um Android", detail: selfHost ? "No SelfHost 4.1+ a lista usa a sessão Web do Softcomshop; no 4.0 o Provisioner preserva o fluxo legado pela configuração raiz instalada." : (state.accessMode === "online" ? "O vínculo será consultado diretamente na tela de Dispositivos do Softcomshop." : "O programa vai comparar oauth_clients.device_id com o Device ID exibido pelo Smart.") });
    }
    renderDeviceMode();
    renderFiscalSeries();
    updateActions();
  }

  function updateActions() {
    const androidOk = !!state.androidSerial;
    const selectedAndroid = state.androidDevices.find(x => x.serial === state.androidSerial);
    const oauthOk = !!state.oauthClient;
    const companyOk = !!state.company;
    const dbOk = !!state.database;
    const tef = isTefMode();
    const multiSelectionOk = !state.multiDevice || state.androidSerials.length >= 2;
    const targetSelectionOk = state.multiDevice ? provisioningTargetsValid() : oauthOk;

    $("open-scrcpy").disabled = !androidOk;
    // O botao avulso Limpar Smart usa o package detectado. No Smart TEF a limpeza
    // deve ser feita pela preparacao, que resolve a variante correta por dispositivo.
    $("clear-smart").disabled = tef || !androidOk || !selectedAndroid?.smartPackage;
    const existingMode = state.deviceMode === "existing";
    $("validate-link").disabled = tef || !existingMode || !(oauthOk && androidOk && companyOk && dbOk);
    $("prepare-smart").disabled = tef
      ? !(androidOk && multiSelectionOk && tefFieldsValid())
      : !existingMode || !(targetSelectionOk && androidOk && multiSelectionOk && companyOk && dbOk);
    $("open-client-site").disabled = !dbOk;
    if ($("create-device")) {
      $("create-device").disabled = !(companyOk && dbOk && $("new-device-name").value.trim());
    }
  }

  function evaluate() {
    if (isTefMode() || !state.oauthClient || !state.androidSerial) return;
    send("evaluateLink", { oauthClient: state.oauthClient, serial: state.androidSerial });
  }

  function loadCompanies() {
    if (!state.database) return;
    send("loadCompanies", { accessMode: state.accessMode, environment: state.environment, database: state.database });
  }

  function loadOauth() {
    if (!state.database || !state.company) return;
    send("loadOauthClients", {
      accessMode: state.accessMode,
      environment: state.environment,
      database: state.database,
      companyId: state.company.id,
      module: state.module,
      useSelfHost: isSelfHostMode(),
      selfHostBaseUrl: getSelfHostBaseUrl()
    });
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;" }[c]));
  }

  function escapeAttr(value) { return escapeHtml(value); }

  function configureEvents() {
    document.querySelectorAll("#access-mode-switch button").forEach(btn => {
      btn.addEventListener("click", () => {
        const requested = btn.dataset.access;
        const next = ["online", "docker"].includes(requested) ? requested : "online";
        if (next === state.accessMode) return;
        state.accessMode = next;
        state.onlineConnected = false;
        resetDatabaseDependents();
        state.databases = next === "online" ? rememberedOnlineClients() : [];
        if (next === "online") {
          const last = state.bootstrap?.settings?.lastOnlineClient || state.bootstrap?.settings?.lastDatabase || "";
          state.database = last ? normalizeDatabaseName(last) : "";
          $("database-input").value = last ? displayDatabaseName(last) : "";
        } else {
          state.environment = "aws1";
          state.database = "";
          $("database-input").value = "";
        }
        renderAccessMode();
      });
    });

    $("load-databases").addEventListener("click", () => {
      if (state.accessMode === "online") return;
      $("database-error").classList.add("hidden");
      send("loadDatabases", { environment: state.environment, accessMode: state.accessMode });
    });

    $("use-database").addEventListener("click", () => {
      hideDatabaseSuggestions();
      useTypedDatabase();
    });

    $("database-input").addEventListener("focus", () => renderDatabaseSuggestions(true));
    $("database-input").addEventListener("input", () => {
      state.onlineConnected = false;
      state.databaseSuggestionIndex = -1;
      renderDatabaseSuggestions(true);
    });
    $("database-input").addEventListener("keydown", (e) => {
      const items = getFilteredDatabases();
      if (e.key === "ArrowDown" && items.length) {
        e.preventDefault();
        state.databaseSuggestionIndex = Math.min(state.databaseSuggestionIndex + 1, items.length - 1);
        renderDatabaseSuggestions(true);
        return;
      }
      if (e.key === "ArrowUp" && items.length) {
        e.preventDefault();
        state.databaseSuggestionIndex = Math.max(state.databaseSuggestionIndex - 1, 0);
        renderDatabaseSuggestions(true);
        return;
      }
      if (e.key === "Escape") {
        hideDatabaseSuggestions();
        return;
      }
      if (e.key === "Enter") {
        e.preventDefault();
        if (state.databaseSuggestionIndex >= 0 && items[state.databaseSuggestionIndex]) {
          selectDatabaseSuggestion(items[state.databaseSuggestionIndex].raw);
        } else {
          hideDatabaseSuggestions();
          useTypedDatabase();
        }
      }
    });

    document.addEventListener("mousedown", e => {
      if (!$("database-combobox").contains(e.target)) hideDatabaseSuggestions();
      const oauthCombo = $("oauth-combobox");
      if (oauthCombo && !oauthCombo.contains(e.target)) hideOauthSuggestions();
    });

    $("company-select").addEventListener("change", (e) => {
      const raw = e.target.value;
      const id = raw === "" ? null : Number(raw);
      state.company = id == null ? null : (state.companies.find(x => Number(x.id) === id) || null);
      state.oauthClient = null;
      state.oauthClients = [];
      state.fiscalSeries = [];
      renderCompanyPreview();
      renderOauthClients();
      renderOauthPreview();
      renderFiscalSeries();
      if (state.company) {
        setSetupCompact(true);
        loadOauth();
      }
    });

    $("expand-setup")?.addEventListener("click", () => setSetupCompact(false));

    $("multi-device-check")?.addEventListener("change", e => {
      state.multiDevice = !!e.target.checked;
      state.androidTargets = {};
      if (!state.multiDevice && state.androidSerials.length > 1) {
        state.androidSerials = [state.androidSerials[0]];
      }
      if (!state.multiDevice && state.oauthClient && state.androidSerials[0]) {
        state.androidTargets[state.androidSerials[0]] = state.oauthClient.clientId;
      }
      renderModuleMode();
      renderAndroid();
      evaluate();
    });

    $("oauth-input")?.addEventListener("focus", () => renderOauthSuggestions(true));
    $("oauth-input")?.addEventListener("input", () => {
      const input = $("oauth-input");
      if (state.oauthClient && String(input?.value || "").trim() !== String(state.oauthClient.name || "").trim()) {
        state.oauthClient = null;
        state.fiscalSeries = [];
        const select = $("oauth-select");
        if (select) select.value = "";
        renderOauthPreview();
        renderFiscalSeries();
      }
      state.oauthSuggestionIndex = -1;
      renderOauthSuggestions(true);
    });
    $("oauth-input")?.addEventListener("keydown", e => {
      const items = getFilteredOauthClients();
      if (e.key === "ArrowDown" && items.length) {
        e.preventDefault();
        state.oauthSuggestionIndex = Math.min(state.oauthSuggestionIndex + 1, items.length - 1);
        renderOauthSuggestions(true);
        return;
      }
      if (e.key === "ArrowUp" && items.length) {
        e.preventDefault();
        state.oauthSuggestionIndex = Math.max(state.oauthSuggestionIndex - 1, 0);
        renderOauthSuggestions(true);
        return;
      }
      if (e.key === "Escape") {
        hideOauthSuggestions();
        return;
      }
      if (e.key === "Enter") {
        e.preventDefault();
        let selected = state.oauthSuggestionIndex >= 0 ? items[state.oauthSuggestionIndex] : null;
        if (!selected) {
          const query = String($("oauth-input")?.value || "").trim().toLowerCase();
          selected = items.find(x => String(x.item.name || "").trim().toLowerCase() === query) || (items.length === 1 ? items[0] : null);
        }
        if (selected) selectOauthSuggestion(selected.idx);
      }
    });

    $("oauth-select").addEventListener("change", (e) => {
      const raw = e.target.value;
      const idx = raw === "" ? null : Number(raw);
      state.oauthClient = idx != null && Number.isInteger(idx) && state.oauthClients[idx] ? state.oauthClients[idx] : null;
      state.fiscalSeries = [];
      clearInlineError("series-error");
      if ($("oauth-input")) $("oauth-input").value = state.oauthClient?.name || "";
      if (state.oauthClient && state.androidSerials[0]) state.androidTargets[state.androidSerials[0]] = state.oauthClient.clientId;
      renderOauthPreview();
      renderAndroid();
      loadFiscalSeries();
    });

    document.querySelectorAll('input[name="device-mode"]').forEach(input => {
      input.addEventListener("change", () => {
        state.deviceMode = input.value;
        renderDeviceMode();
        renderFiscalSeries();
      });
    });

    $("new-device-name").addEventListener("input", updateActions);
    $("create-device").addEventListener("click", () => {
      const name = $("new-device-name").value.trim();
      if (!state.database || !state.company || !name) {
        toast("Selecione cliente e empresa e informe o nome do dispositivo.", "error");
        return;
      }
      if (!confirm(`Criar o dispositivo ${name} para ${state.company.name}?`)) return;
      send("createOauthClient", {
        accessMode: state.accessMode,
        environment: state.environment,
        database: state.database,
        companyId: state.company.id,
        name,
        module: state.module,
        useSelfHost: isSelfHostMode()
      });
    });

    $("selfhost-new-device")?.addEventListener("click", openSelfHostCreateModal);
    $("selfhost-create-close")?.addEventListener("click", closeSelfHostCreateModal);
    $("selfhost-create-cancel")?.addEventListener("click", closeSelfHostCreateModal);
    $("selfhost-create-confirm")?.addEventListener("click", createSelfHostDevice);
    ["selfhost-create-name", "selfhost-create-series", "selfhost-create-number", "selfhost-create-nfe-series", "selfhost-create-nfe-number"].forEach(id => {
      $(id)?.addEventListener("input", () => clearInlineError("selfhost-create-error"));
      $(id)?.addEventListener("keydown", e => {
        if (e.key === "Enter") { e.preventDefault(); createSelfHostDevice(); }
        if (e.key === "Escape") closeSelfHostCreateModal();
      });
    });
    $("selfhost-create-modal")?.addEventListener("mousedown", e => {
      if (e.target === $("selfhost-create-modal")) closeSelfHostCreateModal();
    });
    $("provision-confirm-close")?.addEventListener("click", closeProvisionConfirmation);
    $("provision-confirm-cancel")?.addEventListener("click", closeProvisionConfirmation);
    $("provision-confirm-submit")?.addEventListener("click", acceptProvisionConfirmation);
    $("provision-confirm-modal")?.addEventListener("mousedown", e => {
      if (e.target === $("provision-confirm-modal")) closeProvisionConfirmation();
    });
    $("provision-confirm-modal")?.addEventListener("keydown", e => {
      if (e.key === "Escape") closeProvisionConfirmation();
      if (e.key === "Enter" && e.target?.id !== "provision-confirm-cancel") {
        e.preventDefault();
        acceptProvisionConfirmation();
      }
    });

    $("toggle-series").addEventListener("click", () => {
      state.seriesExpanded = !state.seriesExpanded;
      updateSeriesAccordion();
    });
    $("reload-series").addEventListener("click", e => {
      e.stopPropagation();
      loadFiscalSeries();
    });
    $("nfce-record").addEventListener("change", () => { clearInlineError("series-error"); applySeriesRecord("nfce"); });
    $("nfe-record").addEventListener("change", () => { clearInlineError("series-error"); applySeriesRecord("nfe"); });
    ["nfce-series", "nfce-number", "nfce-environment", "nfe-series", "nfe-number", "nfe-environment"].forEach(id => {
      $(id)?.addEventListener("input", () => clearInlineError("series-error"));
      $(id)?.addEventListener("change", () => clearInlineError("series-error"));
    });
    $("save-nfce-series").addEventListener("click", () => saveFiscalSeries("nfce"));
    $("save-nfe-series").addEventListener("click", () => saveFiscalSeries("nfe"));

    $("use-selfhost-check")?.addEventListener("change", e => {
      if (isTefMode() || moduleRequiresSelfHost()) return;
      state.useSelfHost = !!e.target.checked;
      state.oauthClient = null;
      state.oauthClients = [];
      state.fiscalSeries = [];
      renderModuleMode();
      renderOauthClients();
      renderOauthPreview();
      if (state.company) loadOauth();
    });

    $("module-select").addEventListener("change", e => {
      const wasSelfHost = isSelfHostMode();
      state.module = e.target.value;
      const remainsSelfHost = wasSelfHost && isSelfHostMode();

      // Trocar apenas o modulo nao altera a origem dos dispositivos quando o SelfHost
      // continua ativo. Preservamos a lista/selecionado e evitamos nova consulta remota.
      if (remainsSelfHost) {
        renderModuleMode();
        renderOauthClients();
        renderAndroid();
        renderOauthPreview();
        renderFiscalSeries();
        return;
      }

      state.oauthClient = null;
      state.oauthClients = [];
      state.fiscalSeries = [];
      renderModuleMode();
      renderOauthClients();
      renderOauthPreview();
      renderFiscalSeries();
      if (state.company) loadOauth();
    });

    $("selfhost-base-url")?.addEventListener("input", e => {
      state.selfHostBaseUrl = e.target.value.trim();
      if (isSelfHostMode() && $("selfhost-status")) {
        $("selfhost-status").textContent = state.selfHostBaseUrl ? `Selfhost: ${state.selfHostBaseUrl}` : "Selfhost: IP local será detectado automaticamente.";
      }
    });

    ["tef-device-name", "tef-cnpj", "tef-empresa-id", "tef-token"].forEach(id => {
      $(id).addEventListener("input", () => {
        updateActions();
        if (isTefMode()) {
          renderValidation({
            status: state.androidSerial && tefFieldsValid() ? "ready" : "neutral",
            title: state.androidSerial ? "Smart TEF pronto para configuração" : "Selecione um Android",
            detail: tefFieldsValid() ? "Os dados manuais do Smart TEF serão preenchidos automaticamente." : "Preencha Nome do dispositivo, CNPJ, Empresa ID e Token."
          });
        }
      });
    });
    $("save-tef-settings")?.addEventListener("click", () => {
      const tef = getTefPayload();
      if (!tef.tefDeviceName || !tef.tefCnpj || !tef.tefEmpresaId || (!tef.tefToken && !tef.useSavedTefToken)) {
        toast("Preencha Nome do dispositivo, CNPJ, Empresa ID e Token antes de salvar.", "error");
        return;
      }
      send("saveSmartTefSettings", tef);
    });

    $("refresh-android").addEventListener("click", () => send("refreshAndroid"));
    $("select-all-android")?.addEventListener("click", () => {
      state.androidSerials = state.androidDevices.filter(x => x.isOnline).map(x => x.serial);
      state.androidSerial = state.androidSerials[0] || "";
      renderAndroid();
    });
    $("clear-android-selection")?.addEventListener("click", () => {
      state.androidSerials = [];
      state.androidSerial = "";
      renderAndroid();
    });
    $("cancel-all-provisioning")?.addEventListener("click", () => send("cancelAllProvisioning"));
    $("open-scrcpy").addEventListener("click", () => send("openScrcpy", { serial: state.androidSerial }));
    $("clear-smart").addEventListener("click", () => {
      if (confirm("Limpar os dados locais do Smart neste Android?")) {
        send("clearSmartData", { serial: state.androidSerial });
      }
    });

    $("connect-vpn").addEventListener("click", () => send("connectVpn", { environment: state.environment }));
    $("connect-docker").addEventListener("click", () => send("connectDocker", { environment: state.environment }));
    $("open-client-site").addEventListener("click", () => send("openClientSite", { database: state.database }));

    $("validate-link").addEventListener("click", () => {
      if (!state.database || !state.company || !state.oauthClient || !state.androidSerial) {
        toast(`Selecione cliente, empresa, dispositivo ${isSelfHostMode() ? "SelfHost" : "Softcomshop"} e Android.`, "error");
        return;
      }

      const card = $("validation-card");
      card.className = "validation-card ready";
      card.innerHTML = `<div class="validation-icon">…</div><div><strong>Validando preparação</strong><span>Conferindo vínculo e montando a URL do dispositivo.</span></div>`;
      setBusy("validation", true);
      send("validatePreparation", {
        accessMode: state.accessMode,
        environment: state.environment,
        database: state.database,
        company: state.company,
        oauthClient: state.oauthClient,
        serial: state.androidSerial,
        module: state.module,
        useSelfHost: isSelfHostMode(),
        selfHostBaseUrl: getSelfHostBaseUrl()
      });
    });
    $("prepare-smart").addEventListener("click", () => {
      state.module = $("module-select").value || "smart_pdv";
      const clearData = $("clear-before-link").checked;

      if (isTefMode()) {
        if (!state.androidSerial || (state.multiDevice && state.androidSerials.length < 2) || !tefFieldsValid()) {
          toast(state.multiDevice
            ? "Selecione ao menos dois Androids e confira os dados do Smart TEF."
            : "Selecione um Android e confira os dados do Smart TEF.", "error");
          return;
        }

        const tef = getTefPayload();
        openProvisionConfirmation({
          title: "Configurar Smart TEF",
          description: `O Smart TEF será configurado como ${tef.tefDeviceName}. A VPN será desligada antes do preenchimento das chaves.`,
          moduleLabel: "Smart TEF",
          accessLabel: "Configuração direta no Android",
          clearData,
          onConfirm: () => {
            renderValidation({ status: "ready", title: "Configurando Smart TEF", detail: "Percorrendo o fluxo inicial do package detectado e preenchendo os dados manuais." });
            setBusy("provision", true);
            send("prepareSmart", {
              accessMode: state.accessMode,
              environment: state.environment,
              serials: state.androidSerials,
              module: state.module,
              clearData,
              ...tef
            });
          }
        });
        return;
      }

      if (!state.database || !state.company || (!state.multiDevice && !state.oauthClient) || !state.androidSerials.length) {
        toast(`Selecione cliente, empresa, dispositivo ${isSelfHostMode() ? "SelfHost" : "Softcomshop"} e Android.`, "error");
        return;
      }
      if (state.multiDevice && state.androidSerials.length < 2) {
        toast("No modo multidispositivo, selecione ao menos dois Androids.", "error");
        return;
      }
      if (!provisioningTargetsValid()) {
        toast("Selecione um cadastro diferente para cada Android escolhido.", "error");
        return;
      }

      const moduleLabel = $("module-select").selectedOptions[0]?.textContent || "Smart";
      const modeInfo = isSelfHostMode()
        ? "usando o /device/add do SelfHost"
        : state.accessMode === "online"
          ? "usando a sessão WEB do Softcomshop"
          : state.accessMode === "docker"
            ? "usando o banco pelo Docker isolado, sem VPN no Windows"
            : "usando o banco pelo Docker isolado";
      openProvisionConfirmation({
        title: state.multiDevice ? "Confirmar provisionamento em lote" : "Confirmar provisionamento",
        description: clearData
          ? `O Provisioner verificará e removerá vínculos anteriores ${modeInfo} antes de criar o novo vínculo.`
          : `O Provisioner verificará e removerá vínculos anteriores ${modeInfo}. O Smart será reiniciado sem apagar sua configuração local.`,
        moduleLabel,
        accessLabel: isSelfHostMode() ? "SelfHost" : state.accessMode === "online" ? "Softcomshop Web" : "Docker local",
        clearData,
        onConfirm: () => {
          renderValidation({ status: "ready", title: "Iniciando preparação", detail: "Validando o cenário e abrindo o Smart nos Androids selecionados." });
          setBusy("provision", true);
          send("prepareSmart", {
            accessMode: state.accessMode,
            environment: state.environment,
            database: state.database,
            company: state.company,
            oauthClient: state.oauthClient,
            oauthClientsBySerial: provisioningTargetsPayload(),
            serials: state.androidSerials,
            module: state.module,
            useSelfHost: isSelfHostMode(),
            selfHostBaseUrl: getSelfHostBaseUrl(),
            clearData
          });
        }
      });
    });

    $("copy-url").addEventListener("click", () => send("copyText", { text: state.generatedUrl }));

    $("save-database-credentials").addEventListener("click", () => {
      const username = $("database-username").value.trim();
      const password = $("database-password").value;
      if (!username && !password) {
        toast("Informe usuário e senha para substituir o acesso padrão.", "info");
        return;
      }
      if (!username || !password) {
        toast("Informe usuário e senha do banco.", "error");
        return;
      }
      send("saveDatabaseCredentials", { username, password });
    });

    $("import-secrets").addEventListener("click", () => send("importLegacySecrets"));
    $("choose-vpn").addEventListener("click", () => send("chooseVpnProfile"));
    $("open-log-folder").addEventListener("click", () => send("openLogFolder"));
    $("clear-logs").addEventListener("click", () => send("clearLogs"));

    $("save-update-settings")?.addEventListener("click", () => {
      send("saveUpdateSettings", {
        channel: $("update-channel").value,
        autoCheck: $("update-auto-check").checked,
        autoInstall: $("update-auto-install").checked,
        stableManifestUrl: $("update-stable-url").value.trim(),
        betaManifestUrl: $("update-beta-url").value.trim()
      });
    });
    $("check-update")?.addEventListener("click", () => send("checkForUpdates"));
    $("install-update")?.addEventListener("click", () => send("installUpdate"));
    $("update-available-button")?.addEventListener("click", () => {
      const btn = document.querySelector('.nav-item[data-page="settings"]');
      if (btn) btn.click();
      $("update-result")?.scrollIntoView({ behavior: "smooth", block: "center" });
    });

    $("save-package").addEventListener("click", () => send("saveSmartPackage", { packageName: $("smart-package").value }));
    $("detect-package").addEventListener("click", () => {
      if (!state.androidSerial) {
        toast("Selecione um Android na tela Provisionar.", "error");
        return;
      }
      send("detectSmartPackages", { serial: state.androidSerial });
    });
  }

  window.chrome?.webview?.addEventListener("message", (event) => {
    const { type, payload } = event.data || {};
    switch (type) {
      case "bootstrap":
        state.bootstrap = payload;
        renderBootstrap();
        updateActions();
        break;
      case "busy":
        setBusy(payload.key, payload.active);
        break;
      case "databases":
        state.databases = payload.items || [];
        renderDatabases();
        renderDatabaseSuggestions(true);
        toast(`${state.databases.length} cliente(s) localizado(s). Digite para filtrar.`, "success");
        break;
      case "databaseError": {
        const el = $("database-error");
        el.classList.remove("hidden");
        const accessHint = payload.reachable ? "" : (payload.accessMode === "docker"
          ? "<br><strong>Não foi possível acessar o banco pelo Docker isolado. Consulte os logs do DB Bridge.</strong>"
          : "<br><strong>O host do banco não está acessível. Verifique a conexão VPN.</strong>");
        el.innerHTML = `${escapeHtml(payload.message)}${accessHint}`;
        break;
      }
      case "onlineClients": {
        const items = Array.isArray(payload.items) ? payload.items : [];
        const recent = items.map(normalizeDatabaseName).filter(Boolean);
        const merged = [...recent, ...state.databases].map(normalizeDatabaseName).filter(Boolean);
        state.databases = [...new Map(merged.map(x => [x.toLowerCase(), x])).values()].slice(0, 30);
        if (state.bootstrap?.settings) {
          state.bootstrap.settings.recentOnlineClients = recent;
          if (payload.current) state.bootstrap.settings.lastOnlineClient = normalizeDatabaseName(payload.current);
        }
        renderSavedOnlineClients();
        if (state.accessMode === "online" && document.activeElement === $("database-input")) {
          renderDatabaseSuggestions(true);
        }
        break;
      }
      case "onlineClientRemoved": {
        const items = Array.isArray(payload.items) ? payload.items : [];
        const recent = items.map(normalizeDatabaseName).filter(Boolean);
        const removed = normalizeDatabaseName(payload.removed || "");
        if (state.bootstrap?.settings) {
          state.bootstrap.settings.recentOnlineClients = recent;
          state.bootstrap.settings.lastOnlineClient = payload.current ? normalizeDatabaseName(payload.current) : "";
          if (removed && normalizeDatabaseName(state.bootstrap.settings.lastDatabase || "").toLowerCase() === removed.toLowerCase()) {
            state.bootstrap.settings.lastDatabase = "";
          }
        }
        state.databases = recent;
        if (removed && state.database && normalizeDatabaseName(state.database).toLowerCase() === removed.toLowerCase()) {
          state.database = "";
          state.onlineConnected = false;
          $("database-input").value = "";
          resetDatabaseDependents();
          renderAccessMode();
        }
        renderSavedOnlineClients();
        renderDatabaseSuggestions(document.activeElement === $("database-input"));
        toast(`${payload.removed || "Cliente"} removido da lista local.`, "success");
        break;
      }
      case "onlineState":
        state.onlineConnected = !!payload.connected;
        renderAccessMode();
        if (payload.message) toast(payload.message, payload.connected ? "success" : "info");
        break;
      case "companies":
        state.companies = payload.items || [];
        if (payload.accessMode === "online") state.onlineConnected = true;
        renderAccessMode();
        renderCompanies();
        if (state.companies.length === 1) {
          $("company-select").value = String(state.companies[0].id);
          state.company = state.companies[0];
          renderCompanyPreview();
          setSetupCompact(true);
          loadOauth();
        } else {
          setSetupCompact(false);
        }
        break;
      case "databaseResolved":
        state.environment = payload.environment || "aws1";
        state.database = normalizeDatabaseName(payload.database || state.database);
        $("database-input").value = displayDatabaseName(state.database);
        toast(`${displayDatabaseName(state.database)} localizado em ${String(state.environment).toUpperCase()}.`, "success");
        loadCompanies();
        break;
      case "oauthClients": {
        const currentClientId = state.oauthClient?.clientId || "";
        state.oauthClients = payload.items || [];
        if (currentClientId) {
          const idx = state.oauthClients.findIndex(x => x.clientId === currentClientId);
          state.oauthClient = idx >= 0 ? state.oauthClients[idx] : null;
        }
        renderOauthClients();
        renderAndroid();
        if (state.oauthClient) {
          // A lista atualizada ao final do provisionamento deve refletir o novo
          // device_id sem iniciar outra avaliacao que sobrescreva o resultado final.
          renderOauthPreview(!payload.preservePreparationResult);
          loadFiscalSeries();
        }
        toast(`${state.oauthClients.length} dispositivo(s) ${isSelfHostMode() ? "SelfHost" : "ativo(s)"} localizado(s).`, "success");
        break;
      }
      case "oauthClientCreated": {
        state.oauthClients = payload.items || [];
        const createdId = payload.item?.clientId || "";
        const idx = state.oauthClients.findIndex(x => x.clientId === createdId);
        const createdName = String(payload.item?.name || "").toLowerCase();
        const nameIdx = idx >= 0 ? idx : state.oauthClients.findIndex(x => String(x.name || "").toLowerCase() === createdName);
        state.oauthClient = nameIdx >= 0 ? state.oauthClients[nameIdx] : null;
        renderOauthClients();
        renderAndroid();
        state.deviceMode = "existing";
        const existingRadio = document.querySelector('input[name="device-mode"][value="existing"]');
        if (existingRadio) existingRadio.checked = true;
        $("new-device-name").value = "";
        clearInlineError("selfhost-create-error");
        closeSelfHostCreateModal();
        state.fiscalSeries = [];
        renderDeviceMode();
        renderOauthPreview();
        loadFiscalSeries();
        toast(
          nameIdx >= 0
            ? `Dispositivo ${payload.item?.name || ""} criado e selecionado.`
            : `Dispositivo ${payload.item?.name || ""} criado; ele ainda não apareceu na listagem atualizada.`,
          "success");
        break;
      }
      case "fiscalSeries":
        if (!state.oauthClient || payload.clientId === state.oauthClient.clientId) {
          state.fiscalSeries = payload.items || [];
          clearInlineError("series-error");
          renderFiscalSeries();
        }
        break;
      case "fiscalSeriesSaved": {
        state.fiscalSeries = payload.items || [];
        clearInlineError("series-error");
        renderFiscalSeries();
        const type = payload.item?.documentType === "NFCe" ? "nfce" : "nfe";
        if (payload.item?.id && $(`${type}-record`)) {
          $(`${type}-record`).value = String(payload.item.id);
          applySeriesRecord(type);
        }
        toast(`Série ${payload.item?.documentType || "fiscal"} salva com sucesso.`, "success");
        break;
      }
      case "fiscalSeriesError":
        if (!state.oauthClient || !payload.clientId || payload.clientId === state.oauthClient.clientId) {
          state.seriesExpanded = true;
          renderFiscalSeries();
          showInlineError("series-error", payload.message || "Não foi possível salvar a série.");
        }
        break;
      case "selfHostCreateError":
        showInlineError("selfhost-create-error", payload.message || "Não foi possível criar o dispositivo SelfHost.");
        break;
      case "androidDevices":
        state.androidDevices = payload.items || [];
        renderAndroid();
        break;
      case "generatedUrl":
        state.generatedUrl = payload.url || "";
        $("generated-url").value = state.generatedUrl;
        $("url-box").classList.toggle("hidden", !state.generatedUrl);
        break;
      case "linkEvaluation":
        renderValidation(payload);
        break;
      case "preparationValidated":
        setBusy("validation", false);
        renderValidation(payload.evaluation || {});
        state.generatedUrl = payload.url || "";
        $("generated-url").value = state.generatedUrl;
        $("url-box").classList.toggle("hidden", !state.generatedUrl);
        toast(`Preparação validada: ${payload.database || "cliente"} / ${payload.company || "empresa"}.`, "success");
        break;
      case "provisionProgress":
        renderValidation({
          status: "ready",
          title: payload.friendlyName ? `Preparando ${payload.friendlyName}` : "Preparando Smart",
          detail: payload.message || "Executando etapa do provisionamento..."
        });
        break;
      case "smartTefSettingsSaved":
        state.hasSavedTefToken = !!payload.hasSavedToken;
        $("tef-token").value = "";
        $("tef-token").placeholder = state.hasSavedTefToken
          ? "Token salvo com proteção local"
          : "Token do Smart TEF";
        $("save-tef-configuration").checked = true;
        updateActions();
        break;
      case "smartPreparationFinished": {
        const tef = payload.module === "smart_tef";
        renderValidation({
          status: payload.success ? "success" : "danger",
          title: payload.success ? (tef ? "Smart TEF configurado" : "Smart vinculado") : (tef ? "Erro ao configurar Smart TEF" : "Erro ao vincular dispositivo"),
          detail: payload.message || "Processo finalizado."
        });
        if (!tef && payload.url) {
          state.generatedUrl = payload.url;
          $("generated-url").value = state.generatedUrl;
          $("url-box").classList.remove("hidden");
        }
        if (payload.success) {
          const successText = tef
            ? "Smart TEF configurado. Validação concluída e botão Concluir acionado."
            : (payload.accessMode === "online"
              ? "Vínculo confirmado pelo Softcomshop. Smart preparado para a próxima etapa."
              : (payload.accessMode === "selfhost"
                ? "Vínculo confirmado pela API administrativa do SelfHost."
                : "Vínculo confirmado no banco. Smart preparado para a próxima etapa."));
          toast(successText, "success");
        } else {
          const extra = payload.uiSummary ? ` Tela detectada: ${payload.uiSummary}` : "";
          toast((payload.message || (tef ? "Nao foi possivel concluir a configuracao do Smart TEF." : "Nao foi possivel confirmar o vinculo.")) + extra, "error");
        }
        break;
      }
      case "provisioningStarted":
        state.provisioningJobs = {};
        renderProvisioningJobs();
        renderValidation({ status: "ready", title: `Provisionando ${payload.count} dispositivo(s)`, detail: `Até ${payload.maxParallelism} jobs simultâneos, isolados por UDID.` });
        break;
      case "provisionJob":
        state.provisioningJobs[payload.serial] = payload;
        renderProvisioningJobs();
        break;
      case "provisioningFinished":
        setBusy("provision", false);
        if ($("provisioning-summary")) {
          $("provisioning-summary").textContent = `Provisionamento concluído · Sucesso: ${payload.success} · Falha: ${payload.failed} · Cancelado: ${payload.canceled}`;
        }
        renderValidation({
          status: payload.failed ? "warning" : "success",
          title: "Provisionamento concluído",
          detail: `Sucesso: ${payload.success} · Falha: ${payload.failed} · Cancelado: ${payload.canceled}`
        });
        break;
      case "vpnConnected":
        toast(payload.message, "success");
        send("loadDatabases", { environment: state.environment, accessMode: state.accessMode });
        break;
      case "vpnState":
        toast(payload.message || (payload.connected ? "VPN conectada." : "VPN desconectada."), payload.connected ? "success" : "info");
        break;
      case "dockerState":
        toast(payload.message || (payload.connected ? "Docker isolado conectado." : "Preparando Docker..."), payload.connected ? "success" : "info");
        if (payload.connected) send("loadDatabases", { environment: state.environment, accessMode: "docker" });
        break;
      case "smartPackages": {
        const host = $("package-results");
        const items = payload.items || [];
        const foreground = payload.foregroundPackage || "";
        host.classList.toggle("hidden", !items.length);
        host.innerHTML = items.length
          ? items.map(x => `<div class="package-item${x === foreground ? " package-current" : ""}" data-package="${escapeAttr(x)}">${escapeHtml(x)}${x === foreground ? " <small>aberto agora</small>" : ""}</div>`).join("")
          : `<div class="empty-state">Nenhum package contendo "softcom" ou "smart" foi localizado.</div>`;

        if (foreground && items.includes(foreground)) {
          $("smart-package").value = foreground;
          toast(`Smart identificado automaticamente: ${foreground}`, "success");
        }

        host.querySelectorAll(".package-item").forEach(el => {
          el.addEventListener("click", () => {
            $("smart-package").value = el.dataset.package;
            send("saveSmartPackage", { packageName: el.dataset.package });
          });
        });
        break;
      }
      case "logEntry":
        state.logs.push(payload);
        if (state.logs.length > 600) state.logs.splice(0, state.logs.length - 600);
        renderLogs();
        break;
      case "logsCleared":
        state.logs = [];
        renderLogs();
        toast("Logs da tela limpos.", "info");
        break;
      case "credentialsSaved":
        $("database-username").value = "";
        $("database-password").value = "";
        toast(payload.message || "Acesso do banco salvo.", "success");
        break;
      case "updateSettingsSaved":
        toast(payload.message || "Configuração de atualizações salva.", "success");
        send("checkForUpdates");
        break;
      case "updateStatus": {
        state.updateAvailable = !!payload.available;
        state.updateRequired = !!payload.required;
        state.latestVersion = payload.latestVersion || "";
        const result = $("update-result");
        if (result) {
          const notes = payload.notes ? `<small>${escapeHtml(payload.notes)}</small>` : "";
          result.innerHTML = `<strong>${escapeHtml(payload.message || "Atualização")}</strong>${notes}`;
          result.className = `update-result ${payload.error ? "error" : (payload.available ? "available" : "")}`;
        }
        const badge = $("update-status-badge");
        if (badge) {
          badge.textContent = payload.available ? `v${payload.latestVersion}` : (payload.configured ? "Atualizado" : "Não configurado");
          badge.className = payload.available ? "warn" : (payload.configured ? "ok" : "warn");
        }
        $("install-update")?.classList.toggle("hidden", !payload.available);
        $("update-available-button")?.classList.toggle("hidden", !payload.available);
        if (payload.available) {
          $("update-available-button").textContent = `Atualizar para v${payload.latestVersion}`;
          if (!payload.userInitiated) toast(`Nova versão ${payload.latestVersion} disponível.`, "info");
        } else if (payload.userInitiated && !payload.error) {
          toast(payload.message || "Nenhuma atualização disponível.", "success");
        }
        break;
      }
      case "updateInstallProgress":
        if ($("update-result")) $("update-result").textContent = payload.message || "Preparando atualização...";
        break;
      case "toast":
        toast(payload.message, payload.type);
        break;
    }
  });

  configureNavigation();
  configureTheme();
  configureEvents();
  renderDeviceMode();
  send("appReady");
})();
