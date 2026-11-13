using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
    private readonly ILogger<ArcfinSyncController> _logger;

    public ArcfinSyncController(IAuthorizationContext authContext, IUserManager userManager, IMediaEncoder mediaEncoder,
                                IConfigurationManager config, ILogger<ArcfinSyncController> logger)
    {
        _authContext = authContext;
        _userManager = userManager;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
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
