using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.LLMHarness
{
    sealed class Program
    {
        private const string WatchDirectory = @"C:\OpenRATest";
        private const string OllamaCommand = "ollama";
        private const string OllamaArgs = "run pidrilkin/gemma3_27b_abliterated:Q4_K_M";
        private static Process? ollamaProcess;
        private static readonly HashSet<string> processedFiles = new HashSet<string>();
        private static readonly StringBuilder responseBuffer = new StringBuilder();
        private static readonly object lockObject = new object();
        private static bool isWaitingForResponse = false;

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
                if (args.Data != null)
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    Console.WriteLine($"[{timestamp}] [Ollama Output]: {args.Data}");

                    lock (lockObject)
                    {
                        if (isWaitingForResponse)
                        {
                            responseBuffer.AppendLine(args.Data);
                        }
                    }
                }
            };

            DataReceivedEventHandler errorHandler = (sender, args) =>
            {
                if (args.Data != null && args.Data.Trim() != "")
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    Console.WriteLine($"[{timestamp}] [Ollama Error]: {args.Data}");
                }
            };

            ollamaProcess.OutputDataReceived += outputHandler;
            ollamaProcess.ErrorDataReceived += errorHandler;

            ollamaProcess.Start();
            ollamaProcess.BeginOutputReadLine();
            ollamaProcess.BeginErrorReadLine();

            Console.WriteLine($"Process started with PID: {ollamaProcess.Id}");
            Console.WriteLine("Waiting 5 seconds for Ollama to initialize...");
            await Task.Delay(5000);

            // Test if process is still running
            if (ollamaProcess.HasExited)
            {
                throw new Exception($"Ollama process exited during initialization with code: {ollamaProcess.ExitCode}");
            }

            Console.WriteLine("Assuming Ollama is ready - proceeding...");
        }

        private static void StopOllama()
        {
            if (ollamaProcess != null && !ollamaProcess.HasExited)
            {
                try
                {
                    Console.WriteLine("Stopping Ollama process...");
                    ollamaProcess.CancelOutputRead();
                    ollamaProcess.CancelErrorRead();
                    ollamaProcess.StandardInput.Close();
                    ollamaProcess.WaitForExit(1000);
                    if (!ollamaProcess.HasExited)
                    {
                        ollamaProcess.CloseMainWindow();
                        ollamaProcess.WaitForExit(1000);
                        if (!ollamaProcess.HasExited)
                        {
                            Console.WriteLine("Force killing Ollama process...");
                            ollamaProcess.Kill();
                        }
                    }

                    Console.WriteLine($"Ollama process exited with code: {ollamaProcess.ExitCode}");
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

                // Clear response buffer and set waiting flag
                lock (lockObject)
                {
                    responseBuffer.Clear();
                    isWaitingForResponse = true;
                }

                // Send prompt to Ollama
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                Console.WriteLine($"[{timestamp}] Sending prompt to Ollama stdin...");
                Console.WriteLine($"First 200 chars of prompt: {prompt.Substring(0, Math.Min(200, prompt.Length))}...");

                await ollamaProcess.StandardInput.WriteLineAsync(prompt);
                await ollamaProcess.StandardInput.FlushAsync();

                Console.WriteLine($"[{timestamp}] Prompt sent successfully.");

                // Wait for response
                Console.WriteLine("Waiting 10 seconds for response...");
                await Task.Delay(10000);

                // Check what we got
                lock (lockObject)
                {
                    isWaitingForResponse = false;
                    if (responseBuffer.Length > 0)
                    {
                        Console.WriteLine("\n=== LLM Response ===");
                        Console.WriteLine(responseBuffer.ToString());
                        Console.WriteLine("===================\n");
                    }
                    else
                    {
                        Console.WriteLine("No response received from Ollama.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
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
