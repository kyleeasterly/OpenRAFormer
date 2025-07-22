using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.LLMHarness
{
    sealed class Program
    {
        private const string WatchDirectory = @"C:\OpenRATest";
        private const string OllamaApiUrl = "http://localhost:11434/api/generate";
        private const string ModelName = "pidrilkin/gemma3_27b_abliterated:Q4_K_M";
        private static readonly HttpClient httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly HashSet<string> processedFiles = new HashSet<string>();
        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        static async Task Main(string[] args)
        {
            Console.WriteLine("OpenRA LLM Harness Starting...");

            // Create watch directory if it doesn't exist
            if (!Directory.Exists(WatchDirectory))
            {
                Directory.CreateDirectory(WatchDirectory);
            }

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
            Console.WriteLine("Press 'q' to quit...");

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
            }

            Console.WriteLine("Shutting down...");
        }

        private static async Task<bool> TestOllamaConnection()
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

                var json = JsonSerializer.Serialize(testRequest, jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(OllamaApiUrl, content);
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

        private static async Task ProcessExistingFilesAsync()
        {
            try
            {
                var files = Directory.GetFiles(WatchDirectory, "*.txt");
                if (files.Length > 0)
                {
                    // Process only the most recent file
                    var mostRecent = files.OrderByDescending(f => File.GetCreationTime(f)).First();
                    await ProcessFileAsync(mostRecent);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing existing files: {ex.Message}");
            }
        }

        private static void OnNewFile(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath != null && (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed))
            {
                // Small delay to ensure file is fully written, then process async
                Task.Delay(500).ContinueWith(async _ => await ProcessFileAsync(e.FullPath));
            }
        }

        private static async Task ProcessFileAsync(string filePath)
        {
            // Skip if already processed
            lock (processedFiles)
            {
                if (processedFiles.Contains(filePath))
                    return;
                processedFiles.Add(filePath);
            }

            try
            {
                Console.WriteLine($"\n{new string('=', 80)}");
                Console.WriteLine($"Processing file: {Path.GetFileName(filePath)}");
                Console.WriteLine(new string('=', 80));

                // Read the game state with retry logic for file access
                string gameState = "";
                int retryCount = 0;
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

                Console.WriteLine($"Read {gameState.Length} characters from file.");

                // Construct the prompt
                var prompt = BuildPrompt(gameState);
                Console.WriteLine($"Built prompt with {prompt.Length} characters.");

                // Send to Ollama API with streaming
                await StreamOllamaResponse(prompt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private static async Task StreamOllamaResponse(string prompt)
        {
            try
            {
                Console.WriteLine("\nSending prompt to Ollama API (streaming)...");
                Console.WriteLine("\n=== LLM Response ===");

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

                using var stream = await response.Content.ReadAsStreamAsync();
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
                                // Stream the response to console as it arrives
                                Console.Write(responseObj.Response);
                            }

                            if (responseObj?.Done == true)
                            {
                                Console.WriteLine("\n===================\n");
                                if (responseObj.TotalDuration > 0)
                                {
                                    var seconds = responseObj.TotalDuration / 1_000_000_000.0;
                                    Console.WriteLine($"Generation completed in {seconds:F2} seconds");
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

        private static string BuildPrompt(string gameState)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Your task is to review the current game state and advise a next action.");
            sb.AppendLine("Your should only respond with the next action and action parameters.");
            sb.AppendLine("The available actions are:");
            sb.AppendLine("MOVE(unit, x, y)");
            sb.AppendLine("BUILD(unit, x, y)");
            sb.AppendLine("ATTACK(friendly_unit, enemy_unit)");
            sb.AppendLine("IDLE");
            sb.AppendLine();
            sb.AppendLine("<game_state>");
            sb.AppendLine(gameState);
            sb.AppendLine("</game_state>");

            return sb.ToString();
        }

        private sealed class OllamaResponse
        {
            public string? Model { get; set; }
            public string? Response { get; set; }
            public bool Done { get; set; }
            public long TotalDuration { get; set; }
        }
    }
}
