using System.Security.Cryptography;
using System.Text.Json;
using filestore.Models;

namespace filestore.Services;

/// <summary>
/// Reads and writes the profile list as an encrypted file.
///
/// Encryption is AES-256-GCM. The 32-byte key is generated randomly on first
/// use and kept in profiles.key next to the data; both files are readable only
/// by the current user on Linux/macOS.
///
/// profiles.dat layout: "S3FE" | version (1 byte) | nonce (12) | tag (16) | ciphertext
/// </summary>
public sealed class CredentialStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte Version = 1;
    private static readonly byte[] Magic = "S3FE"u8.ToArray();
    private static readonly int HeaderSize = Magic.Length + 1;

    private readonly string _dataPath;
    private readonly string _keyPath;

    public CredentialStore(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "filestore");
        Directory.CreateDirectory(folder);
        _dataPath = Path.Combine(folder, "profiles.dat");
        _keyPath = Path.Combine(folder, "profiles.key");
    }

    public ProfileData Load()
    {
        if (!File.Exists(_dataPath))
            return new ProfileData();

        try
        {
            var plain = Decrypt(File.ReadAllBytes(_dataPath), GetOrCreateKey());
            return JsonSerializer.Deserialize<ProfileData>(plain) ?? new ProfileData();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            // Move the unreadable file aside so saving new profiles doesn't overwrite it.
            var badPath = _dataPath + ".unreadable";
            File.Move(_dataPath, badPath, overwrite: true);
            throw new InvalidDataException(
                $"Saved profiles could not be decrypted. The file was renamed to {badPath}.", ex);
        }
    }

    public void Save(ProfileData data)
    {
        var encrypted = Encrypt(JsonSerializer.SerializeToUtf8Bytes(data), GetOrCreateKey());

        // Write to a temp file then swap it in, so a crash can't leave a half-written file.
        var tempPath = _dataPath + ".tmp";
        File.Delete(tempPath);
        WriteOwnerOnly(tempPath, encrypted);
        File.Move(tempPath, _dataPath, overwrite: true);
    }

    private static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var output = new byte[HeaderSize + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(output, 0);
        output[Magic.Length] = Version;

        var header = output.AsSpan(0, HeaderSize);
        var nonce = output.AsSpan(HeaderSize, NonceSize);
        var tag = output.AsSpan(HeaderSize + NonceSize, TagSize);
        var cipher = output.AsSpan(HeaderSize + NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, header);
        return output;
    }

    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (data.Length < HeaderSize + NonceSize + TagSize
            || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || data[Magic.Length] != Version)
        {
            throw new InvalidDataException("profiles.dat is not a recognised profile file.");
        }

        var header = data.AsSpan(0, HeaderSize);
        var nonce = data.AsSpan(HeaderSize, NonceSize);
        var tag = data.AsSpan(HeaderSize + NonceSize, TagSize);
        var cipher = data.AsSpan(HeaderSize + NonceSize + TagSize);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, header);
        return plain;
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var key = File.ReadAllBytes(_keyPath);
            if (key.Length != KeySize)
                throw new InvalidDataException("profiles.key is damaged.");
            return key;
        }

        var newKey = RandomNumberGenerator.GetBytes(KeySize);
        WriteOwnerOnly(_keyPath, newKey);
        return newKey;
    }

    /// <summary>Creates a new file that only the current user can read (mode 600 on Unix).</summary>
    private static void WriteOwnerOnly(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }
}
