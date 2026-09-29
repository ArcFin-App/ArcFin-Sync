using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api;

[ApiController]
[Route("ArcfinSync")]
public class ArcfinSyncController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;

    public ArcfinSyncController(IAuthorizationContext authContext, IUserManager userManager)
    {
        _authContext = authContext;
        _userManager = userManager;
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
