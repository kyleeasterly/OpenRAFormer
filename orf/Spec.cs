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
		if (spec.Players.Count == 0)
			throw new InvalidOperationException("Spec has no players");

		var dupes = spec.Players.CountBy(p => p.Slug).Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
		if (dupes.Count > 0)
			throw new InvalidOperationException($"Duplicate player slugs: {string.Join(", ", dupes)}");

		foreach (var p in spec.Players)
			spec.ProviderFor(p); // validates provider references

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
}

public sealed class ProviderSpec
{
	public string BaseUrl { get; set; } = "";
	public string ApiKeyEnv { get; set; } = "";

	public bool IsTest => string.IsNullOrEmpty(BaseUrl);
}
