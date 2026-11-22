using Aion.Protocol.Native;

namespace Aion.HeadlessClient.Protocol;

/// <summary>Login and game flows for a headless native client against our server.</summary>
public sealed class HeadlessSession
{
    readonly ServerPacketDecoder decoder;
    readonly SessionMetrics metrics = new();
    readonly ClientRsaKey rsa;

    public HeadlessSession(ServerPacketDecoder decoder, ClientRsaKey? rsa = null)
    {
        this.decoder = decoder;
        rsa = rsa ?? ClientRsaKey.Generate();
        this.rsa = rsa;
    }

    public SessionMetrics Metrics => metrics;

    public async Task<GameWorldClient> LoginAndEnterWorldAsync(LoginTarget target, GameTarget game, HeadlessAccount account, CancellationToken ct)
    {
        var loginStarted = DateTimeOffset.UtcNow;
        await using var login = await NativeConnection.ConnectAsync(target.Host, target.LoginPort, NativeHandshake.LoginGreeting, rsa, ct);
        var authToken = account.AuthnToken;
        await login.SendCipheredAsync(ClientPackets.LoginIdentity(authToken), ct);
        var accountReply = await login.WaitForOpcodeAsync(0x3906, TimeSpan.FromSeconds(15), ct);
        var accountSession = ParseAccountSession(accountReply);
        await login.SendCipheredAsync(ClientPackets.LoginWorldDirectory(), ct);
        _ = await login.WaitForOpcodeAsync(0x3909, TimeSpan.FromSeconds(10), ct);
        await login.SendCipheredAsync(ClientPackets.LoginWorldSelect(target.WorldId), ct);
        _ = await login.WaitForOpcodeAsync(0x390d, TimeSpan.FromSeconds(10), ct);
        await login.SendCipheredAsync(ClientPackets.LoginRedirect(target.WorldId), ct);
        var redirect = await login.WaitForOpcodeAsync(0x390f, TimeSpan.FromSeconds(10), ct);
        var gamePort = ParseRedirectPort(redirect, game.Port);
        metrics.RecordLogin(loginStarted);

        var gameStarted = DateTimeOffset.UtcNow;
        var connection = await NativeConnection.ConnectAsync(game.Host, gamePort, NativeHandshake.GameGreeting, rsa, ct);
        await connection.SendCipheredAsync(ClientPackets.GameIdentity(accountSession, authToken), ct);
        var envelope = await connection.WaitForOpcodeAsync(0xffff, TimeSpan.FromSeconds(20), ct);
        RecordFrames(decoder.UnwrapEnvelope(envelope));
        await connection.SendCipheredAsync(ClientPackets.GameNameCheck(account.CharacterName), ct);
        _ = await connection.WaitForOpcodeAsync(0x361a, TimeSpan.FromSeconds(10), ct);
        await connection.SendCipheredAsync(ClientPackets.GameCreate(account.CharacterName), ct);
        _ = await connection.WaitForOpcodeAsync(0x361c, TimeSpan.FromSeconds(10), ct);
        await connection.SendCipheredAsync(ClientPackets.GameSelectCharacter(65536), ct);
        var selected = await connection.WaitForOpcodeAsync(0x3621, TimeSpan.FromSeconds(20), ct);
        var spawn = ParsePrepareEnterMap(selected);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await connection.SendCipheredAsync(ClientPackets.GameLoadComplete(), ct);
        await DrainBackgroundAsync(connection, TimeSpan.FromSeconds(4), ct);
        metrics.RecordWorldEntry(gameStarted);
        return new GameWorldClient(connection, decoder, metrics, spawn);
    }

    public async Task<GameWorldClient> ConnectDevelopmentGameAsync(GameTarget game, HeadlessAccount account, CancellationToken ct)
    {
        var gameStarted = DateTimeOffset.UtcNow;
        const string accountSid = "1000001";
        const string grant = "00000000-0000-0000-0000-000000000000";
        var session = $"{accountSid}:{grant}";
        var authToken = account.AuthnToken;
        var connection = await NativeConnection.ConnectAsync(game.Host, game.Port, NativeHandshake.GameGreeting, rsa, ct);
        await connection.SendCipheredAsync(ClientPackets.GameIdentity(session, authToken), ct);
        _ = await connection.WaitForOpcodeAsync(0xffff, TimeSpan.FromSeconds(20), ct);
        await connection.SendCipheredAsync(ClientPackets.GameNameCheck(account.CharacterName), ct);
        _ = await connection.WaitForOpcodeAsync(0x361a, TimeSpan.FromSeconds(10), ct);
        await connection.SendCipheredAsync(ClientPackets.GameCreate(account.CharacterName), ct);
        _ = await connection.WaitForOpcodeAsync(0x361c, TimeSpan.FromSeconds(10), ct);
        await connection.SendCipheredAsync(ClientPackets.GameSelectCharacter(65536), ct);
        var selected = await connection.WaitForOpcodeAsync(0x3621, TimeSpan.FromSeconds(20), ct);
        var spawn = ParsePrepareEnterMap(selected);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await connection.SendCipheredAsync(ClientPackets.GameLoadComplete(), ct);
        await DrainBackgroundAsync(connection, TimeSpan.FromSeconds(4), ct);
        metrics.RecordWorldEntry(gameStarted);
        return new GameWorldClient(connection, decoder, metrics, spawn);
    }

    static async Task DrainBackgroundAsync(NativeConnection connection, TimeSpan window, CancellationToken ct) =>
        await connection.ReceivePlainAsync(window, 256, ct);

    void RecordFrames(IEnumerable<byte[]> bodies)
    {
        foreach (var body in bodies)
        {
            var parsed = decoder.Parse(body);
            metrics.RecordServerPacket(parsed);
        }
    }

    static string ParseAccountSession(byte[] accountReply)
    {
        for (var i = 2; i < accountReply.Length - 40; i++)
        {
            if (!char.IsAsciiDigit((char)accountReply[i])) continue;
            var end = i;
            while (end < accountReply.Length && accountReply[end] is not (0 or 8) && end - i < 80) end++;
            var candidate = System.Text.Encoding.ASCII.GetString(accountReply, i, end - i);
            var parts = candidate.Split(':', 2);
            if (parts.Length == 2 && parts[0].Length is >= 1 and <= 16 && Guid.TryParse(parts[1], out _))
                return candidate;
        }
        throw new ProtocolException("Account session missing from 0x3906");
    }

    static int ParseRedirectPort(byte[] redirect, int fallback)
    {
        if (redirect.Length >= 4) return BitConverter.ToUInt16(redirect, redirect.Length - 2);
        return fallback;
    }

    static WorldSpawn ParsePrepareEnterMap(byte[] body)
    {
        if (body.Length >= 30)
            return new WorldSpawn(new Vector3d(
                BitConverter.ToSingle(body, 18),
                BitConverter.ToSingle(body, 22),
                BitConverter.ToSingle(body, 26)), 0);
        return new WorldSpawn(new Vector3d(7259.9, -18223.4, 22404.5), 0);
    }
}

public sealed record LoginTarget(string Host, int LoginPort, int WorldId = 1361);
public sealed record GameTarget(string Host, int Port);
public sealed record HeadlessAccount(string CharacterName, string AuthnToken);
public sealed record WorldSpawn(Vector3d Position, float HeadingDegrees);

public sealed class GameWorldClient : IAsyncDisposable
{
    readonly NativeConnection connection;
    readonly ServerPacketDecoder decoder;
    readonly SessionMetrics metrics;
    ulong pingCounter;

    public GameWorldClient(NativeConnection connection, ServerPacketDecoder decoder, SessionMetrics metrics, WorldSpawn spawn)
    {
        this.connection = connection;
        this.decoder = decoder;
        this.metrics = metrics;
        Spawn = spawn;
    }

    public WorldSpawn Spawn { get; }

    public async Task PingAsync(CancellationToken ct)
    {
        pingCounter++;
        var sent = DateTimeOffset.UtcNow;
        await connection.SendCipheredAsync(ClientPackets.GamePing(pingCounter), ct);
        var reply = await connection.WaitForOpcodeAsync(0x3603, TimeSpan.FromSeconds(5), ct);
        if (reply.Length >= 12) metrics.RecordPing(sent, BitConverter.ToUInt64(reply, 4));
    }

    public async Task WalkRouteAsync(IReadOnlyList<Vector3d> route, float speedCmPerSecond, double reportsPerSecond, double maxSeconds, CancellationToken ct)
    {
        var position = Spawn.Position;
        var heading = Spawn.HeadingDegrees;
        var step = TimeSpan.FromSeconds(1 / Math.Max(0.25, reportsPerSecond));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, maxSeconds));
        foreach (var target in route)
        {
            while (Distance(position, target) > speedCmPerSecond * step.TotalSeconds && DateTimeOffset.UtcNow < deadline)
            {
                heading = (float)(Math.Atan2(target.Y - position.Y, target.X - position.X) * 180 / Math.PI);
                var rad = heading * Math.PI / 180;
                var advance = speedCmPerSecond * step.TotalSeconds;
                position = new Vector3d(position.X + Math.Cos(rad) * advance, position.Y + Math.Sin(rad) * advance, position.Z);
                await connection.SendCipheredAsync(ClientPackets.PositionReport(0x3701, position, heading), ct);
                await Task.Delay(step, ct);
            }
            position = target;
        }
        await connection.SendCipheredAsync(ClientPackets.PositionReport(0x3700, position, heading), ct);
    }

    public async Task AutoAttackAsync(uint targetKey, int skillId, CancellationToken ct)
    {
        await connection.SendCipheredAsync(ClientPackets.SkillCast(0x3800, skillId, targetKey), ct);
        var frames = await connection.ReceivePlainAsync(TimeSpan.FromSeconds(3), 8, ct);
        foreach (var frame in frames)
            metrics.RecordServerPacket(decoder.Parse(frame));
    }

    public async Task TalkToNpcAsync(int npcKey, CancellationToken ct)
    {
        await connection.SendCipheredAsync(ClientPackets.NpcInteract(npcKey), ct);
        var frames = await connection.ReceivePlainAsync(TimeSpan.FromSeconds(3), 16, ct);
        foreach (var frame in frames) metrics.RecordServerPacket(decoder.Parse(frame));
    }

    public async Task ChatAsync(string text, CancellationToken ct) =>
        await connection.SendCipheredAsync(ClientPackets.ChatSay(0, text), ct);

    public async Task AcceptQuestAsync(int questId, CancellationToken ct)
    {
        await connection.SendCipheredAsync(ClientPackets.QuestAccept(questId), ct);
        var frames = await connection.ReceivePlainAsync(TimeSpan.FromSeconds(3), 16, ct);
        foreach (var frame in frames) metrics.RecordServerPacket(decoder.Parse(frame));
    }

    public async Task IdleAsync(TimeSpan duration, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < end)
        {
            await PingAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    static double Distance(Vector3d a, Vector3d b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}