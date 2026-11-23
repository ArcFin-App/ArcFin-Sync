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
    private const int MaxSecretLength = 512;
    private const int MaxYamlLength = 256 * 1024;

    private readonly IUserManager _userManager;

    public ArcfinSyncAdminController(IUserManager userManager)
    {
        _userManager = userManager;
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
                Settings = store.LoadValues(u.Id)
            })
            .ToList();
    }

    [HttpGet("Config")]
    public ActionResult<AdminConfigDto> GetConfig()
    {
        var config = Plugin.Instance!.Configuration;
        return new AdminConfigDto
        {
            NotificationsEnabled = config.NotificationsEnabled,
            SeerrWebhookEnabled = config.SeerrWebhookEnabled,
            SeerrWebhookSecretSet = !string.IsNullOrEmpty(config.SeerrWebhookSecret),
            AdvancedYamlConfig = config.AdvancedYamlConfig ?? string.Empty
        };
    }

    [HttpPut("Config")]
    [RequestSizeLimit(1024 * 1024)]
    public ActionResult<AdminConfigDto> PutConfig([FromBody] AdminConfigBody body)
    {
        if (body is null)
        {
            return BadRequest("No configuration.");
        }

        if (body.SeerrWebhookSecret is { } secret && (secret.Length > MaxSecretLength || secret.Any(char.IsControl)))
        {
            return BadRequest($"The webhook secret must be plain text of at most {MaxSecretLength} characters.");
        }

        if (body.AdvancedYamlConfig is { Length: > MaxYamlLength })
        {
            return BadRequest("The advanced configuration is too long.");
        }

        // The configuration is saved as XML, which can't hold most control characters.
        if (body.AdvancedYamlConfig is { } yaml && yaml.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            return BadRequest("The advanced configuration can't contain control characters.");
        }

        PolicyService.UpdateConfig(config =>
        {
            config.NotificationsEnabled = body.NotificationsEnabled ?? config.NotificationsEnabled;
            config.SeerrWebhookEnabled = body.SeerrWebhookEnabled ?? config.SeerrWebhookEnabled;
            if (body.SeerrWebhookSecret is not null)
            {
                config.SeerrWebhookSecret = body.SeerrWebhookSecret.Trim();
            }

            config.AdvancedYamlConfig = body.AdvancedYamlConfig ?? config.AdvancedYamlConfig;
        });

        return GetConfig();
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
