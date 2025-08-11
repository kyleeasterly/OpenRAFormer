namespace OpenRA.LLMHarness.Services
{
	public sealed class FileWatcherService : IHostedService
	{
		private readonly OllamaService ollamaService;
		private readonly ILogger<FileWatcherService> logger;

		public FileWatcherService(OllamaService ollamaService, ILogger<FileWatcherService> logger)
		{
			this.ollamaService = ollamaService;
			this.logger = logger;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			logger.LogInformation("FileWatcherService starting...");

			var initialized = await ollamaService.InitializeAsync();
			if (initialized)
			{
				ollamaService.StartWatching();
				logger.LogInformation("File watching started successfully.");
			}
			else
			{
				logger.LogError("Failed to initialize Ollama service. File watching will not start.");
			}
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			logger.LogInformation("FileWatcherService stopping...");
			await ollamaService.StopAsync();
		}
	}
}
