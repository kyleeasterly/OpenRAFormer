using System.Text;
using System.Text.Json;

namespace OpenRA.LLMHarness.Services
{
	public sealed class OllamaService
	{
		private const string WatchDirectory = @"C:\OpenRATest";
		private const string LogDirectory = @"C:\OpenRATest\LLM_Coach_Logs";
		private const string OllamaApiUrl = "http://localhost:11434/api/generate";
		private const string ModelName = "gemma3:27b";

		private readonly HttpClient httpClient;
		private readonly HashSet<string> processedFiles = [];
		private readonly JsonSerializerOptions jsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
		private bool verboseMode = false;
		private string? currentLogFile;

		// Queue management for LLM processing
		private bool isProcessingLLM = false;
		private string? pendingFile = null;
		private readonly object processLock = new();
		private CancellationTokenSource? shutdownCts;

		// Events for UI updates
		public event Func<string, Task>? OnResponseChunk;
		public event Func<LLMResponse, Task>? OnResponseComplete;
		public event Func<string, Task>? OnStatusUpdate;

		public OllamaService(HttpClient httpClient)
		{
			this.httpClient = httpClient;
			this.httpClient.Timeout = TimeSpan.FromMinutes(5);
		}

		public async Task<bool> InitializeAsync()
		{
			shutdownCts = new CancellationTokenSource();

			// Create directories if they don't exist
			if (!Directory.Exists(WatchDirectory))
			{
				Directory.CreateDirectory(WatchDirectory);
			}

			if (!Directory.Exists(LogDirectory))
			{
				Directory.CreateDirectory(LogDirectory);
			}

			// Initialize log file for this session
			var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			currentLogFile = Path.Combine(LogDirectory, $"llm_coach_log_{timestamp}.txt");

			await NotifyStatusAsync($"Logging to: {currentLogFile}");
			await LogToFileAsync($"=== OpenRA LLM Coach Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
			await LogToFileAsync($"Model: {ModelName}");
			await LogToFileAsync($"Watch Directory: {WatchDirectory}");
			await LogToFileAsync("");

			// Test Ollama API connectivity
			return await TestOllamaConnectionAsync();
		}

		public void StartWatching()
		{
			// Set up file watcher
			var watcher = new FileSystemWatcher(WatchDirectory);
			watcher.Filter = "*.txt";
			watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
			watcher.Created += OnNewFile;
			watcher.Changed += OnNewFile;
			watcher.EnableRaisingEvents = true;

			_ = NotifyStatusAsync($"Monitoring directory: {WatchDirectory}");

			// Process any existing files
			_ = ProcessExistingFilesAsync();
		}

		public async Task StopAsync()
		{
			shutdownCts?.Cancel();

			// Wait for any ongoing LLM processing to complete
			for (var waitCount = 0; isProcessingLLM && waitCount < 30; waitCount++)
			{
				await NotifyStatusAsync("Waiting for LLM processing to complete...");
				await Task.Delay(1000);
			}

			if (isProcessingLLM)
			{
				await NotifyStatusAsync("Warning: Shutting down while LLM is still processing.");
			}

			await LogToFileAsync($"\n=== Session Ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
		}

		private async Task<bool> TestOllamaConnectionAsync()
		{
			try
			{
				await NotifyStatusAsync("Testing Ollama API connection...");
				var testRequest = new
				{
					model = ModelName,
					prompt = "Say 'OK' if you're working.",
					stream = false
				};

				var json = JsonSerializer.Serialize(testRequest, jsonOptions);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				var response = await httpClient.PostAsync(OllamaApiUrl, content);
				if (response.IsSuccessStatusCode)
				{
					await NotifyStatusAsync("Ollama API is responding!");
					return true;
				}
				else
				{
					await NotifyStatusAsync($"Ollama API returned error: {response.StatusCode}");
					return false;
				}
			}
			catch (Exception ex)
			{
				await NotifyStatusAsync($"Failed to connect to Ollama API: {ex.Message}");
				return false;
			}
		}

		private async Task ProcessExistingFilesAsync()
		{
			try
			{
				var files = Directory.GetFiles(WatchDirectory, "*.txt");
				if (files.Length > 0)
				{
					// Process only the most recent file
					var mostRecent = files.OrderByDescending(File.GetCreationTime).First();
					await ProcessFileAsync(mostRecent);
				}
			}
			catch (Exception ex)
			{
				await NotifyStatusAsync($"Error processing existing files: {ex.Message}");
			}
		}

		private void OnNewFile(object sender, FileSystemEventArgs e)
		{
			if (e.FullPath != null && (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed))
			{
				// Small delay to ensure file is fully written, then process async
				Task.Delay(500).ContinueWith(async _ => await ProcessFileAsync(e.FullPath));
			}
		}

		private async Task ProcessFileAsync(string filePath)
		{
			// Check if we're shutting down
			if (shutdownCts?.Token.IsCancellationRequested ?? true)
				return;

			// Skip if already processed
			lock (processedFiles)
			{
				if (processedFiles.Contains(filePath))
					return;
				processedFiles.Add(filePath);
			}

			// Check if LLM is currently processing
			lock (processLock)
			{
				if (isProcessingLLM)
				{
					// Queue this file as the latest pending
					pendingFile = filePath;
					_ = NotifyStatusAsync($"LLM is busy. Queued file: {Path.GetFileName(filePath)}");
					return;
				}

				isProcessingLLM = true;
			}

			try
			{
				var separator = new string('=', 80);
				await NotifyStatusAsync($"Processing file: {Path.GetFileName(filePath)}");

				await LogToFileAsync($"\n{separator}");
				await LogToFileAsync($"Processing file: {Path.GetFileName(filePath)} at {DateTime.Now:HH:mm:ss}");
				await LogToFileAsync(separator);

				// Read the game state with retry logic for file access
				var gameState = "";
				var retryCount = 0;
				while (retryCount < 3)
				{
					try
					{
						gameState = await File.ReadAllTextAsync(filePath);
						break;
					}
					catch (IOException) when (retryCount < 2)
					{
						retryCount++;
						await Task.Delay(100);
					}
				}

				if (string.IsNullOrEmpty(gameState))
				{
					await NotifyStatusAsync("Failed to read game state file.");
					lock (processLock)
					{
						isProcessingLLM = false;
					}

					await ProcessPendingFileAsync();
					return;
				}

				// Skip menu/lobby states
				if (gameState.Contains("Blank Shellmap") || gameState.Contains("Map: Shellmap") ||
					!gameState.Contains("Resource Cells:"))
				{
					await NotifyStatusAsync("Skipping menu/lobby state.");
					lock (processLock)
					{
						isProcessingLLM = false;
					}

					await ProcessPendingFileAsync();
					return;
				}

				await NotifyStatusAsync($"Read {gameState.Length} characters from file.");

				// Construct the prompt
				var prompt = BuildPrompt(gameState);
				await NotifyStatusAsync($"Built prompt with {prompt.Length} characters.");

				// Always log the full prompt to file
				await LogToFileAsync("\n=== FULL PROMPT TO LLM ===");
				await LogToFileAsync(prompt);
				await LogToFileAsync("=== END OF PROMPT ===\n");

				// Display full prompt in verbose mode
				if (verboseMode)
				{
					await NotifyStatusAsync("Full prompt logged to file.");
				}

				// Send to Ollama API with streaming
				await StreamOllamaResponseAsync(prompt, gameState);
			}
			catch (Exception ex)
			{
				await NotifyStatusAsync($"Error processing file {filePath}: {ex.Message}");
			}
			finally
			{
				// Always release the processing lock and check for pending files
				lock (processLock)
				{
					isProcessingLLM = false;
				}

				await ProcessPendingFileAsync();
			}
		}

		private async Task ProcessPendingFileAsync()
		{
			string? fileToProcess = null;

			lock (processLock)
			{
				if (pendingFile != null && !isProcessingLLM)
				{
					fileToProcess = pendingFile;
					pendingFile = null;
				}
			}

			if (fileToProcess != null)
			{
				await NotifyStatusAsync($"Processing pending file: {Path.GetFileName(fileToProcess)}");
				await ProcessFileAsync(fileToProcess);
			}
		}

		private async Task StreamOllamaResponseAsync(string prompt, string gameState)
		{
			try
			{
				// Check if we're shutting down
				if (shutdownCts?.Token.IsCancellationRequested ?? true)
					return;

				await NotifyStatusAsync("Sending prompt to Ollama API (streaming)...");

				await LogToFileAsync("Sending prompt to Ollama API...");
				await LogToFileAsync("\n=== LLM RESPONSE ===");

				var responseBuilder = new StringBuilder();
				var startTime = DateTime.Now;

				var request = new
				{
					model = ModelName,
					prompt = prompt,
					stream = true
				};

				var json = JsonSerializer.Serialize(request, jsonOptions);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				using var requestMessage = new HttpRequestMessage(HttpMethod.Post, OllamaApiUrl)
				{
					Content = content
				};

				using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
				response.EnsureSuccessStatusCode();

				await using var stream = await response.Content.ReadAsStreamAsync();
				using var reader = new StreamReader(stream);

				while (!reader.EndOfStream)
				{
					var line = await reader.ReadLineAsync();
					if (!string.IsNullOrWhiteSpace(line))
					{
						try
						{
							var responseObj = JsonSerializer.Deserialize<OllamaResponse>(line, jsonOptions);
							if (responseObj != null && !string.IsNullOrEmpty(responseObj.Response))
							{
								// Stream the response chunk
								responseBuilder.Append(responseObj.Response);
								if (OnResponseChunk != null)
									await OnResponseChunk(responseObj.Response);
							}

							if (responseObj?.Done == true)
							{
								// Log the complete response
								await LogToFileAsync(responseBuilder.ToString());
								await LogToFileAsync("===================");

								var seconds = responseObj.TotalDuration > 0
									? responseObj.TotalDuration / 1_000_000_000.0
									: (DateTime.Now - startTime).TotalSeconds;

								await LogToFileAsync($"Generation completed in {seconds:F2} seconds");

								// Notify completion
								if (OnResponseComplete != null)
								{
									var llmResponse = new LLMResponse
									{
										Id = Guid.NewGuid().ToString(),
										Timestamp = startTime,
										GameState = gameState,
										Response = responseBuilder.ToString(),
										DurationSeconds = seconds
									};
									await OnResponseComplete(llmResponse);
								}

								break;
							}
						}
						catch (JsonException ex)
						{
							await NotifyStatusAsync($"Error parsing response: {ex.Message}");
						}
					}
				}
			}
			catch (Exception ex)
			{
				await NotifyStatusAsync($"Error communicating with Ollama API: {ex.Message}");
			}
		}

		private string BuildPrompt(string gameState)
		{
			var sb = new StringBuilder();
			var strategyGuidePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CnC_Strategy_Guide.txt");

			// First try the local directory
			if (!File.Exists(strategyGuidePath))
			{
				// Try the bin directory where the DLL is located
				strategyGuidePath = Path.Combine(Path.GetDirectoryName(typeof(OllamaService).Assembly.Location) ?? "", "CnC_Strategy_Guide.txt");
			}

			// Also try the project source directory
			if (!File.Exists(strategyGuidePath))
			{
				strategyGuidePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "CnC_Strategy_Guide.txt");
				if (File.Exists(strategyGuidePath))
					strategyGuidePath = Path.GetFullPath(strategyGuidePath);
			}

			if (!File.Exists(strategyGuidePath))
			{
				throw new FileNotFoundException("CRITICAL: CnC_Strategy_Guide.txt not found! Searched in:\n" +
					$"- {AppDomain.CurrentDomain.BaseDirectory}\n" +
					$"- {Path.GetDirectoryName(typeof(OllamaService).Assembly.Location)}\n" +
					"- Project source directory");
			}

			// Load strategy guide - this is REQUIRED
			string? strategyGuide;
			try
			{
				strategyGuide = File.ReadAllText(strategyGuidePath);
				_ = NotifyStatusAsync($"Loaded strategy guide from: {strategyGuidePath} ({strategyGuide.Length} characters)");
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException($"CRITICAL: Failed to load strategy guide from {strategyGuidePath}: {ex.Message}", ex);
			}

			sb.AppendLine("You are a helpful OpenRA Command & Conquer strategy game coach. Analyze the current game state and give advice to help the player win.");
			sb.AppendLine("Consider the economy, military strength, map control, and immediate threats.");
			sb.AppendLine("Give specific, actionable advice about what to do next.");
			sb.AppendLine("Keep your response concise and focused on the most important next steps.");
			sb.AppendLine();

			sb.AppendLine("<game_knowledge>");
			sb.AppendLine(strategyGuide);
			sb.AppendLine("</game_knowledge>");
			sb.AppendLine();

			sb.AppendLine("<game_state>");
			sb.AppendLine(gameState);
			sb.AppendLine("</game_state>");
			sb.AppendLine();
			sb.AppendLine("Based on the game knowledge above and current state, what should the player do next?");

			return sb.ToString();
		}

		private async Task LogToFileAsync(string message)
		{
			if (string.IsNullOrEmpty(currentLogFile))
				return;

			try
			{
				await File.AppendAllTextAsync(currentLogFile, message + Environment.NewLine);
			}
			catch (Exception)
			{
				// Silently fail log writes
			}
		}

		private async Task NotifyStatusAsync(string message)
		{
			if (OnStatusUpdate != null)
				await OnStatusUpdate(message);
		}

		public bool VerboseMode
		{
			get => verboseMode;
			set => verboseMode = value;
		}
	}

	public sealed class LLMResponse
	{
		public required string Id { get; init; }
		public required DateTime Timestamp { get; init; }
		public required string GameState { get; init; }
		public required string Response { get; init; }
		public required double DurationSeconds { get; init; }
	}

	sealed class OllamaResponse
	{
		public string? Model { get; set; }
		public string? Response { get; set; }
		public bool Done { get; set; }
		public long TotalDuration { get; set; }
	}
}
