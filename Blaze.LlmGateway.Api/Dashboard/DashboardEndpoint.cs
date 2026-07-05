using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Blaze.LlmGateway.Api.Dashboard;

/// <summary>
/// P2: self-contained operator dashboard at /dashboard. Static shell (no secrets baked in);
/// every data call hits /admin/* with the X-Admin-Key the operator enters, so data access
/// stays behind the existing admin guard. No external assets (CSP-friendly).
/// </summary>
public static class DashboardEndpoint
{
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/dashboard", () => Results.Content(Html, "text/html; charset=utf-8"))
           .ExcludeFromDescription();
    }

    private const string Html =
        """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <title>CodebrewRouter Dashboard</title>
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <style>
          :root { color-scheme: light dark; --accent:#7c5cff; --ok:#22a06b; --bad:#e5484d; --muted:#8886; }
          body { font-family: system-ui, sans-serif; margin:0; padding:0 1rem 4rem; max-width:1080px; margin-inline:auto; line-height:1.5; }
          header { display:flex; gap:1rem; align-items:center; flex-wrap:wrap; padding:1rem 0; border-bottom:1px solid var(--muted); }
          h1 { font-size:1.2rem; margin:0; }
          nav button { background:none; border:1px solid var(--muted); border-radius:6px; padding:.35rem .8rem; cursor:pointer; color:inherit; }
          nav button.active { border-color:var(--accent); color:var(--accent); font-weight:600; }
          input[type=password] { padding:.35rem .5rem; border:1px solid var(--muted); border-radius:6px; background:transparent; color:inherit; }
          section { display:none; padding-top:1rem; }
          section.active { display:block; }
          table { border-collapse:collapse; width:100%; font-size:.9rem; }
          th, td { text-align:left; padding:.35rem .5rem; border-bottom:1px solid var(--muted); }
          .pill { display:inline-block; padding:.05rem .5rem; border-radius:999px; font-size:.8rem; }
          .pill.ok { background:color-mix(in srgb, var(--ok) 15%, transparent); color:var(--ok); }
          .pill.bad { background:color-mix(in srgb, var(--bad) 15%, transparent); color:var(--bad); }
          .cards { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:.75rem; }
          .card { border:1px solid var(--muted); border-radius:10px; padding:.75rem 1rem; }
          .card h3 { margin:.1rem 0 .3rem; font-size:1rem; }
          .kpis { display:flex; gap:1.5rem; flex-wrap:wrap; margin-bottom:1rem; }
          .kpi b { display:block; font-size:1.4rem; }
          pre { background:#8881; padding:.75rem; border-radius:8px; overflow-x:auto; font-size:.82rem; }
          canvas { width:100%; max-width:900px; height:220px; border:1px solid var(--muted); border-radius:8px; }
          .err { color:var(--bad); }
          small.muted { opacity:.65; }
        </style>
        </head>
        <body>
        <header>
          <h1>CodebrewRouter</h1>
          <nav>
            <button data-tab="home" class="active">Home</button>
            <button data-tab="providers">Providers</button>
            <button data-tab="usage">Usage</button>
            <button data-tab="routes">Routes</button>
            <button data-tab="quota">Quota</button>
            <button data-tab="keys">Keys</button>
          </nav>
          <span style="margin-left:auto">
            <input id="adminKey" type="password" placeholder="X-Admin-Key" />
            <button id="saveKey">Connect</button>
          </span>
        </header>

        <section id="home" class="active">
          <div class="kpis">
            <span class="kpi"><b id="homeEndpoint">–</b>endpoint</span>
            <span class="kpi"><b id="homeHealth">–</b>health</span>
            <span class="kpi"><b id="homeModels">–</b>models</span>
          </div>
          <h2>Connect a CLI tool</h2>
          <div class="cards">
            <div class="card"><h3>Claude Code</h3><pre id="recipeClaude"></pre></div>
            <div class="card"><h3>Codex CLI</h3><pre id="recipeCodex"></pre></div>
            <div class="card"><h3>OpenCode / Cline / Continue</h3><pre id="recipeOpenAi"></pre></div>
            <div class="card"><h3>curl smoke test</h3><pre id="recipeCurl"></pre></div>
          </div>
        </section>

        <section id="providers">
          <h2>Provider status <small class="muted">(from availability probes)</small></h2>
          <div id="providerCards" class="cards"></div>
        </section>

        <section id="usage">
          <div class="kpis">
            <span class="kpi"><b id="uReq">–</b>requests</span>
            <span class="kpi"><b id="uTok">–</b>tokens</span>
            <span class="kpi"><b id="uCost">–</b>est. cost</span>
          </div>
          <canvas id="usageChart" width="900" height="220"></canvas>
          <h2>Recent requests</h2>
          <table><thead><tr><th>time</th><th>model</th><th>provider model</th><th>key</th><th>prompt</th><th>completion</th><th>ms</th><th>status</th></tr></thead>
          <tbody id="usageRows"></tbody></table>
        </section>

        <section id="routes">
          <h2>Recent route decisions</h2>
          <table><thead><tr><th>time</th><th>model</th><th>provider</th><th>reason</th></tr></thead>
          <tbody id="routeRows"></tbody></table>
        </section>

        <section id="quota">
          <h2>Active rate-limit cooldowns</h2>
          <table><thead><tr><th>provider</th><th>locked until</th><th>remaining</th><th>backoff level</th><th>reason</th></tr></thead>
          <tbody id="lockRows"></tbody></table>
          <p><small class="muted">Providers listed here are skipped pre-emptively by the router until the lock expires.</small></p>
        </section>

        <section id="keys">
          <h2>API keys</h2>
          <p><input id="newKeyName" placeholder="key name" /> <button id="mintKey">Mint key</button></p>
          <p id="mintResult"></p>
          <table><thead><tr><th>id</th><th>name</th><th>key (redacted)</th><th>created</th><th></th></tr></thead>
          <tbody id="keyRows"></tbody></table>
        </section>

        <p id="status" class="err"></p>

        <script>
        const $ = (id) => document.getElementById(id);
        const esc = (value) => String(value ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
        let adminKey = localStorage.getItem("cbr-admin-key") || "";
        $("adminKey").value = adminKey;

        async function api(path, options = {}) {
          options.headers = Object.assign({ "X-Admin-Key": adminKey, "Content-Type": "application/json" }, options.headers);
          const response = await fetch(path, options);
          if (!response.ok) throw new Error(path + " → HTTP " + response.status);
          return response.json();
        }
        function setStatus(message) { $("status").textContent = message || ""; }

        document.querySelectorAll("nav button").forEach(button => button.addEventListener("click", () => {
          document.querySelectorAll("nav button").forEach(b => b.classList.remove("active"));
          document.querySelectorAll("section").forEach(s => s.classList.remove("active"));
          button.classList.add("active");
          $(button.dataset.tab).classList.add("active");
          load(button.dataset.tab);
        }));
        $("saveKey").addEventListener("click", () => {
          adminKey = $("adminKey").value.trim();
          localStorage.setItem("cbr-admin-key", adminKey);
          load(document.querySelector("nav button.active").dataset.tab);
        });

        function fillRecipes() {
          const base = location.origin;
          $("recipeClaude").textContent = `export ANTHROPIC_BASE_URL=${base}\nexport ANTHROPIC_API_KEY=<gateway key>\nclaude`;
          $("recipeCodex").textContent = `export OPENAI_BASE_URL=${base}/v1\nexport OPENAI_API_KEY=<gateway key>\ncodex`;
          $("recipeOpenAi").textContent = `Base URL: ${base}/v1\nAPI key:  <gateway key>\nModel:    codebrewRouter | auto | fusion`;
          $("recipeCurl").textContent = `curl ${base}/v1/chat/completions \\\n  -H "Authorization: Bearer <gateway key>" \\\n  -H "Content-Type: application/json" \\\n  -d '{"model":"auto","messages":[{"role":"user","content":"hi"}]}'`;
        }

        async function load(tab) {
          setStatus("");
          try {
            if (tab === "home") {
              fillRecipes();
              $("homeEndpoint").textContent = location.origin + "/v1";
              const health = await fetch("/health");
              $("homeHealth").textContent = health.ok ? "healthy" : "HTTP " + health.status;
              const providers = await api("/admin/providers");
              $("homeModels").textContent = (providers.models || []).length;
            } else if (tab === "providers") {
              const data = await api("/admin/providers");
              $("providerCards").innerHTML = (data.models || []).map(m => `
                <div class="card">
                  <h3>${esc(m.id)}</h3>
                  <div>${esc(m.provider)}</div>
                  <span class="pill ${m.enabled ? "ok" : "bad"}">${m.enabled ? "available" : "unavailable"}</span>
                  ${m.errorMessage ? `<div class="err"><small>${esc(m.errorMessage)}</small></div>` : ""}
                </div>`).join("");
            } else if (tab === "usage") {
              const summary = await api("/admin/usage/summary");
              $("uReq").textContent = summary.total_requests;
              $("uTok").textContent = summary.total_tokens.toLocaleString();
              $("uCost").textContent = "$" + Number(summary.estimated_cost_usd).toFixed(4);
              const chart = await api("/admin/usage/chart?days=30");
              drawChart(chart.data || []);
              const history = await api("/admin/usage/history?limit=50");
              $("usageRows").innerHTML = (history.data || []).map(row => `
                <tr><td>${esc((row.created_at || "").replace("T", " ").slice(0, 19))}</td>
                <td>${esc(row.model)}</td><td>${esc(row.provider_model || "")}</td><td>${esc(row.api_key_id || "-")}</td>
                <td>${row.prompt_tokens}</td><td>${row.completion_tokens}</td><td>${row.latency_ms}</td>
                <td><span class="pill ${row.status === "ok" ? "ok" : "bad"}">${esc(row.status)}</span></td></tr>`).join("");
            } else if (tab === "routes") {
              const data = await api("/admin/routes/recent");
              $("routeRows").innerHTML = (data.data || []).map(row => `
                <tr><td>${esc((row.created_at || "").replace("T", " ").slice(0, 19))}</td>
                <td>${esc(row.model)}</td><td>${esc(row.provider)}</td><td>${esc(row.reason)}</td></tr>`).join("");
            } else if (tab === "quota") {
              const data = await api("/admin/quota/locks");
              $("lockRows").innerHTML = (data.data || []).map(row => {
                const remainingSeconds = Math.max(0, (new Date(row.lockedUntil) - Date.now()) / 1000).toFixed(0);
                return `<tr><td>${esc(row.providerKey)}</td><td>${esc(row.lockedUntil)}</td><td>${remainingSeconds}s</td>
                        <td>${row.backoffLevel}</td><td>${esc(row.reason)}</td></tr>`;
              }).join("") || '<tr><td colspan="5">No active cooldowns 🎉</td></tr>';
            } else if (tab === "keys") {
              const data = await api("/admin/keys");
              $("keyRows").innerHTML = (data.data || []).map(key => `
                <tr><td>${esc(key.id)}</td><td>${esc(key.name)}</td><td><code>${esc(key.key)}</code></td>
                <td>${esc((key.created_at || "").slice(0, 19))}</td>
                <td><button onclick="revoke('${esc(key.id)}')">revoke</button></td></tr>`).join("");
            }
          } catch (error) { setStatus(String(error.message || error)); }
        }

        window.revoke = async (id) => {
          if (!confirm("Revoke key " + id + "?")) return;
          try { await api("/admin/keys/" + id, { method: "DELETE" }); load("keys"); }
          catch (error) { setStatus(String(error.message || error)); }
        };
        $("mintKey").addEventListener("click", async () => {
          try {
            const key = await api("/admin/keys", { method: "POST", body: JSON.stringify({ name: $("newKeyName").value || "default" }) });
            $("mintResult").innerHTML = "New key (copy now — shown once): <code>" + esc(key.key) + "</code>";
            load("keys");
          } catch (error) { setStatus(String(error.message || error)); }
        });

        function drawChart(buckets) {
          const canvas = $("usageChart");
          const context = canvas.getContext("2d");
          context.clearRect(0, 0, canvas.width, canvas.height);
          if (!buckets.length) { context.fillText("no usage yet", 20, 30); return; }
          const max = Math.max(...buckets.map(bucket => bucket.total_tokens), 1);
          const barWidth = Math.min(40, (canvas.width - 40) / buckets.length);
          context.fillStyle = "#7c5cff";
          buckets.forEach((bucket, index) => {
            const height = (bucket.total_tokens / max) * (canvas.height - 40);
            context.fillRect(20 + index * barWidth, canvas.height - 20 - height, barWidth - 4, height);
          });
          context.fillStyle = "#888";
          context.fillText(buckets[0].date, 20, canvas.height - 5);
          context.fillText(buckets[buckets.length - 1].date, canvas.width - 90, canvas.height - 5);
          context.fillText(max.toLocaleString() + " tokens", 20, 14);
        }

        load("home");
        </script>
        </body>
        </html>
        """;
}
