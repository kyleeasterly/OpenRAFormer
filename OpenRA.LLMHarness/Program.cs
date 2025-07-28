using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenRA.LLMHarness
{
	sealed class Program
	{
		const string WatchDirectory = @"C:\OpenRATest";
		const string LogDirectory = @"C:\OpenRATest\LLM_Coach_Logs";
		const string OllamaApiUrl = "http://localhost:11434/api/generate";
		const string ModelName = "gemma3:27b";

		static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
		static readonly HashSet<string> ProcessedFiles = new();
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
		static bool VerboseMode = false;
		static string? CurrentLogFile;

		static async Task Main()
		{
			Console.WriteLine("OpenRA LLM Harness Starting...");

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
			CurrentLogFile = Path.Combine(LogDirectory, $"llm_coach_log_{timestamp}.txt");
			Console.WriteLine($"Logging to: {CurrentLogFile}");
			
			await LogToFileAsync($"=== OpenRA LLM Coach Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
			await LogToFileAsync($"Model: {ModelName}");
			await LogToFileAsync($"Watch Directory: {WatchDirectory}");
			await LogToFileAsync("");

			// Test Ollama API connectivity
			if (!await TestOllamaConnection())
			{
				Console.WriteLine("Failed to connect to Ollama API. Make sure Ollama is running.");
				return;
			}

			// Set up file watcher
			using var watcher = new FileSystemWatcher(WatchDirectory);
			watcher.Filter = "*.txt";
			watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
			watcher.Created += OnNewFile;
			watcher.Changed += OnNewFile;
			watcher.EnableRaisingEvents = true;

			Console.WriteLine($"Monitoring directory: {WatchDirectory}");
			Console.WriteLine("Press 'q' to quit, 'v' to toggle verbose mode");
			Console.WriteLine($"Verbose mode: {(VerboseMode ? "ON" : "OFF")}");

			// Process any existing files
			await ProcessExistingFilesAsync();

			// Keep the application running
			while (true)
			{
				var key = Console.ReadKey(true);
				if (key.KeyChar == 'q' || key.KeyChar == 'Q')
				{
					break;
				}
				else if (key.KeyChar == 'v' || key.KeyChar == 'V')
				{
					VerboseMode = !VerboseMode;
					Console.WriteLine($"\nVerbose mode: {(VerboseMode ? "ON" : "OFF")}");
				}
			}

			Console.WriteLine("Shutting down...");
			await LogToFileAsync($"\n=== Session Ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
		}

		static async Task<bool> TestOllamaConnection()
		{
			try
			{
				Console.WriteLine("Testing Ollama API connection...");
				var testRequest = new
				{
					model = ModelName,
					prompt = "Say 'OK' if you're working.",
					stream = false
				};

				var json = JsonSerializer.Serialize(testRequest, JsonOptions);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				var response = await HttpClient.PostAsync(OllamaApiUrl, content);
				if (response.IsSuccessStatusCode)
				{
					Console.WriteLine("Ollama API is responding!");
					return true;
				}
				else
				{
					Console.WriteLine($"Ollama API returned error: {response.StatusCode}");
					return false;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to connect to Ollama API: {ex.Message}");
				return false;
			}
		}

		static async Task ProcessExistingFilesAsync()
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
				Console.WriteLine($"Error processing existing files: {ex.Message}");
			}
		}

		static void OnNewFile(object sender, FileSystemEventArgs e)
		{
			if (e.FullPath != null && (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed))
			{
				// Small delay to ensure file is fully written, then process async
				Task.Delay(500).ContinueWith(async _ => await ProcessFileAsync(e.FullPath));
			}
		}

		static async Task ProcessFileAsync(string filePath)
		{
			// Skip if already processed
			lock (ProcessedFiles)
			{
				if (ProcessedFiles.Contains(filePath))
					return;
				ProcessedFiles.Add(filePath);
			}

			try
			{
				var separator = new string('=', 80);
				Console.WriteLine($"\n{separator}");
				Console.WriteLine($"Processing file: {Path.GetFileName(filePath)}");
				Console.WriteLine(separator);
				
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
					Console.WriteLine("Failed to read game state file.");
					return;
				}

				// Skip menu/lobby states
				if (gameState.Contains("Blank Shellmap") || gameState.Contains("Map: Shellmap") ||
					!gameState.Contains("Resource Cells:"))
				{
					Console.WriteLine("Skipping menu/lobby state.");
					return;
				}

				Console.WriteLine($"Read {gameState.Length} characters from file.");

				// Construct the prompt
				var prompt = BuildPrompt(gameState);
				Console.WriteLine($"Built prompt with {prompt.Length} characters.");

				// Always log the full prompt to file
				await LogToFileAsync("\n=== FULL PROMPT TO LLM ===");
				await LogToFileAsync(prompt);
				await LogToFileAsync("=== END OF PROMPT ===\n");

				// Display full prompt in verbose mode
				if (VerboseMode)
				{
					Console.WriteLine("\n=== FULL PROMPT TO LLM ===");
					Console.WriteLine(prompt);
					Console.WriteLine("=== END OF PROMPT ===\n");
				}

				// Send to Ollama API with streaming
				await StreamOllamaResponse(prompt);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
				Console.WriteLine($"Stack trace: {ex.StackTrace}");
			}
		}

		static async Task StreamOllamaResponse(string prompt)
		{
			try
			{
				Console.WriteLine("\nSending prompt to Ollama API (streaming)...");
				Console.WriteLine("\n=== LLM Response ===");
				
				await LogToFileAsync("Sending prompt to Ollama API...");
				await LogToFileAsync("\n=== LLM RESPONSE ===");
				
				var responseBuilder = new StringBuilder();

				var request = new
				{
					model = ModelName,
					prompt = prompt,
					stream = true
				};

				var json = JsonSerializer.Serialize(request, JsonOptions);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				using var requestMessage = new HttpRequestMessage(HttpMethod.Post, OllamaApiUrl)
				{
					Content = content
				};

				using var response = await HttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
				response.EnsureSuccessStatusCode();

				using var stream = await response.Content.ReadAsStreamAsync();
				using var reader = new StreamReader(stream);

				while (!reader.EndOfStream)
				{
					var line = await reader.ReadLineAsync();
					if (!string.IsNullOrWhiteSpace(line))
					{
						try
						{
							var responseObj = JsonSerializer.Deserialize<OllamaResponse>(line, JsonOptions);
							if (responseObj != null && !string.IsNullOrEmpty(responseObj.Response))
							{
								// Stream the response to console as it arrives
								Console.Write(responseObj.Response);
								responseBuilder.Append(responseObj.Response);
							}

							if (responseObj?.Done == true)
							{
								Console.WriteLine("\n===================\n");
								
								// Log the complete response
								await LogToFileAsync(responseBuilder.ToString());
								await LogToFileAsync("===================");
								
								if (responseObj.TotalDuration > 0)
								{
									var seconds = responseObj.TotalDuration / 1_000_000_000.0;
									Console.WriteLine($"Generation completed in {seconds:F2} seconds");
									await LogToFileAsync($"Generation completed in {seconds:F2} seconds");
								}

								break;
							}
						}
						catch (JsonException ex)
						{
							Console.WriteLine($"\nError parsing response: {ex.Message}");
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"\nError communicating with Ollama API: {ex.Message}");
			}
		}

		static string BuildPrompt(string gameState)
		{
			var sb = new StringBuilder();

			// Load strategy guide - this is REQUIRED
			var strategyGuide = "";
			var strategyGuidePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CnC_Strategy_Guide.txt");
			
			// First try the local directory
			if (!File.Exists(strategyGuidePath))
			{
				// Try the bin directory where the DLL is located
				strategyGuidePath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "", "CnC_Strategy_Guide.txt");
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
				throw new FileNotFoundException($"CRITICAL: CnC_Strategy_Guide.txt not found! Searched in:\n" +
					$"- {AppDomain.CurrentDomain.BaseDirectory}\n" +
					$"- {Path.GetDirectoryName(typeof(Program).Assembly.Location)}\n" +
					$"- Project source directory");
			}
			
			try
			{
				strategyGuide = File.ReadAllText(strategyGuidePath);
				Console.WriteLine($"Loaded strategy guide from: {strategyGuidePath} ({strategyGuide.Length} characters)");
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

		static async Task LogToFileAsync(string message)
		{
			if (string.IsNullOrEmpty(CurrentLogFile))
				return;
				
			try
			{
				await File.AppendAllTextAsync(CurrentLogFile, message + Environment.NewLine);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Warning: Failed to write to log file: {ex.Message}");
			}
		}

		sealed class OllamaResponse
		{
			public string? Model { get; set; }
			public string? Response { get; set; }
			public bool Done { get; set; }
			public long TotalDuration { get; set; }
		}
	}
}
