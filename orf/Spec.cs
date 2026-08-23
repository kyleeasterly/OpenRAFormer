using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orf;

public sealed class Spec
{
	public string Name { get; set; } = "";
	public string Map { get; set; } = "";
	public WebSpec Web { get; set; } = new();
	public string Display { get; set; } = ":97";
	public List<int> Resolution { get; set; } = [2540, 2540];
	public StreamSpec Stream { get; set; } = new();
	public int TurnIntervalSeconds { get; set; } = 12;
	public int MaxTurnsPerPlayer { get; set; }
	public string StateFormat { get; set; } = "json";
	public List<PlayerSpec> Players { get; set; } = [];
	public Dictionary<string, ProviderSpec> Providers { get; set; } = [];

	public int DisplayNumber => int.TryParse(Display.TrimStart(':'), out var n) ? n : 97;
	public int Width => Resolution.Count > 0 ? Resolution[0] : 2540;
	public int Height => Resolution.Count > 1 ? Resolution[1] : 2540;

	public ProviderSpec ProviderFor(PlayerSpec player)
	{
		if (!Providers.TryGetValue(player.Provider, out var provider))
			throw new InvalidOperationException($"Player '{player.Slug}' references unknown provider '{player.Provider}'");
		return provider;
	}

	public static Spec Load(string path)
	{
		var deserializer = new DeserializerBuilder()
			.WithNamingConvention(CamelCaseNamingConvention.Instance)
			.IgnoreUnmatchedProperties()
			.Build();

		var spec = deserializer.Deserialize<Spec>(File.ReadAllText(path))
			?? throw new InvalidOperationException($"Spec file '{path}' is empty");

		if (string.IsNullOrWhiteSpace(spec.Name))
			throw new InvalidOperationException("Spec is missing 'name'");
		if (spec.StateFormat is not ("json" or "markdown"))
			throw new InvalidOperationException($"Unknown stateFormat '{spec.StateFormat}' (expected 'json' or 'markdown')");
		if (spec.Players.Count == 0)
			throw new InvalidOperationException("Spec has no players");

		var dupes = spec.Players.CountBy(p => p.Slug).Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
		if (dupes.Count > 0)
			throw new InvalidOperationException($"Duplicate player slugs: {string.Join(", ", dupes)}");

		foreach (var p in spec.Players)
		{
			spec.ProviderFor(p); // validates provider references

			if (p.Advisor != null)
			{
				if (string.IsNullOrWhiteSpace(p.Advisor.Model))
					throw new InvalidOperationException($"Player '{p.Slug}' advisor is missing 'model'");
				var advisorProvider = p.Advisor.Provider ?? p.Provider;
				if (!spec.Providers.ContainsKey(advisorProvider))
					throw new InvalidOperationException($"Player '{p.Slug}' advisor references unknown provider '{advisorProvider}'");
			}
		}

		return spec;
	}
}

public sealed class WebSpec
{
	public int Port { get; set; } = 5199;
}

public sealed class StreamSpec
{
	public int Fps { get; set; } = 6;
	public int Scale { get; set; } = 1280;
	public int Quality { get; set; } = 5;
}

public sealed class PlayerSpec
{
	public string Slug { get; set; } = "";
	public string Display { get; set; } = "";
	public string Provider { get; set; } = "test";
	public string Model { get; set; } = "scripted";
	public string Faction { get; set; } = "Random";
	public int Spawn { get; set; }
	public int Team { get; set; }
	public double Temperature { get; set; } = 0.6;
	public string? PromptFile { get; set; }
	public bool RecentActions { get; set; } = true;

	/// <summary>Passed through as reasoning_effort when set (e.g. "low"/"high"/"max" for stealth/ox-alpha).</summary>
	public string? ReasoningEffort { get; set; }

	/// <summary>Completion budget per turn. Reasoning models spend this on thinking too — give them room.</summary>
	public int MaxTokens { get; set; } = 2000;

	/// <summary>Per-request timeout. High reasoning efforts can legitimately exceed the old 120s default.</summary>
	public int TimeoutSeconds { get; set; } = 120;

	/// <summary>Optional add-on module: a second model that reviews this player's recent turns
	/// asynchronously and feeds advice into its prompts. Never blocks the driver's turn loop.</summary>
	public AdvisorSpec? Advisor { get; set; }
}

/// <summary>Config for the over-the-shoulder advisor add-on.</summary>
public sealed class AdvisorSpec
{
	/// <summary>Lane label in the dashboard and modules/<name>.json filename.</summary>
	public string Name { get; set; } = "advisor";

	/// <summary>Provider key; defaults to the driver player's provider.</summary>
	public string? Provider { get; set; }

	public string Model { get; set; } = "";
	public string? ReasoningEffort { get; set; }
	public double Temperature { get; set; } = 1.0;
	public int MaxTokens { get; set; } = 16000;
	public int TimeoutSeconds { get; set; } = 300;
	public string? PromptFile { get; set; }

	/// <summary>How many recent driver turns each review covers.</summary>
	public int WindowTurns { get; set; } = 10;

	/// <summary>Minimum new driver turns before the next review fires (advisor is otherwise idle).</summary>
	public int MinNewTurns { get; set; } = 3;
}

public sealed class ProviderSpec
{
	public string BaseUrl { get; set; } = "";
	public string ApiKeyEnv { get; set; } = "";

	public bool IsTest => string.IsNullOrEmpty(BaseUrl);
}
