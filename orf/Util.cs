using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orf;

public static class Util
{
	public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>Atomic write per PROTOCOL: write &lt;name&gt;.tmp in the same dir, then rename.</summary>
	public static void WriteAtomic(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, content);
		File.Move(tmp, path, overwrite: true);
	}

	public static JsonNode? TryReadJson(string path)
	{
		try
		{
			if (!File.Exists(path))
				return null;
			var text = File.ReadAllText(path);
			return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
		}
		catch
		{
			return null;
		}
	}

	public static string FindRepoRoot()
	{
		var d = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (d != null)
		{
			if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
				return d.FullName;
			d = d.Parent;
		}

		return Directory.GetCurrentDirectory();
	}

	/// <summary>
	/// Resolve a runtime asset (prompts/, wwwroot/) relative to the binary,
	/// falling back to the orf source directory.
	/// </summary>
	public static string AssetPath(string relative)
	{
		var candidates = new[]
		{
			Path.Combine(AppContext.BaseDirectory, relative),
			Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", relative)),
			Path.Combine(FindRepoRoot(), "orf", relative),
		};

		foreach (var c in candidates)
			if (File.Exists(c) || Directory.Exists(c))
				return c;

		return candidates[0];
	}

	/// <summary>
	/// Resolve a user-supplied input path. `dotnet run --project orf` sets the app cwd to the
	/// project dir, so repo-root-relative paths get a fallback resolution against the repo root.
	/// </summary>
	public static string ResolveInputPath(string path)
	{
		if (Path.IsPathRooted(path) || File.Exists(path) || Directory.Exists(path))
			return path;

		var fromRoot = Path.Combine(FindRepoRoot(), path);
		return File.Exists(fromRoot) || Directory.Exists(fromRoot) ? fromRoot : path;
	}

	public static void Log(string tag, string message)
	{
		Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{tag}] {message}");
	}
}
