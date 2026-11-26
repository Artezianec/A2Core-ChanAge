using System;
using System.Text;
using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Exceptions;
using GenericReader;
using Newtonsoft.Json;

namespace CUE4Parse.GameTypes.Aion2.Objects;

[JsonConverter(typeof(FAion2KeyManifestFileConverter))]
public class FAion2KeyManifestFile
{
    private const int PayloadOffset = 8;
    private const int RecordBytes = 0x30;
    // Reserved tail of every decrypted record: the oracle a candidate AppVersion is validated against.
    private const int RecordTailOffset = 40;

    private static readonly byte[] _manifestHashKey =
    [
        0xeb, 0x0b, 0xc0, 0x97, 0x28, 0x47, 0x5a, 0xb5,
        0x19, 0x6a, 0xf8, 0xce, 0x78, 0x5d, 0x8a, 0x79,
        0xdf, 0x18, 0xd2, 0x14, 0x8f, 0x51, 0xcb, 0xef,
        0x8b, 0x39, 0xe4, 0x3b, 0x2a, 0x1d, 0x40, 0x56
    ];

    private static readonly byte[] _versionContextPrefix = Encoding.UTF8.GetBytes("AION2.TableKey.AppVersion");

    /// <summary>
    /// AppVersion tried first when the manifest is versioned (a hint only: it is still validated
    /// against the decrypted records). Null recovers the version by search.
    /// </summary>
    public static string? AppVersionHint { get; set; }

    public Dictionary<ulong, FAesKey> AesKeys = [];

    /// <summary>The 32-byte AppVersion context the manifest decrypted with; null for the legacy scheme.</summary>
    public byte[]? VersionContext { get; private set; }

    /// <summary>The recovered AppVersion ("X.Y.Z"), or null for the legacy scheme.</summary>
    public string? AppVersion { get; private set; }

    public FAion2KeyManifestFile(GameFile file)
    {
        var data = file.SafeRead() ?? throw new ParserException("Unable to read key_manifest.dat");

        if (data.Length < PayloadOffset + RecordBytes || (data.Length - PayloadOffset) % RecordBytes != 0)
            throw new ParserException("Invalid AION2 key_manifest.dat header");

        var count = (data.Length - PayloadOffset) / RecordBytes;

        // Before 2.0.11: the manifest key is derived from the format root alone.
        if (TryDecrypt(data, count, Blake3_32(_manifestHashKey), out var legacy))
        {
            Load(legacy);
            return;
        }

        // 2.0.11 and later: the key is derived from an AppVersion. A hint is verified, never trusted.
        if (AppVersionHint is { Length: > 0 } hint &&
            TryVersion(data, count, hint, out var hintContext, out var hintRecords))
        {
            Load(hintRecords, hintContext, hint);
            return;
        }

        // The version is not stored in the manifest: recover it from the reserved zero tails.
        for (var major = 0; major < 10; major++)
        for (var minor = 0; minor < 10; minor++)
        for (var patch = 0; patch < 1000; patch++)
        {
            var version = $"{major}.{minor}.{patch}";
            if (!TryVersion(data, count, version, out var context, out var records)) continue;

            Load(records, context, version);
            return;
        }

        throw new ParserException("AION2 key_manifest.dat did not decrypt for any supported AppVersion");
    }

    private static bool TryVersion(byte[] data, int count, string version, out byte[] context, out byte[] plain)
    {
        context = Blake3_32(_versionContextPrefix, Encoding.UTF8.GetBytes(version));
        return TryDecrypt(data, count, Blake3_32(_manifestHashKey, context), out plain);
    }

    private static bool TryDecrypt(byte[] data, int count, byte[] key, out byte[] plain)
    {
        var aesKey = new FAesKey(key);

        // One record first: rejecting a candidate costs one 48-byte block, not the whole manifest.
        var first = data.Decrypt(PayloadOffset, RecordBytes, aesKey);
        if (!HasZeroTails(first))
        {
            plain = [];
            return false;
        }

        plain = data.Decrypt(PayloadOffset, count * RecordBytes, aesKey);
        return HasZeroTails(plain);
    }

    private static bool HasZeroTails(ReadOnlySpan<byte> records)
    {
        for (var offset = 0; offset + RecordBytes <= records.Length; offset += RecordBytes)
        for (var index = 0; index < 8; index++)
            if (records[offset + RecordTailOffset + index] != 0)
                return false;

        return true;
    }

    private void Load(byte[] plain, byte[]? context = null, string? version = null)
    {
        AesKeys = new Dictionary<ulong, FAesKey>(plain.Length / RecordBytes);
        using var reader = new GenericBufferReader(plain);
        for (var offset = 0; offset < plain.Length; offset += RecordBytes)
        {
            var seed = reader.Read<ulong>();
            var key = reader.ReadArray<byte>(32);
            reader.Position += 8;   // reserved zero tail
            AesKeys[seed] = new FAesKey(key);
        }

        VersionContext = context;
        AppVersion = version;
    }

    internal static byte[] Blake3_32(params byte[][] parts)
    {
        using var hasher = Hasher.New();
        foreach (var part in parts)
            hasher.Update(part);

        return hasher.Finalize().AsSpan().ToArray();
    }
}

public class FAion2KeyManifestFileConverter : JsonConverter<FAion2KeyManifestFile>
{
    public override FAion2KeyManifestFile? ReadJson(JsonReader reader, Type objectType, FAion2KeyManifestFile? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        throw new NotImplementedException();
    }

    public override void WriteJson(JsonWriter writer, FAion2KeyManifestFile? value, JsonSerializer serializer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName(nameof(value.AesKeys));
        serializer.Serialize(writer, value?.AesKeys);

        writer.WriteEndObject();
    }
}
