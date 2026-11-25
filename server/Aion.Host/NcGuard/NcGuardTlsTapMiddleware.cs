using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Aion.Host.NcGuard;

/// <summary>
/// Logs the first application bytes after TLS and (CANDIDATE) echoes small payloads on SNI-less sockets
/// that never complete HTTP — playtest correlate with the ~150 s NCGuard watchdog.
/// </summary>
public static class NcGuardTlsTapMiddleware
{
    public static bool LooksLikeHttp(ReadOnlySpan<byte> span) => TappedDuplexPipe.LooksLikeHttp(span);
    internal static void Use(ListenOptions listener, ILogger logger) => listener.Use(async (connection, next) =>
    {
        if (connection is not DefaultConnectionContext defaults)
        {
            await next();
            return;
        }
        var sni = connection.Features.Get<ITlsHandshakeFeature>()?.HostName;
        var handshake = connection.Features.Get<ITlsHandshakeFeature>();
        var alpn = connection.Features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol;
        var alpnText = alpn is { Length: > 0 } proto ? System.Text.Encoding.UTF8.GetString(proto.Span) : "(none)";
        logger.LogInformation(
            "NCGuard TLS application stream Remote={Remote} SNI={Sni} ALPN={Alpn} Cipher={Cipher}",
            connection.RemoteEndPoint, sni ?? "(none)", alpnText, handshake?.NegotiatedCipherSuite);
        defaults.Application = new TappedDuplexPipe(defaults.Application!, logger, sni, alpnText, connection.ConnectionClosed);
        await next();
    });

    sealed class TappedDuplexPipe : IDuplexPipe
    {
        readonly PipeReader input;
        readonly PipeWriter output;
        readonly ILogger logger;
        readonly string? sni;
        readonly string alpn;
        readonly CancellationToken closed;
        int logged;
        int echoed;

        public TappedDuplexPipe(IDuplexPipe inner, ILogger logger, string? sni, string alpn, CancellationToken closed)
        {
            this.logger = logger;
            this.sni = sni;
            this.alpn = alpn;
            this.closed = closed;
            input = new TapReader(inner.Input, this);
            output = inner.Output;
            if (sni is null)
                _ = ServerIdlePokeAsync(inner.Output);
        }

        public PipeReader Input => input;
        public PipeWriter Output => output;

        async Task ServerIdlePokeAsync(PipeWriter writer)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), closed);
                if (closed.IsCancellationRequested || Volatile.Read(ref logged) != 0) return;
                // CANDIDATE: some builds open a no-SNI TLS socket and wait for any application bytes before HTTP.
                var poke = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();
                logger.LogWarning(
                    "NCGuard no-SNI idle 60s with zero application bytes (ALPN={Alpn}); sending HTTP/1.1 200 poke",
                    alpn);
                await writer.WriteAsync(poke, closed);
                await writer.FlushAsync(closed);
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        }

        internal void OnFirstBytes(ReadOnlySequence<byte> buffer)
        {
            if (Interlocked.Exchange(ref logged, 1) != 0) return;
            var len = (int)Math.Min(128, buffer.Length);
            var preview = buffer.Slice(0, len).ToArray();
            var looksHttp = LooksLikeHttp(preview);
            logger.LogWarning(
                "NCGuard first application bytes SNI={Sni} ALPN={Alpn} len={Length} httpish={Httpish} hex={Hex}",
                sni ?? "(none)", alpn, buffer.Length, looksHttp, Convert.ToHexString(preview));
        }

        internal bool ShouldEcho(ReadOnlySequence<byte> buffer) =>
            sni is null && Interlocked.CompareExchange(ref echoed, 1, 0) == 0
            && buffer.Length is > 0 and <= 4096
            && !LooksLikeHttp(buffer.First.Span);

        internal void WriteEcho(ReadOnlySequence<byte> buffer, PipeWriter echoTo)
        {
            logger.LogInformation("NCGuard echoing {Length}B on no-SNI socket (CANDIDATE heartbeat)", buffer.Length);
            foreach (var segment in buffer) echoTo.Write(segment.Span);
        }

        internal static bool LooksLikeHttp(ReadOnlySpan<byte> span)
        {
            if (span.Length >= 3 && span[0] is (byte)'G' or (byte)'P' or (byte)'H' or (byte)'O' or (byte)'D') return true;
            return span.Length >= 24 && span[..24].SequenceEqual("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8);
        }

        sealed class TapReader : PipeReader
        {
            readonly PipeReader inner;
            readonly TappedDuplexPipe tap;

            public TapReader(PipeReader inner, TappedDuplexPipe tap)
            {
                this.inner = inner;
                this.tap = tap;
            }

            public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
            public override void CancelPendingRead() => inner.CancelPendingRead();
            public override void Complete(Exception? exception = null) => inner.Complete(exception);

            public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                var result = await inner.ReadAsync(cancellationToken);
                if (!result.IsCompleted && result.Buffer.Length > 0)
                {
                    tap.OnFirstBytes(result.Buffer);
                    if (tap.ShouldEcho(result.Buffer))
                    {
                        tap.WriteEcho(result.Buffer, tap.Output);
                        await tap.Output.FlushAsync(cancellationToken);
                    }
                }
                return result;
            }

            public override bool TryRead(out ReadResult result)
            {
                if (!inner.TryRead(out result)) return false;
                if (!result.IsCompleted && result.Buffer.Length > 0)
                {
                    tap.OnFirstBytes(result.Buffer);
                    if (tap.ShouldEcho(result.Buffer))
                    {
                        tap.WriteEcho(result.Buffer, tap.Output);
                        tap.Output.FlushAsync().GetAwaiter().GetResult();
                    }
                }
                return true;
            }
        }
    }
}