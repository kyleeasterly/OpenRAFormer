using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Full match run: run dir + match.json, Xvfb/game/ffmpeg, agent loops, dashboard, teardown.</summary>
public sealed class MatchRunner(Spec spec, string specPath)
{
	readonly string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";

	Process? gameProcess;
	Process? ffmpegProcess;

	public async Task<int> RunAsync(int? portOverride, bool noGame, bool noWeb, CancellationToken ct)
	{
		var repoRoot = Util.FindRepoRoot();
		var runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{spec.Name}";
		var runDir = Path.Combine(repoRoot, "runs", runId);
		var runStartUtc = DateTime.UtcNow;

		CreateRunDir(runDir, runId);
		Util.Log("orf", $"run dir: {runDir}");

		var webApp = noWeb ? null : await WebServer.StartAsync(spec, runDir, runId, portOverride ?? spec.Web.Port);

		var logStdout = Task.CompletedTask;
		var logStderr = Task.CompletedTask;

		try
		{
			if (!noGame)
			{
				EnsureXvfb(spec);
				(gameProcess, logStdout, logStderr) = SpawnGame(repoRoot, runDir, spec);
				ffmpegProcess = SpawnFfmpeg(runDir, spec);
			}
			else
			{
				Util.Log("orf", "--no-game: skipping Xvfb/game/ffmpeg spawn");
			}

			// Agents start once the engine begins exporting state.
			var gameJson = Path.Combine(runDir, "state", "game.json");
			while (!File.Exists(gameJson))
			{
				ct.ThrowIfCancellationRequested();
				if (gameProcess != null && gameProcess.HasExited)
				{
					Util.Log("orf", $"game process exited (code {gameProcess.ExitCode}) before writing state/game.json — see {runDir}/engine.err");
					return 1;
				}

				Util.Log("orf", "waiting for state/game.json…");
				await Task.Delay(1000, ct);
			}

			Util.Log("orf", "game state detected, starting agent loops");

			using var agentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var agentTasks = spec.Players
				.Select((p, i) => new AgentLoop(runDir, spec, p, i, specDir).RunAsync(agentCts.Token))
				.ToList();

			// Wait for game over (result.json) or game process exit.
			var resultPath = Path.Combine(runDir, "result.json");
			while (!File.Exists(resultPath))
			{
				ct.ThrowIfCancellationRequested();
				if (gameProcess != null && gameProcess.HasExited)
				{
					Util.Log("orf", $"game process exited (code {gameProcess.ExitCode})");
					break;
				}

				await Task.Delay(1000, ct);
			}

			agentCts.Cancel();
			await Task.WhenAll(agentTasks).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ContinueWith(_ => { });

			StopFfmpeg();
			CopyReplay(runDir, runStartUtc);
			PrintSummary(runDir);
			return 0;
		}
		catch (OperationCanceledException)
		{
			Util.Log("orf", "interrupted, shutting down");
			return 130;
		}
		finally
		{
			KillChildren();
			await Task.WhenAny(Task.WhenAll(logStdout, logStderr), Task.Delay(3000));
			if (webApp != null)
				await webApp.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ContinueWith(_ => { });
		}
	}

	void CreateRunDir(string runDir, string runId)
	{
		Directory.CreateDirectory(Path.Combine(runDir, "state"));
		foreach (var p in spec.Players)
		{
			Directory.CreateDirectory(Path.Combine(runDir, "orders", p.Slug, "inbox"));
			Directory.CreateDirectory(Path.Combine(runDir, "orders", p.Slug, "results"));
			Directory.CreateDirectory(Path.Combine(runDir, "agents", p.Slug, "turns"));
		}

		var players = new JsonArray();
		foreach (var p in spec.Players)
			players.Add(new JsonObject
			{
				["slug"] = p.Slug,
				["display"] = p.Display,
				["bot"] = "llm",
				["faction"] = p.Faction,
				["spawn"] = p.Spawn,
				["team"] = p.Team,
			});

		var match = new JsonObject
		{
			["runId"] = runId,
			["map"] = spec.Map,
			["stateIntervalTicks"] = 25,
			["options"] = new JsonObject { ["gamespeed"] = "default" },
			["players"] = players,
		};

		Util.WriteAtomic(Path.Combine(runDir, "match.json"), match.ToJsonString(Util.Indented));
	}

	static void EnsureXvfb(Spec spec)
	{
		var socket = $"/tmp/.X11-unix/X{spec.DisplayNumber}";
		if (File.Exists(socket))
		{
			Util.Log("orf", $"Xvfb already running on {spec.Display}");
			return;
		}

		Util.Log("orf", $"starting Xvfb on {spec.Display}");
		var psi = new ProcessStartInfo("Xvfb")
		{
			UseShellExecute = false,
		};
		psi.ArgumentList.Add(spec.Display);
		psi.ArgumentList.Add("-screen");
		psi.ArgumentList.Add("0");
		psi.ArgumentList.Add($"{spec.Width}x{spec.Height}x24");
		psi.ArgumentList.Add("-nolisten");
		psi.ArgumentList.Add("tcp");

		Process.Start(psi); // intentionally left running after the match

		// Give the server a moment to create the socket.
		for (var i = 0; i < 20 && !File.Exists(socket); i++)
			Thread.Sleep(250);
	}

	(Process Game, Task LogOut, Task LogErr) SpawnGame(string repoRoot, string runDir, Spec spec)
	{
		Util.Log("orf", "starting game process");
		var psi = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = repoRoot,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("bin/OpenRA.dll");
		psi.ArgumentList.Add("Engine.EngineDir=..");
		psi.ArgumentList.Add("Game.Mod=cnc");
		psi.ArgumentList.Add("Graphics.Mode=Windowed");
		psi.ArgumentList.Add($"Graphics.WindowedSize={spec.Width},{spec.Height}");

		psi.Environment["ORF_RUN_DIR"] = runDir;
		psi.Environment["DISPLAY"] = spec.Display;
		psi.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
		psi.Environment["GALLIUM_DRIVER"] = "llvmpipe";

		var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start game process");
		var logOut = PumpAsync(process.StandardOutput.BaseStream, Path.Combine(runDir, "engine.log"));
		var logErr = PumpAsync(process.StandardError.BaseStream, Path.Combine(runDir, "engine.err"));
		return (process, logOut, logErr);
	}

	static async Task PumpAsync(Stream source, string path)
	{
		try
		{
			await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
			await source.CopyToAsync(file);
		}
		catch
		{
			// process torn down
		}
	}

	static Process SpawnFfmpeg(string runDir, Spec spec)
	{
		Util.Log("orf", "starting ffmpeg x11grab");
		var psi = new ProcessStartInfo("ffmpeg")
		{
			UseShellExecute = false,
			RedirectStandardInput = true, // lets us send 'q' for a clean stop
		};

		foreach (var arg in new[]
		{
			"-hide_banner", "-loglevel", "error", "-y",
			"-f", "x11grab",
			"-video_size", $"{spec.Width}x{spec.Height}",
			"-framerate", spec.Stream.Fps.ToString(),
			"-i", spec.Display,
			"-vf", $"scale={spec.Stream.Scale}:-1",
			"-q:v", spec.Stream.Quality.ToString(),
			"-update", "1",
			Path.Combine(runDir, "live.jpg"),
		})
			psi.ArgumentList.Add(arg);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
	}

	void StopFfmpeg()
	{
		if (ffmpegProcess == null || ffmpegProcess.HasExited)
			return;

		try
		{
			// SIGINT for a clean stop; escalate if it ignores us.
			Process.Start("kill", ["-INT", ffmpegProcess.Id.ToString()])?.WaitForExit(2000);
			if (!ffmpegProcess.WaitForExit(3000))
				ffmpegProcess.Kill(entireProcessTree: true);
		}
		catch
		{
		}
	}

	static void CopyReplay(string runDir, DateTime runStartUtc)
	{
		try
		{
			var replaysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openra", "Replays");
			if (!Directory.Exists(replaysDir))
				return;

			var newest = Directory.GetFiles(replaysDir, "*.orarep", SearchOption.AllDirectories)
				.Select(f => new FileInfo(f))
				.Where(f => f.LastWriteTimeUtc >= runStartUtc)
				.MaxBy(f => f.LastWriteTimeUtc);

			if (newest != null)
			{
				File.Copy(newest.FullName, Path.Combine(runDir, "replay.orarep"), overwrite: true);
				Util.Log("orf", $"archived replay: {newest.Name}");
			}
		}
		catch (Exception ex)
		{
			Util.Log("orf", $"replay archive failed: {ex.Message}");
		}
	}

	void PrintSummary(string runDir)
	{
		Util.Log("orf", "=== match summary ===");
		if (Util.TryReadJson(Path.Combine(runDir, "result.json")) is JsonObject result)
		{
			Util.Log("orf", $"finished at tick {result["finishedAtTick"]} ({result["second"]}s)");
			foreach (var p in result["players"] as JsonArray ?? [])
				Util.Log("orf", $"  {p?["slug"]}: {p?["winState"]}");
		}
		else
		{
			Util.Log("orf", "no result.json (game did not finish normally)");
		}

		Util.Log("orf", $"artifacts: {runDir}");
	}

	void KillChildren()
	{
		foreach (var p in new[] { ffmpegProcess, gameProcess })
		{
			try
			{
				if (p != null && !p.HasExited)
					p.Kill(entireProcessTree: true);
			}
			catch
			{
			}
		}
	}
}
