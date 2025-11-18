using System.ClientModel;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace OpenRA.LLMHarness.Services
{
	public sealed class OllamaService
	{
		readonly string watchDirectory;
		readonly string logDirectory;
		readonly string orderInputDirectory;
		readonly string orderArchiveDirectory;
		readonly SessionManager sessionManager;
		// Note: phi4:14b and llama3.1:8b work very well, 5 to 10 seconds per response and they produce the correct order format more consistently.
		public string OllamaApiUrl { get; set; }
		public string ModelName { get; set; }

		ChatClient chatClient;
		readonly Dictionary<string, DateTime> processedFiles = new();

		public bool VerboseMode { get; set; } = false;
		public string ThinkingLevel { get; set; } = "medium";
		public bool WriteOrdersToGame { get; set; } = true;
		public string HumanMessage { get; set; } = "";

		string? currentLogFile;

		// LLM processing management
		bool isProcessingLLM = false;
		readonly object processLock = new();
		CancellationTokenSource? shutdownCts;
		CancellationTokenSource? currentRequestCts;

		// File watcher management
		FileSystemWatcher? fileWatcher;
		Timer? fallbackScanTimer;
		DateTime lastFileProcessedTime = DateTime.Now;

		// Events for UI updates
		public event Func<string, Task>? OnResponseChunk;
		public event Func<string, Task>? OnThinkingChunk;
		public event Func<LLMResponse, Task>? OnResponseComplete;
		public event Func<string, Task>? OnStatusUpdate;

		public OllamaService(HttpClient httpClient, IOptions<LLMHarnessOptions> options, SessionManager sessionManager)
		{
			var config = options.Value;
			watchDirectory = config.WatchDirectory;
			logDirectory = config.LogDirectory;
			orderInputDirectory = config.OrderInputDirectory;
			orderArchiveDirectory = config.OrderArchiveDirectory;
			OllamaApiUrl = config.OllamaApiUrl;
			ModelName = config.ModelName;
			this.sessionManager = sessionManager;

			InitializeChatClient();
		}

		void InitializeChatClient()
		{
			// Create OpenAI client with custom endpoint for Ollama and a dummy API key
			var openAiClient = new OpenAIClient(
				new ApiKeyCredential("ollama"),
				new OpenAIClientOptions
				{
					Endpoint = new Uri(OllamaApiUrl),
					NetworkTimeout = TimeSpan.FromMinutes(5)
				});

			chatClient = openAiClient.GetChatClient(ModelName);
		}

		public void UpdateConfiguration()
		{
			InitializeChatClient();
		}

		public async Task<bool> InitializeAsync()
		{
			shutdownCts = new CancellationTokenSource();

			// Create directories if they don't exist
			if (!Directory.Exists(watchDirectory))
			{
				Directory.CreateDirectory(watchDirectory);
			}

			if (!Directory.Exists(logDirectory))
			{
				Directory.CreateDirectory(logDirectory);
			}

			// Create order directories if they don't exist
			if (!Directory.Exists(orderInputDirectory))
			{
				Directory.CreateDirectory(orderInputDirectory);
			}

			if (!Directory.Exists(orderArchiveDirectory))
			{
				Directory.CreateDirectory(orderArchiveDirectory);
			}

			// Initialize log file for this session
			var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			currentLogFile = Path.Combine(logDirectory, $"llm_coach_log_{timestamp}.txt");

			await NotifyStatusAsync($"Logging to: {currentLogFile}");
			await LogToFileAsync($"=== OpenRA LLM Coach Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
			await LogToFileAsync($"Model: {ModelName}");
			await LogToFileAsync($"Watch Directory: {watchDirectory}");
			await LogToFileAsync($"Order Input Directory: {orderInputDirectory}");
			await LogToFileAsync($"Order Archive Directory: {orderArchiveDirectory}");
			await LogToFileAsync($"Write Orders To Game: {WriteOrdersToGame}");
			await LogToFileAsync($"Thinking Level: {ThinkingLevel}");
			await LogToFileAsync($"Ollama API URL: {OllamaApiUrl}");
			await LogToFileAsync("");

			// Test Ollama API connectivity
			return await TestOllamaConnectionAsync();
		}

		public void StartWatching()
		{
			// Set up file watcher with increased buffer and error handling
			fileWatcher = new FileSystemWatcher(watchDirectory);
			fileWatcher.Filter = "*.txt";
			fileWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
			fileWatcher.Created += OnNewFile;
			fileWatcher.Changed += OnNewFile;
			fileWatcher.Error += OnWatcherError;

			// Increase internal buffer to prevent overflow (default is 8KB, max is 64KB)
			fileWatcher.InternalBufferSize = 65536; // 64KB max buffer

			fileWatcher.EnableRaisingEvents = true;

			_ = NotifyStatusAsync($"Monitoring directory: {watchDirectory}");
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

				// Create a simple test message
				var testMessages = new List<ChatMessage>
				{
					new SystemChatMessage("You are a helpful assistant."),
					new UserChatMessage("Say 'OK' if you're working.")
				};

				// Try to get a response (non-streaming for test)
				var response = await chatClient.CompleteChatAsync(testMessages);
				
				if (response?.Value?.Content != null && response.Value.Content.Count > 0)
				{
					await NotifyStatusAsync("Ollama API is responding!");
					await LogToFileAsync($"[TEST] Ollama API test successful. Response: {response.Value.Content[0].Text}");
					return true;
				}
				else
				{
					await NotifyStatusAsync("Ollama API returned empty response");
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
				var files = Directory.GetFiles(watchDirectory, "*.txt");
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
				var files = Directory.GetFiles(watchDirectory, "*.txt")
					.OrderByDescending(File.GetLastWriteTime)
					.ToArray();

				if (files.Length == 0)
				{
					await LogToFileAsync("[FALLBACK] No files found in directory");
					return;
				}

				// Check if the most recent file needs processing based on timestamp
				var mostRecentFile = files.First();
				var fileTime = File.GetLastWriteTime(mostRecentFile);

				// Check if file has been updated since we last processed it
				bool shouldProcess = false;
				lock (processedFiles)
				{
					if (processedFiles.TryGetValue(mostRecentFile, out var lastProcessedTime))
					{
						// File was processed before - check if it's been updated since
						if (fileTime > lastProcessedTime)
						{
							shouldProcess = true;
						}
					}
					else
					{
						// File never processed - check if it's newer than our last activity
						if (fileTime > lastFileProcessedTime)
						{
							shouldProcess = true;
						}
					}
				}

				if (shouldProcess && !isProcessingLLM)
				{
					await LogToFileAsync($"[FALLBACK] Found unprocessed or updated file: {Path.GetFileName(mostRecentFile)}");
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

			// Check if a session is active
			if (!sessionManager.IsSessionActive)
			{
				await LogToFileAsync($"[PROCESS_FILE] Skipping {Path.GetFileName(filePath)} - no active session");
				await NotifyStatusAsync("No active session - please start a session to process game states");
				return;
			}

			// Check if already processed based on file timestamp
			var fileLastWriteTime = File.GetLastWriteTime(filePath);
			bool alreadyProcessed = false;
			lock (processedFiles)
			{
				if (processedFiles.TryGetValue(filePath, out var lastProcessedTime))
				{
					if (fileLastWriteTime <= lastProcessedTime)
					{
						alreadyProcessed = true;
						_ = LogToFileAsync($"[PROCESS_FILE] Skipping {Path.GetFileName(filePath)} - file not modified since last processing (last: {lastProcessedTime:HH:mm:ss.fff}, current: {fileLastWriteTime:HH:mm:ss.fff})");
					}
					else
					{
						_ = LogToFileAsync($"[PROCESS_FILE] File {Path.GetFileName(filePath)} has been updated (last: {lastProcessedTime:HH:mm:ss.fff}, current: {fileLastWriteTime:HH:mm:ss.fff})");
					}
				}
			}

			if (alreadyProcessed)
				return;

			// Check if LLM is currently processing
			lock (processLock)
			{
				if (isProcessingLLM)
				{
					// Cancel current request to process new file immediately
					_ = NotifyStatusAsync($"Canceling current LLM request to process: {Path.GetFileName(filePath)}");
					_ = LogToFileAsync($"[LOCK] Canceling current request to process new file: {Path.GetFileName(filePath)}");
					currentRequestCts?.Cancel();
					return;
				}

				// We got the lock, now mark this file as processed with its timestamp
				isProcessingLLM = true;
				lock (processedFiles)
				{
					processedFiles[filePath] = fileLastWriteTime;
					_ = LogToFileAsync($"[PROCESS_FILE] Recorded {Path.GetFileName(filePath)} with timestamp {fileLastWriteTime:HH:mm:ss.fff} (total tracked: {processedFiles.Count})");
				}
				_ = LogToFileAsync($"[LOCK] Starting processing for: {Path.GetFileName(filePath)}");
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

					return;
				}

				// Skip menu/lobby states
				if (gameState.Contains("Blank Shellmap") || gameState.Contains("Map: Shellmap"))
				{
					await NotifyStatusAsync("Skipping menu/lobby state.");
					await LogToFileAsync($"[SKIP] Skipping menu/lobby state in: {Path.GetFileName(filePath)}");
					lock (processLock)
					{
						isProcessingLLM = false;
						_ = LogToFileAsync($"[LOCK] Released processing lock due to menu/lobby state");
					}

					return;
				}

				await NotifyStatusAsync($"Read {gameState.Length} characters from file.");

				// Construct the system and user prompts
				var systemPrompt = BuildSystemPrompt();
				var userPrompt = BuildUserPrompt(gameState);
				await NotifyStatusAsync($"Built system prompt: {systemPrompt.Length} chars, user prompt: {userPrompt.Length} chars.");

				// Log human message if present
				if (!string.IsNullOrWhiteSpace(HumanMessage))
				{
					await LogToFileAsync($"\n=== HUMAN MESSAGE ===");
					await LogToFileAsync(HumanMessage);
					await LogToFileAsync("=== END OF HUMAN MESSAGE ===\n");
				}

				// Always log the full prompts to file
				await LogToFileAsync("\n=== SYSTEM PROMPT TO LLM ===");
				await LogToFileAsync(systemPrompt);
				await LogToFileAsync("=== END OF SYSTEM PROMPT ===\n");
				await LogToFileAsync("\n=== USER PROMPT TO LLM ===");
				await LogToFileAsync(userPrompt);
				await LogToFileAsync("=== END OF USER PROMPT ===\n");

				// Display full prompt in verbose mode
				if (VerboseMode)
				{
					await NotifyStatusAsync("Full prompts logged to file.");
				}

				// Create new cancellation token for this request
				currentRequestCts = new CancellationTokenSource();
				
				// Send to OpenAI-compatible API with streaming
				await LogToFileAsync($"[LLM] Starting OpenAI API request for: {Path.GetFileName(filePath)}");
				await StreamOpenAIResponseAsync(systemPrompt, userPrompt, gameState, currentRequestCts.Token);
				await LogToFileAsync($"[LLM] Completed OpenAI API request for: {Path.GetFileName(filePath)}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Exception in ProcessFileAsync for {Path.GetFileName(filePath)}: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error processing file {filePath}: {ex.Message}");
				await LogToFileAsync($"[ERROR] Exception in ProcessFileAsync for {Path.GetFileName(filePath)}: {ex.Message}\n{ex.StackTrace}");
			}
			finally
			{
				// Always release the processing lock
				lock (processLock)
				{
					isProcessingLLM = false;
					currentRequestCts?.Dispose();
					currentRequestCts = null;
					_ = LogToFileAsync($"[LOCK] Released processing lock for: {Path.GetFileName(filePath)}");
				}
			}
		}

		private async Task StreamOpenAIResponseAsync(string systemPrompt, string userPrompt, string gameState, CancellationToken cancellationToken)
		{
			try
			{
				// Check if we're shutting down
				if (shutdownCts?.Token.IsCancellationRequested ?? true)
					return;

				await NotifyStatusAsync("Sending prompt to OpenAI-compatible API (streaming)...");

				await LogToFileAsync("Sending prompt to OpenAI-compatible API...");
				await LogToFileAsync("\n=== LLM RESPONSE ===");

				var responseBuilder = new StringBuilder();
				var startTime = DateTime.Now;

				// Build chat messages
				var messages = new List<ChatMessage>
				{
					new SystemChatMessage(systemPrompt),
					new UserChatMessage(userPrompt)
				};

				await LogToFileAsync($"[HTTP] Sending request to OpenAI-compatible API at {DateTime.Now:HH:mm:ss.fff}");

				var chatCompletionOptions = new ChatCompletionOptions
				{
					Temperature = 0
				};

				// Stream the response with cancellation support
				var streamingResponse = chatClient.CompleteChatStreamingAsync(messages, chatCompletionOptions, cancellationToken: cancellationToken);
				
				await LogToFileAsync($"[HTTP] Starting to receive streaming response at {DateTime.Now:HH:mm:ss.fff}");

				await foreach (var chunk in streamingResponse)
				{
					// Check for shutdown or cancellation
					if ((shutdownCts?.Token.IsCancellationRequested ?? true) || cancellationToken.IsCancellationRequested)
					{
						if (cancellationToken.IsCancellationRequested)
							await LogToFileAsync("[HTTP] Stream canceled due to new game state");
						break;
					}

					// Process content updates
					// Note: The gpt-oss model includes reasoning in its output when configured via system prompt
					// We could potentially detect reasoning sections by looking for patterns in the text
					foreach (var contentPart in chunk.ContentUpdate)
					{
						if (!string.IsNullOrEmpty(contentPart.Text))
						{
							responseBuilder.Append(contentPart.Text);
							
							// Stream the response chunk to UI
							if (OnResponseChunk != null)
								await OnResponseChunk(contentPart.Text);
						}
					}

					// Check if we're done
					if (chunk.FinishReason != null)
					{
						await LogToFileAsync($"[HTTP] Stream completed at {DateTime.Now:HH:mm:ss.fff} with reason: {chunk.FinishReason}");
						break;
					}
				}

				// Log the complete response
				var fullResponse = responseBuilder.ToString();
				await LogToFileAsync(fullResponse);
				await LogToFileAsync("===================");

				var seconds = (DateTime.Now - startTime).TotalSeconds;
				await LogToFileAsync($"Generation completed in {seconds:F2} seconds");

				// Only process orders if not canceled
				if (!cancellationToken.IsCancellationRequested)
				{
					// Extract and process orders
					var orders = ExtractOrders(fullResponse);
					if (!string.IsNullOrEmpty(orders))
					{
						await LogToFileAsync("\n=== EXTRACTED ORDERS ===");
						await LogToFileAsync(orders);
						await LogToFileAsync("=== END OF EXTRACTED ORDERS ===\n");
					}
					else
					{
						await LogToFileAsync("[ORDERS] No orders found in LLM response - writing empty orders file");
						orders = ""; // Ensure empty string rather than null
					}
					
					// Always write order files (even if empty) to maintain the request-response loop
					await WriteOrderFiles(orders);
				}
				else
				{
					await LogToFileAsync("[ORDERS] Skipping order extraction - request was canceled");
				}

				// Notify completion
				if (OnResponseComplete != null)
				{
					var llmResponse = new LLMResponse
					{
						Id = Guid.NewGuid().ToString(),
						Timestamp = startTime,
						GameState = gameState,
						Response = fullResponse,
						Thinking = "", // gpt-oss includes reasoning in the main output when configured
						DurationSeconds = seconds
					};
					await OnResponseComplete(llmResponse);
				}

				// Save turn artifacts to session
				if (sessionManager.IsSessionActive)
				{
					try
					{
						var turnPath = sessionManager.SaveTurn(
							systemPrompt,
							userPrompt,
							fullResponse,
							ModelName,
							ThinkingLevel,
							seconds,
							seconds); // Use same value for total pipeline time for now

						await LogToFileAsync($"[SESSION] Saved turn artifacts to: {turnPath}");
					}
					catch (Exception ex)
					{
						await LogToFileAsync($"[SESSION] Failed to save turn artifacts: {ex.Message}\n{ex.StackTrace}");
					}
				}
			}
			catch (OperationCanceledException)
			{
				await LogToFileAsync("[ERROR] Request was canceled");
				await NotifyStatusAsync("Request canceled for new game state");
			}
			catch (ClientResultException ex)
			{
				Console.WriteLine($"[ERROR] OpenAI API client error: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error communicating with OpenAI API: {ex.Message}");
				await LogToFileAsync($"[ERROR] OpenAI API client error: {ex.Message}\n{ex.StackTrace}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] OpenAI API communication error: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Error communicating with OpenAI API: {ex.Message}");
				await LogToFileAsync($"[ERROR] OpenAI API communication error: {ex.Message}\n{ex.StackTrace}");
			}
		}

		private string BuildSystemPrompt()
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

			// Add thinking/reasoning level as the first line of the system prompt (for gpt-oss models)
			sb.AppendLine($"Reasoning: {ThinkingLevel}");
			sb.AppendLine();
			sb.AppendLine("You are a Command & Conquer AI copilot helping augment a human player. Analyze the current game state and issue orders to help Player1 win.");
			sb.AppendLine("Consider the economy, military strength, map control, and immediate threats.");
			sb.AppendLine();
			sb.AppendLine("IMPORTANT: Your response must have two parts:");
			sb.AppendLine("1. Think strategically about what to do. Keep it concise and write your thoughts out as a simple single-level Markdown bullet list.");
			sb.AppendLine("2. Provide specific production orders in a section marked with <orders> tags. Only follow the given order format.");
			sb.AppendLine();
			sb.AppendLine("For the orders section:");
			sb.AppendLine("- Issue StartProduction orders for constructing buildings AND training units.");
			sb.AppendLine("- ALWAYS use quotes for ALL building and unit names.");
			sb.AppendLine("- ALWAYS include the producing building's index number (e.g., Barracks#1, War Factory#2).");
			sb.AppendLine("- Use Player1 for all orders.");
			sb.AppendLine("- Only order items that can actually be built given current tech/prerequisites.");
			sb.AppendLine("- Never Queue more than one building at a time. You should never have more than 1 of the same building in a queue.");
			sb.AppendLine("- Spread unit production across multiple buildings when available. Ensure you are respecting the rules about \"what builds what\" and not issuing impossible orders like trying to construct a building *from* a Power Plant or Refinery.");
			sb.AppendLine();
			sb.AppendLine("Order format:");
			sb.AppendLine("Player1: StartProduction (Building:\"BuildingType#Index\" Item:\"ItemName\" Count:Number)");
			sb.AppendLine();
			sb.AppendLine("Example building construction orders:");
			sb.AppendLine("IMPORTANT: Buildings are only produced by the Construction Yard. Never attempt to produce buildings from any other building than the Construction Yard.");
			sb.AppendLine("<orders>");
			sb.AppendLine("Player1: StartProduction (Building:\"Construction Yard#1\" Item:\"Advanced Power Plant\" Count:1)");
			sb.AppendLine("Player1: StartProduction (Building:\"Construction Yard#1\" Item:\"Advanced Communications Center\" Count:1)");
			sb.AppendLine("Player1: StartProduction (Building:\"Construction Yard#1\" Item:\"War Factory\" Count:1)");
			sb.AppendLine("</orders>");
			sb.AppendLine();
			sb.AppendLine("Example unit production orders:");
			sb.AppendLine("<orders>");
			sb.AppendLine("Player1: StartProduction (Building:\"Barracks#1\" Item:\"Minigunner\" Count:3)");
			sb.AppendLine("Player1: StartProduction (Building:\"Barracks#1\" Item:\"Rocket Soldier\" Count:2)");
			sb.AppendLine("Player1: StartProduction (Building:\"War Factory#1\" Item:\"Medium Tank\" Count:1)");
			sb.AppendLine("Player1: StartProduction (Building:\"War Factory#1\" Item:\"Harvester\" Count:1)");
			sb.AppendLine("</orders>");
			sb.AppendLine();
			sb.AppendLine("If multiple production buildings exist, distribute the load:");
			sb.AppendLine("<orders>");
			sb.AppendLine("Player1: StartProduction (Building:\"Barracks#1\" Item:\"Minigunner\" Count:2)");
			sb.AppendLine("Player1: StartProduction (Building:\"Barracks#2\" Item:\"Rocket Soldier\" Count:2)");
			sb.AppendLine("</orders>");
			sb.AppendLine();
			sb.AppendLine("Most of the time, you should be using multiple types of production buildings:");
			sb.AppendLine("<orders>");
			sb.AppendLine("Player1: StartProduction (Building:\"Construction Yard#1\" Item:\"Power Plant\" Count:1)");
			sb.AppendLine("Player1: StartProduction (Building:\"Barracks#1\" Item:\"Minigunner\" Count:3)");
			sb.AppendLine("Player1: StartProduction (Building:\"War Factory#1\" Item:\"Medium Tank\" Count:2)");
			sb.AppendLine("</orders>");
			sb.AppendLine();

			sb.AppendLine("<game_knowledge>");
			sb.AppendLine(strategyGuide);
			sb.AppendLine("</game_knowledge>");

			return sb.ToString();
		}

		private string BuildUserPrompt(string gameState)
		{
			var sb = new StringBuilder();
			sb.AppendLine("<game_state>");
			sb.AppendLine(gameState);
			sb.AppendLine("</game_state>");
			sb.AppendLine();
			sb.AppendLine("Based on the game knowledge above and current state:");
			sb.AppendLine("1. First, provide strategic advice about what the player should do next.");
			sb.AppendLine("2. Then, provide production orders for buildings to construct and units to train.");
			sb.AppendLine();
			sb.AppendLine("Remember to:");
			sb.AppendLine("- Place your orders between <orders> and </orders> tags.");
			sb.AppendLine("- Always use building indices (e.g., Barracks#1, War Factory#1).");
			sb.AppendLine("- Include both building construction and unit production orders.");
			sb.AppendLine("- Pay close attention to the Player1 production queues and don't queue duplicate production items on accident.");
			
			// Include human message if provided
			if (!string.IsNullOrWhiteSpace(HumanMessage))
			{
				sb.AppendLine();
				sb.AppendLine("## IMPORTANT EMERGENT INFORMATION FROM HUMAN PLAYER:");
				sb.AppendLine(HumanMessage);
				sb.AppendLine("(Take this information into account when making your strategic decisions)");
			}

			return sb.ToString();
		}

		private string? ExtractOrders(string llmResponse)
		{
			// Find content between <orders> and </orders> tags
			var startTag = "<orders>";
			var endTag = "</orders>";
			
			var startIndex = llmResponse.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
			if (startIndex == -1)
			{
				Console.WriteLine("[OllamaService] No <orders> tag found in LLM response");
				return null;
			}
			
			startIndex += startTag.Length;
			var endIndex = llmResponse.IndexOf(endTag, startIndex, StringComparison.OrdinalIgnoreCase);
			if (endIndex == -1)
			{
				Console.WriteLine("[OllamaService] No closing </orders> tag found in LLM response");
				return null;
			}
			
			var orders = llmResponse.Substring(startIndex, endIndex - startIndex).Trim();
			if (string.IsNullOrWhiteSpace(orders))
			{
				Console.WriteLine("[OllamaService] Empty orders section in LLM response");
				return null;
			}
			
			// Clean up each line - remove indentation while preserving the order format
			var lines = orders.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			var cleanedLines = lines.Select(line => line.TrimStart()).Where(line => !string.IsNullOrWhiteSpace(line));
			orders = string.Join(Environment.NewLine, cleanedLines);
			
			return orders;
		}

		private async Task WriteOrderFiles(string orders)
		{
			try
			{
				var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
				var filename = $"order_{timestamp}.txt";

				// ALWAYS write to archive
				var archivePath = Path.Combine(orderArchiveDirectory, filename);
				await File.WriteAllTextAsync(archivePath, orders);
				await LogToFileAsync($"[ORDERS] Archived orders to: {archivePath}");
				await NotifyStatusAsync($"Orders archived: {filename}");

				// Conditionally write to input (for game processing)
				if (WriteOrdersToGame)
				{
					var inputPath = Path.Combine(orderInputDirectory, filename);
					await File.WriteAllTextAsync(inputPath, orders);
					await LogToFileAsync($"[ORDERS] Wrote orders for game processing to: {inputPath}");
					await NotifyStatusAsync($"Orders sent to game: {filename}");
				}
				else
				{
					await LogToFileAsync("[ORDERS] Orders NOT sent to game (WriteOrdersToGame=false)");
					await NotifyStatusAsync("Orders archived only (not sent to game)");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Failed to write order files: {ex.Message}\n{ex.StackTrace}");
				await LogToFileAsync($"[ERROR] Failed to write order files: {ex.Message}\n{ex.StackTrace}");
				await NotifyStatusAsync($"Failed to write order files: {ex.Message}");
			}
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
}
