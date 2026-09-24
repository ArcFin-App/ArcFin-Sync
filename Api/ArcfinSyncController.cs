using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api;

[ApiController]
[Route("ArcfinSync")]
public class ArcfinSyncController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;

    public ArcfinSyncController(IAuthorizationContext authContext)
    {
        _authContext = authContext;
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

        return Ok(SettingsStore.GetMergedSettings(auth.UserId));
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
