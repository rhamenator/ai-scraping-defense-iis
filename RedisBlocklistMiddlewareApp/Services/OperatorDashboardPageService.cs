namespace RedisBlocklistMiddlewareApp.Services;

public interface IOperatorDashboardPageService
{
    string Render();
}

public sealed class OperatorDashboardPageService : IOperatorDashboardPageService
{
    public string Render()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Defense Operator Console</title>
  <style>
    :root {
      --paper: #f4efe4;
      --paper-deep: #e8dcc4;
      --ink: #1d2218;
      --muted: #5f6658;
      --line: rgba(29, 34, 24, 0.14);
      --accent: #8d2a1e;
      --accent-soft: rgba(141, 42, 30, 0.12);
      --ok: #355e3b;
      --warn: #8c5c12;
      --panel: rgba(255, 252, 245, 0.88);
      --shadow: 0 24px 60px rgba(33, 26, 17, 0.16);
      --radius: 24px;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      min-height: 100vh;
      color: var(--ink);
      background:
        radial-gradient(circle at top left, rgba(141, 42, 30, 0.16), transparent 32%),
        radial-gradient(circle at top right, rgba(53, 94, 59, 0.14), transparent 28%),
        linear-gradient(180deg, #f8f4eb 0%, #ece0c9 100%);
      font-family: "Avenir Next", "Segoe UI", sans-serif;
    }

    .shell {
      width: min(1240px, calc(100vw - 32px));
      margin: 24px auto 48px;
      display: grid;
      gap: 18px;
    }

    .hero,
    .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: var(--radius);
      box-shadow: var(--shadow);
      backdrop-filter: blur(18px);
    }

    .hero {
      padding: 28px;
      display: grid;
      gap: 18px;
    }

    .hero-grid {
      display: grid;
      gap: 16px;
      grid-template-columns: 1.7fr 1fr;
    }

    .eyebrow {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      border-radius: 999px;
      background: var(--accent-soft);
      color: var(--accent);
      font-size: 0.82rem;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      width: fit-content;
    }

    h1, h2, h3 {
      margin: 0;
      font-family: "Iowan Old Style", "Palatino Linotype", Georgia, serif;
      font-weight: 700;
      letter-spacing: -0.02em;
    }

    h1 { font-size: clamp(2.3rem, 4vw, 4.2rem); line-height: 0.94; max-width: 10ch; }
    h2 { font-size: 1.35rem; }
    h3 { font-size: 1rem; }

    p {
      margin: 0;
      color: var(--muted);
      line-height: 1.55;
    }

    .status-bar {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      align-items: center;
      justify-content: space-between;
    }

    .status-pill {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 10px 14px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.72);
      border: 1px solid var(--line);
      font-size: 0.92rem;
    }

    .dot {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      background: var(--warn);
      box-shadow: 0 0 0 6px rgba(140, 92, 18, 0.12);
    }

    .dot.live {
      background: var(--ok);
      box-shadow: 0 0 0 6px rgba(53, 94, 59, 0.12);
    }

    .panel-grid {
      display: grid;
      gap: 18px;
      grid-template-columns: 1.25fr 0.95fr;
    }

    .panel {
      padding: 22px;
    }

    .metric-grid {
      display: grid;
      gap: 12px;
      grid-template-columns: repeat(4, minmax(0, 1fr));
    }

    .metric-card {
      padding: 18px;
      border-radius: 18px;
      background: rgba(255, 255, 255, 0.82);
      border: 1px solid var(--line);
      min-height: 124px;
      display: grid;
      gap: 10px;
      align-content: start;
    }

    .metric-label {
      color: var(--muted);
      font-size: 0.82rem;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }

    .metric-value {
      font-family: "Iowan Old Style", "Palatino Linotype", Georgia, serif;
      font-size: clamp(2rem, 3vw, 2.8rem);
      line-height: 1;
    }

    .toolbar,
    .action-row,
    .status-grid {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      align-items: center;
    }

    .status-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }

    .status-card,
    .login-panel {
      padding: 18px;
      border-radius: 18px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.82);
      display: grid;
      gap: 10px;
    }

    .login-panel {
      align-content: start;
      min-height: 100%;
    }

    .table-wrap {
      overflow-x: auto;
      border-radius: 18px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.78);
    }

    table {
      width: 100%;
      border-collapse: collapse;
      min-width: 760px;
    }

    th, td {
      padding: 14px 16px;
      text-align: left;
      border-bottom: 1px solid var(--line);
      font-size: 0.94rem;
      vertical-align: top;
    }

    th {
      color: var(--muted);
      font-size: 0.78rem;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }

    .chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 6px 10px;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.72);
      margin: 0 6px 6px 0;
      font-size: 0.78rem;
    }

    .chip.blocked { color: var(--accent); border-color: rgba(141, 42, 30, 0.24); background: rgba(141, 42, 30, 0.08); }
    .chip.observed { color: var(--ok); border-color: rgba(53, 94, 59, 0.24); background: rgba(53, 94, 59, 0.08); }

    form {
      display: grid;
      gap: 12px;
    }

    label {
      display: grid;
      gap: 8px;
      font-size: 0.84rem;
      color: var(--muted);
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }

    input, button {
      font: inherit;
    }

    input {
      width: 100%;
      padding: 14px 16px;
      border-radius: 14px;
      border: 1px solid rgba(29, 34, 24, 0.16);
      background: rgba(255, 255, 255, 0.86);
      color: var(--ink);
    }

    input:focus {
      outline: 2px solid rgba(141, 42, 30, 0.18);
      border-color: rgba(141, 42, 30, 0.38);
    }

    button {
      border: 0;
      border-radius: 999px;
      padding: 12px 16px;
      cursor: pointer;
      transition: transform 150ms ease, opacity 150ms ease, background 150ms ease;
    }

    button:hover { transform: translateY(-1px); }
    button:disabled { opacity: 0.55; cursor: wait; transform: none; }

    .primary {
      background: var(--accent);
      color: white;
    }

    .secondary {
      background: rgba(29, 34, 24, 0.07);
      color: var(--ink);
    }

    .ghost {
      background: transparent;
      color: var(--muted);
      border: 1px solid var(--line);
    }

    .inline-note,
    .empty,
    .mono {
      color: var(--muted);
      font-size: 0.92rem;
    }

    .mono {
      font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      word-break: break-word;
    }

    .banner {
      display: none;
      padding: 14px 16px;
      border-radius: 16px;
      border: 1px solid rgba(141, 42, 30, 0.24);
      background: rgba(141, 42, 30, 0.08);
      color: var(--accent);
    }

    .banner.visible {
      display: block;
    }

    .hidden {
      display: none !important;
    }

    @media (max-width: 960px) {
      .hero-grid,
      .panel-grid,
      .metric-grid,
      .status-grid {
        grid-template-columns: 1fr;
      }
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="hero">
      <div class="eyebrow">Operator Console</div>
      <div class="hero-grid">
        <div>
          <h1>Scraping defense command board.</h1>
          <p>Recent decisions, edge metrics, peer and community sync health, and manual blocklist controls all live here. Every action still flows through the authenticated management API.</p>
        </div>
        <aside class="login-panel">
          <div class="status-bar">
            <h2>Session</h2>
            <button id="logoutButton" class="ghost hidden" type="button">Log out</button>
          </div>
          <p id="sessionCopy">Sign in with the management API key to unlock the dashboard and issue blocklist actions from the browser.</p>
          <form id="loginForm">
            <label>
              Management API key
              <input id="apiKeyInput" type="password" autocomplete="current-password" placeholder="Paste API key">
            </label>
            <div class="action-row">
              <button class="primary" id="loginButton" type="submit">Open dashboard</button>
              <button class="secondary" id="refreshButton" type="button">Refresh now</button>
            </div>
          </form>
          <p class="inline-note">The browser session uses a server-issued cookie after the initial sign-in. The dashboard then reads the same `/defense/*` management endpoints as any other operator client.</p>
        </aside>
      </div>
      <div class="status-bar">
        <div class="status-pill"><span id="liveDot" class="dot"></span><strong id="liveLabel">Locked</strong></div>
        <div class="status-pill">Last sync <span id="lastRefresh" class="mono">never</span></div>
      </div>
      <div id="errorBanner" class="banner" role="alert"></div>
    </section>

    <section class="panel">
      <div class="status-bar">
        <h2>Edge metrics</h2>
        <p class="inline-note">Derived from the persisted defense event store.</p>
      </div>
      <div class="metric-grid">
        <article class="metric-card">
          <div class="metric-label">Total decisions</div>
          <div id="metricTotal" class="metric-value">0</div>
          <p>All recorded decisions in the durable audit store.</p>
        </article>
        <article class="metric-card">
          <div class="metric-label">Blocked</div>
          <div id="metricBlocked" class="metric-value">0</div>
          <p>Requests escalated into active enforcement.</p>
        </article>
        <article class="metric-card">
          <div class="metric-label">Observed</div>
          <div id="metricObserved" class="metric-value">0</div>
          <p>Requests retained for analysis without an immediate block.</p>
        </article>
        <article class="metric-card">
          <div class="metric-label">Latest decision</div>
          <div id="metricLatest" class="metric-value" style="font-size:1.4rem;">none</div>
          <p>Most recent decision timestamp, formatted in local operator time.</p>
        </article>
      </div>
    </section>

    <section class="panel-grid">
      <section class="panel">
        <div class="status-bar">
          <h2>Recent decisions</h2>
          <div class="toolbar">
            <button id="loadMoreButton" class="secondary" type="button">Load 100</button>
          </div>
        </div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Action</th>
                <th>IP</th>
                <th>Path</th>
                <th>Score</th>
                <th>Signals</th>
                <th>Observed</th>
              </tr>
            </thead>
            <tbody id="eventsTableBody">
              <tr><td colspan="6" class="empty">Sign in to load recent defense decisions.</td></tr>
            </tbody>
          </table>
        </div>
      </section>

      <aside class="panel">
        <div class="status-bar">
          <h2>Control actions</h2>
          <span class="inline-note">Lookup, block, and unblock IPs.</span>
        </div>
        <form id="blocklistForm">
          <label>
            IP address
            <input id="ipInput" type="text" inputmode="text" placeholder="203.0.113.10">
          </label>
          <label>
            Reason
            <input id="reasonInput" type="text" placeholder="manual_block">
          </label>
          <div class="action-row">
            <button class="primary" id="lookupButton" type="button">Check status</button>
            <button class="secondary" id="blockButton" type="button">Block IP</button>
            <button class="ghost" id="unblockButton" type="button">Unblock IP</button>
          </div>
        </form>
        <div id="blocklistResult" class="status-card">
          <h3>Blocklist result</h3>
          <p class="mono">No lookup performed yet.</p>
        </div>
        <div class="status-grid">
          <section class="status-card">
            <h3>Community feeds</h3>
            <p id="communityStatusCopy">Waiting for session.</p>
          </section>
          <section class="status-card">
            <h3>Peer sync</h3>
            <p id="peerStatusCopy">Waiting for session.</p>
          </section>
        </div>
      </aside>
    </section>
  </main>

  <script>
    const state = {
      authenticated: false,
      eventCount: 50
    };

    const loginForm = document.getElementById("loginForm");
    const logoutButton = document.getElementById("logoutButton");
    const refreshButton = document.getElementById("refreshButton");
    const loadMoreButton = document.getElementById("loadMoreButton");
    const apiKeyInput = document.getElementById("apiKeyInput");
    const liveDot = document.getElementById("liveDot");
    const liveLabel = document.getElementById("liveLabel");
    const lastRefresh = document.getElementById("lastRefresh");
    const sessionCopy = document.getElementById("sessionCopy");
    const errorBanner = document.getElementById("errorBanner");
    const eventsTableBody = document.getElementById("eventsTableBody");
    const blocklistResult = document.getElementById("blocklistResult");
    const ipInput = document.getElementById("ipInput");
    const reasonInput = document.getElementById("reasonInput");

    document.getElementById("lookupButton").addEventListener("click", () => runBlocklistAction("lookup"));
    document.getElementById("blockButton").addEventListener("click", () => runBlocklistAction("block"));
    document.getElementById("unblockButton").addEventListener("click", () => runBlocklistAction("unblock"));
    refreshButton.addEventListener("click", () => refreshDashboard());
    loadMoreButton.addEventListener("click", () => {
      state.eventCount = 100;
      refreshEvents();
    });
    logoutButton.addEventListener("click", logout);

    loginForm.addEventListener("submit", async event => {
      event.preventDefault();
      clearError();
      setBusy(true);

      try {
        const response = await fetch("/defense/dashboard/session", {
          method: "POST",
          credentials: "same-origin",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ apiKey: apiKeyInput.value })
        });

        if (!response.ok) {
          throw new Error(response.status === 401 ? "The supplied management API key was rejected." : "The dashboard session could not be created.");
        }

        apiKeyInput.value = "";
        state.authenticated = true;
        await refreshDashboard();
      } catch (error) {
        showError(error.message);
      } finally {
        setBusy(false);
      }
    });

    async function refreshDashboard() {
      clearError();

      try {
        const authenticated = await checkSession();
        updateSessionUi(authenticated);

        if (!authenticated) {
          renderLockedState();
          return;
        }

        await Promise.all([
          refreshMetrics(),
          refreshEvents(),
          refreshStatuses()
        ]);

        lastRefresh.textContent = formatDate(new Date().toISOString());
      } catch (error) {
        showError(error.message || "The dashboard refresh failed.");
      }
    }

    async function checkSession() {
      try {
        const response = await fetch("/defense/dashboard/session", {
          credentials: "same-origin"
        });

        if (!response.ok) {
          return false;
        }

        const payload = await response.json();
        state.authenticated = Boolean(payload.authenticated);
        return state.authenticated;
      } catch {
        state.authenticated = false;
        return false;
      }
    }

    async function refreshMetrics() {
      const metrics = await fetchJson("/defense/metrics");
      document.getElementById("metricTotal").textContent = number(metrics.totalDecisions);
      document.getElementById("metricBlocked").textContent = number(metrics.blockedCount);
      document.getElementById("metricObserved").textContent = number(metrics.observedCount);
      document.getElementById("metricLatest").textContent = metrics.latestDecisionAtUtc ? formatDate(metrics.latestDecisionAtUtc) : "none";
    }

    async function refreshEvents() {
      const events = await fetchJson("/defense/events?count=" + encodeURIComponent(state.eventCount));

      if (!events.length) {
        eventsTableBody.innerHTML = '<tr><td colspan="6" class="empty">No defense decisions are stored yet.</td></tr>';
        return;
      }

      eventsTableBody.innerHTML = events.map(event => {
        const actionClass = event.action === "blocked" ? "blocked" : "observed";
        const signalHtml = (event.signals || []).map(signal => '<span class="chip">' + escapeHtml(signal) + '</span>').join("");

        return `
          <tr>
            <td><span class="chip ${actionClass}">${escapeHtml(event.action)}</span></td>
            <td class="mono">${escapeHtml(event.ipAddress)}</td>
            <td class="mono">${escapeHtml(event.path)}</td>
            <td>${number(event.score)}</td>
            <td>${signalHtml || '<span class="empty">none</span>'}</td>
            <td>${formatDate(event.observedAtUtc)}</td>
          </tr>`;
      }).join("");
    }

    async function refreshStatuses() {
      const [community, peer] = await Promise.all([
        fetchJson("/defense/community-blocklist/status"),
        fetchJson("/defense/peer-sync/status")
      ]);

      document.getElementById("communityStatusCopy").textContent = summarizeStatus(community.enabled, community.importedCount, community.rejectedCount, community.lastSuccessAtUtc, community.lastError);
      document.getElementById("peerStatusCopy").textContent = summarizePeerStatus(peer);
    }

    async function runBlocklistAction(action) {
      clearError();

      if (!state.authenticated) {
        showError("Open a dashboard session before issuing blocklist actions.");
        return;
      }

      const ip = ipInput.value.trim();
      const reason = reasonInput.value.trim() || "manual_block";

      if (!ip) {
        showError("Enter an IP address first.");
        return;
      }

      const baseUrl = "/defense/blocklist?ip=" + encodeURIComponent(ip);

      try {
        let response;

        if (action === "lookup") {
          response = await fetch(baseUrl, { credentials: "same-origin" });
        } else if (action === "block") {
          response = await fetch(baseUrl + "&reason=" + encodeURIComponent(reason), {
            method: "POST",
            credentials: "same-origin"
          });
        } else {
          response = await fetch(baseUrl, {
            method: "DELETE",
            credentials: "same-origin"
          });
        }

        const payload = await parseJson(response);
        if (!response.ok) {
          throw new Error(payload.error || "The blocklist action failed.");
        }

        blocklistResult.innerHTML = `
          <h3>Blocklist result</h3>
          <p class="mono">IP ${escapeHtml(payload.ip || ip)} is ${payload.blocked ? "blocked" : "not blocked"}.</p>`;

        await refreshDashboard();
      } catch (error) {
        showError(error.message);
      }
    }

    async function logout() {
      await fetch("/defense/dashboard/session", {
        method: "DELETE",
        credentials: "same-origin"
      });

      state.authenticated = false;
      updateSessionUi(false);
      renderLockedState();
      clearError();
    }

    async function fetchJson(url) {
      const response = await fetch(url, {
        credentials: "same-origin"
      });

      const payload = await parseJson(response);
      if (!response.ok) {
        throw new Error(payload.error || "Request failed.");
      }

      return payload;
    }

    async function parseJson(response) {
      const contentType = response.headers.get("content-type") || "";
      if (!contentType.includes("application/json")) {
        return {};
      }

      return response.json();
    }

    function updateSessionUi(authenticated) {
      liveDot.classList.toggle("live", authenticated);
      liveLabel.textContent = authenticated ? "Live" : "Locked";
      logoutButton.classList.toggle("hidden", !authenticated);
      sessionCopy.textContent = authenticated
        ? "The console is using the authenticated management API through a same-origin session cookie."
        : "Sign in with the management API key to unlock the dashboard and issue blocklist actions from the browser.";
    }

    function renderLockedState() {
      document.getElementById("metricTotal").textContent = "0";
      document.getElementById("metricBlocked").textContent = "0";
      document.getElementById("metricObserved").textContent = "0";
      document.getElementById("metricLatest").textContent = "none";
      eventsTableBody.innerHTML = '<tr><td colspan="6" class="empty">Sign in to load recent defense decisions.</td></tr>';
      document.getElementById("communityStatusCopy").textContent = "Waiting for session.";
      document.getElementById("peerStatusCopy").textContent = "Waiting for session.";
      blocklistResult.innerHTML = '<h3>Blocklist result</h3><p class="mono">No lookup performed yet.</p>';
    }

    function setBusy(isBusy) {
      document.getElementById("loginButton").disabled = isBusy;
      refreshButton.disabled = isBusy;
    }

    function showError(message) {
      errorBanner.textContent = message;
      errorBanner.classList.add("visible");
    }

    function clearError() {
      errorBanner.textContent = "";
      errorBanner.classList.remove("visible");
    }

    function summarizeStatus(enabled, imported, rejected, lastSuccessAtUtc, lastError) {
      if (!enabled) {
        return "Feature disabled.";
      }

      return `${number(imported)} imported, ${number(rejected)} rejected, last success ${lastSuccessAtUtc ? formatDate(lastSuccessAtUtc) : "never"}${lastError ? ", error: " + lastError : ""}`;
    }

    function summarizePeerStatus(peer) {
      if (!peer.enabled) {
        return "Feature disabled.";
      }

      return `${number(peer.importedCount)} imported, ${number(peer.blockedCount)} blocked, ${number(peer.observedCount)} observed, ${number(peer.rejectedCount)} rejected, last success ${peer.lastSuccessAtUtc ? formatDate(peer.lastSuccessAtUtc) : "never"}${peer.lastError ? ", error: " + peer.lastError : ""}`;
    }

    function number(value) {
      return new Intl.NumberFormat().format(Number(value || 0));
    }

    function formatDate(value) {
      return new Intl.DateTimeFormat(undefined, {
        dateStyle: "medium",
        timeStyle: "short"
      }).format(new Date(value));
    }

    function escapeHtml(value) {
      return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
    }

    refreshDashboard();
    setInterval(() => {
      refreshDashboard();
    }, 30000);
  </script>
</body>
</html>
""";
    }
}
