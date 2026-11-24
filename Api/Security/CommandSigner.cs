using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Security;

/// <summary>
/// Admin commands the apps honor only when they're signed with this server's key: reset a
/// profile's PIN, lock a profile, unlock it. PINs never leave the devices; the server only says
/// what to do with them, and the app checks the signature against the public key it pinned the
/// first time it saw this server.
/// <para>
/// ECDSA P-256 with SHA-256 and the raw r‖s signature, which is what CryptoKit's
/// <c>P256.Signing</c> reads. The private key lives in the <see cref="SecretVault"/>; the public key,
/// its fingerprint and the proof that a new key was made by the old one are kept in plain JSON.
/// </para>
/// </summary>
public sealed class CommandSigner
{
    public const string PrivateKeyName = "signing.privateKey";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private static readonly object Gate = new();
    private readonly SecretVault _vault;
    private readonly string _keyPath;
    private readonly string _commandsPath;
    private readonly string _historyPath;
    private const int MaxHistory = 1000;

    public CommandSigner(string folder, SecretVault vault)
    {
        _vault = vault;
        _keyPath = Path.Combine(folder, "signing.json");
        _historyPath = Path.Combine(folder, "signing-history.json");
        _commandsPath = Path.Combine(folder, "commands.json");
    }

    public static CommandSigner Current => new(Plugin.Instance!.DataFolderPath, SecretVault.Current);

    /// <summary>The public key, or null before an admin has made or added one.</summary>
    public PublicKeyInfo? PublicKey
    {
        get
        {
            lock (Gate)
            {
                return _vault.Has(PrivateKeyName) ? JsonFile.Read<PublicKeyInfo>(_keyPath) : null;
            }
        }
    }

    /// <summary>
    /// The key, made now if there isn't one yet, so devices learn it on their first sync, long
    /// before an admin needs to send anything.
    /// </summary>
    public PublicKeyInfo EnsureKey()
    {
        lock (Gate)
        {
            return PublicKey ?? Generate();
        }
    }

    /// <summary>Makes a new key, signed over by the one it replaces.</summary>
    public PublicKeyInfo Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Install(key);
    }

    /// <summary>
    /// Throws the key away and makes an entirely new one that nothing vouches for: no signature
    /// from the old key, and the history of past keys deleted, so the old key can't be used to
    /// move devices anywhere. For a key that may have leaked. Standing commands are signed again
    /// with the new key so locks stay in force.
    /// </summary>
    public PublicKeyInfo Reset()
    {
        lock (Gate)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var spki = key.ExportSubjectPublicKeyInfo();
            var info = new PublicKeyInfo
            {
                PublicKey = Convert.ToBase64String(spki),
                Fingerprint = Fingerprint(spki),
                Created = DateTime.UtcNow,
                ResetAt = DateTime.UtcNow
            };
            _vault.Set(PrivateKeyName, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
            JsonFile.Write(_keyPath, info);
            JsonFile.Write(_historyPath, new KeyHistory());
            ResignCommands(key);
            return info;
        }
    }

    /// <summary>Adds the admin's own key: a P-256 private key in PEM (PKCS#8 or SEC1).</summary>
    public PublicKeyInfo Import(string pem)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            throw new ArgumentException("That isn't a private key in PEM. Use an EC P-256 key, e.g. openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt.", e);
        }

        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new ArgumentException("Only EC P-256 (prime256v1) keys can be used.");
        }

        var info = Install(key);
        info.Imported = true;
        JsonFile.Write(_keyPath, info);
        return info;
    }

    /// <summary>
    /// Signs a command for one user, replacing that user's earlier command of the same kind
    /// (a lock replaces an unlock and the other way round). Returns null without a key.
    /// </summary>
    public SignedCommand? Issue(string kind, Guid userId, string serverId, DateTime now, string? deviceId = null,
                                Dictionary<string, string>? data = null)
    {
        lock (Gate)
        {
            using var key = LoadPrivateKey();
            if (key is null)
            {
                return null;
            }

            var file = JsonFile.Read<CommandFile>(_commandsPath) ?? new CommandFile();
            file.Counter++;
            // A lock or unlock is the user's current state, so it stays until the next one;
            // a PIN reset is a one-off that devices pick up within a month.
            var expires = now + (kind is CommandKind.ResetPin or CommandKind.Wipe ? Lifetime : TimeSpan.FromDays(3650));
            var payload = new CommandPayload
            {
                Version = 1,
                Kind = kind,
                User = userId.ToString("N"),
                Server = serverId,
                Counter = file.Counter,
                Issued = new DateTimeOffset(now).ToUnixTimeSeconds(),
                Expires = new DateTimeOffset(expires).ToUnixTimeSeconds(),
                Device = deviceId,
                Data = data is { Count: > 0 } ? data : null
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var command = new SignedCommand
            {
                Payload = Convert.ToBase64String(bytes),
                Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
                Kind = kind,
                UserId = userId,
                Counter = file.Counter,
                Expires = expires,
                DeviceId = deviceId,
                Data = payload.Data
            };

            file.Commands.RemoveAll(c => c.UserId == userId && (c.Expires <= now || (Group(c.Kind) == Group(kind) && c.DeviceId == deviceId)));
            file.Commands.Add(command);
            JsonFile.Write(_commandsPath, file);
            return command;
        }
    }

    /// <summary>A user's commands that haven't expired, oldest first.</summary>
    public List<SignedCommand> CommandsFor(Guid userId, DateTime now)
    {
        lock (Gate)
        {
            var file = JsonFile.Read<CommandFile>(_commandsPath) ?? new CommandFile();
            return file.Commands.Where(c => c.UserId == userId && c.Expires > now).OrderBy(c => c.Counter).ToList();
        }
    }

    /// <summary>Whether a user's latest lock, on all their devices, is still in force.</summary>
    public bool IsLocked(Guid userId, DateTime now) => IsLocked(userId, null, now);

    /// <summary>Whether a lock for just this one device is in force.</summary>
    public bool IsLocked(Guid userId, string? deviceId, DateTime now)
    {
        var latest = CommandsFor(userId, now).LastOrDefault(c => Group(c.Kind) == "lock" && c.DeviceId == deviceId);
        return latest?.Kind == CommandKind.Lock;
    }

    /// <summary>
    /// The keys after the one with <paramref name="fingerprint"/>, oldest first, ending with the
    /// current one: each signed by the one before, so a device that missed several rotations can
    /// follow them all. Empty when the fingerprint is the current key or isn't known.
    /// </summary>
    public List<PublicKeyInfo> ChainFrom(string? fingerprint)
    {
        lock (Gate)
        {
            var current = PublicKey;
            if (current is null || string.IsNullOrEmpty(fingerprint) || fingerprint == current.Fingerprint)
            {
                return new List<PublicKeyInfo>();
            }

            var all = (JsonFile.Read<KeyHistory>(_historyPath)?.Keys ?? new List<PublicKeyInfo>()).Append(current).ToList();
            var start = all.FindIndex(k => k.Fingerprint == fingerprint);
            return start < 0 ? new List<PublicKeyInfo>() : all.Skip(start + 1).ToList();
        }
    }

    /// <summary>Checks a command against a public key, as the apps do. For tests and the dashboard.</summary>
    public static bool Verify(SignedCommand command, string publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(Convert.FromBase64String(command.Payload), Convert.FromBase64String(command.Signature),
                              HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static string Group(string kind) => kind is CommandKind.Lock or CommandKind.Unlock ? "lock" : kind;

    private PublicKeyInfo Install(ECDsa key)
    {
        lock (Gate)
        {
            var spki = key.ExportSubjectPublicKeyInfo();
            var info = new PublicKeyInfo
            {
                PublicKey = Convert.ToBase64String(spki),
                Fingerprint = Fingerprint(spki),
                Created = DateTime.UtcNow
            };

            using var previous = LoadPrivateKey();
            if (previous is not null)
            {
                var previousSpki = previous.ExportSubjectPublicKeyInfo();
                if (!previousSpki.SequenceEqual(spki))
                {
                    info.PreviousPublicKey = Convert.ToBase64String(previousSpki);
                    info.RotationSignature = Convert.ToBase64String(
                        previous.SignData(spki, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                }
            }

            if (PublicKey is { } replaced && replaced.PublicKey != info.PublicKey)
            {
                var history = JsonFile.Read<KeyHistory>(_historyPath) ?? new KeyHistory();
                history.Keys.Add(replaced);
                if (history.Keys.Count > MaxHistory)
                {
                    history.Keys.RemoveRange(0, history.Keys.Count - MaxHistory);
                }

                JsonFile.Write(_historyPath, history);
            }

            _vault.Set(PrivateKeyName, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
            JsonFile.Write(_keyPath, info);
            ResignCommands(key);
            return info;
        }
    }

    /// <summary>
    /// Standing commands (locks, unlocks, PIN resets not yet expired) signed again with the new
    /// key, same payload and counter, so a device that has moved to it can still check them: a
    /// lock must outlast every rotation.
    /// </summary>
    private void ResignCommands(ECDsa key)
    {
        var file = JsonFile.Read<CommandFile>(_commandsPath);
        if (file is null || file.Commands.Count == 0)
        {
            return;
        }

        foreach (var command in file.Commands)
        {
            var bytes = Convert.FromBase64String(command.Payload);
            command.Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        JsonFile.Write(_commandsPath, file);
    }

    private ECDsa? LoadPrivateKey()
    {
        var stored = _vault.Get(PrivateKeyName);
        if (stored is null)
        {
            return null;
        }

        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
        return key;
    }

    public static string Fingerprint(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        return string.Join(":", hash.Take(8).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private sealed class CommandFile
    {
        public long Counter { get; set; }

        public List<SignedCommand> Commands { get; set; } = new();
    }

    private sealed class CommandPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("server")]
        public string Server { get; set; } = string.Empty;

        [JsonPropertyName("counter")]
        public long Counter { get; set; }

        [JsonPropertyName("issued")]
        public long Issued { get; set; }

        [JsonPropertyName("expires")]
        public long Expires { get; set; }

        /// <summary>One device only, by its Jellyfin device ID; absent for all of the user's devices.</summary>
        [JsonPropertyName("device")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Device { get; set; }

        /// <summary>What a policy sets, or why a lock was sent (reason: trust, pinTries); signed with the rest.</summary>
        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Data { get; set; }
    }

    private sealed class KeyHistory
    {
        public List<PublicKeyInfo> Keys { get; set; } = new();
    }
}

public static class CommandKind
{
    public const string ResetPin = "resetPin";
    public const string Lock = "lock";
    public const string Unlock = "unlock";

    /// <summary>Delete this device's downloads, sign it out and lock Arcfin on it.</summary>
    public const string Wipe = "wipe";

    /// <summary>The user's rules: PIN (required, length, tries) and HTTPS away from home.</summary>
    public const string Policy = "policy";

    public static bool IsKnown(string kind) => kind is ResetPin or Lock or Unlock;
}

public sealed class PublicKeyInfo
{
    /// <summary>SubjectPublicKeyInfo DER, base64.</summary>
    public string PublicKey { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    /// <summary>The key this one replaced, when there was one.</summary>
    public string? PreviousPublicKey { get; set; }

    /// <summary>The previous key's signature over this key's DER, so apps that pinned it move over.</summary>
    public string? RotationSignature { get; set; }

    /// <summary>The admin's own key rather than one the plugin made; it isn't rotated automatically.</summary>
    public bool Imported { get; set; }

    /// <summary>Set when an admin replaced the key completely, with no link to the one before.</summary>
    public DateTime? ResetAt { get; set; }
}

public sealed class SignedCommand
{
    /// <summary>The signed bytes, base64: JSON with v, kind, user, server, counter, issued and expires.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>ECDSA P-256 SHA-256, raw r‖s, base64.</summary>
    public string Signature { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public long Counter { get; set; }

    public DateTime Expires { get; set; }

    /// <summary>One device only, or null for all of the user's devices.</summary>
    public string? DeviceId { get; set; }

    /// <summary>The same data that's in the signed payload, kept here so the server can compare without parsing.</summary>
    public Dictionary<string, string>? Data { get; set; }
}
