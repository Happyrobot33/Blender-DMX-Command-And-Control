using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Client.Worker;

public sealed class ServerApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ILogger<ServerApi> _logger;

    public ServerApi(IOptions<ClientOptions> options, ILogger<ServerApi> logger)
    {
        _logger = logger;
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
            throw new InvalidOperationException(
                "No API token configured. Run 'Client configure', or set Emulator__ApiToken / --Emulator:ApiToken=<token>.");

        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan, // large downloads/uploads; callers pass cancellation tokens
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
    }

    public async Task<Client.Configuration.MachineIdentity?> GetIdentityAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/machines/me", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<Client.Configuration.MachineIdentity>(Json, cancellationToken);
    }

    public async Task<ClaimResponse?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("api/tasks/claim", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ClaimResponse>(Json, cancellationToken);
    }

    public async Task ReportProgressAsync(long taskId, int framesCompleted, string? status, string? error, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"api/tasks/{taskId}/progress",
            new { framesCompleted, status, error }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CompleteAsync(long taskId, string zipPath, CancellationToken cancellationToken)
    {
        await using var zip = File.OpenRead(zipPath);
        using var content = new StreamContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await _http.PostAsync($"api/tasks/{taskId}/complete", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var partPath = destination + ".part";
        var etagPath = destination + ".etag";

        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimStart('/'));
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (resumeFrom > 0 && File.Exists(etagPath))
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(await File.ReadAllTextAsync(etagPath, cancellationToken)));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        if (response.Headers.ETag is { } etag)
            await File.WriteAllTextAsync(etagPath, etag.ToString(), cancellationToken);
        if (resuming)
            _logger.LogInformation("Resuming download of {Url} from byte {Offset:N0}", url, resumeFrom);

        await using (var target = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await source.CopyToAsync(target, cancellationToken);

        File.Move(partPath, destination, overwrite: true);
        File.Delete(etagPath);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message;
        try
        {
            message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? body;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            message = body.Length > 300 ? body[..300] : body;
        }

        var status = (int)response.StatusCode;
        if (status is >= 400 and < 500)
            throw new ServerRejectedException(status, $"{status} {response.ReasonPhrase}: {message}");
        throw new HttpRequestException($"{status} {response.ReasonPhrase}: {message}", null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
