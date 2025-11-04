using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenRA.LLMHarness.Services
{
	public sealed class SessionManager
	{
		readonly string sessionsBaseDirectory;
		string? currentSessionId;
		string? currentSessionPath;
		DateTime? sessionStartTime;
		int turnCounter = 0;
		readonly string sessionMarkerFile;

		public bool IsSessionActive => currentSessionId != null;
		public string? CurrentSessionId => currentSessionId;
		public int TurnCount => turnCounter;
		public TimeSpan? SessionDuration => sessionStartTime.HasValue ? DateTime.Now - sessionStartTime.Value : null;

		public SessionManager(IOptions<LLMHarnessOptions> options)
		{
			var config = options.Value;

			// Resolve sessions directory - if relative, make it relative to project root (not bin/)
			if (Path.IsPathRooted(config.SessionsDirectory))
			{
				sessionsBaseDirectory = config.SessionsDirectory;
			}
			else
			{
				// Get project root by going up from bin/Debug/net8.0 to project directory
				var assemblyLocation = Assembly.GetExecutingAssembly().Location;
				var binDirectory = Path.GetDirectoryName(assemblyLocation);
				var projectRoot = Path.GetFullPath(Path.Combine(binDirectory!, "..", "..", ".."));
				sessionsBaseDirectory = Path.Combine(projectRoot, config.SessionsDirectory);
			}

			// Session marker file goes in watch directory
			sessionMarkerFile = Path.Combine(config.WatchDirectory, ".session_active");

			// Ensure base sessions directory exists
			if (!Directory.Exists(sessionsBaseDirectory))
			{
				Directory.CreateDirectory(sessionsBaseDirectory);
			}
		}

		public string StartSession()
		{
			if (IsSessionActive)
				throw new InvalidOperationException("A session is already active. Stop the current session before starting a new one.");

			currentSessionId = Guid.NewGuid().ToString("N")[..8]; // Short GUID for readability
			sessionStartTime = DateTime.Now;
			turnCounter = 0;

			// Create session folder: YYYYMMDD_HHMMSS_{guid}
			var timestamp = sessionStartTime.Value.ToString("yyyyMMdd_HHmmss");
			var folderName = $"{timestamp}_{currentSessionId}";
			currentSessionPath = Path.Combine(sessionsBaseDirectory, folderName);

			// Create directory structure
			Directory.CreateDirectory(currentSessionPath);
			Directory.CreateDirectory(Path.Combine(currentSessionPath, "turns"));

			// Write session marker file for game to detect
			File.WriteAllText(sessionMarkerFile, currentSessionId);

			return currentSessionId;
		}

		public void StopSession()
		{
			if (!IsSessionActive)
				return;

			// Write final session metadata
			var metadata = new
			{
				session_id = currentSessionId,
				start_time = sessionStartTime,
				end_time = DateTime.Now,
				turn_count = turnCounter,
				total_duration_seconds = SessionDuration?.TotalSeconds ?? 0
			};

			var metadataPath = Path.Combine(currentSessionPath!, "session_metadata.json");
			File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

			// Remove session marker file
			if (File.Exists(sessionMarkerFile))
			{
				File.Delete(sessionMarkerFile);
			}

			// Clear session state
			currentSessionId = null;
			currentSessionPath = null;
			sessionStartTime = null;
			turnCounter = 0;
		}

		public string SaveTurn(
			string systemPrompt,
			string userMessage,
			string assistantResponse,
			string modelName,
			string thinkingLevel,
			double llmLatencySeconds,
			double totalPipelineSeconds)
		{
			if (!IsSessionActive)
				throw new InvalidOperationException("No active session. Start a session before saving turns.");

			turnCounter++;
			var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			var turnFolderName = $"turn_{turnCounter:D3}_{timestamp}";
			var turnPath = Path.Combine(currentSessionPath!, "turns", turnFolderName);

			Directory.CreateDirectory(turnPath);

			// Save prompts and response as markdown
			File.WriteAllText(Path.Combine(turnPath, "system_prompt.md"), systemPrompt);
			File.WriteAllText(Path.Combine(turnPath, "user_message.md"), userMessage);
			File.WriteAllText(Path.Combine(turnPath, "assistant_response.md"), assistantResponse);

			// Save settings
			var settings = new
			{
				turn_number = turnCounter,
				timestamp = DateTime.Now,
				model = modelName,
				thinking_level = thinkingLevel
			};
			File.WriteAllText(
				Path.Combine(turnPath, "settings.json"),
				JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

			// Save timing
			var timing = new
			{
				timestamp = DateTime.Now,
				llm_latency_seconds = llmLatencySeconds,
				total_pipeline_seconds = totalPipelineSeconds
			};
			File.WriteAllText(
				Path.Combine(turnPath, "timing.json"),
				JsonSerializer.Serialize(timing, new JsonSerializerOptions { WriteIndented = true }));

			return turnPath;
		}

		public void CleanupOrphanedMarker()
		{
			// Called on startup to clean up marker file if harness crashed
			if (File.Exists(sessionMarkerFile) && !IsSessionActive)
			{
				File.Delete(sessionMarkerFile);
			}
		}
	}
}
