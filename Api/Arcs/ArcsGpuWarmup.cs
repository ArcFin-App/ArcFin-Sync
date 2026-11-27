using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>Warms the GPU for clips shortly after the server starts, so the first clip after a restart doesn't wait.</summary>
public sealed class ArcsGpuWarmup : IHostedService
{
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ArcsGpuWarmup> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public ArcsGpuWarmup(IConfigurationManager config, IMediaEncoder mediaEncoder, ILogger<ArcsGpuWarmup> logger)
    {
        _config = config;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // After the server's own start-up work.
                await Task.Delay(TimeSpan.FromSeconds(20), _stopping.Token).ConfigureAwait(false);
                if (Plugin.Instance?.Configuration.Arcs.Enabled == true)
                {
                    ArcsService.WarmGpu(_config, _mediaEncoder, _logger);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Arcfin Sync Arcs: GPU warm-up didn't start");
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
