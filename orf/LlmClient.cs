using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Minimal OpenAI-compatible chat completions client over raw HttpClient.</summary>
public sealed class LlmClient(string baseUrl, string apiKey)
{
	static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
	static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
	const int MaxRetries = 3;

	readonly string endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

	/// <summary>POSTs the payload; retries up to 3 times with exponential backoff on 429/5xx/network errors. Returns the raw response body.</summary>
	public async Task<string> ChatAsync(JsonObject payload, CancellationToken ct)
	{
		var body = payload.ToJsonString();
		Exception? last = null;

		for (var attempt = 0; attempt <= MaxRetries; attempt++)
		{
			if (attempt > 0)
				await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);

			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeoutCts.CancelAfter(RequestTimeout);

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
				request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");

				using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
				var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);

				if (response.IsSuccessStatusCode)
					return text;

				var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
				var error = new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode} from {endpoint}: {Truncate(text, 500)}");
				if (!retryable)
					throw error;

				last = error;
			}
			catch (HttpRequestException ex)
			{
				last = ex;
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				last = new TimeoutException($"Request to {endpoint} timed out after {RequestTimeout.TotalSeconds}s");
			}
		}

		throw last ?? new HttpRequestException($"Request to {endpoint} failed");
	}

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
