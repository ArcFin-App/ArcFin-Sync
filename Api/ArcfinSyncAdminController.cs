using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>Server administrators only, enforced by Jellyfin's elevation policy.</summary>
[ApiController]
[Route("ArcfinSync/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ArcfinSyncAdminController : ControllerBase
{
    private readonly IUserManager _userManager;

    public ArcfinSyncAdminController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>Who may use Pick Together: on or off for everyone, and the users it's off for.</summary>
    [HttpGet("PickTogether")]
    public ActionResult<PickTogetherSettings> GetPickTogether()
    {
        return Plugin.Instance!.Configuration.PickTogether;
    }

    [HttpPut("PickTogether")]
    public ActionResult<PickTogetherSettings> PutPickTogether([FromBody] PickTogetherSettings settings)
    {
        if (settings is null)
        {
            return BadRequest("No settings.");
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.PickTogether = new PickTogetherSettings
        {
            Enabled = settings.Enabled,
            DisabledUsers = (settings.DisabledUsers ?? new List<Guid>()).Distinct().Where(id => _userManager.GetUserById(id) is not null).ToList()
        };
        plugin.SaveConfiguration();
        return plugin.Configuration.PickTogether;
    }

    [HttpGet("Policy")]
    public ActionResult<SettingsPolicy> GetPolicy()
    {
        return PolicyService.Get();
    }

    [HttpPut("Policy")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public ActionResult<SettingsPolicy> PutPolicy([FromBody] SettingsPolicy policy)
    {
        var (clean, error) = PolicyService.Validate(SettingsSchema.Embedded, policy, PolicyService.Get(), id => _userManager.GetUserById(id) is not null);
        if (error is not null)
        {
            return BadRequest(error);
        }

        PolicyService.Save(clean!);
        return clean!;
    }

    [HttpGet("Users")]
    public ActionResult<List<AdminUserDto>> GetUsers()
    {
        var store = SettingsStore.Current;
        return _userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new AdminUserDto
            {
                UserId = u.Id,
                Name = u.Username,
                IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                Devices = DeviceDto.List(store.LoadDevices(u.Id), null),
                Settings = store.LoadSyncedValues(u.Id, SettingsSchema.Embedded)
            })
            .ToList();
    }

    [HttpPut("Users/{userId}/Devices/{deviceId}/Name")]
    public ActionResult<DeviceDto> RenameDevice([FromRoute] Guid userId, [FromRoute] string deviceId, [FromBody] DeviceNameBody body)
    {
        if (userId == Guid.Empty || _userManager.GetUserById(userId) is null)
        {
            return NotFound();
        }

        return ArcfinSyncController.RenameIn(this, userId, deviceId, body, null);
    }

    [HttpDelete("Users/{userId}/Devices/{deviceId}")]
    public ActionResult ForgetDevice([FromRoute] Guid userId, [FromRoute] string deviceId)
    {
        if (userId == Guid.Empty || _userManager.GetUserById(userId) is null)
        {
            return NotFound();
        }

        return ArcfinSyncController.ForgetIn(this, userId, deviceId);
    }
}
