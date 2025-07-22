using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.LLMHarness
{
    sealed class Program
    {
        private static Process? ollamaProcess;
        private const string WatchDirectory = @"C:\OpenRATest";
        private const string OllamaCommand = "ollama";
        private const string OllamaArgs = "run pidrilkin/gemma3_27b_abliterated:Q4_K_M";
        private static readonly HashSet<string> processedFiles = new HashSet<string>();
        private static readonly StringBuilder pendingOutput = new StringBuilder();
        private static TaskCompletionSource<bool>? waitingForResponse;

        static async Task Main(string[] args)
        {
            Console.WriteLine("OpenRA LLM Harness Starting...");

            // Create watch directory if it doesn't exist
            if (!Directory.Exists(WatchDirectory))
            {
                Directory.CreateDirectory(WatchDirectory);
            }

            // Start Ollama process
            try
            {
                await StartOllamaAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start Ollama: {ex.Message}");
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

            // Cleanup
            Console.WriteLine("Shutting down...");
            StopOllama();
        }

        private static async Task StartOllamaAsync()
        {
            var completionSource = new TaskCompletionSource<bool>();
            ollamaProcess = new Process();

            ollamaProcess.StartInfo = new ProcessStartInfo
            {
                FileName = OllamaCommand,
                Arguments = OllamaArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Console.WriteLine($"Starting process: {ollamaProcess.StartInfo.FileName} {ollamaProcess.StartInfo.Arguments}");

            DataReceivedEventHandler outputHandler = (sender, args) =>
            {
                Console.WriteLine($"[Ollama]: {args.Data}");
                if (args.Data == null || args.Data.Trim() == "") return;
                
                // Check if Ollama is ready (looking for >>> prompt)
                if (args.Data.Contains(">>>"))
                {
                    completionSource.TrySetResult(true); // Signal that Ollama is ready
                    
                    // If we were collecting output, print it now
                    if (pendingOutput.Length > 0)
                    {
                        Console.WriteLine("\n=== LLM Response ===");
                        Console.WriteLine(pendingOutput.ToString());
                        Console.WriteLine("===================\n");
                        pendingOutput.Clear();
                        
                        // Signal any waiting response handler
                        waitingForResponse?.TrySetResult(true);
                    }
                }
                else
                {
                    // Accumulate output
                    pendingOutput.AppendLine(args.Data);
                }
            };

            DataReceivedEventHandler errorHandler = (sender, args) =>
            {
                if (args.Data != null && args.Data.Trim() != "")
                {
                    Console.WriteLine($"[Ollama Error]: {args.Data}");
                }
            };

            ollamaProcess.OutputDataReceived += outputHandler;
            ollamaProcess.ErrorDataReceived += errorHandler;

            ollamaProcess.Start();
            ollamaProcess.BeginOutputReadLine();
            ollamaProcess.BeginErrorReadLine();

            Console.WriteLine("Waiting for Ollama to initialize (this may take a while if downloading the model)...");

            // Wait for the signal from Ollama with timeout
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(10));
            var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                ollamaProcess.OutputDataReceived -= outputHandler;
                ollamaProcess.ErrorDataReceived -= errorHandler;
                throw new TimeoutException("Ollama did not initialize within 10 minutes");
            }

            Console.WriteLine("Ollama is ready!");
            
            // Keep the handlers attached for ongoing communication
        }

        private static void StopOllama()
        {
            if (ollamaProcess != null && !ollamaProcess.HasExited)
            {
                try
                {
                    ollamaProcess.CancelOutputRead();
                    ollamaProcess.CancelErrorRead();
                    ollamaProcess.StandardInput.Close();
                    ollamaProcess.WaitForExit(1000);
                    if (!ollamaProcess.HasExited)
                    {
                        ollamaProcess.CloseMainWindow();
                        ollamaProcess.WaitForExit(1000);
                        if (!ollamaProcess.HasExited) ollamaProcess.Kill();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] StopOllama: {ex.Message}");
                }
                finally
                {
                    ollamaProcess.Dispose();
                    ollamaProcess = null;
                }
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
            if (ollamaProcess == null || ollamaProcess.HasExited)
            {
                Console.WriteLine("Ollama process is not running.");
                return;
            }

            // Skip if already processed
            lock (processedFiles)
            {
                if (processedFiles.Contains(filePath))
                    return;
                processedFiles.Add(filePath);
            }

            try
            {
                Console.WriteLine($"\nProcessing file: {Path.GetFileName(filePath)}");

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

                // Construct the prompt
                var prompt = BuildPrompt(gameState);

                // Clear any pending output
                pendingOutput.Clear();

                // Set up completion source for response
                waitingForResponse = new TaskCompletionSource<bool>();

                // Send prompt to Ollama
                Console.WriteLine("Sending game state to LLM for analysis...");
                await ollamaProcess.StandardInput.WriteLineAsync(prompt);
                await ollamaProcess.StandardInput.FlushAsync();

                // Wait for response with timeout
                var responseTimeout = Task.Delay(TimeSpan.FromMinutes(2));
                var responseTask = await Task.WhenAny(waitingForResponse.Task, responseTimeout);

                if (responseTask == responseTimeout)
                {
                    Console.WriteLine("Warning: No response from Ollama within 2 minutes.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
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
    }
}