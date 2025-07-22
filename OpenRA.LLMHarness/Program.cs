using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.LLMHarness
{
    class Program
    {
        private static Process? ollamaProcess;
        private static StreamWriter? ollamaInput;
        private static readonly string WatchDirectory = @"C:\OpenRATest";
        private static readonly string OllamaCommand = "ollama";
        private static readonly string OllamaArgs = "run pidrilkin/gemma3_27b_abliterated:Q4_K_M";
        private static readonly HashSet<string> processedFiles = new HashSet<string>();
        private static bool ollamaReady = false;
        private static readonly object lockObject = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("OpenRA LLM Harness Starting...");

            // Create watch directory if it doesn't exist
            if (!Directory.Exists(WatchDirectory))
            {
                Directory.CreateDirectory(WatchDirectory);
            }

            // Start Ollama process
            if (!StartOllama())
            {
                Console.WriteLine("Failed to start Ollama. Exiting.");
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
            ProcessExistingFiles();

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
            if (ollamaProcess != null && !ollamaProcess.HasExited)
            {
                ollamaProcess.Kill();
                ollamaProcess.Dispose();
            }
        }

        private static bool StartOllama()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = OllamaCommand,
                    Arguments = OllamaArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                ollamaProcess = Process.Start(startInfo);
                if (ollamaProcess == null)
                {
                    Console.WriteLine("Failed to start Ollama process.");
                    return false;
                }

                ollamaInput = ollamaProcess.StandardInput;

                // Start reading output asynchronously
                Task.Run(() => ReadOllamaOutput());
                Task.Run(() => ReadOllamaError());

                // Wait for Ollama to be ready
                Console.WriteLine("Waiting for Ollama to initialize...");
                var timeout = DateTime.Now.AddSeconds(30);
                while (!ollamaReady && DateTime.Now < timeout)
                {
                    Thread.Sleep(100);
                }

                if (!ollamaReady)
                {
                    Console.WriteLine("Ollama did not initialize within timeout.");
                    return false;
                }

                Console.WriteLine("Ollama is ready!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting Ollama: {ex.Message}");
                return false;
            }
        }

        private static async Task ReadOllamaOutput()
        {
            if (ollamaProcess?.StandardOutput == null) return;

            var buffer = new StringBuilder();
            var outputStarted = false;

            while (!ollamaProcess.StandardOutput.EndOfStream)
            {
                var line = await ollamaProcess.StandardOutput.ReadLineAsync();
                if (line != null)
                {
                    // Check if Ollama is ready
                    if (line.Contains(">>>"))
                    {
                        if (!ollamaReady)
                        {
                            lock (lockObject)
                            {
                                ollamaReady = true;
                            }
                        }
                        
                        // If we were collecting output, print it now
                        if (outputStarted && buffer.Length > 0)
                        {
                            Console.WriteLine("\n=== LLM Response ===");
                            Console.WriteLine(buffer.ToString());
                            Console.WriteLine("===================\n");
                            buffer.Clear();
                            outputStarted = false;
                        }
                    }
                    else if (ollamaReady)
                    {
                        // We're ready and this is actual output
                        outputStarted = true;
                        buffer.AppendLine(line);
                    }
                }
            }
        }

        private static async Task ReadOllamaError()
        {
            if (ollamaProcess?.StandardError == null) return;

            while (!ollamaProcess.StandardError.EndOfStream)
            {
                var line = await ollamaProcess.StandardError.ReadLineAsync();
                if (line != null)
                {
                    Console.WriteLine($"[Ollama Error] {line}");
                }
            }
        }

        private static void ProcessExistingFiles()
        {
            var files = Directory.GetFiles(WatchDirectory, "*.txt");
            if (files.Length > 0)
            {
                // Process only the most recent file
                var mostRecent = files.OrderByDescending(f => File.GetCreationTime(f)).First();
                ProcessFile(mostRecent);
            }
        }

        private static void OnNewFile(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath != null && e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Changed)
            {
                // Small delay to ensure file is fully written
                Thread.Sleep(500);
                ProcessFile(e.FullPath);
            }
        }

        private static void ProcessFile(string filePath)
        {
            lock (lockObject)
            {
                // Skip if already processed or Ollama not ready
                if (processedFiles.Contains(filePath) || !ollamaReady)
                {
                    return;
                }

                processedFiles.Add(filePath);
            }

            try
            {
                Console.WriteLine($"\nProcessing file: {Path.GetFileName(filePath)}");

                // Read the game state
                string gameState = File.ReadAllText(filePath);

                // Construct the prompt
                var prompt = BuildPrompt(gameState);

                // Send to Ollama
                if (ollamaInput != null)
                {
                    Console.WriteLine("Sending game state to LLM for analysis...");
                    ollamaInput.WriteLine(prompt);
                    ollamaInput.Flush();
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