using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orf;

/// <summary>Minimal OpenAI-compatible chat completions client over raw HttpClient.</summary>
public sealed class LlmClient(string baseUrl, string apiKey, int timeoutSeconds = 120, Action<string>? onRetry = null)
{
	static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
	readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
	const int MaxRetries = 3;
	const int MaxRateLimitRetries = 8;

	readonly string endpoint = baseUrl.TrimEnd('/') + "/chat/completions";

	/// <summary>Total 429 responses seen, for status displays.</summary>
	public long RateLimited429s { get; private set; }

	/// <summary>
	/// POSTs the payload; retries on 429/5xx/network errors and returns the raw
	/// response body. 429s get more attempts with longer, jittered waits — dozens
	/// of agents share one API key, and synchronized short backoffs just re-collide
	/// (observed 2026-08-22: a multi-minute fleet-wide stall against Nous).
	/// </summary>
	public async Task<string> ChatAsync(JsonObject payload, CancellationToken ct)
	{
		var body = payload.ToJsonString();
		Exception? last = null;
		var rateLimited = 0;

		for (var attempt = 0; attempt <= MaxRetries + rateLimited && rateLimited <= MaxRateLimitRetries; attempt++)
		{
			if (attempt > 0)
			{
				var delay = last is RateLimitException
					? TimeSpan.FromSeconds(5 * rateLimited + Random.Shared.NextDouble() * 10)
					: TimeSpan.FromSeconds(Math.Pow(2, attempt - rateLimited));
				onRetry?.Invoke($"attempt {attempt} failed ({Truncate(last?.Message ?? "?", 120)}); retrying in {delay.TotalSeconds:0.0}s");
				await Task.Delay(delay, ct);
			}

			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeoutCts.CancelAfter(requestTimeout);

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
				request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");

				using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
				var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);

				if (response.IsSuccessStatusCode)
					return text;

				if (response.StatusCode == HttpStatusCode.TooManyRequests)
				{
					rateLimited++;
					RateLimited429s++;
					last = new RateLimitException($"429 from {endpoint}: {Truncate(text, 200)}");
					continue;
				}

				var error = new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode} from {endpoint}: {Truncate(text, 500)}");
				if ((int)response.StatusCode < 500)
					throw error;

				last = error;
			}
			catch (HttpRequestException ex)
			{
				last = ex;
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				last = new TimeoutException($"Request to {endpoint} timed out after {requestTimeout.TotalSeconds}s");
			}
		}

		throw last ?? new HttpRequestException($"Request to {endpoint} failed");
	}

	static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Distinguishes 429s so the retry loop can wait longer with jitter.</summary>
public sealed class RateLimitException(string message) : Exception(message);
