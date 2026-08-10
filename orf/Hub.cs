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

	public static async Task<int> RunAsync(int port, CancellationToken ct)
	{
		var runsDir = Path.Combine(Util.FindRepoRoot(), "runs");

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = AppContext.BaseDirectory,
			EnvironmentName = "Production",
		});
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

		var app = builder.Build();

		app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

		app.MapGet("/api/runs", () =>
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

				runs.Add(new JsonObject
				{
					["runId"] = Path.GetFileName(dir),
					["players"] = (match["players"] as JsonArray)?.Count ?? 0,
					["live"] = live,
					["webPort"] = match["webPort"]?.DeepClone(),
					["outcome"] = outcome,
				});
			}

			return Results.Content(runs.ToJsonString(), "application/json");
		});

		app.MapGet("/r/{runId}/{**path}", async (string runId, string? path, HttpContext context) =>
		{
			// Bare /r/<runId> must gain a trailing slash so the dashboard's relative URLs resolve.
			if (string.IsNullOrEmpty(path) && context.Request.Path.Value?.EndsWith('/') != true)
			{
				context.Response.Redirect($"/r/{runId}/");
				return;
			}

			if (runId.Contains("..") || Util.TryReadJson(Path.Combine(runsDir, runId, "match.json")) is not JsonObject match
				|| match["webPort"] is not JsonValue portValue || !portValue.TryGetValue<int>(out var target))
			{
				context.Response.StatusCode = 404;
				await context.Response.WriteAsync("unknown run or run has no dashboard");
				return;
			}

			var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : "";
			using var upstream = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{target}/{path}{query}");

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

	const string IndexHtml = """
		<!doctype html>
		<html>
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title>orf hub</title>
		<style>
		  body { font-family: system-ui, sans-serif; background: #101418; color: #dde3ea; margin: 2rem auto; max-width: 720px; padding: 0 1rem; }
		  h1 { font-size: 1.3rem; } a { color: #7ab8f5; text-decoration: none; }
		  .run { display: flex; gap: .8rem; align-items: baseline; padding: .55rem .8rem; border-bottom: 1px solid #232a33; }
		  .run:hover { background: #161c23; }
		  .badge { font-size: .72rem; padding: .1rem .45rem; border-radius: .6rem; background: #2c3540; }
		  .live { background: #14532d; color: #6ee7a0; }
		  .outcome { color: #93a0af; font-size: .85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
		</style>
		</head>
		<body>
		<h1>orf hub — matches</h1>
		<div id="list">loading…</div>
		<script>
		async function refresh() {
		  try {
		    const runs = await (await fetch('api/runs')).json();
		    document.getElementById('list').innerHTML = runs.map(r => `
		      <div class="run">
		        <span class="badge ${r.live ? 'live' : ''}">${r.live ? 'LIVE' : 'done'}</span>
		        ${r.live && r.webPort ? `<a href="r/${r.runId}/">${r.runId}</a>` : r.runId}
		        <span>${r.players}p</span>
		        <span class="outcome">${r.outcome ?? ''}</span>
		      </div>`).join('') || 'no runs yet';
		  } catch (e) { /* hub restarting */ }
		}
		refresh(); setInterval(refresh, 5000);
		</script>
		</body>
		</html>
		""";
}
