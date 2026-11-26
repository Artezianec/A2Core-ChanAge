using System.Buffers.Binary;
using System.Text;
using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Exceptions;

namespace CUE4Parse.GameTypes.Aion2.Encryption.Aes;

public sealed partial class Aion2DatFileEncryption
{
    private const ulong HeaderXorConstant = 0xCD02190910CE83F6;
    private const int HeaderPermutationSalt = 0x51;
    // A root is a BLAKE3 u64; this sentinel means the manifest used the legacy (pre-2.0.11) scheme.
    private const long NoPermutationRoot = long.MinValue;
    private static readonly ulong _legacyHeaderKeyRoot = Blake3Hash(HeaderXorConstant);
    private static ulong _headerKeyRoot = _legacyHeaderKeyRoot;
    private static long _permutationRootBits = NoPermutationRoot;

    /// <summary>
    /// AppVersion hint for a versioned key manifest (2.0.11+); it only skips the version search and is
    /// still validated against the manifest records. Null recovers the version by search.
    /// </summary>
    public static string? AppVersionHint
    {
        get => FAion2KeyManifestFile.AppVersionHint;
        set => FAion2KeyManifestFile.AppVersionHint = value;
    }

    public static Dictionary<ulong, FAesKey> AesKeys = [];
    private static readonly Lock _instanceLock = new();

    public static void Initialize(IFileProvider provider)
    {
        if (AesKeys.Count != 0)
            return;

        var keyManifest = provider.Files.Values.FirstOrDefault(x => x.Path.EndsWith("/key_manifest.dat", StringComparison.OrdinalIgnoreCase))
            ?? throw new ParserException("Unable to locate key_manifest.dat");

        try
        {
            var manifestFile = new FAion2KeyManifestFile(keyManifest);
            lock (_instanceLock)
            {
                if (manifestFile.VersionContext is { Length: 32 } context)
                {
                    // 2.0.11+: both the header key root and the field permutation derive from the
                    // AppVersion context. Never hardcode either: the permutation changes per build.
                    var root = Blake3U64([.. BitConverter.GetBytes(HeaderXorConstant), .. context]);
                    Volatile.Write(ref _permutationRootBits, (long) root);
                    Volatile.Write(ref _headerKeyRoot, Blake3U64(BitConverter.GetBytes(root)));
                }
                else
                {
                    Volatile.Write(ref _permutationRootBits, NoPermutationRoot);
                    Volatile.Write(ref _headerKeyRoot, _legacyHeaderKeyRoot);
                }

                AesKeys = manifestFile.AesKeys;
            }

            Log.Information("AION2 key manifest: {Count} table keys, AppVersion {Version}",
                manifestFile.AesKeys.Count, manifestFile.AppVersion ?? "(legacy)");
        }
        catch (Exception error)
        {
            Log.Error(error, "Failed to read key_manifest.dat");
        }
    }

    private static byte[] DeriveHeaderKey(ulong seed, EHeaderType type)
    {
        Span<byte> input = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(input, Volatile.Read(ref _headerKeyRoot));
        BinaryPrimitives.WriteUInt64LittleEndian(input[8..], seed);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], (int) type);
        input[20..].Clear();
        using var hasher = Hasher.New();
        hasher.Update(input);

        return hasher.Finalize().AsSpan().ToArray();
    }

    private static ulong Blake3Hash(string value)
    {
        using var hasher = Hasher.New();
        hasher.Update(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    private static ulong Blake3Hash(ulong value)
    {
        Span<byte> input = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(input, value);
        using var hasher = Hasher.New();
        hasher.Update(input);
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    private static ulong Blake3U64(ReadOnlySpan<byte> input)
    {
        using var hasher = Hasher.New();
        hasher.Update(input);
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    /// <summary>The four header ints of a compressed data table or L10N header, by field.</summary>
    internal readonly record struct FCompressedHeader(int EncryptionType, int RawSize, int PackedSize, int AlignedSize);

    /// <summary>
    /// Fisher-Yates over BLAKE3(root || 0x51); the order changes with the client build
    /// (2.0.11 gives [0,3,2,1], 2.0.12 gives [2,1,3,0]). Computed for every header, never hardcoded.
    /// </summary>
    private static byte[] DeriveHeaderPermutation(ulong root)
    {
        Span<byte> input = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(input, root);
        BinaryPrimitives.WriteInt32LittleEndian(input[8..], HeaderPermutationSalt);
        var shuffle = FAion2KeyManifestFile.Blake3_32(input.ToArray());

        var permutation = new byte[] { 0, 1, 2, 3 };
        for (var index = 3; index >= 1; index--)
        {
            var other = shuffle[3 - index] % (index + 1);
            (permutation[index], permutation[other]) = (permutation[other], permutation[index]);
        }

        return permutation;
    }

    /// <summary>Maps a decrypted 16-byte compressed header to its fields: fields[permutation[i]] = word[i].</summary>
    internal static FCompressedHeader ReadCompressedHeader(ReadOnlySpan<byte> header)
    {
        Span<int> fields = stackalloc int[4];
        var rootBits = Volatile.Read(ref _permutationRootBits);

        if (rootBits != NoPermutationRoot)
        {
            var permutation = DeriveHeaderPermutation((ulong) rootBits);
            for (var index = 0; index < 4; index++)
                fields[permutation[index]] = BinaryPrimitives.ReadInt32LittleEndian(header[(index * 4)..]);
        }
        else
        {
            // Legacy layout (before 2.0.11): packed, type, aligned, raw.
            fields[0] = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            fields[1] = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            fields[2] = BinaryPrimitives.ReadInt32LittleEndian(header[0..]);
            fields[3] = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        }

        return new FCompressedHeader(fields[0], fields[1], fields[2], fields[3]);
    }

    private enum EHeaderType
    {
        CompressedDataTable = 1,
        StreamDataTable = 2,
        Localization = 3
    }

    private enum EEncryptionType : int
    {
        CompressedAES = 2,
        XorAES = 3
    }
}
