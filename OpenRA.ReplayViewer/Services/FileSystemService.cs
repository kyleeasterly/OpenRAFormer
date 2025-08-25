using System.Collections.Concurrent;
using OpenRA;
using OpenRA.FileSystem;
using OpenRA.Mods.Cnc.FileSystem;
using OpenRA.Primitives;

namespace OpenRA.ReplayViewer.Services;

public class FileSystemService
{
	private readonly ILogger<FileSystemService> logger;
	private readonly IWebHostEnvironment environment;
	private readonly ConcurrentDictionary<string, OpenRA.FileSystem.FileSystem> modFileSystems = new();
	private bool loggedDesPaths = false;

	public FileSystemService(ILogger<FileSystemService> logger, IWebHostEnvironment environment)
	{
		this.logger = logger;
		this.environment = environment;
	}

	public OpenRA.FileSystem.FileSystem GetOrCreateFileSystem(string modId)
	{
		return modFileSystems.GetOrAdd(modId, CreateFileSystem);
	}

	private OpenRA.FileSystem.FileSystem CreateFileSystem(string modId)
	{
		logger.LogInformation("Creating filesystem for mod: {ModId}", modId);
		
		var gameRoot = Path.Combine(environment.ContentRootPath, "..");
		var modPath = Path.Combine(gameRoot, "mods", modId.ToLowerInvariant());
		
		if (!Directory.Exists(modPath))
		{
			logger.LogError("Mod directory not found: {ModPath}", modPath);
			throw new DirectoryNotFoundException($"Mod directory not found: {modPath}");
		}

		var packages = new List<OpenRA.FileSystem.IReadOnlyPackage>();
		
		try
		{
			// Add the mod directory itself as a folder package
			packages.Add(new OpenRA.FileSystem.Folder(modPath));
			logger.LogInformation("Added mod folder: {ModPath}", modPath);
			
			// Look for common directories that might contain assets
			var commonPath = Path.Combine(gameRoot, "mods", "common");
			if (Directory.Exists(commonPath))
			{
				packages.Add(new OpenRA.FileSystem.Folder(commonPath));
				logger.LogInformation("Added common folder: {CommonPath}", commonPath);
			}
			
			// Find and load MIX files in the mod directory
			var mixFiles = Directory.GetFiles(modPath, "*.mix", SearchOption.AllDirectories);
			foreach (var mixFile in mixFiles)
			{
				try
				{
					// Load the MIX file
					var stream = File.OpenRead(mixFile);
					var mix = new MixLoader.MixFile(stream, mixFile, Array.Empty<string>());
					packages.Add(mix);
					logger.LogInformation("Loaded MIX file: {MixFile}", 
						Path.GetFileName(mixFile));
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Failed to load MIX file: {MixFile}", mixFile);
				}
			}
			
			// Look for additional content packages in multiple locations
			var contentPaths = new[]
			{
				Path.Combine(gameRoot, "mods", $"{modId}-content"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA", "Content", modId),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenRA", "Content", modId),
				Path.Combine(gameRoot, "Content", modId)
			};
			
			foreach (var contentPath in contentPaths)
			{
				if (!Directory.Exists(contentPath))
				{
					logger.LogDebug("Content path not found: {Path}", contentPath);
					continue;
				}
				
				packages.Add(new OpenRA.FileSystem.Folder(contentPath));
				logger.LogInformation("Added content folder: {ContentPath}", contentPath);
				
				// Load MIX files from content directory - especially important ones
				var importantMixFiles = new[] { "desert.mix", "temperat.mix", "winter.mix", "conquer.mix", "tempicnh.mix" };
				foreach (var mixName in importantMixFiles)
				{
					var mixPath = Path.Combine(contentPath, mixName);
					if (File.Exists(mixPath))
					{
						try
						{
							var stream = File.OpenRead(mixPath);
							var mix = new MixLoader.MixFile(stream, mixPath, Array.Empty<string>());
							packages.Add(mix);
							logger.LogInformation("Loaded content MIX file: {MixFile} ({Count} files)", 
								Path.GetFileName(mixPath), mix.Contents.Count());
						}
						catch (Exception ex)
						{
							logger.LogWarning(ex, "Failed to load content MIX file: {MixFile}", mixPath);
						}
					}
				}
				
				// Also load any other MIX files
				var contentMixFiles = Directory.GetFiles(contentPath, "*.mix", SearchOption.AllDirectories);
				foreach (var mixFile in contentMixFiles)
				{
					// Skip if already loaded
					if (importantMixFiles.Any(m => mixFile.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
						continue;
					
					try
					{
						// Load the MIX file
						var stream = File.OpenRead(mixFile);
						var mix = new MixLoader.MixFile(stream, mixFile, Array.Empty<string>());
						packages.Add(mix);
						logger.LogInformation("Loaded additional content MIX file: {MixFile}", 
							Path.GetFileName(mixFile));
					}
					catch (Exception ex)
					{
						logger.LogWarning(ex, "Failed to load content MIX file: {MixFile}", mixFile);
					}
				}
			}
			
			// For RA, also check for REDALERT.MIX in the game files
			if (modId.Equals("ra", StringComparison.OrdinalIgnoreCase))
			{
				var redalertMix = Path.Combine(modPath, "packages", "redalert.mix");
				if (File.Exists(redalertMix))
				{
					try
					{
						// MixFile loading disabled for now
						// var mix = new MixFile(File.OpenRead(redalertMix), redalertMix, null);
						// packages.Add(mix);
						logger.LogInformation("Found REDALERT.MIX (loading disabled)");
					}
					catch (Exception ex)
					{
						logger.LogWarning(ex, "Failed to load REDALERT.MIX");
					}
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to create filesystem for mod {ModId}", modId);
			throw;
		}

		// Create a simple in-memory filesystem (FileSystem constructor requires parameters we don't have)
		var fileSystem = new OpenRA.FileSystem.FileSystem(
			modId,
			new Dictionary<string, Manifest>(),
			Array.Empty<IPackageLoader>());
		foreach (var package in packages)
		{
			fileSystem.Mount(package);
		}
		
		logger.LogInformation("Created filesystem for mod {ModId} with {Count} packages", 
			modId, packages.Count);
		
		// Log package count for debugging
		logger.LogInformation("FileSystem created with {Count} packages", packages.Count);
		
		return fileSystem;
	}

	public Stream? OpenFile(string modId, string filename)
	{
		try
		{
			var fs = GetOrCreateFileSystem(modId);
			if (fs.Exists(filename))
			{
				logger.LogDebug("Opening file: {Filename}", filename);
				return fs.Open(filename);
			}
			
			// Try with different extensions
			var baseFilename = Path.GetFileNameWithoutExtension(filename);
			var extensions = new[] { ".tem", ".shp", ".png", ".pal", ".mix" };
			
			foreach (var ext in extensions)
			{
				var tryFilename = baseFilename + ext;
				if (fs.Exists(tryFilename))
				{
					logger.LogDebug("Opening file with extension: {Filename}", tryFilename);
					return fs.Open(tryFilename);
				}
			}
			
			// Log detailed debugging info on first .des file miss
			if (filename.EndsWith(".des") && !loggedDesPaths)
			{
				loggedDesPaths = true;
				logger.LogWarning("=== ASSET SEARCH DEBUG INFO ===");
				logger.LogWarning("Looking for: {Filename}", filename);
				logger.LogWarning("Current directory: {Dir}", Directory.GetCurrentDirectory());
				logger.LogWarning("Content root: {Root}", environment.ContentRootPath);
				logger.LogWarning("Mod folder searched: {ModPath}", Path.Combine(environment.ContentRootPath, "..", "mods", modId.ToLowerInvariant()));
				
				// List all mounted packages
				var mountedCount = 0;
				foreach (var pkg in fs.MountedPackages)
				{
					mountedCount++;
					var fileCount = pkg.Contents.Count();
					logger.LogWarning("Package {Index}: {Name} ({Count} files)", mountedCount, pkg.Name, fileCount);
					
					// Show first few terrain files in this package
					var terrainFiles = pkg.Contents
						.Where(f => f.EndsWith(".tem") || f.EndsWith(".des") || f.Contains("clear") || f.Contains("s01"))
						.Take(5)
						.ToList();
					
					if (terrainFiles.Any())
					{
						logger.LogWarning("  Sample terrain files: {Files}", string.Join(", ", terrainFiles));
					}
				}
				
				// Try to find any file with "clear" or "s01" in the name
				var searchTerms = new[] { "clear", "s01", "w1" };
				foreach (var term in searchTerms)
				{
					var matches = fs.MountedPackages
						.SelectMany(p => p.Contents)
						.Where(f => f.Contains(term, StringComparison.OrdinalIgnoreCase))
						.Take(3)
						.ToList();
					
					if (matches.Any())
					{
						logger.LogWarning("Files containing '{Term}': {Files}", term, string.Join(", ", matches));
					}
				}
				
				logger.LogWarning("=== END DEBUG INFO ===");
			}
			
			logger.LogWarning("File not found in filesystem: {Filename}", filename);
			return null;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to open file {Filename} for mod {ModId}", filename, modId);
			return null;
		}
	}

	public bool FileExists(string modId, string filename)
	{
		try
		{
			var fs = GetOrCreateFileSystem(modId);
			return fs.Exists(filename);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to check file existence {Filename} for mod {ModId}", filename, modId);
			return false;
		}
	}

	public IEnumerable<string> GetAvailableFiles(string modId, string pattern = "*")
	{
		try
		{
			var fs = GetOrCreateFileSystem(modId);
			// Note: IReadOnlyFileSystem doesn't expose AllFileNames
			// Would need to enumerate files differently
			return Enumerable.Empty<string>();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to get available files for mod {ModId}", modId);
			return Enumerable.Empty<string>();
		}
	}
}