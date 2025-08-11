using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenRA.StateAnalyzer
{
	class Program
	{
		static async Task<int> Main(string[] args)
		{
			if (args.Length != 2)
			{
				Console.WriteLine("Usage: OpenRA.StateAnalyzer <state1.txt> <state2.txt>");
				Console.WriteLine("Analyzes two game state files that are 10 seconds apart to infer actions taken.");
				return 1;
			}

			var state1Path = args[0];
			var state2Path = args[1];

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