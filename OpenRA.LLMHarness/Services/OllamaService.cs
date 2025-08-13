using System.Text;
using System.Text.Json;

namespace OpenRA.LLMHarness.Services
{
	public sealed class OllamaService
	{
		private const string WatchDirectory = @"C:\OpenRATest";
		private const string LogDirectory = @"C:\OpenRATest\LLM_Coach_Logs";
		private const string OllamaApiUrl = "http://localhost:11434/api/generate";
		private const string ModelName = "gpt-oss:20b";
		private const bool EnableThinking = true; // Set to true for models that support thinking

		private readonly HttpClient httpClient;
		private readonly HashSet<string> processedFiles = [];
		private readonly JsonSerializerOptions jsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
		private bool verboseMode = false;
		private string thinkingLevel = "medium";
		private string? currentLogFile;

		// Queue management for LLM processing
		private bool isProcessingLLM = false;
		private string? pendingFile = null;
		private readonly object processLock = new();
		private CancellationTokenSource? shutdownCts;
		
		// File watcher management
		private FileSystemWatcher? fileWatcher;
		private System.Threading.Timer? fallbackScanTimer;
		private DateTime lastFileProcessedTime = DateTime.Now;

		// Events for UI updates
		public event Func<string, Task>? OnResponseChunk;
		public event Func<string, Task>? OnThinkingChunk;
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
			await LogToFileAsync($"Thinking Mode: {(EnableThinking ? "Enabled" : "Disabled")}");
			await LogToFileAsync($"Ollama API URL: {OllamaApiUrl}");
			await LogToFileAsync("");

			// Test Ollama API connectivity
			return await TestOllamaConnectionAsync();
		}

		public void StartWatching()
		{
			// Set up file watcher with increased buffer and error handling
			fileWatcher = new FileSystemWatcher(WatchDirectory);
			fileWatcher.Filter = "*.txt";
			fileWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
			fileWatcher.Created += OnNewFile;
			fileWatcher.Changed += OnNewFile;
			fileWatcher.Error += OnWatcherError;
			
			// Increase internal buffer to prevent overflow (default is 8KB, max is 64KB)
			fileWatcher.InternalBufferSize = 65536; // 64KB max buffer
			
			fileWatcher.EnableRaisingEvents = true;

			_ = NotifyStatusAsync($"Monitoring directory: {WatchDirectory}");
			_ = LogToFileAsync($"[WATCHER] File watcher started with buffer size: {fileWatcher.InternalBufferSize} bytes");

			// Set up fallback timer to scan for new files every 15 seconds
			// This ensures we don't miss files even if the watcher fails
			fallbackScanTimer = new System.Threading.Timer(
				async _ => await FallbackFileScan(),
				null,
				TimeSpan.FromSeconds(15),
				TimeSpan.FromSeconds(15));

			// Process any existing files
			_ = ProcessExistingFilesAsync();
		}

		public async Task StopAsync()
		{
			shutdownCts?.Cancel();
			
			// Stop the fallback timer
			fallbackScanTimer?.Dispose();
			
			// Stop and dispose the file watcher
			if (fileWatcher != null)
			{
				fileWatcher.EnableRaisingEvents = false;
				fileWatcher.Dispose();
			}

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
				Console.WriteLine($"[ERROR] Failed to connect to Ollama API: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Failed to connect to Ollama API: {ex.Message}");
				await LogToFileAsync($"[ERROR] Failed to connect to Ollama API: {ex.Message}\n{ex.StackTrace}");
				return false;
			}
		}

		private async Task ProcessExistingFilesAsync()
		{
			try
			{
				var files = Directory.GetFiles(WatchDirectory, "*.txt");
				await LogToFileAsync($"[INIT] Found {files.Length} existing files in watch directory");
				if (files.Length > 0)
				{
					// Process only the most recent file
					var mostRecent = files.OrderByDescending(File.GetCreationTime).First();
					await LogToFileAsync($"[INIT] Processing most recent existing file: {Path.GetFileName(mostRecent)}");
					await ProcessFileAsync(mostRecent);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Error processing existing files: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error processing existing files: {ex.Message}");
				await LogToFileAsync($"[ERROR] Error processing existing files: {ex.Message}\n{ex.StackTrace}");
			}
		}

		private void OnNewFile(object sender, FileSystemEventArgs e)
		{
			if (e.FullPath != null && (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed))
			{
				_ = LogToFileAsync($"[FILE_EVENT] Detected {e.ChangeType} for file: {Path.GetFileName(e.FullPath)}");
				// Small delay to ensure file is fully written, then process async
				Task.Delay(500).ContinueWith(async _ => 
				{
					try
					{
						await LogToFileAsync($"[FILE_EVENT] Starting delayed processing for: {Path.GetFileName(e.FullPath)}");
						await ProcessFileAsync(e.FullPath);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[ERROR] Exception in file processing task: {ex.Message}\n{ex.StackTrace}");
						await LogToFileAsync($"[ERROR] Exception in file processing task: {ex.Message}\n{ex.StackTrace}");
					}
				}, TaskContinuationOptions.None);
			}
			else
			{
				_ = LogToFileAsync($"[FILE_EVENT] Ignored event {e.ChangeType} for: {e.FullPath ?? "null path"}");
			}
		}

		private void OnWatcherError(object sender, ErrorEventArgs e)
		{
			var ex = e.GetException();
			_ = LogToFileAsync($"[WATCHER_ERROR] FileSystemWatcher error: {ex.Message}\n{ex.StackTrace}");
			_ = NotifyStatusAsync($"File watcher error detected: {ex.Message}");
			
			// Try to restart the watcher
			try
			{
				if (fileWatcher != null)
				{
					fileWatcher.EnableRaisingEvents = false;
					fileWatcher.EnableRaisingEvents = true;
					_ = LogToFileAsync("[WATCHER_ERROR] File watcher restarted after error");
				}
			}
			catch (Exception restartEx)
			{
				Console.WriteLine($"[WATCHER_ERROR] Failed to restart watcher: {restartEx.Message}");
				_ = LogToFileAsync($"[WATCHER_ERROR] Failed to restart watcher: {restartEx.Message}");
			}
		}

		private async Task FallbackFileScan()
		{
			try
			{
				// Don't run if shutting down
				if (shutdownCts?.Token.IsCancellationRequested ?? true)
					return;

				// Check if we haven't processed any files in a while
				var timeSinceLastFile = DateTime.Now - lastFileProcessedTime;
				if (timeSinceLastFile.TotalSeconds < 20)
				{
					// Recent activity, no need for fallback scan
					return;
				}

				await LogToFileAsync($"[FALLBACK] Running fallback file scan (no activity for {timeSinceLastFile.TotalSeconds:F0} seconds)");

				// Get all txt files in the directory
				var files = Directory.GetFiles(WatchDirectory, "*.txt")
					.OrderByDescending(File.GetLastWriteTime)
					.ToArray();

				if (files.Length == 0)
				{
					await LogToFileAsync("[FALLBACK] No files found in directory");
					return;
				}

				// Check if the most recent file needs processing
				var mostRecentFile = files.First();
				var fileTime = File.GetLastWriteTime(mostRecentFile);
				
				// If the file is newer than our last processed time and not in processedFiles
				bool shouldProcess = false;
				lock (processedFiles)
				{
					if (!processedFiles.Contains(mostRecentFile) && fileTime > lastFileProcessedTime)
					{
						shouldProcess = true;
					}
				}

				if (shouldProcess && !isProcessingLLM)
				{
					await LogToFileAsync($"[FALLBACK] Found unprocessed file: {Path.GetFileName(mostRecentFile)}");
					await ProcessFileAsync(mostRecentFile);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[FALLBACK_ERROR] Error during fallback scan: {ex.Message}\n{ex.StackTrace}");
				await LogToFileAsync($"[FALLBACK_ERROR] Error during fallback scan: {ex.Message}\n{ex.StackTrace}");
			}
		}

		private async Task ProcessFileAsync(string filePath)
		{
			await LogToFileAsync($"[PROCESS_FILE] Starting ProcessFileAsync for: {Path.GetFileName(filePath)}");
			
			// Check if we're shutting down
			if (shutdownCts?.Token.IsCancellationRequested ?? true)
			{
				await LogToFileAsync($"[PROCESS_FILE] Skipping {Path.GetFileName(filePath)} - shutdown requested");
				return;
			}

			// Check if already processed first
			bool alreadyProcessed = false;
			lock (processedFiles)
			{
				if (processedFiles.Contains(filePath))
				{
					alreadyProcessed = true;
					_ = LogToFileAsync($"[PROCESS_FILE] Skipping {Path.GetFileName(filePath)} - already in processedFiles set (total: {processedFiles.Count})");
				}
			}

			if (alreadyProcessed)
				return;

			// Check if LLM is currently processing
			lock (processLock)
			{
				if (isProcessingLLM)
				{
					var oldPending = pendingFile;
					// Queue this file as the latest pending
					pendingFile = filePath;
					_ = NotifyStatusAsync($"LLM is busy. Queued file: {Path.GetFileName(filePath)}");
					_ = LogToFileAsync($"[LOCK] LLM is busy. Replacing pending file: {(oldPending != null ? Path.GetFileName(oldPending) : "none")} -> {Path.GetFileName(filePath)}");
					return;
				}

				// We got the lock, now mark this file as processed
				isProcessingLLM = true;
				lock (processedFiles)
				{
					processedFiles.Add(filePath);
					_ = LogToFileAsync($"[PROCESS_FILE] Added {Path.GetFileName(filePath)} to processedFiles set (total: {processedFiles.Count})");
				}
				_ = LogToFileAsync($"[LOCK] Acquired processing lock for: {Path.GetFileName(filePath)}");
			}

			try
			{
				// Update last processed time
				lastFileProcessedTime = DateTime.Now;
				
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
					await LogToFileAsync($"[ERROR] Failed to read game state from: {Path.GetFileName(filePath)}");
					lock (processLock)
					{
						isProcessingLLM = false;
						_ = LogToFileAsync($"[LOCK] Released processing lock due to read failure");
					}

					await ProcessPendingFileAsync();
					return;
				}

				// Skip menu/lobby states
				if (gameState.Contains("Blank Shellmap") || gameState.Contains("Map: Shellmap") ||
					!gameState.Contains("Resource Cells:"))
				{
					await NotifyStatusAsync("Skipping menu/lobby state.");
					await LogToFileAsync($"[SKIP] Skipping menu/lobby state in: {Path.GetFileName(filePath)}");
					lock (processLock)
					{
						isProcessingLLM = false;
						_ = LogToFileAsync($"[LOCK] Released processing lock due to menu/lobby state");
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
				await LogToFileAsync($"[LLM] Starting Ollama API request for: {Path.GetFileName(filePath)}");
				await StreamOllamaResponseAsync(prompt, gameState);
				await LogToFileAsync($"[LLM] Completed Ollama API request for: {Path.GetFileName(filePath)}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Exception in ProcessFileAsync for {Path.GetFileName(filePath)}: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error processing file {filePath}: {ex.Message}");
				await LogToFileAsync($"[ERROR] Exception in ProcessFileAsync for {Path.GetFileName(filePath)}: {ex.Message}\n{ex.StackTrace}");
			}
			finally
			{
				// Always release the processing lock and check for pending files
				lock (processLock)
				{
					isProcessingLLM = false;
					_ = LogToFileAsync($"[LOCK] Released processing lock in finally block for: {Path.GetFileName(filePath)}");
				}

				await LogToFileAsync($"[PROCESS_FILE] Checking for pending files after completing: {Path.GetFileName(filePath)}");
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
					_ = LogToFileAsync($"[PENDING] Retrieved pending file: {Path.GetFileName(fileToProcess)} (isProcessingLLM={isProcessingLLM})");
				}
				else
				{
					_ = LogToFileAsync($"[PENDING] No pending file to process (pendingFile={(pendingFile != null ? Path.GetFileName(pendingFile) : "null")}, isProcessingLLM={isProcessingLLM})");
				}
			}

			if (fileToProcess != null)
			{
				await NotifyStatusAsync($"Processing pending file: {Path.GetFileName(fileToProcess)}");
				await LogToFileAsync($"[PENDING] Starting processing of pending file: {Path.GetFileName(fileToProcess)}");
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
				var thinkingBuilder = new StringBuilder();
				var startTime = DateTime.Now;
				var isReceivingThinking = false;

				// Build request - only include think parameter if enabled
				object request;
				if (EnableThinking)
				{
					request = new
					{
						model = ModelName,
						prompt = prompt,
						stream = true,
						think = true
					};
				}
				else
				{
					request = new
					{
						model = ModelName,
						prompt = prompt,
						stream = true
					};
				}

				var json = JsonSerializer.Serialize(request, jsonOptions);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				using var requestMessage = new HttpRequestMessage(HttpMethod.Post, OllamaApiUrl)
				{
					Content = content
				};

				await LogToFileAsync($"[HTTP] Sending request to Ollama API at {DateTime.Now:HH:mm:ss.fff}");
				using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
				await LogToFileAsync($"[HTTP] Received response headers at {DateTime.Now:HH:mm:ss.fff} - Status: {response.StatusCode}");
				
				if (!response.IsSuccessStatusCode)
				{
					var errorBody = await response.Content.ReadAsStringAsync();
					await LogToFileAsync($"[HTTP] Ollama API error: {response.StatusCode} - {errorBody}");
					throw new HttpRequestException($"Ollama API error {response.StatusCode}: {errorBody}");
				}

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
							if (responseObj != null)
							{
								// Check if we have thinking output
								if (!string.IsNullOrEmpty(responseObj.Thinking))
								{
									isReceivingThinking = true;
									thinkingBuilder.Append(responseObj.Thinking);
									if (OnThinkingChunk != null)
										await OnThinkingChunk(responseObj.Thinking);
								}
								
								// Check if we have regular response output
								if (!string.IsNullOrEmpty(responseObj.Response))
								{
									// If we were receiving thinking and now getting response, we've switched
									if (isReceivingThinking && OnThinkingChunk != null)
									{
										// Signal end of thinking phase
										await OnThinkingChunk("\n\n=== End of Thinking ===\n\n");
										isReceivingThinking = false;
									}
									
									// Stream the response chunk
									responseBuilder.Append(responseObj.Response);
									if (OnResponseChunk != null)
										await OnResponseChunk(responseObj.Response);
								}
							}

							if (responseObj?.Done == true)
							{
								await LogToFileAsync($"[HTTP] Stream completed at {DateTime.Now:HH:mm:ss.fff}");
								// Log the complete response
								if (thinkingBuilder.Length > 0)
								{
									await LogToFileAsync("\n=== THINKING PROCESS ===");
									await LogToFileAsync(thinkingBuilder.ToString());
									await LogToFileAsync("=== END OF THINKING ===\n");
								}
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
										Thinking = thinkingBuilder.ToString(),
										DurationSeconds = seconds
									};
									await OnResponseComplete(llmResponse);
								}

								break;
							}
						}
						catch (JsonException ex)
						{
							Console.WriteLine($"[JSON_ERROR] Error parsing Ollama response: {ex.Message}");
							await NotifyStatusAsync($"Error parsing response: {ex.Message}");
							await LogToFileAsync($"[JSON_ERROR] Error parsing response: {ex.Message}");
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Ollama API communication error: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error communicating with Ollama API: {ex.Message}");
				await LogToFileAsync($"[ERROR] Ollama API communication error: {ex.Message}\n{ex.StackTrace}");
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
				Console.WriteLine($"[CRITICAL] Failed to load strategy guide from {strategyGuidePath}: {ex.Message}\n{ex.StackTrace}");
				throw new InvalidOperationException($"CRITICAL: Failed to load strategy guide from {strategyGuidePath}: {ex.Message}", ex);
			}

			// Add thinking/reasoning level as the first line of the system prompt
		sb.AppendLine($"Reasoning: {thinkingLevel}");
		sb.AppendLine();
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
			catch (Exception ex)
			{
				// Log write failed - output to console at least
				Console.WriteLine($"[LOG_ERROR] Failed to write to log file: {ex.Message}");
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

		public string ThinkingLevel
		{
			get => thinkingLevel;
			set => thinkingLevel = value;
		}
	}

	public sealed class LLMResponse
	{
		public required string Id { get; init; }
		public required DateTime Timestamp { get; init; }
		public required string GameState { get; init; }
		public required string Response { get; init; }
		public required string Thinking { get; init; }
		public required double DurationSeconds { get; init; }
	}

	sealed class OllamaResponse
	{
		public string? Model { get; set; }
		public string? Response { get; set; }
		public string? Thinking { get; set; } // For models that support thinking/reasoning
		public bool Done { get; set; }
		public long TotalDuration { get; set; }
		// Additional fields that might be present
		public long LoadDuration { get; set; }
		public int PromptEvalCount { get; set; }
		public long PromptEvalDuration { get; set; }
		public int EvalCount { get; set; }
		public long EvalDuration { get; set; }
	}
}
