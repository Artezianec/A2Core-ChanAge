using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.Utils;
using GenericReader;
using K4os.Compression.LZ4;

namespace CUE4Parse.GameTypes.Aion2.Encryption.Aes;

public sealed partial class Aion2DatFileEncryption
{
    private static readonly byte[] _nonceSuffix = Encoding.ASCII.GetBytes("nonc");

    public static byte[] DecryptDataTable(byte[] dat, string filePath)
    {
        using var Ar = new GenericBufferReader(dat);
        if (Ar.Length < 0x0C || Ar.Read<int>() != 13)
            throw new ParserException("Invalid AION2 data table container header");

        var seed = Blake3Hash(Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/')));
        if (!AesKeys.TryGetValue(seed, out var key))
            throw new ParserException($"AION2 data table content key not found for '{filePath}'");

        var header = new FDataTableHeader(Ar, seed);
        return header.EncryptionType switch
        {
            EEncryptionType.CompressedAES => DecryptCompressedDataTable(Ar, header, key),
            EEncryptionType.XorAES => DecryptStreamDataTable(Ar, header, key),
            _ => throw new ParserException($"Unsupported AION2 data table encryption type '{header.EncryptionType}'"),
        };
    }

    private static byte[] DecryptStreamDataTable(GenericBufferReader Ar, FDataTableHeader header, FAesKey key)
    {
        if (header.OutputSize <= 0 || header.OutputSize != Ar.Length - Ar.Position)
            throw new ParserException("Invalid current AION2 data table stream header");

        using var nonceHasher = Hasher.New();
        nonceHasher.Update(key.Key);
        nonceHasher.Update(_nonceSuffix);
        var nonce = BinaryPrimitives.ReadUInt64LittleEndian(nonceHasher.Finalize().AsSpan());

        var encrypted = Ar.ReadMemory(header.OutputSize).Span;
        var output = new byte[header.OutputSize];
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key.Key, null);
        var counter = new byte[16];
        var stream = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(counter, nonce);
        for (var offset = 0; offset < output.Length; offset += 16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(counter.AsSpan(8), (ulong) offset / 16);
            encryptor.TransformBlock(counter, 0, 16, stream, 0);
            var blockSize = Math.Min(16, output.Length - offset);
            TensorUtils.Xor(encrypted.Slice(offset, blockSize), stream.AsSpan(0, blockSize), output.AsSpan(offset, blockSize));
        }

        return output;
    }

    private static byte[] DecryptCompressedDataTable(GenericBufferReader Ar, FDataTableHeader header, FAesKey key)
    {
        var encryptedMemory = Ar.ReadMemory(header.AlignedSize);
        if (!MemoryMarshal.TryGetArray(encryptedMemory, out ArraySegment<byte> encrypted))
            throw new ParserException("AION2 data table payload is not backed by an array");

        var decrypted = encrypted.Decrypt(key);
        var output = new byte[header.RawSize];
        var written = LZ4Codec.Decode(decrypted.AsSpan(0x20, header.PackedSize), output);
        if (written != output.Length)
            throw new ParserException($"AION2 data table LZ4 decode failed ({written}/{output.Length})");

        return output;
    }

    private readonly struct FDataTableHeader
    {
        public readonly EEncryptionType EncryptionType;
        public readonly int OutputSize;
        public readonly int PackedSize;
        public readonly int AlignedSize;
        public readonly int RawSize;

        public FDataTableHeader(GenericBufferReader Ar, ulong seed)
        {
            var start = Ar.Position;
            var compressedKey = DeriveHeaderKey(seed, EHeaderType.CompressedDataTable);

            // The encryption type may sit in any of the four words, so peek the whole 16-byte header
            // before deciding. A stream table has only an 8-byte header: rewind when it is not compressed.
            if (Ar.Length - start >= 0x10)
            {
                var encoded = Ar.ReadArray<byte>(0x10);
                FAion2DatFileArchive.DecryptData(encoded, compressedKey);

                var fields = ReadCompressedHeader(encoded);
                if ((EEncryptionType) fields.EncryptionType == EEncryptionType.CompressedAES)
                {
                    EncryptionType = EEncryptionType.CompressedAES;
                    PackedSize = fields.PackedSize;
                    AlignedSize = fields.AlignedSize;
                    RawSize = fields.RawSize;
                    OutputSize = 0;
                    return;
                }

                Ar.Position = start;
            }

            var streamKey = DeriveHeaderKey(seed, EHeaderType.StreamDataTable);
            var prefix = Ar.ReadArray<byte>(8);
            FAion2DatFileArchive.DecryptData(prefix, streamKey);

            using var streamAr = new GenericBufferReader(prefix);
            OutputSize = streamAr.Read<int>();
            EncryptionType = streamAr.Read<EEncryptionType>();
            PackedSize = 0;
            AlignedSize = 0;
            RawSize = 0;
        }
    }
}
