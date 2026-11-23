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
using Jellyfin.Database.Implementations.Entities;
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
            Version = Plugin.Instance!.Version.ToString(),
            ApiVersion = 2,
            Features = new List<string> { "schema", "devices", "policy", "syncMeta" }
        };
    }

    [HttpGet("Settings")]
    [Authorize]
    public async Task<ActionResult<List<SyncedSettingDto>>> GetSettings()
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var rules = SettingsRules.RulesForUser(PolicyService.Get(), user!.Id);
        return Ok(SettingsRules.LegacyMerge(rules, SettingsStore.Current.LoadValues(user.Id), user.Username));
    }

    /// <summary>
    /// The 0.3.x save. Keys the schema doesn't know are still stored as 0.3.x did: released apps
    /// sync offlineModeEnabled, which the schema leaves out, and dropping it while GET Settings
    /// still returns the stored value would keep putting the old value back on those apps.
    /// Device-only keys and values of the wrong type are dropped rather than refused.
    /// </summary>
    [HttpPut("Settings")]
    [Authorize]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<List<SyncedSettingDto>>> PutSettings([FromBody] Dictionary<string, string?> values)
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var tooBig = SettingsRules.CheckPayload(values);
        if (tooBig is not null)
        {
            return BadRequest(tooBig);
        }

        var rules = SettingsRules.RulesForUser(PolicyService.Get(), user!.Id);
        var values2 = SaveSynced(auth!, user, rules, values, replace: false, keepUnknown: true);

        // 0.3.x answered a save without the Jellyfin username, so a forced seerrUsername comes
        // back with no value here; kept as it was for the apps that read this.
        return Ok(SettingsRules.LegacyMerge(rules, values2, null));
    }

    [HttpGet("Schema")]
    [Authorize]
    public ActionResult GetSchema()
    {
        return Content(SettingsSchema.Embedded.Json, "application/json");
    }

    [HttpGet("Sync")]
    [Authorize]
    public async Task<ActionResult<SyncStateDto>> GetSync()
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var rules = SettingsRules.RulesForUser(PolicyService.Get(), user!.Id);
        return SyncState(user, rules, SettingsStore.Current.LoadValues(user.Id));
    }

    /// <summary>
    /// Saves synced values. With Replace, this device's settings become everyone's: each unlocked
    /// synced setting its platform shows that it didn't send goes back to its default. The
    /// platform comes from this device's record (PUT Devices/Current); without one, Replace
    /// clears nothing.
    /// </summary>
    [HttpPut("Sync")]
    [Authorize]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<SyncStateDto>> PutSync([FromBody] SyncPutBody body)
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var tooBig = SettingsRules.CheckPayload(body?.Values);
        if (tooBig is not null)
        {
            return BadRequest(tooBig);
        }

        var rules = SettingsRules.RulesForUser(PolicyService.Get(), user!.Id);
        var values = SaveSynced(auth!, user, rules, body?.Values ?? new Dictionary<string, string?>(), body?.Replace ?? false, keepUnknown: false);
        return SyncState(user, rules, values);
    }

    [HttpGet("Devices")]
    [Authorize]
    public async Task<ActionResult<List<DeviceDto>>> GetDevices()
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return DeviceDto.List(SettingsStore.Current.LoadDevices(user!.Id), auth!.DeviceId);
    }

    /// <summary>
    /// Records the calling device: its ID and Jellyfin device name come from the sign-in, never
    /// from the body. A name in the body is only taken when the device has no name of its own yet,
    /// or with RenameCurrent.
    /// </summary>
    [HttpPut("Devices/Current")]
    [Authorize]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<DeviceDto>> PutCurrentDevice([FromBody] CurrentDeviceBody body)
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var deviceId = auth!.DeviceId;
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length > SettingsRules.MaxDeviceIdLength)
        {
            return BadRequest("This sign-in has no usable device ID.");
        }

        body ??= new CurrentDeviceBody();
        if (body.Platform is not null && !SettingsRules.Platforms.Contains(body.Platform))
        {
            return BadRequest("Platform must be iOS, iPadOS, macOS or tvOS.");
        }

        if (body.SyncMode is not null && !SettingsRules.SyncModes.Contains(body.SyncMode))
        {
            return BadRequest("SyncMode must be automatic, ask or off.");
        }

        if (!SettingsRules.IsCleanField(body.Model) || !SettingsRules.IsCleanField(body.AppVersion))
        {
            return BadRequest("Model and AppVersion must be short plain text.");
        }

        var (name, nameError) = SettingsRules.CleanName(body.Name);
        if (nameError is not null)
        {
            return BadRequest(nameError);
        }

        var tooBig = SettingsRules.CheckPayload(body.Settings);
        if (tooBig is not null)
        {
            return BadRequest(tooBig);
        }

        var snapshot = body.Settings is null ? null : SettingsRules.FilterToSchema(SettingsSchema.Embedded, body.Settings, null);
        var jellyfinName = Truncate(auth.Device ?? string.Empty, SettingsRules.MaxFieldLength);
        var now = DateTime.UtcNow;

        var record = SettingsStore.Current.WithUser(user!.Id, files =>
        {
            var devices = files.Devices;
            if (!devices.TryGetValue(deviceId, out var device))
            {
                while (devices.Count >= SettingsRules.MaxDevicesPerUser)
                {
                    devices.Remove(devices.Values.OrderBy(d => d.LastSeen).First().DeviceId);
                }

                device = new DeviceRecord { DeviceId = deviceId, FirstSeen = now, SyncMode = "automatic" };
                devices[deviceId] = device;
            }

            device.JellyfinDeviceName = jellyfinName;
            device.LastSeen = now;
            device.Platform = body.Platform ?? device.Platform;
            device.Model = body.Model ?? device.Model;
            device.AppVersion = body.AppVersion ?? device.AppVersion;
            device.SyncMode = body.SyncMode ?? device.SyncMode;
            if (snapshot is not null)
            {
                device.Settings = snapshot;
            }

            if (!string.IsNullOrEmpty(name) && (string.IsNullOrEmpty(device.CustomName) || body.RenameCurrent))
            {
                device.CustomName = name == jellyfinName ? null : name;
            }

            files.DevicesChanged();
            return device;
        });

        return DeviceDto.From(record, deviceId);
    }

    [HttpPut("Devices/{deviceId}/Name")]
    [Authorize]
    public async Task<ActionResult<DeviceDto>> RenameDevice([FromRoute] string deviceId, [FromBody] DeviceNameBody body)
    {
        var (auth, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return Rename(user!.Id, deviceId, body, auth!.DeviceId);
    }

    [HttpDelete("Devices/{deviceId}")]
    [Authorize]
    public async Task<ActionResult> ForgetDevice([FromRoute] string deviceId)
    {
        var (_, user, error) = await GetCallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return Forget(user!.Id, deviceId);
    }

    internal const long MaxBodyBytes = 4 * 1024 * 1024;

    internal static ActionResult<DeviceDto> RenameIn(ControllerBase controller, Guid userId, string deviceId, DeviceNameBody? body, string? currentDeviceId)
    {
        var (name, nameError) = SettingsRules.CleanName(body?.Name);
        if (nameError is not null)
        {
            return controller.BadRequest(nameError);
        }

        var record = SettingsStore.Current.WithUser(userId, files =>
        {
            if (!files.Devices.TryGetValue(deviceId, out var device))
            {
                return null;
            }

            device.CustomName = string.IsNullOrEmpty(name) || name == device.JellyfinDeviceName ? null : name;
            files.DevicesChanged();
            return device;
        });

        return record is null ? controller.NotFound() : DeviceDto.From(record, currentDeviceId);
    }

    internal static ActionResult ForgetIn(ControllerBase controller, Guid userId, string deviceId)
    {
        var removed = SettingsStore.Current.WithUser(userId, files =>
        {
            if (!files.Devices.Remove(deviceId))
            {
                return false;
            }

            files.DevicesChanged();
            return true;
        });

        return removed ? controller.NoContent() : controller.NotFound();
    }

    private ActionResult<DeviceDto> Rename(Guid userId, string deviceId, DeviceNameBody body, string? current) => RenameIn(this, userId, deviceId, body, current);

    private ActionResult Forget(Guid userId, string deviceId) => ForgetIn(this, userId, deviceId);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// The signed-in user behind this request. API keys and tokens without a user are refused:
    /// these endpoints are about one person's settings.
    /// </summary>
    private async Task<(AuthorizationInfo? Auth, User? User, ActionResult? Error)> GetCallerAsync()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
        {
            return (null, null, Unauthorized());
        }

        var user = _userManager.GetUserById(auth.UserId);
        return user is null ? (null, null, Unauthorized()) : (auth, user, null);
    }

    /// <summary>Saves synced values, stamping who changed them when something actually changed.</summary>
    private static Dictionary<string, string?> SaveSynced(AuthorizationInfo auth, User user, List<EffectiveRule> rules,
                                                          IReadOnlyDictionary<string, string?> incoming, bool replace, bool keepUnknown)
    {
        var schema = SettingsSchema.Embedded;
        var filtered = SettingsRules.FilterToSchema(schema, incoming, SettingsSchema.ScopeSynced, keepUnknown);
        var locked = SettingsRules.LockedKeys(rules);
        return SettingsStore.Current.WithUser(user.Id, files =>
        {
            var values = files.Values;
            var deviceId = string.IsNullOrEmpty(auth.DeviceId) || auth.DeviceId.Length > SettingsRules.MaxDeviceIdLength ? null : auth.DeviceId;
            var device = deviceId is not null && files.Devices.TryGetValue(deviceId, out var record) ? record : null;
            if (SettingsRules.Apply(schema, values, filtered, locked, replace, device?.Platform))
            {
                files.ValuesChanged();
                var deviceName = device?.DisplayName ?? auth.Device;
                files.Meta.UpdatedAt = DateTime.UtcNow;
                files.Meta.UpdatedByDeviceId = deviceId;
                files.Meta.UpdatedByDeviceName = deviceName is null ? null : Truncate(deviceName, SettingsRules.MaxFieldLength);
                files.MetaChanged();
            }

            return new Dictionary<string, string?>(values, StringComparer.Ordinal);
        });
    }

    private static SyncStateDto SyncState(User user, List<EffectiveRule> rules, Dictionary<string, string?> values)
    {
        var store = SettingsStore.Current;
        var meta = store.LoadMeta(user.Id);
        var name = meta.UpdatedByDeviceName;
        if (meta.UpdatedByDeviceId is not null && store.LoadDevices(user.Id).TryGetValue(meta.UpdatedByDeviceId, out var device))
        {
            name = device.DisplayName;
        }

        return new SyncStateDto
        {
            Settings = SettingsRules.SyncMerge(SettingsSchema.Embedded, rules, values, user.Username),
            UpdatedAt = meta.UpdatedAt,
            UpdatedByDeviceId = meta.UpdatedByDeviceId,
            UpdatedByDeviceName = name
        };
    }
}
