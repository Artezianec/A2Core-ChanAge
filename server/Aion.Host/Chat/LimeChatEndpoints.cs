using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Aion.Application.Chat;
using Aion.Protocol.Lime;

namespace Aion.Host.Chat;

/// <summary>
/// The dev client's Lime chat API on its own loopback port (policy custom_data ChatServerURL):
/// POST /gameLogin/loginWithToken, POST /gameClient/{route} and STOMP over WebSocket at
/// /stomp. Sessions are keyed by the mtalk token the client sends as Authorization.
/// </summary>
public static class LimeChatEndpoints
{
    public static void MapLimeChat(this WebApplication app, int port, string publicAddress = "127.0.0.1")
    {
        // The native SDK omits the port from Host during WebSocket upgrades.
        // Isolate chat by the actual listener, not the client-supplied header.
        var chat = app.MapGroup("").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Connection.LocalPort == port
                ? await next(context) : Results.NotFound());
        var stompUrl = $"ws://{publicAddress}:{port}/stomp";

        chat.MapPost("/gameLogin/{route}", async (string route, HttpContext context, ChatRelay relay, StompHub hub,
            IChatLoginVerifier verifier, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            if (route is "logoutWithJwtToken" or "logout")
            {
                await LogoutAsync(LimeWire.Token(context.Request.Headers.Authorization.ToString()), relay, hub);
                var reply = LimeWire.Ok();
                reply["message"] = "Logged out";
                return Results.Json(reply);
            }
            if (route is not ("loginWithToken" or "loginWithGameAccessToken")) return Results.Json(LimeWire.Ok());
            var body = await ReadAsync(context, cancellationToken);
            var request = LimeWire.ReadLogin(body);
            var identity = await verifier.VerifyAsync(request, cancellationToken);
            var log = logs.CreateLogger("Aion.Chat");
            if (identity is null)
            {
                // Never the token. Account logins need a valid game ticket and a character in the world.
                var tokenShape = request.AuthnToken is { Length: 64 } t && t.All(char.IsAsciiHexDigit) ? "game_ticket" : "invalid_token";
                log.LogInformation(
                    "Chat login rejected ({Reason}; server key {ServerKey}, character id {CharacterId}, token {TokenShape})",
                    tokenShape == "game_ticket" ? "game ticket invalid or character not in world yet" : "authnToken is not a 64-char game ticket",
                    Digits(request.ServerKey), Digits(request.CharacterId), tokenShape);
                return Results.Json(LimeWire.Error(401, tokenShape == "game_ticket"
                    ? "Chat login rejected: invalid game ticket or character not in the world yet."
                    : "Chat login rejected: authnToken must be the 64-character game ticket."));
            }
            log.LogInformation("Chat login {Name} ({Kind})", identity.Character?.Name ?? identity.CharacterId,
                identity.Authenticated ? "account" : "development");
            return Results.Json(LimeWire.LoginReply(relay.Login(identity), identity, stompUrl));
        });

        chat.MapPost("/gameClient/{route}", async (string route, HttpContext context, ChatRelay relay,
            StompHub hub, CancellationToken cancellationToken) =>
        {
            var token = LimeWire.Token(context.Request.Headers.Authorization.ToString());
            var body = await ReadAsync(context, cancellationToken);
            var commands = context.RequestServices.GetService<IChatCommandHandler>();
            try
            {
                return Results.Json(await HandleAsync(route, token, body, relay, hub, stompUrl, commands, cancellationToken));
            }
            catch (ChatRejectedException rejected)
            {
                var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aion.Chat");
                var sessionKnown = string.IsNullOrEmpty(token) ? "no_mtalk_token"
                    : relay.Find(token) is null ? "chat session not authenticated" : "authenticated";
                log.LogInformation("Chat {Route} rejected ({SessionState}): {Reason}", route, sessionKnown, rejected.Message);
                return Results.Json(LimeWire.Error(400, sessionKnown == "chat session not authenticated"
                    ? "Chat session is not authenticated. Log in with POST /gameLogin/loginWithToken first."
                    : rejected.Message));
            }
        });

        // Loopback only (deploy/linux/update.sh): who is online, and a chat announcement to everyone.
        chat.MapGet("/internal/online", (HttpContext context, IServiceProvider services) =>
            System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
                ? Results.Ok(new { players = services.GetService<Aion.World.WorldPresence>()?.All.Count ?? 0 }) : Results.NotFound());
        // Loopback only (tools/load_test.py): world lock totals since startup and the process CPU seconds.
        chat.MapGet("/internal/world", (HttpContext context, IServiceProvider services) =>
            System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
            && services.GetService<Aion.World.SharedWorld>() is { } world
                ? Results.Ok(new
                {
                    players = services.GetService<Aion.World.WorldPresence>()?.All.Count ?? 0,
                    connections = services.GetService<Aion.World.WorldPresence>()?.Connections ?? 0,
                    cpuSeconds = Aion.Host.Native.WorldLoadReporter.CpuSeconds(),
                    gcHeapBytes = GC.GetTotalMemory(false),
                    gen0 = GC.CollectionCount(0),
                    gen1 = GC.CollectionCount(1),
                    gen2 = GC.CollectionCount(2),
                    locks = world.Lock.Stats.Read(),
                }) : Results.NotFound());
        chat.MapPost("/internal/announce", async (HttpContext context, ChatRelay relay, StompHub hub) =>
        {
            if (!System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)) return Results.NotFound();
            using var reader = new StreamReader(context.Request.Body);
            var text = (await reader.ReadToEndAsync(context.RequestAborted)).Trim();
            if (text.Length is 0 or > 100) return Results.BadRequest();
            var deliveries = relay.Announce(text);
            await hub.DeliverAsync(deliveries);
            return Results.Ok(new { delivered = deliveries.Count });
        });

        chat.Map("/stomp", async (HttpContext context, ChatRelay relay, StompHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            var offered = context.WebSockets.WebSocketRequestedProtocols;
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                offered.FirstOrDefault(p => p == "v12.stomp") ?? offered.FirstOrDefault());
            await hub.RunAsync(socket, relay, context.RequestAborted);
            return Results.Empty;
        });
    }

    static async Task<JsonObject> HandleAsync(string route, string token, JsonNode? body, ChatRelay relay,
        StompHub hub, string stompUrl, IChatCommandHandler? commands, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "getSubscriptionInfo":
                var identity = relay.Find(token) ?? throw new ChatRejectedException("Chat session is not authenticated.");
                return LimeWire.LoginReply(token, identity, stompUrl);
            case "logoutWithJwtToken" or "logout":
            {
                await LogoutAsync(token, relay, hub);
                var reply = LimeWire.Ok();
                reply["message"] = "Logged out";
                return reply;
            }
            case "joinRoom" or "createRoom" or "getRoom" or "updateRoom":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                if (route is "joinRoom" or "createRoom") relay.Join(token, room);
                return LimeWire.RoomReply(room, relay.Find(token), relay.MemberCount(room), relay.StartedAtMilliseconds);
            }
            case "leaveRoom" or "deleteRoom":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                relay.Leave(token, room);
                return LimeWire.RoomReply(room, relay.Find(token), relay.MemberCount(room), relay.StartedAtMilliseconds);
            }
            case "getBlockUserList":
                // The social store only answers "is there a block between these two characters"; it cannot list a
                // character's blocks yet, so the list is empty (whisper blocks are still enforced by the relay).
                return LimeWire.BlockedUsers();
            case "sendMessage" or "sendWhisper":
            {
                var room = LimeWire.ReadRoom(body?["gameRoomKeyInfo"]);
                var message = route == "sendWhisper" ? LimeWire.ReadWhisper(body) : LimeWire.ReadMessage(body?["gameMessageInfo"]);
                if (route == "sendMessage" && commands is not null && commands.Owns(message.Content))
                {
                    // Commands are answered privately and never shown to the room. The handler gets
                    // the relay's sender name (the world's stored name for account logins), not the
                    // client's userName.
                    var (commandGuid, answer) = await relay.CommandAsync(token, room, message,
                        (sender, name) => commands.HandleAsync(sender, name, message.Content, cancellationToken));
                    await hub.DeliverAsync(answer);
                    return LimeWire.SendReply(commandGuid);
                }
                var (guid, deliveries) = route == "sendWhisper"
                    ? relay.Whisper(token, room, message)
                    : relay.Send(token, room, message);
                await hub.DeliverAsync(deliveries);
                return LimeWire.SendReply(guid);
            }
            case "getJoinedRoomList":
            {
                var reply = LimeWire.Ok();
                reply["gameRoomInfoList"] = new JsonArray();
                return reply;
            }
            case "getJoinedRoomListWithRoomUsers":
            {
                var reply = LimeWire.Ok();
                reply["roomWithRoomUserInfoList"] = new JsonArray();
                return reply;
            }
            default:
                // Block lists, history, profiles and voice are not implemented yet: success, empty.
                return LimeWire.Ok();
        }
    }

    static async Task LogoutAsync(string token, ChatRelay relay, StompHub hub)
    {
        relay.Logout(token);
        await hub.DisconnectAsync(token);
    }

    /// <summary>Client text for logs: decimal digits only (IDs, server keys), else "?".</summary>
    static string Digits(string text) => text is { Length: >= 1 and <= 20 } && text.All(char.IsAsciiDigit) ? text : "?";

    static async Task<JsonNode?> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// STOMP heart-beat and idle limits. The defaults follow the official server: it answers
/// CONNECT with <c>heart-beat:0,30000</c> (it sends none and wants a client beat every 30 s).
/// </summary>
/// <param name="ServerSendMs">What the server can send (first number of CONNECTED's heart-beat); 0 sends none.</param>
/// <param name="ServerReceiveMs">What the server wants to receive (second number); 0 expects none.</param>
/// <param name="ConnectTimeout">How long a socket may stay silent before CONNECT.</param>
/// <param name="IdleFallback">The silence limit when no client heart-beat was negotiated.</param>
public sealed record StompHubOptions(int ServerSendMs = 0, int ServerReceiveMs = 30000,
    TimeSpan? ConnectTimeout = null, TimeSpan? IdleFallback = null)
{
    public TimeSpan ConnectLimit => ConnectTimeout ?? TimeSpan.FromSeconds(30);
    public TimeSpan FallbackLimit => IdleFallback ?? TimeSpan.FromMinutes(10);
}

/// <summary>
/// STOMP connections by session token; delivers notifications as MESSAGE frames. A socket that
/// goes silent past its negotiated heart-beat (or never sends CONNECT) is closed, and a closed
/// socket ends its chat session (<see cref="ChatRelay.Logout"/>) unless another socket still
/// uses the same token.
/// </summary>
public sealed class StompHub(StompHubOptions? options = null)
{
    sealed record Subscription(string Id, string Destination);

    sealed class Connection(WebSocket socket, TimeSpan idleLimit)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string Token { get; set; } = "";
        public List<Subscription> Subscriptions { get; } = [];
        public TimeSpan IdleLimit { get; set; } = idleLimit;
        public int SendIntervalMs { get; set; }
        public CancellationTokenSource? Stop { get; set; }
    }

    readonly StompHubOptions settings = options ?? new StompHubOptions();
    readonly ConcurrentDictionary<Connection, byte> connections = new();

    public async Task RunAsync(WebSocket socket, ChatRelay relay, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket, settings.ConnectLimit);
        connections[connection] = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connection.Stop = stop;
        var buffer = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();  // keeps multi-byte characters split across reads
        var text = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                idle.CancelAfter(connection.IdleLimit);
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    break;  // idle past the negotiated heart-beat: the socket is dead
                }
                if (result.MessageType == WebSocketMessageType.Close) break;
                text.Append(chars, 0, decoder.GetChars(buffer, 0, result.Count, chars, 0));
                if (text.Length > 1 << 20) break;
                foreach (var frame in StompFrame.ReadAll(text))
                {
                    await HandleAsync(connection, frame, relay, stop, cancellationToken);
                    if (stop.IsCancellationRequested) break;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        finally
        {
            stop.Cancel();
            connections.TryRemove(connection, out _);
            if (connection.Token.Length > 0 && !connections.Keys.Any(c => c.Token == connection.Token))
                relay.Logout(connection.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>Closes the STOMP sockets of a session (logout route); the session itself is ended by the caller.</summary>
    public async Task DisconnectAsync(string token)
    {
        if (token.Length == 0) return;
        foreach (var connection in connections.Keys.Where(c => c.Token == token))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (connection.Socket.State == WebSocketState.Open)
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                connection.Stop?.Cancel();
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// The session token a CONNECT carries: the authorization header, else the passcode and login
    /// headers the client takes from subscriptionInfo; "Bearer " is optional in each.
    /// </summary>
    static string? ConnectToken(StompFrame frame, ChatRelay relay) =>
        new[] { "authorization", "passcode", "login" }.Select(name => LimeWire.Token(frame.Header(name)))
            .FirstOrDefault(token => token.Length > 0 && relay.Find(token) is not null);

    async Task HandleAsync(Connection connection, StompFrame frame, ChatRelay relay, CancellationTokenSource stop,
        CancellationToken cancellationToken)
    {
        switch (frame.Command)
        {
            case "CONNECT" or "STOMP":
                if (ConnectToken(frame, relay) is not { } token)
                {
                    await SendAsync(connection, StompFrame.Create("ERROR",
                        new Dictionary<string, string> { ["message"] = "not authenticated" }).Encode(), cancellationToken);
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, cancellationToken);
                    return;
                }
                connection.Token = token;
                Negotiate(connection, frame.Header("heart-beat"));
                await SendAsync(connection, StompFrame.Create("CONNECTED", new Dictionary<string, string>
                {
                    ["version"] = "1.2", ["heart-beat"] = $"{settings.ServerSendMs},{settings.ServerReceiveMs}",
                    ["server"] = "aion-local-chat/1",
                }).Encode(), cancellationToken);
                if (connection.SendIntervalMs > 0) _ = BeatAsync(connection, stop.Token);
                break;
            case "SUBSCRIBE":
                lock (connection.Subscriptions)
                    connection.Subscriptions.Add(new Subscription(frame.Header("id"), frame.Header("destination")));
                break;
            case "UNSUBSCRIBE":
                lock (connection.Subscriptions)
                    connection.Subscriptions.RemoveAll(s => s.Id == frame.Header("id"));
                break;
        }
        if (frame.Header("receipt") is { Length: > 0 } receipt)
            await SendAsync(connection, StompFrame.Create("RECEIPT",
                new Dictionary<string, string> { ["receipt-id"] = receipt }).Encode(), cancellationToken);
        if (frame.Command == "DISCONNECT") stop.Cancel();
    }

    /// <summary>
    /// STOMP heart-beat negotiation: a direction beats only when both sides offer it, at the
    /// larger interval. The client's beat sets the idle limit (1.5 intervals); without one the
    /// socket gets the long fallback limit.
    /// </summary>
    void Negotiate(Connection connection, string clientHeartBeat)
    {
        var parts = clientHeartBeat.Split(',');
        var clientSend = parts.Length > 0 && int.TryParse(parts[0], out var cx) ? Math.Max(cx, 0) : 0;
        var clientReceive = parts.Length > 1 && int.TryParse(parts[1], out var cy) ? Math.Max(cy, 0) : 0;
        connection.IdleLimit = clientSend > 0 && settings.ServerReceiveMs > 0
            ? TimeSpan.FromMilliseconds(Math.Max(clientSend, settings.ServerReceiveMs) * 1.5)
            : settings.FallbackLimit;
        connection.SendIntervalMs = clientReceive > 0 && settings.ServerSendMs > 0 ? Math.Max(clientReceive, settings.ServerSendMs) : 0;
    }

    static async Task BeatAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(connection.SendIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SendAsync(connection, "\n"u8.ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public async Task DeliverAsync(IEnumerable<ChatDelivery> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            var body = LimeWire.NotificationEnvelope(delivery.Notification);
            foreach (var connection in connections.Keys.Where(c => c.Token == delivery.SessionToken))
            {
                Subscription? subscription;
                lock (connection.Subscriptions) subscription = connection.Subscriptions.FirstOrDefault();
                if (subscription is null) continue;
                var frame = StompFrame.Create("MESSAGE", new Dictionary<string, string>
                {
                    ["destination"] = subscription.Destination, ["subscription"] = subscription.Id,
                    ["message-id"] = delivery.Notification.Guid, ["content-type"] = "application/json",
                    ["content-length"] = Encoding.UTF8.GetByteCount(body).ToString(),
                }, body);
                await SendAsync(connection, frame.Encode(), CancellationToken.None);
            }
        }
    }

    static async Task SendAsync(Connection connection, byte[] data, CancellationToken cancellationToken)
    {
        await connection.Gate.WaitAsync(cancellationToken);
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
                await connection.Socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException) { }
        finally
        {
            connection.Gate.Release();
        }
    }
}
