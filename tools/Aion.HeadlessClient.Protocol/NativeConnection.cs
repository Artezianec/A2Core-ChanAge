using System.Net.Sockets;
using Aion.Protocol.Native;

namespace Aion.HeadlessClient.Protocol;

/// <summary>TCP native port with handshake, RC4 client encryption, and framed I/O.</summary>
public sealed class NativeConnection : IAsyncDisposable
{
    readonly TcpClient client;
    readonly NetworkStream stream;
    readonly Rc4 cipher;
    readonly FrameBuffer buffer = new();
    readonly ushort greetingTag;

    public NativeConnection(TcpClient client, NetworkStream stream, Rc4 cipher, ushort greetingTag)
    {
        this.client = client;
        this.stream = stream;
        this.cipher = cipher;
        this.greetingTag = greetingTag;
    }

    public static async Task<NativeConnection> ConnectAsync(string host, int port, ushort greetingTag, ClientRsaKey rsa, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(host, port, linked.Token);
        var stream = client.GetStream();
        var greetingBody = greetingTag == NativeHandshake.LoginGreeting ? rsa.LoginGreeting() : rsa.GameGreeting();
        var greeting = NativeFraming.Frame(greetingBody);
        await stream.WriteAsync(greeting, ct);
        var reply = await ReadOneFrameAsync(stream, TimeSpan.FromSeconds(15), ct);
        var sessionKey = rsa.UnwrapSessionKey(reply);
        return new NativeConnection(client, stream, new Rc4(sessionKey), greetingTag);
    }

    public async Task SendCipheredAsync(byte[] plainBody, CancellationToken ct)
    {
        var ciphered = cipher.Crypt(plainBody);
        await stream.WriteAsync(NativeFraming.Frame(ciphered), ct);
    }

    public async Task<byte[][]> ReceivePlainAsync(TimeSpan window, int maxFrames, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + window;
        var output = new List<byte[]>();
        while (output.Count < maxFrames && DateTime.UtcNow < deadline)
        {
            foreach (var body in buffer.Drain())
                output.Add(body);
            if (output.Count >= maxFrames) break;
            using var readWindow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readWindow.CancelAfter(deadline - DateTime.UtcNow);
            try
            {
                var chunk = new byte[65536];
                var read = await stream.ReadAsync(chunk, readWindow.Token);
                if (read == 0) break;
                buffer.Append(chunk.AsSpan(0, read));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        foreach (var body in buffer.Drain())
            output.Add(body);
        return [.. output.Take(maxFrames)];
    }

    public async Task<byte[]> WaitForOpcodeAsync(ushort opcode, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frames = await ReceivePlainAsync(TimeSpan.FromMilliseconds(250), 64, ct);
            foreach (var frame in frames)
            {
                if (frame.Length >= 2 && (ushort)(frame[0] | frame[1] << 8) == opcode) return frame;
            }
        }
        throw new TimeoutException($"Opcode 0x{opcode:x4} not received within {timeout.TotalSeconds:F0}s");
    }

    static async Task<byte[]> ReadOneFrameAsync(NetworkStream stream, TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new FrameBuffer();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);
        var chunk = new byte[8192];
        while (true)
        {
            var frames = buffer.Drain();
            if (frames.Count > 0) return frames[0];
            var read = await stream.ReadAsync(chunk, window.Token);
            if (read == 0) throw new IOException("Connection closed during handshake");
            buffer.Append(chunk.AsSpan(0, read));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        client.Dispose();
    }
}