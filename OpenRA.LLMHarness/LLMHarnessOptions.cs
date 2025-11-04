namespace OpenRA.LLMHarness
{
	public sealed class LLMHarnessOptions
	{
		public string WatchDirectory { get; set; } = @"C:\OpenRATest";
		public string LogDirectory { get; set; } = @"C:\OpenRATest\LLM_Coach_Logs";
		public string OrderInputDirectory { get; set; } = @"C:\OpenRATest_Orders\input";
		public string OrderArchiveDirectory { get; set; } = @"C:\OpenRATest_Orders\archive";
		public string OllamaApiUrl { get; set; } = "http://localhost:11434/v1";
		public string ModelName { get; set; } = "phi4:14b";
	}
}
