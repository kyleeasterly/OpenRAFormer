using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OpenRA.StateAnalyzer
{
	class Program
	{
		static async Task<int> Main(string[] args)
		{
			if (args.Length == 2)
			{
				return await AnalyzeStatesFromArgs(args[0], args[1]);
			}

			return await RunInteractiveMode();
		}

		static async Task<int> RunInteractiveMode()
		{
			Console.WriteLine("OpenRA State Analyzer - Interactive Mode");
			Console.WriteLine("=======================================");
			Console.WriteLine();

			string state1Path = null;
			string state2Path = null;

			while (true)
			{
				Console.Clear();
				Console.WriteLine("OpenRA State Analyzer - Interactive Mode");
				Console.WriteLine("=======================================");
				Console.WriteLine();
				Console.WriteLine($"State 1 (earlier): {(state1Path ?? "Not selected")}");
				Console.WriteLine($"State 2 (later):   {(state2Path ?? "Not selected")}");
				Console.WriteLine();
				Console.WriteLine("Options:");
				Console.WriteLine("1. Select State 1 file (earlier state)");
				Console.WriteLine("2. Select State 2 file (later state)");
				Console.WriteLine("3. Analyze states");
				Console.WriteLine("4. Exit");
				Console.WriteLine();
				Console.Write("Select option (1-4): ");

				var input = Console.ReadLine();

				switch (input)
				{
					case "1":
						state1Path = SelectFile("Select State 1 file (earlier state):");
						break;
					case "2":
						state2Path = SelectFile("Select State 2 file (later state):");
						break;
					case "3":
						if (state1Path != null && state2Path != null)
						{
							var result = await AnalyzeStatesFromArgs(state1Path, state2Path);
							Console.WriteLine();
							Console.WriteLine("Press any key to continue...");
							Console.ReadKey();
						}
						else
						{
							Console.WriteLine("Please select both state files first.");
							Console.WriteLine("Press any key to continue...");
							Console.ReadKey();
						}
						break;
					case "4":
						return 0;
					default:
						Console.WriteLine("Invalid option. Press any key to continue...");
						Console.ReadKey();
						break;
				}
			}
		}

		static string SelectFile(string prompt)
		{
			Console.WriteLine(prompt);
			Console.WriteLine();
			
			var currentDir = Directory.GetCurrentDirectory();
			var txtFiles = Directory.GetFiles(currentDir, "*.txt").Take(10).ToArray();
			
			if (txtFiles.Length > 0)
			{
				Console.WriteLine("Found .txt files in current directory:");
				for (int i = 0; i < txtFiles.Length; i++)
				{
					Console.WriteLine($"  {i + 1}. {Path.GetFileName(txtFiles[i])}");
				}
				Console.WriteLine();
				Console.WriteLine("Options:");
				Console.WriteLine("  1-{0}: Select file from list above", txtFiles.Length);
				Console.WriteLine("  f: Open file manager and enter path manually");
				Console.WriteLine("  p: Enter full path directly");
				Console.WriteLine();
			}
			else
			{
				Console.WriteLine("No .txt files found in current directory.");
				Console.WriteLine();
				Console.WriteLine("Options:");
				Console.WriteLine("  f: Open file manager and enter path manually");
				Console.WriteLine("  p: Enter full path directly");
				Console.WriteLine();
			}
			
			Console.Write("Your choice: ");
			var choice = Console.ReadLine()?.Trim().ToLower();
			
			if (int.TryParse(choice, out int fileIndex) && fileIndex >= 1 && fileIndex <= txtFiles.Length)
			{
				var selectedFile = txtFiles[fileIndex - 1];
				Console.WriteLine($"Selected: {selectedFile}");
				return selectedFile;
			}
			else if (choice == "f")
			{
				return OpenFileManagerAndGetPath();
			}
			else if (choice == "p")
			{
				return GetManualFilePath();
			}
			else
			{
				Console.WriteLine("Invalid choice.");
				Console.WriteLine("Press any key to try again...");
				Console.ReadKey();
				return SelectFile(prompt);
			}
		}

		static string OpenFileManagerAndGetPath()
		{
			try
			{
				var currentDir = Directory.GetCurrentDirectory();
				
				if (OperatingSystem.IsWindows())
				{
					Process.Start(new ProcessStartInfo("explorer.exe", currentDir) { UseShellExecute = true });
				}
				else if (OperatingSystem.IsLinux())
				{
					Process.Start(new ProcessStartInfo("xdg-open", currentDir) { UseShellExecute = true });
				}
				else if (OperatingSystem.IsMacOS())
				{
					Process.Start(new ProcessStartInfo("open", currentDir) { UseShellExecute = true });
				}
				
				Console.WriteLine($"File manager opened at: {currentDir}");
				Console.WriteLine("Navigate to your file, right-click it, and copy its path.");
				Console.WriteLine("(In Windows: Shift+Right-click → 'Copy as path')");
				Console.WriteLine("(In Linux: Right-click → Properties to see full path)");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Could not open file manager: {ex.Message}");
			}
			
			return GetManualFilePath();
		}

		static string GetManualFilePath()
		{
			Console.WriteLine();
			Console.Write("Enter the full path to the file: ");
			var filePath = Console.ReadLine()?.Trim();
			
			if (string.IsNullOrWhiteSpace(filePath))
			{
				return null;
			}
			
			filePath = filePath.Trim('"');
			
			if (File.Exists(filePath))
			{
				Console.WriteLine($"File found: {filePath}");
				return filePath;
			}
			else
			{
				Console.WriteLine($"File not found: {filePath}");
				Console.Write("Try again? (y/n): ");
				var retry = Console.ReadLine()?.Trim().ToLower();
				if (retry == "y" || retry == "yes")
				{
					return GetManualFilePath();
				}
				return null;
			}
		}

		static async Task<int> AnalyzeStatesFromArgs(string state1Path, string state2Path)
		{
			if (!File.Exists(state1Path))
			{
				Console.WriteLine($"Error: State file '{state1Path}' not found.");
				return 1;
			}

			if (!File.Exists(state2Path))
			{
				Console.WriteLine($"Error: State file '{state2Path}' not found.");
				return 1;
			}

			try
			{
				var analyzer = new GameStateAnalyzer();
				var actions = await analyzer.AnalyzeStateChanges(state1Path, state2Path);
				
				Console.WriteLine("=== INFERRED ACTIONS ===");
				Console.WriteLine();

				if (actions.Count == 0)
				{
					Console.WriteLine("No significant actions detected between these states.");
				}
				else
				{
					foreach (var action in actions)
					{
						Console.WriteLine(action);
					}
				}

				return 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error analyzing states: {ex.Message}");
				return 1;
			}
		}
	}
}