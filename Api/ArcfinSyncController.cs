using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api;

[ApiController]
[Route("ArcfinSync")]
public class ArcfinSyncController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArcfinSyncController> _logger;

    public ArcfinSyncController(IAuthorizationContext authContext, IUserManager userManager, IMediaEncoder mediaEncoder,
                                IConfigurationManager config, ILibraryManager libraryManager, ILogger<ArcfinSyncController> logger)
    {
        _authContext = authContext;
        _userManager = userManager;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// One audio track of an item as AAC in an .m4a, for a converted download that keeps more
    /// than one. Needs the same access as downloading the item.
    /// </summary>
    [HttpGet("Items/{itemId}/Audio/{streamIndex}")]
    [Authorize]
    public async Task<ActionResult> GetAudioTrack([FromRoute] Guid itemId, [FromRoute] int streamIndex, [FromQuery] string? mediaSourceId)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        if (!item.CanDownload(user))
        {
            return Forbid();
        }

        var source = item.GetMediaSources(false)
            .FirstOrDefault(s => string.IsNullOrEmpty(mediaSourceId) || string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        var stream = source?.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == streamIndex);
        if (source?.Path is null || stream is null || !System.IO.File.Exists(source.Path))
        {
            return NotFound();
        }

        var file = await AudioTrackExtractor.ExtractAsync(_mediaEncoder.EncoderPath, _config.CommonApplicationPaths.TempDirectory,
                                                          source.Path, stream, _logger, HttpContext.RequestAborted).ConfigureAwait(false);
        if (file is null)
        {
            return StatusCode(500);
        }

        Response.OnCompleted(() =>
        {
            System.IO.File.Delete(file);
            return Task.CompletedTask;
        });
        return PhysicalFile(file, "audio/mp4", enableRangeProcessing: true);
    }

    /// <summary>
    /// Which video encoders work with the server's hardware acceleration, from a one-frame test
    /// encode per codec, so Arcfin only asks for a conversion the server can make.
    /// <c>refresh</c> runs the tests again; otherwise they're kept until the setting changes.
    /// </summary>
    [HttpGet("Encoders")]
    [Authorize]
    public async Task<ActionResult<EncodersDto>> GetEncoders([FromQuery] bool refresh = false)
    {
        return await EncoderProbe.GetAsync(_mediaEncoder, _config, _logger, refresh).ConfigureAwait(false);
    }

    [HttpGet("Status")]
    [AllowAnonymous]
    public ActionResult<StatusDto> GetStatus()
    {
        return new StatusDto
        {
            Installed = true,
            Version = Plugin.Instance!.Version.ToString()
        };
    }

    [HttpGet("Settings")]
    [Authorize]
    public async Task<ActionResult<List<SyncedSettingDto>>> GetSettings()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            return Unauthorized();
        }

        var username = _userManager.GetUserById(auth.UserId)?.Username;
        return Ok(SettingsStore.GetMergedSettings(auth.UserId, username));
    }

    [HttpPut("Settings")]
    [Authorize]
    public async Task<ActionResult<List<SyncedSettingDto>>> PutSettings([FromBody] Dictionary<string, string> values)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            return Unauthorized();
        }

        SettingsStore.SaveUserSettings(auth.UserId, values);
        return Ok(SettingsStore.GetMergedSettings(auth.UserId));
    }
}

public class StatusDto
{
    public bool Installed { get; set; }

    public string Version { get; set; } = string.Empty;
}

public class SyncedSettingDto
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public bool IsLocked { get; set; }
}
