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
        private static StreamWriter? ollamaInput;
        private const string WatchDirectory = @"C:\OpenRATest";
        private const string OllamaCommand = "ollama";
        private const string OllamaArgs = "run pidrilkin/gemma3_27b_abliterated:Q4_K_M";
        private static readonly HashSet<string> processedFiles = new HashSet<string>();
        private static bool ollamaReady = false;
        private static readonly object lockObject = new object();
        private static readonly StringBuilder pendingOutput = new StringBuilder();

        static async Task Main(string[] args)
        {
            Console.WriteLine("OpenRA LLM Harness Starting...");

            // Create watch directory if it doesn't exist
            if (!Directory.Exists(WatchDirectory))
            {
                Directory.CreateDirectory(WatchDirectory);
            }

            // Start Ollama process
            if (!await StartOllamaAsync())
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
                try
                {
                    ollamaProcess.Kill();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error killing Ollama process: {ex.Message}");
                }
                ollamaProcess.Dispose();
            }
        }

        private static async Task<bool> StartOllamaAsync()
        {
            try
            {
                Console.WriteLine($"Starting Ollama with command: {OllamaCommand} {OllamaArgs}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = OllamaCommand,
                    Arguments = OllamaArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                ollamaProcess = new Process { StartInfo = startInfo };
                
                // Set up output handlers before starting
                ollamaProcess.OutputDataReceived += OnOllamaOutputReceived;
                ollamaProcess.ErrorDataReceived += OnOllamaErrorReceived;

                if (!ollamaProcess.Start())
                {
                    Console.WriteLine("Failed to start Ollama process.");
                    return false;
                }

                ollamaInput = ollamaProcess.StandardInput;
                
                // Begin async reading of streams
                ollamaProcess.BeginOutputReadLine();
                ollamaProcess.BeginErrorReadLine();

                // Wait for Ollama to be ready (with timeout)
                Console.WriteLine("Waiting for Ollama to initialize (this may take a while if downloading the model)...");
                var timeout = DateTime.Now.AddMinutes(10); // Longer timeout for model download
                
                while (!ollamaReady && DateTime.Now < timeout)
                {
                    if (ollamaProcess.HasExited)
                    {
                        Console.WriteLine($"Ollama process exited unexpectedly with code: {ollamaProcess.ExitCode}");
                        return false;
                    }
                    await Task.Delay(100);
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
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Check if ollama is installed
                try
                {
                    var checkProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "ollama",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    
                    if (checkProcess != null)
                    {
                        await checkProcess.WaitForExitAsync();
                        if (checkProcess.ExitCode == 0)
                        {
                            var path = await checkProcess.StandardOutput.ReadToEndAsync();
                            Console.WriteLine($"Ollama found at: {path.Trim()}");
                        }
                        else
                        {
                            Console.WriteLine("Ollama not found in PATH. Please ensure Ollama is installed.");
                        }
                    }
                }
                catch
                {
                    // Ignore errors from where command
                }
                
                return false;
            }
        }

        private static void OnOllamaOutputReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            lock (lockObject)
            {
                // Check if Ollama is ready (looking for >>> prompt)
                if (!ollamaReady && e.Data.Contains(">>>"))
                {
                    ollamaReady = true;
                    // Clear any pending output
                    pendingOutput.Clear();
                }
                else if (ollamaReady)
                {
                    // Check if this line contains the >>> prompt (end of response)
                    if (e.Data.Contains(">>>"))
                    {
                        // Print accumulated output
                        if (pendingOutput.Length > 0)
                        {
                            Console.WriteLine("\n=== LLM Response ===");
                            Console.WriteLine(pendingOutput.ToString());
                            Console.WriteLine("===================\n");
                            pendingOutput.Clear();
                        }
                    }
                    else
                    {
                        // Accumulate output
                        pendingOutput.AppendLine(e.Data);
                    }
                }
                else
                {
                    // During initialization, show output to help debug
                    Console.WriteLine($"[Ollama Init] {e.Data}");
                }
            }
        }

        private static void OnOllamaErrorReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Console.WriteLine($"[Ollama Error] {e.Data}");
            }
        }

        private static void ProcessExistingFiles()
        {
            try
            {
                var files = Directory.GetFiles(WatchDirectory, "*.txt");
                if (files.Length > 0)
                {
                    // Process only the most recent file
                    var mostRecent = files.OrderByDescending(f => File.GetCreationTime(f)).First();
                    ProcessFile(mostRecent);
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
                // Small delay to ensure file is fully written
                Task.Delay(500).ContinueWith(_ => ProcessFile(e.FullPath));
            }
        }

        private static void ProcessFile(string filePath)
        {
            lock (lockObject)
            {
                // Skip if already processed or Ollama not ready
                if (processedFiles.Contains(filePath) || !ollamaReady || ollamaInput == null)
                {
                    return;
                }

                processedFiles.Add(filePath);
            }

            try
            {
                Console.WriteLine($"\nProcessing file: {Path.GetFileName(filePath)}");

                // Read the game state with retry logic for file access
                string gameState;
                int retryCount = 0;
                while (retryCount < 3)
                {
                    try
                    {
                        gameState = File.ReadAllText(filePath);
                        break;
                    }
                    catch (IOException) when (retryCount < 2)
                    {
                        retryCount++;
                        Thread.Sleep(100);
                        continue;
                    }
                }

                // Construct the prompt
                var prompt = BuildPrompt(File.ReadAllText(filePath));

                // Send to Ollama
                lock (lockObject)
                {
                    if (ollamaInput != null && !ollamaProcess!.HasExited)
                    {
                        Console.WriteLine("Sending game state to LLM for analysis...");
                        ollamaInput.WriteLine(prompt);
                        ollamaInput.Flush();
                    }
                    else
                    {
                        Console.WriteLine("Warning: Cannot send to Ollama - process may have exited.");
                    }
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