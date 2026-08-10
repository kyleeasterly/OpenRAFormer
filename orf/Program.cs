using System.Text.Json.Nodes;
using Orf;

const string Usage =
	"""
	orf — OpenRAFormer2 orchestrator

	Usage:
	  orf run --spec <path> [--port N] [--no-game] [--no-web]
	      Run a full match: run dir + match.json, Xvfb/game/ffmpeg (unless --no-game),
	      agent loops, and the web dashboard (unless --no-web).

	  orf agent-turn --spec <path> --slug <slug> --state <fixture.json> --outdir <dir>
	      Execute exactly one agent turn offline against a state fixture. Writes the
	      inbox order file and turn artifacts under <dir>.

	  orf stop [runId]
	      Request a clean shutdown of a running match by writing runs/<id>/stop.
	      Without runId, targets the newest run that has not finished.

	  orf hub [--port N]
	      Landing page over all runs on one fixed port (default 5100). Lists live and
	      finished matches and proxies each run's dashboard under /r/<runId>/, so a
	      single tunnel reaches everything. Only the open dashboard streams video.
	""";

if (args.Length == 0)
{
	Console.WriteLine(Usage);
	return 1;
}

try
{
	switch (args[0])
	{
		case "run":
			return await RunCommand(args[1..]);
		case "agent-turn":
			return await AgentTurnCommand(args[1..]);
		case "stop":
			return StopCommand(args[1..]);
		case "hub":
			return await HubCommand(args[1..]);
		case "-h" or "--help" or "help":
			Console.WriteLine(Usage);
			return 0;
		default:
			Console.Error.WriteLine($"Unknown command '{args[0]}'\n");
			Console.WriteLine(Usage);
			return 1;
	}
}
catch (Exception ex)
{
	Console.Error.WriteLine($"orf: {ex.Message}");
	return 1;
}

static async Task<int> RunCommand(string[] args)
{
	string? specPath = null;
	int? port = null;
	var noGame = false;
	var noWeb = false;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--port":
				port = int.Parse(Expect(args, ref i, "--port"));
				break;
			case "--no-game":
				noGame = true;
				break;
			case "--no-web":
				noWeb = true;
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'run'");
		}
	}

	if (specPath == null)
		throw new ArgumentException("run requires --spec <path>");

	specPath = Util.ResolveInputPath(specPath);
	var spec = Spec.Load(specPath);

	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		Util.Log("orf", "Ctrl+C received");
		cts.Cancel();
	};

	// `dotnet run` swallows console SIGINT, and a plain `kill` (SIGTERM) would skip
	// every finally block — either way children leak. Catch both and cancel instead.
	using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
		{
			ctx.Cancel = true;
			Util.Log("orf", "SIGTERM received");
			cts.Cancel();
		});
	using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGINT, ctx =>
		{
			ctx.Cancel = true;
			cts.Cancel();
		});

	return await new MatchRunner(spec, specPath).RunAsync(port, noGame, noWeb, cts.Token);
}

static async Task<int> AgentTurnCommand(string[] args)
{
	string? specPath = null, slug = null, statePath = null, outDir = null;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "--spec":
				specPath = Expect(args, ref i, "--spec");
				break;
			case "--slug":
				slug = Expect(args, ref i, "--slug");
				break;
			case "--state":
				statePath = Expect(args, ref i, "--state");
				break;
			case "--outdir":
				outDir = Expect(args, ref i, "--outdir");
				break;
			default:
				throw new ArgumentException($"Unknown option '{args[i]}' for 'agent-turn'");
		}
	}

	if (specPath == null || slug == null || statePath == null || outDir == null)
		throw new ArgumentException("agent-turn requires --spec, --slug, --state and --outdir");

	specPath = Util.ResolveInputPath(specPath);
	statePath = Util.ResolveInputPath(statePath);
	var spec = Spec.Load(specPath);
	var player = spec.Players.FirstOrDefault(p => p.Slug == slug)
		?? throw new ArgumentException($"No player with slug '{slug}' in spec");

	var state = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject
		?? throw new ArgumentException($"State fixture '{statePath}' is not a JSON object");

	outDir = Path.GetFullPath(outDir);
	Directory.CreateDirectory(outDir);

	var specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";
	var index = spec.Players.IndexOf(player);
	var loop = new AgentLoop(outDir, spec, player, index, specDir);

	await loop.TakeTurnAsync(state, [], CancellationToken.None);

	Util.Log("orf", $"agent-turn complete; artifacts under {outDir}");
	return 0;
}

static async Task<int> HubCommand(string[] args)
{
	var port = 5100;
	for (var i = 0; i < args.Length; i++)
	{
		if (args[i] == "--port")
			port = int.Parse(Expect(args, ref i, "--port"));
		else
			throw new ArgumentException($"Unknown option '{args[i]}' for 'hub'");
	}

	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
	using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
		System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

	return await Hub.RunAsync(port, cts.Token);
}

static int StopCommand(string[] args)
{
	var runsDir = Path.Combine(Util.FindRepoRoot(), "runs");
	string? runDir = null;

	if (args.Length > 0)
	{
		runDir = Path.Combine(runsDir, args[0]);
		if (!Directory.Exists(runDir))
			throw new ArgumentException($"No run dir '{runDir}'");
	}
	else
	{
		runDir = Directory.GetDirectories(runsDir)
			.Where(d => !File.Exists(Path.Combine(d, "result.json")))
			.OrderByDescending(Path.GetFileName)
			.FirstOrDefault()
			?? throw new ArgumentException("No unfinished run found to stop");
	}

	File.WriteAllText(Path.Combine(runDir, "stop"), $"requested {DateTime.UtcNow:o}\n");
	Util.Log("orf", $"stop requested for {Path.GetFileName(runDir)} (takes effect within ~1s if that match is live)");
	return 0;
}

static string Expect(string[] args, ref int i, string flag)
{
	if (i + 1 >= args.Length)
		throw new ArgumentException($"{flag} requires a value");
	return args[++i];
}
