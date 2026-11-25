using Client.Rendering;
using Microsoft.Extensions.Options;

namespace Client.Worker;

public class RenderWorker(
    ServerApi api,
    JobCache cache,
    TaskRunner runner,
    IOptions<ClientOptions> options,
    ILogger<RenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var pollInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
        logger.LogInformation("Render machine polling {Server} every {Interval}s (cache: {Cache})",
            settings.ServerUrl, settings.PollIntervalSeconds, settings.ResolveCacheDirectory());
        logger.LogInformation("HNode: {HNode}; Blender: {Blender}",
            ApplicationPaths.Resolve(settings.HNodePath, ApplicationPaths.DefaultHNode) ?? "NOT FOUND",
            ApplicationPaths.Resolve(settings.BlenderPath, ApplicationPaths.DefaultBlender) ?? "NOT FOUND");

        try
        {
            var me = await api.GetIdentityAsync(stoppingToken);
            logger.LogInformation("Connected as render machine \"{Name}\" (id {Id})", me?.Name, me?.Id);
        }
        catch (Exception ex) when (ex is ServerRejectedException or HttpRequestException)
        {
            logger.LogWarning("Couldn't confirm this machine's identity: {Message}", ex.Message);
        }

        var idleLogged = false;
        var missingLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Don't take work this machine can't do: a missing application would fail every task.
                if (MissingApplications() is { } missing)
                {
                    if (!missingLogged)
                        logger.LogError("{Missing}. Not taking tasks until it's installed or configured (run 'Client configure').", missing);
                    missingLogged = true;
                    await Task.Delay(pollInterval, stoppingToken);
                    continue;
                }
                missingLogged = false;

                var claim = await api.ClaimAsync(stoppingToken);
                if (claim is null)
                {
                    if (!idleLogged)
                        logger.LogInformation("No tasks available; waiting");
                    idleLogged = true;
                    await Task.Delay(pollInterval, stoppingToken);
                    continue;
                }

                idleLogged = false;
                var job = await cache.EnsureAsync(claim.Job, stoppingToken);
                if (!await runner.RunAsync(claim, job, stoppingToken))
                {
                    // The failed task is back in the queue; pause before claiming so a machine with a
                    // persistent problem doesn't spin on it.
                    logger.LogInformation("Waiting {Interval}s before taking another task", settings.PollIntervalSeconds);
                    await Task.Delay(pollInterval, stoppingToken);
                }
                // Otherwise ask for the next task straight away.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ServerRejectedException ex) when (ex.StatusCode == 401)
            {
                logger.LogError("The server rejected this machine's API token ({Message}). Check Emulator:ApiToken and that the machine is enabled.", ex.Message);
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is ServerRejectedException or HttpRequestException or IOException)
            {
                logger.LogWarning("{Message}; retrying in {Interval}s", ex.Message, settings.PollIntervalSeconds);
                await Task.Delay(pollInterval, stoppingToken);
            }
        }
    }

    private string? MissingApplications()
    {
        var settings = options.Value;
        if (ApplicationPaths.Resolve(settings.HNodePath, ApplicationPaths.DefaultHNode) is null)
            return $"HNode not found (HNodePath: {(string.IsNullOrWhiteSpace(settings.HNodePath) ? ApplicationPaths.DefaultHNode() + " (default)" : settings.HNodePath)})";
        if (ApplicationPaths.Resolve(settings.BlenderPath, ApplicationPaths.DefaultBlender) is null)
            return $"Blender not found (BlenderPath: {(string.IsNullOrWhiteSpace(settings.BlenderPath) ? ApplicationPaths.DefaultBlender() + " (default)" : settings.BlenderPath)})";
        return null;
    }
}
