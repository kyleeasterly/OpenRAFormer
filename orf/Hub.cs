using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>
/// Fixed-port landing page over all runs: lists live and finished matches, and
/// reverse-proxies each run's own dashboard under /r/&lt;runId&gt;/ so one tunnel
/// (ngrok) reaches every match. Only the dashboard you open streams video.
/// </summary>
public static class Hub
{
	static readonly HttpClient Proxy = new() { Timeout = Timeout.InfiniteTimeSpan };

	public static async Task<int> RunAsync(int port, IReadOnlyList<string> nodes, CancellationToken ct)
	{
		var runsDir = Path.Combine(Util.FindRepoRoot(), "runs");

		// runId → proxy target prefix. Local runs map to their dashboard port;
		// runs discovered on peer nodes map through that node's own hub, so one
		// URL reaches every match in the fleet. Refreshed by /api/runs polls.
		var targets = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = AppContext.BaseDirectory,
			EnvironmentName = "Production",
		});
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

		var app = builder.Build();

		app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

		JsonArray LocalRuns()
		{
			var runs = new JsonArray();
			foreach (var dir in (Directory.Exists(runsDir) ? Directory.GetDirectories(runsDir) : []).OrderByDescending(Path.GetFileName))
			{
				if (Util.TryReadJson(Path.Combine(dir, "match.json")) is not JsonObject match)
					continue;

				var historyPath = Path.Combine(dir, "history.jsonl");
				var live = File.Exists(historyPath)
					&& (DateTime.UtcNow - File.GetLastWriteTimeUtc(historyPath)).TotalSeconds < 10;

				string? outcome = null;
				if (Util.TryReadJson(Path.Combine(dir, "result.json")) is JsonObject result)
					outcome = (result["players"] as JsonArray)?.OfType<JsonObject>()
						.FirstOrDefault(p => p["winState"]?.GetValue<string>() == "Won")?["slug"]?.GetValue<string>() is string winner
						? $"won by {winner} at {result["second"]}s"
						: "finished";
				else if (Util.TryReadJson(Path.Combine(dir, "verdict.json")) is JsonObject verdict)
					outcome = $"called: {verdict["reason"]?.GetValue<string>()}";

				var runId = Path.GetFileName(dir);
				if (match["webPort"] is JsonValue pv && pv.TryGetValue<int>(out var webPort))
					targets[runId] = $"http://127.0.0.1:{webPort}/";

				runs.Add(new JsonObject
				{
					["runId"] = runId,
					["players"] = (match["players"] as JsonArray)?.Count ?? 0,
					["live"] = live,
					["webPort"] = match["webPort"]?.DeepClone(),
					["node"] = "local",
					["outcome"] = outcome,
				});
			}

			return runs;
		}

		app.MapGet("/api/runs", async (HttpContext context) =>
		{
			var runs = LocalRuns();

			// Peer nodes: merge their local runs (localOnly stops recursion if
			// two hubs ever list each other).
			if (!context.Request.Query.ContainsKey("localOnly"))
			{
				foreach (var node in nodes)
				{
					try
					{
						using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
						cts.CancelAfter(TimeSpan.FromSeconds(3));
						var body = await Proxy.GetStringAsync($"{node}/api/runs?localOnly=1", cts.Token);
						foreach (var run in JsonNode.Parse(body) as JsonArray ?? [])
						{
							if (run is not JsonObject ro || ro["runId"]?.GetValue<string>() is not string id)
								continue;

							ro["node"] = new Uri(node).Host;
							targets[id] = $"{node}/r/{id}/";
							runs.Add(ro.DeepClone());
						}
					}
					catch
					{
						// node unreachable; its runs just don't list this cycle
					}
				}
			}

			var sorted = new JsonArray([.. runs.OfType<JsonObject>()
				.OrderByDescending(r => r["runId"]?.GetValue<string>())
				.Select(r => (JsonNode)r.DeepClone())]);
			return Results.Content(sorted.ToJsonString(), "application/json");
		});

		// --- Runner control plane (LAN-trusted): lets `orf launch/stop/fleet` on
		// any machine drive this node without shell access. The posted spec is
		// saved into orf/specs/ (so relative promptFile paths resolve) and the
		// match runs as a detached process that survives hub restarts.
		var repoRoot = Util.FindRepoRoot();

		app.MapPost("/api/launch", async (HttpContext context) =>
		{
			using var reader = new StreamReader(context.Request.Body);
			var yaml = await reader.ReadToEndAsync(context.RequestAborted);

			var specsDir = Path.Combine(repoRoot, "orf", "specs");
			Directory.CreateDirectory(specsDir);
			var specPath = Path.Combine(specsDir, "_uploaded.tmp.yaml");
			await File.WriteAllTextAsync(specPath, yaml, context.RequestAborted);

			Spec spec;
			try
			{
				spec = Spec.Load(specPath);
			}
			catch (Exception ex)
			{
				return Results.BadRequest($"spec rejected: {ex.Message}");
			}

			var finalPath = Path.Combine(specsDir, $"{spec.Name}.uploaded.yaml");
			File.Move(specPath, finalPath, overwrite: true);

			var logsDir = Path.Combine(repoRoot, "runs", "_launch-logs");
			Directory.CreateDirectory(logsDir);
			var logPath = Path.Combine(logsDir, $"{spec.Name}-{DateTime.Now:HHmmss}.log");

			var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash")
			{
				WorkingDirectory = repoRoot,
				UseShellExecute = false,
			};
			psi.ArgumentList.Add("-c");
			psi.ArgumentList.Add($". ~/.orf-secrets 2>/dev/null; exec dotnet run --project orf --no-build -- run --spec '{finalPath}' >> '{logPath}' 2>&1");

			var process = System.Diagnostics.Process.Start(psi);
			if (process == null)
				return Results.Problem("failed to start match process");

			Util.Log("orf", $"remote launch: {spec.Name} (pid {process.Id}, log {logPath})");
			return Results.Json(new { ok = true, spec = spec.Name, pid = process.Id, log = logPath });
		});

		app.MapPost("/api/stop/{runId}", (string runId) =>
		{
			if (runId.Contains("..") || !Directory.Exists(Path.Combine(runsDir, runId)))
				return Results.NotFound("unknown run");

			File.WriteAllText(Path.Combine(runsDir, runId, "stop"), "");
			Util.Log("orf", $"remote stop requested: {runId}");
			return Results.Json(new { ok = true });
		});

		app.MapGet("/api/node", () =>
		{
			double load1 = 0;
			try
			{
				load1 = double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0]);
			}
			catch
			{
			}

			return Results.Json(new
			{
				host = Environment.MachineName,
				cores = Environment.ProcessorCount,
				load1,
			});
		});

		app.MapGet("/r/{runId}/{**path}", async (string runId, string? path, HttpContext context) =>
		{
			// Bare /r/<runId> must gain a trailing slash so the dashboard's relative URLs resolve.
			if (string.IsNullOrEmpty(path) && context.Request.Path.Value?.EndsWith('/') != true)
			{
				context.Response.Redirect($"/r/{runId}/");
				return;
			}

			// Resolve locally first (covers runs newer than the last poll), then
			// fall back to the peer map built by /api/runs.
			string? targetPrefix = null;
			if (!runId.Contains("..") && Util.TryReadJson(Path.Combine(runsDir, runId, "match.json")) is JsonObject match
				&& match["webPort"] is JsonValue portValue && portValue.TryGetValue<int>(out var target))
				targetPrefix = $"http://127.0.0.1:{target}/";
			else if (!targets.TryGetValue(runId, out targetPrefix))
				targetPrefix = null;

			if (targetPrefix == null)
			{
				context.Response.StatusCode = 404;
				await context.Response.WriteAsync("unknown run or run has no dashboard");
				return;
			}

			var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : "";
			using var upstream = new HttpRequestMessage(HttpMethod.Get, $"{targetPrefix}{path}{query}");

			HttpResponseMessage response;
			try
			{
				response = await Proxy.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
			}
			catch (HttpRequestException)
			{
				context.Response.StatusCode = 502;
				await context.Response.WriteAsync("run dashboard is not reachable (match probably ended)");
				return;
			}

			using (response)
			{
				context.Response.StatusCode = (int)response.StatusCode;
				if (response.Content.Headers.ContentType != null)
					context.Response.ContentType = response.Content.Headers.ContentType.ToString();

				// Manual copy loop with per-chunk flush so SSE and MJPEG stream through.
				await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
				var buffer = new byte[64 * 1024];
				try
				{
					int read;
					while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
					{
						await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
						await context.Response.Body.FlushAsync(context.RequestAborted);
					}
				}
				catch (Exception) when (context.RequestAborted.IsCancellationRequested)
				{
					// viewer navigated away
				}
			}
		});

		Util.Log("orf", $"hub listening on http://0.0.0.0:{port} (runs dir: {runsDir})");
		await app.StartAsync(ct);
		await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
		await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
		return 0;
	}

	// Live overview: one card per running match with a live score graph (fed by
	// that run's own /api/history + /api/live through the proxy — no video, so
	// N concurrent matches cost almost nothing to watch at once). Click a card
	// for the full dashboard; finished runs collapse into the list below.
	const string IndexHtml = """
		<!doctype html>
		<html>
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title>orf hub</title>
		<style>
		  body { font-family: system-ui, sans-serif; background: #101418; color: #dde3ea; margin: 1.2rem auto; max-width: 1180px; padding: 0 1rem; }
		  h1 { font-size: 1.2rem; margin: .2rem 0 .8rem; } a { color: #7ab8f5; text-decoration: none; }
		  h2 { font-size: .95rem; color: #93a0af; margin: 1.4rem 0 .4rem; }
		  #cards { display: flex; flex-wrap: wrap; gap: 14px; }
		  .mcard { width: 360px; background: #161c23; border: 1px solid #232a33; border-radius: 8px; padding: 10px 12px; cursor: pointer; }
		  .mcard:hover { border-color: #3b4756; }
		  .mhead { display: flex; align-items: baseline; gap: 8px; margin-bottom: 6px; }
		  .mname { font-weight: 700; font-size: .92rem; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
		  .mclock { font-family: ui-monospace, monospace; color: #9fd7ff; }
		  .malive { margin-left: auto; font-size: .78rem; color: #93a0af; white-space: nowrap; }
		  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #f33; margin-right: 4px; animation: pulse 1.2s infinite; }
		  @keyframes pulse { 50% { opacity: .35; } }
		  .mcard.over .dot { background: #ffd75e; animation: none; }
		  canvas { display: block; width: 100%; height: 130px; background: #10151b; border-radius: 4px; }
		  .legend { display: flex; flex-wrap: wrap; gap: 3px 10px; margin-top: 6px; font-size: .72rem; color: #aab6c2; }
		  .legend .dead { text-decoration: line-through; opacity: .45; }
		  .legend i { display: inline-block; width: 8px; height: 8px; border-radius: 2px; margin-right: 4px; }
		  .run { display: flex; gap: .8rem; align-items: baseline; padding: .5rem .8rem; border-bottom: 1px solid #232a33; }
		  .run:hover { background: #161c23; }
		  .badge { font-size: .72rem; padding: .1rem .45rem; border-radius: .6rem; background: #2c3540; }
		  .outcome { color: #93a0af; font-size: .85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
		</style>
		</head>
		<body>
		<h1>orf hub — matches</h1>
		<div id="cards"></div>
		<h2 id="doneHead" style="display:none">finished</h2>
		<div id="list"></div>
		<script>
		const cards = new Map(); // runId -> {el, cv, legend, clockEl, aliveEl, es, data, colors, names, dead}

		function fmtClock(s) { return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(Math.floor(s % 60)).padStart(2, "0")}`; }

		function drawMini(c) {
		  const cv = c.cv, ctx = cv.getContext("2d");
		  const dpr = window.devicePixelRatio || 1;
		  const w = cv.clientWidth, h = cv.clientHeight;
		  if (cv.width !== w * dpr) { cv.width = w * dpr; cv.height = h * dpr; }
		  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
		  ctx.clearRect(0, 0, w, h);
		  const data = c.data;
		  if (data.length < 2) return;
		  let ymax = 1000;
		  for (const row of data) for (const k in row.points) ymax = Math.max(ymax, row.points[k]);
		  const x0 = data[0].second, x1 = Math.max(data[data.length - 1].second, x0 + 60);
		  const X = s => 2 + (w - 4) * (s - x0) / (x1 - x0);
		  const Y = v => h - 3 - (h - 8) * v / ymax;
		  const slugs = new Set();
		  for (const row of data) for (const k in row.points) slugs.add(k);
		  for (const slug of slugs) {
		    ctx.beginPath();
		    ctx.strokeStyle = c.colors[slug] || "#888";
		    ctx.lineWidth = 1.4;
		    let started = false;
		    for (const row of data) {
		      if (!(slug in row.points)) continue;
		      const x = X(row.second), y = Y(row.points[slug]);
		      started ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
		      started = true;
		    }
		    ctx.stroke();
		  }
		  ctx.fillStyle = "#5a6673";
		  ctx.font = "9px ui-monospace, monospace";
		  ctx.fillText(ymax >= 1000 ? Math.round(ymax / 1000) + "k" : ymax, 4, 10);
		}

		function renderLegend(c) {
		  c.legend.innerHTML = [...Object.keys(c.names)].map(slug =>
		    `<span class="${c.dead.has(slug) ? "dead" : ""}"><i style="background:${c.colors[slug] || "#888"}"></i>${c.names[slug]}</span>`).join("");
		}

		async function addCard(runId, node) {
		  const el = document.createElement("div");
		  el.className = "mcard";
		  el.onclick = () => location.href = `r/${runId}/`;
		  const tag = node && node !== "local" ? `<span class="badge">${node}</span>` : "";
		  el.innerHTML = `<div class="mhead"><span class="dot"></span><span class="mname">${runId.replace(/^\d{8}-\d{6}-/, "")}</span>${tag}
		    <span class="mclock">--:--</span><span class="malive"></span></div><canvas></canvas><div class="legend"></div>`;
		  document.getElementById("cards").appendChild(el);
		  const c = { el, cv: el.querySelector("canvas"), legend: el.querySelector(".legend"),
		    clockEl: el.querySelector(".mclock"), aliveEl: el.querySelector(".malive"),
		    es: null, data: [], colors: {}, names: {}, dead: new Set() };
		  cards.set(runId, c);

		  try {
		    const text = await (await fetch(`r/${runId}/api/history`)).text();
		    try { c.data = JSON.parse(text); }
		    catch { c.data = (text.match(/\{"second":\d+,"points":\{[^{}]*\}\}/g) || []).map(JSON.parse); }
		  } catch (e) { c.data = []; }
		  drawMini(c);

		  c.es = new EventSource(`r/${runId}/api/live`);
		  c.es.onmessage = e => {
		    try {
		      const d = JSON.parse(e.data);
		      for (const p of d.players || []) c.names[p.slug] = p.display || p.slug;
		      const g = d.game;
		      if (g && Array.isArray(g.players)) {
		        for (const gp of g.players) {
		          if (gp.colorHex) c.colors[gp.slug] = "#" + gp.colorHex;
		          gp.winState === "Lost" ? c.dead.add(gp.slug) : c.dead.delete(gp.slug);
		        }
		        c.clockEl.textContent = fmtClock(g.second || 0);
		        c.aliveEl.textContent = `${g.players.filter(p => p.winState !== "Lost").length}/${g.players.length} alive`;
		        const last = c.data[c.data.length - 1];
		        if (!last || last.second !== g.second) {
		          const points = {};
		          for (const gp of g.players) points[gp.slug] = (gp.armyValue || 0) + (gp.assetsValue || 0);
		          c.data.push({ second: g.second, points });
		          if (c.data.length > 7200) c.data.shift();
		        }
		      }
		      if (d.finished) {
		        el.classList.add("over");
		        const winners = (d.finished.players || []).filter(p => p.winState === "Won").map(p => c.names[p.slug] || p.slug);
		        if (winners.length) c.aliveEl.textContent = `🏆 ${winners.join(", ")}`;
		        c.es.close();
		      }
		      drawMini(c); renderLegend(c);
		    } catch (err) { /* partial frame */ }
		  };
		  c.es.onerror = () => { /* match ended; next refresh() moves it to the list */ };
		}

		function dropCard(runId) {
		  const c = cards.get(runId);
		  if (!c) return;
		  if (c.es) c.es.close();
		  c.el.remove();
		  cards.delete(runId);
		}

		async function refresh() {
		  try {
		    const runs = await (await fetch("api/runs")).json();
		    const liveRuns = runs.filter(r => r.live && r.webPort);
		    const liveIds = new Set(liveRuns.map(r => r.runId));
		    for (const r of liveRuns) if (!cards.has(r.runId)) addCard(r.runId, r.node);
		    for (const id of [...cards.keys()]) if (!liveIds.has(id)) dropCard(id);

		    const done = runs.filter(r => !liveIds.has(r.runId));
		    document.getElementById("doneHead").style.display = done.length ? "" : "none";
		    document.getElementById("list").innerHTML = done.map(r => `
		      <div class="run">
		        <span class="badge">done</span>
		        ${r.node && r.node !== "local" ? `<span class="badge">${r.node}</span>` : ""}
		        ${r.webPort ? `<a href="r/${r.runId}/">${r.runId}</a>` : r.runId}
		        <span>${r.players}p</span>
		        <span class="outcome">${r.outcome ?? ""}</span>
		      </div>`).join("") || "no finished runs yet";
		  } catch (e) { /* hub restarting */ }
		}
		refresh(); setInterval(refresh, 5000);
		</script>
		</body>
		</html>
		""";
}
