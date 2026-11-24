using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Security;

/// <summary>
/// The server's secrets (the Seerr and TMDB keys, the signing key), encrypted at rest with
/// AES-256-GCM and kept out of the plugin's XML configuration, which the dashboard reads back
/// whole and backups copy. Secrets are only ever read here, on the server; no endpoint returns one.
/// <para>
/// The master key comes from, in order: <c>ARCFIN_SYNC_MASTER_KEY</c> (32 bytes, base64),
/// the file named by <c>ARCFIN_SYNC_MASTER_KEY_FILE</c> (e.g. a Docker secret), or a key file the
/// plugin makes beside the vault, readable by the server's account only. Keeping the master key
/// outside the data folder means a copied config folder or backup holds nothing readable.
/// </para>
/// </summary>
public sealed class SecretVault
{
    public const string MasterKeyVariable = "ARCFIN_SYNC_MASTER_KEY";
    public const string MasterKeyFileVariable = "ARCFIN_SYNC_MASTER_KEY_FILE";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly object Gate = new();
    private readonly string _vaultPath;
    private readonly byte[] _masterKey;

    public SecretVault(string folder, Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        _vaultPath = Path.Combine(folder, "vault.json");
        (_masterKey, KeySource) = LoadMasterKey(folder, env);
    }

    public static SecretVault Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>Where the master key came from: "environment", "file" or "generated".</summary>
    public string KeySource { get; }

    public bool Has(string name)
    {
        lock (Gate)
        {
            return ReadFile().Entries.ContainsKey(name);
        }
    }

    /// <summary>The secret, or null when it isn't set or can't be decrypted (the master key changed).</summary>
    public string? Get(string name)
    {
        VaultEntry? entry;
        lock (Gate)
        {
            ReadFile().Entries.TryGetValue(name, out entry);
        }

        if (entry is null)
        {
            return null;
        }

        try
        {
            var nonce = Convert.FromBase64String(entry.Nonce);
            var cipher = Convert.FromBase64String(entry.Cipher);
            var tag = Convert.FromBase64String(entry.Tag);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_masterKey, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(name));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Saves a secret; an empty or null value removes it.</summary>
    public void Set(string name, string? value)
    {
        lock (Gate)
        {
            var file = ReadFile();
            if (string.IsNullOrEmpty(value))
            {
                file.Entries.Remove(name);
            }
            else
            {
                var plain = Encoding.UTF8.GetBytes(value);
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var cipher = new byte[plain.Length];
                var tag = new byte[TagSize];
                using (var aes = new AesGcm(_masterKey, TagSize))
                {
                    aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
                }

                CryptographicOperations.ZeroMemory(plain);
                file.Entries[name] = new VaultEntry
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Cipher = Convert.ToBase64String(cipher),
                    Tag = Convert.ToBase64String(tag)
                };
            }

            JsonFile.Write(_vaultPath, file, ownerOnly: true);
        }
    }

    private VaultFile ReadFile() => JsonFile.Read<VaultFile>(_vaultPath) ?? new VaultFile();

    private static (byte[] Key, string Source) LoadMasterKey(string folder, Func<string, string?> env)
    {
        var fromEnv = env(MasterKeyVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return (Decode(fromEnv, MasterKeyVariable), "environment");
        }

        var keyFile = env(MasterKeyFileVariable);
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            return (Decode(File.ReadAllText(keyFile), MasterKeyFileVariable), "file");
        }

        var generated = Path.Combine(folder, "vault.key");
        lock (Gate)
        {
            if (!File.Exists(generated))
            {
                Directory.CreateDirectory(folder);
                var key = RandomNumberGenerator.GetBytes(KeySize);
                File.WriteAllText(generated, Convert.ToBase64String(key));
                JsonFile.RestrictToOwner(generated);
                return (key, "generated");
            }

            return (Decode(File.ReadAllText(generated), generated), "generated");
        }
    }

    private static byte[] Decode(string text, string from)
    {
        try
        {
            var key = Convert.FromBase64String(text.Trim());
            if (key.Length == KeySize)
            {
                return key;
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidOperationException($"{from} must hold a 32-byte key in base64 (openssl rand -base64 32).");
    }

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, VaultEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class VaultEntry
    {
        public string Nonce { get; set; } = string.Empty;

        public string Cipher { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;
    }
}
