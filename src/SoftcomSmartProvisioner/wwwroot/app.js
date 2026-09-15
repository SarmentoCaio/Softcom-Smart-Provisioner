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
    busy: new Set()
  };

  const $ = (id) => document.getElementById(id);
  const send = (action, payload = {}) => window.chrome?.webview?.postMessage({ action, payload });
  const databasePrefix = "softcoms_softcomshop_";

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
      if (key === "provision") el.textContent = active ? "Preparando..." : "Preparar Smart";
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
          android: ["Dispositivos Android", "ADB, Android ID e scrcpy em uma única tela."],
          logs: ["Logs", "Acompanhe VPN, banco, ADB e preparação sem janelas de console."],
          settings: ["Configurações", "Acessos internos, VPN e parâmetros locais do Smart."]
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
    $("phase-badge").textContent = b.app.phase;
    state.environment = b.settings.lastEnvironment || "aws1";
    state.accessMode = ["online", "docker", "database"].includes(b.settings.accessMode) ? b.settings.accessMode : "online";
    if (state.accessMode === "online") {
      state.databases = rememberedOnlineClients();
      const lastOnline = b.settings.lastOnlineClient || b.settings.lastDatabase || "";
      if (lastOnline) {
        state.database = normalizeDatabaseName(lastOnline);
        $("database-input").value = displayDatabaseName(lastOnline);
      }
    } else if (state.environment === "aws2") {
      state.database = normalizeDatabaseName("jormungandr");
      $("database-input").value = "jormungandr";
    }
    $("smart-package").value = b.settings.smartPackageName || "";
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

    document.querySelectorAll("#environment-switch button").forEach(btn => {
      btn.classList.toggle("active", btn.dataset.env === state.environment);
    });

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
    $("global-substatus").textContent = state.accessMode === "online" ? "Modo Online" : (state.accessMode === "docker" ? "Banco via Docker isolado" : (dbOk ? "Banco via VPN local" : "Importe as credenciais"));
    renderAccessMode();
    renderSavedOnlineClients();
  }

  function renderAccessMode() {
    const online = state.accessMode === "online";
    const docker = state.accessMode === "docker";
    const database = state.accessMode === "database";
    document.querySelectorAll("#access-mode-switch button").forEach(btn => {
      btn.classList.toggle("active", btn.dataset.access === state.accessMode);
    });
    $("database-access-options").classList.toggle("hidden", online);
    $("load-databases").classList.toggle("hidden", online);
    $("connect-vpn").classList.toggle("hidden", !database);
    $("connect-docker").classList.toggle("hidden", !docker);
    $("use-database").textContent = online ? "Conectar" : "Usar";
    $("db-credential-status").textContent = online
      ? (state.onlineConnected ? "Online conectado" : "Online")
      : docker
        ? (state.bootstrap?.capabilities?.dockerAvailable ? "Docker disponível" : "Docker ausente")
        : (state.bootstrap?.capabilities?.databaseCredentials ? "Credenciais OK" : "Sem credenciais");
    $("db-credential-status").className = `mini-status ${online ? (state.onlineConnected ? "ok" : "") : (docker ? (state.bootstrap?.capabilities?.dockerAvailable ? "ok" : "warn") : (state.bootstrap?.capabilities?.databaseCredentials ? "ok" : "warn"))}`;
    $("client-helper").textContent = online
      ? "Informe o cliente. O acesso ocorre pela sessão WEB do Softcomshop."
      : docker
        ? "O banco é acessado por 127.0.0.1:13306; a VPN fica somente dentro do Docker."
        : "Acesso direto ao banco usando a VPN instalada no Windows.";
    $("access-mode-helper").textContent = online
      ? "Sem VPN e sem banco: usa os endpoints do Softcomshop."
      : docker
        ? "Recomendado para banco direto: o Windows e o Android permanecem fora da VPN."
        : "Modo legado: conecta o OpenVPN diretamente no Windows quando necessário.";
    $("open-client-site").disabled = !state.database;
    $("global-substatus").textContent = online
      ? (state.onlineConnected ? "Softcomshop Online conectado" : "Modo Online")
      : docker
        ? (state.bootstrap?.capabilities?.dockerAvailable ? "Banco via Docker isolado" : "Docker Desktop não localizado")
        : (state.bootstrap?.capabilities?.databaseCredentials ? "Banco via VPN local" : "Banco direto requer credenciais");
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
    updateActions();
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
      else loadCompanies();
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
    hideOauthSuggestions();
    renderOauthPreview();
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
    input.value = "";
    $("selfhost-create-series").value = "";
    $("selfhost-create-number").value = "1";
    $("selfhost-create-nfe-series").value = "";
    $("selfhost-create-nfe-number").value = "1";
    clearInlineError("selfhost-create-error");
    modal.classList.remove("hidden");
    setTimeout(() => input.focus(), 0);
  }

  function closeSelfHostCreateModal() {
    $("selfhost-create-modal")?.classList.add("hidden");
  }

  function createSelfHostDevice() {
    clearInlineError("selfhost-create-error");
    const name = $("selfhost-create-name")?.value.trim() || "";
    const seriesText = $("selfhost-create-series")?.value.trim() || "";
    const numberText = $("selfhost-create-number")?.value.trim() || "";
    const nfeSeriesText = $("selfhost-create-nfe-series")?.value.trim() || "";
    const nfeNumberText = $("selfhost-create-nfe-number")?.value.trim() || "";
    const series = Number(seriesText);
    const initialNumber = Number(numberText);
    const nfeSeries = Number(nfeSeriesText);
    const nfeInitialNumber = Number(nfeNumberText);
    const modernSelfHost = state.bootstrap?.selfHost?.generation === "SelfHost 4.1+";

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
    if (!modernSelfHost && nfeSeriesText && (!/^\d+$/.test(nfeSeriesText) || !Number.isInteger(nfeSeries) || nfeSeries < 0)) {
      showInlineError("selfhost-create-error", "Informe uma série NF-e válida ou deixe a NF-e em branco.");
      return;
    }
    if (!modernSelfHost && nfeSeriesText && (!/^\d+$/.test(nfeNumberText) || !Number.isInteger(nfeInitialNumber) || nfeInitialNumber < 1)) {
      showInlineError("selfhost-create-error", "Informe uma numeração inicial NF-e válida.");
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
    const grid = $("android-grid");
    const online = state.androidDevices.filter(x => x.isOnline);

    if (!online.length) {
      picker.innerHTML = `<div class="empty-state">Nenhum Android online no ADB.</div>`;
      grid.innerHTML = `<div class="empty-state">Conecte um emulador ou aparelho e clique em Atualizar.</div>`;
      state.androidSerial = "";
      updateActions();
      return;
    }

    if (!online.some(x => x.serial === state.androidSerial)) {
      state.androidSerial = online[0].serial;
    }

    picker.innerHTML = online.map(d => `
      <div class="android-option ${d.serial === state.androidSerial ? "selected" : ""}" data-serial="${escapeAttr(d.serial)}">
        <div class="android-radio"></div>
        <div>
          <strong>${escapeHtml(d.model || d.serial)}</strong>
          <span>${escapeHtml(d.transport)} · Android ${escapeHtml(d.androidVersion || "?")} · Smart ${escapeHtml(d.smartVersion || "não detectado")} · ${escapeHtml(d.serial)}</span>
        </div>
        <div class="device-meta">
          <b>ONLINE</b>
          <span>${d.confirmedSmartDeviceId
            ? `Smart ID ${escapeHtml(d.confirmedSmartDeviceId)}`
            : `ADB ID ${escapeHtml(d.androidId || "não lido")} (não autoritativo)`}</span>
        </div>
      </div>
    `).join("");

    picker.querySelectorAll(".android-option").forEach(el => {
      el.addEventListener("click", () => {
        state.androidSerial = el.dataset.serial;
        renderAndroid();
        evaluate();
      });
    });

    grid.innerHTML = online.map(d => `
      <article class="device-card">
        <div class="device-card-top">
          <div><h3>${escapeHtml(d.model || d.serial)}</h3><p>${escapeHtml(d.serial)}</p></div>
          <span class="device-chip">● ONLINE</span>
        </div>
        <div class="device-details">
          <div><span>Conexão</span><strong>${escapeHtml(d.transport)}</strong></div>
          <div><span>Android</span><strong>${escapeHtml(d.androidVersion || "?")}</strong></div>
          <div><span>Smart</span><strong>${escapeHtml(d.smartVersion || "Não detectado")}</strong></div>
          <div><span>Fluxo</span><strong>${escapeHtml(d.smartFlow || "—")}</strong></div>
          <div><span>Bateria</span><strong>${d.battery ?? "—"}${d.battery != null ? "%" : ""}</strong></div>
        </div>
        <div class="device-details">
          <div style="grid-column:1/-1"><span>${d.confirmedSmartDeviceId ? "Device ID real do Smart" : "Android ID do shell ADB"}</span><strong>${escapeHtml(d.confirmedSmartDeviceId || d.androidId || "Não identificado")}${d.confirmedSmartDeviceId ? "" : " (não autoritativo em Android 8+)"}</strong></div>
        </div>
        <div class="device-card-actions">
          <button class="secondary compact device-scrcpy" data-serial="${escapeAttr(d.serial)}">Abrir scrcpy</button>
        </div>
      </article>
    `).join("");

    grid.querySelectorAll(".device-scrcpy").forEach(btn => {
      btn.addEventListener("click", () => send("openScrcpy", { serial: btn.dataset.serial }));
    });

    updateActions();
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
    return {
      tefDeviceName: $("tef-device-name").value.trim(),
      tefCnpj: $("tef-cnpj").value.trim(),
      tefEmpresaId: $("tef-empresa-id").value.trim(),
      tefToken: $("tef-token").value.trim()
    };
  }

  function tefFieldsValid() {
    const x = getTefPayload();
    return !!(x.tefDeviceName && x.tefCnpj && x.tefEmpresaId && x.tefToken);
  }

  function renderModuleMode() {
    const tef = isTefMode();
    const selfHostRequired = moduleRequiresSelfHost();
    const selfHost = isSelfHostMode();
    $("standard-device-config").classList.toggle("hidden", tef);
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
      $("selfhost-status").textContent = base ? `${version} · ${base}` : `${version} · IP local automático`;
    }
    $("tef-config").classList.toggle("hidden", !tef);
    $("generate-url").classList.toggle("hidden", tef);
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
    const oauthOk = !!state.oauthClient;
    const companyOk = !!state.company;
    const dbOk = !!state.database;
    const tef = isTefMode();

    $("open-scrcpy").disabled = !androidOk;
    // O botao avulso Limpar Smart usa o package padrao salvo. No Smart TEF a limpeza
    // deve ser feita pela opcao da preparacao, que seleciona o package RedeFlex correto.
    $("clear-smart").disabled = tef || !androidOk || !state.bootstrap?.settings?.smartPackageName;
    const existingMode = state.deviceMode === "existing";
    $("generate-url").disabled = tef || !existingMode || !(oauthOk && companyOk && dbOk);
    $("validate-link").disabled = tef || !existingMode || !(oauthOk && androidOk && companyOk && dbOk);
    $("prepare-smart").disabled = tef
      ? !(androidOk && tefFieldsValid())
      : !existingMode || !(oauthOk && androidOk && companyOk && dbOk);
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
        const next = ["online", "docker", "database"].includes(requested) ? requested : "online";
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
          state.database = state.environment === "aws2" ? normalizeDatabaseName("jormungandr") : "";
          $("database-input").value = state.environment === "aws2" ? "jormungandr" : "";
        }
        renderAccessMode();
      });
    });

    document.querySelectorAll("#environment-switch button").forEach(btn => {
      btn.addEventListener("click", () => {
        state.environment = btn.dataset.env;
        document.querySelectorAll("#environment-switch button").forEach(x => x.classList.toggle("active", x === btn));
        state.database = state.environment === "aws2" ? normalizeDatabaseName("jormungandr") : "";
        state.company = null;
        state.oauthClient = null;
        state.databases = [];
        state.companies = [];
        state.oauthClients = [];
        $("database-input").value = state.environment === "aws2" ? "jormungandr" : "";
        renderDatabases();
        renderCompanies();
        renderCompanyPreview();
        renderOauthClients();
        renderOauthPreview();
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
      if (state.company) loadOauth();
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
      renderOauthPreview();
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

    $("refresh-android").addEventListener("click", () => send("refreshAndroid"));
    $("refresh-android-page").addEventListener("click", () => send("refreshAndroid"));
    $("open-scrcpy").addEventListener("click", () => send("openScrcpy", { serial: state.androidSerial }));
    $("clear-smart").addEventListener("click", () => {
      if (confirm("Limpar os dados locais do Smart neste Android?")) {
        send("clearSmartData", { serial: state.androidSerial });
      }
    });

    $("connect-vpn").addEventListener("click", () => send("connectVpn", { environment: state.environment }));
    $("connect-docker").addEventListener("click", () => send("connectDocker", { environment: state.environment }));
    $("open-client-site").addEventListener("click", () => send("openClientSite", { database: state.database }));

    $("generate-url").addEventListener("click", () => {
      send("generateUrl", {
        accessMode: state.accessMode,
        database: state.database,
        company: state.company,
        oauthClient: state.oauthClient,
        module: state.module,
        useSelfHost: isSelfHostMode(),
        selfHostBaseUrl: getSelfHostBaseUrl()
      });
    });

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
        if (!state.androidSerial || !tefFieldsValid()) {
          toast("Selecione um Android e confira os dados do Smart TEF.", "error");
          return;
        }

        const tef = getTefPayload();
        const warning = `Smart TEF será configurado no Android selecionado com o nome ${tef.tefDeviceName}. A VPN será desligada antes da configuração${clearData ? " e os dados locais do Smart TEF serão limpos" : "; o aplicativo será fechado e aberto novamente sem limpar os dados"}. Deseja continuar?`;
        if (!confirm(warning)) return;

        renderValidation({ status: "ready", title: "Configurando Smart TEF", detail: "Percorrendo o fluxo inicial do RedeFlex e preenchendo os dados manuais." });
        setBusy("provision", true);
        send("prepareSmart", {
          accessMode: state.accessMode,
          environment: state.environment,
          serial: state.androidSerial,
          module: state.module,
          clearData,
          ...tef
        });
        return;
      }

      if (!state.database || !state.company || !state.oauthClient || !state.androidSerial) {
        toast(`Selecione cliente, empresa, dispositivo ${isSelfHostMode() ? "SelfHost" : "Softcomshop"} e Android.`, "error");
        return;
      }

      const moduleLabel = $("module-select").selectedOptions[0]?.textContent || "Smart";
      const modeInfo = isSelfHostMode()
        ? "usando o /device/add do SelfHost"
        : state.accessMode === "online"
          ? "usando a sessão WEB do Softcomshop"
          : state.accessMode === "docker"
            ? "usando o banco pelo Docker isolado, sem VPN no Windows"
            : "usando o acesso direto ao banco e VPN local quando necessário";
      const warning = clearData
        ? `Modulo selecionado: ${moduleLabel}. O Provisioner verificará e removerá vínculos anteriores ${modeInfo} e limpará os dados locais do Smart antes do novo vínculo. Deseja continuar?`
        : `Modulo selecionado: ${moduleLabel}. O Provisioner verificará e removerá vínculos anteriores ${modeInfo}, fechará e abrirá novamente o Smart sem limpar os dados e então continuará o vínculo. Deseja continuar?`;
      if (!confirm(warning)) return;

      renderValidation({ status: "ready", title: "Iniciando preparacao", detail: "Validando o cenario e abrindo o Smart no Android selecionado." });
      setBusy("provision", true);
      send("prepareSmart", {
        accessMode: state.accessMode,
        environment: state.environment,
        database: state.database,
        company: state.company,
        oauthClient: state.oauthClient,
        serial: state.androidSerial,
        module: state.module,
        useSelfHost: isSelfHostMode(),
        selfHostBaseUrl: getSelfHostBaseUrl(),
        clearData
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
          loadOauth();
        }
        break;
      case "oauthClients": {
        const currentClientId = state.oauthClient?.clientId || "";
        state.oauthClients = payload.items || [];
        if (currentClientId) {
          const idx = state.oauthClients.findIndex(x => x.clientId === currentClientId);
          state.oauthClient = idx >= 0 ? state.oauthClients[idx] : null;
        }
        renderOauthClients();
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
          title: "Preparando Smart",
          detail: payload.message || "Executando etapa do provisionamento..."
        });
        break;
      case "smartPreparationFinished": {
        setBusy("provision", false);
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
