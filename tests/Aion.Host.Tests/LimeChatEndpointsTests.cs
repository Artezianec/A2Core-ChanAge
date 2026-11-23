using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Application.Chat;
using Aion.Host.Chat;
using Aion.Protocol.Lime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aion.Host.Tests;

public sealed class LimeChatEndpointsTests
{
    const string ServerKey = "1204";
    static readonly JsonObject Region = new() { ["type"] = "REGION", ["serverKey"] = ServerKey, ["roomKey"] = "RG" };

    sealed class Chat : IAsyncDisposable
    {
        public WebApplication App { get; }
        public ChatRelay Relay { get; }
        public HttpClient Http { get; }

        public Chat(StompHubOptions? options = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(provider => new ChatRelay(provider.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton(new StompHub(options));
            builder.Services.AddSingleton<IChatLoginVerifier, DevelopmentChatLoginVerifier>();
            App = builder.Build();
            App.UseWebSockets();
            App.MapLimeChat(0);
            App.StartAsync().GetAwaiter().GetResult();
            Relay = App.Services.GetRequiredService<ChatRelay>();
            Http = App.GetTestClient();
        }

        public async Task<(string Token, JsonNode Reply)> LoginAsync(string characterId = "1099511629056", string locale = "ru-RU")
        {
            var reply = await PostAsync("/gameLogin/loginWithToken", new JsonObject
            {
                ["authnToken"] = "local", ["gameCode"] = "aion2", ["deviceId"] = "d", ["clientType"] = "PC",
                ["characterId"] = characterId, ["serverKey"] = ServerKey, ["locale"] = locale,
            }, "");
            return ((string)reply["mtalkToken"]!, reply);
        }

        public async Task<JsonNode> PostAsync(string path, JsonNode body, string authorization)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (authorization.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var response = await Http.SendAsync(request);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        public async Task<WebSocket> ConnectAsync()
        {
            var client = App.GetTestServer().CreateWebSocketClient();
            client.SubProtocols.Add("v12.stomp");
            return await client.ConnectAsync(new Uri("ws://localhost/stomp"), Timeout());
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    static CancellationToken Timeout(int seconds = 5) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static Task SendFrameAsync(WebSocket socket, string command, params (string Name, string Value)[] headers) =>
        SendRawAsync(socket, StompFrame.Create(command, headers.ToDictionary(h => h.Name, h => h.Value)).Encode());

    static Task SendRawAsync(WebSocket socket, byte[] data) =>
        socket.SendAsync(data, WebSocketMessageType.Text, true, Timeout());

    static async Task<string> ReceiveTextAsync(WebSocket socket, int seconds = 5)
    {
        var buffer = new byte[16384];
        var result = await socket.ReceiveAsync(buffer, Timeout(seconds));
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    static async Task<StompFrame> ReceiveFrameAsync(WebSocket socket) => StompFrame.Parse((await ReceiveTextAsync(socket)).TrimEnd('\0'));

    static async Task<bool> ClosedWithinAsync(WebSocket socket, int seconds)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
                if ((await socket.ReceiveAsync(buffer, Timeout(seconds))).MessageType == WebSocketMessageType.Close) return true;
        }
        catch (WebSocketException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    static async Task<WebSocket> ConnectStompAsync(Chat chat, string tokenHeader, string value, string heartBeat = "0,0")
    {
        var socket = await chat.ConnectAsync();
        await SendFrameAsync(socket, "CONNECT", (tokenHeader, value), ("accept-version", "1.0,1.1,1.2"), ("heart-beat", heartBeat));
        return socket;
    }

    [Fact]
    public async Task Bearer_prefix_is_accepted_on_http_routes_and_room_replies_use_official_fields()
    {
        await using var chat = new Chat();
        var (token, _) = await chat.LoginAsync();
        var joined = await chat.PostAsync("/gameClient/joinRoom", new JsonObject { ["gameRoomKeyInfo"] = Region.DeepClone() }, "Bearer " + token);

        Assert.Equal(0, (int)joined["error"]!);
        var info = joined["gameRoomInfo"]!;
        Assert.Equal("aion2", (string)info["gameCode"]!);
        Assert.Equal(JsonValueKind.Number, info["gameRoomId"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, info["dateCreated"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, info["adminGameUserId"]!.GetValueKind());
        Assert.Equal(1, (int)info["memberCount"]!);
        Assert.False((bool)info["deleted"]!);
        Assert.Equal(1099511629056, (long)joined["gameUserId"]!);
        Assert.Null(info["lastMessageGuid"]);

        var bare = await chat.PostAsync("/gameClient/getRoom", new JsonObject { ["gameRoomKeyInfo"] = Region.DeepClone() }, token);
        Assert.Equal((long)info["gameRoomId"]!, (long)bare["gameRoomInfo"]!["gameRoomId"]!);

        var guild = LimeWire.RoomInfo(new ChatRoomKey("GUILD", ServerKey, "GU1"));
        Assert.Equal(JsonValueKind.Number, guild["dateLastMessagePublished"]!.GetValueKind());
        Assert.Equal("", (string)guild["lastMessageGuid"]!);
    }

    [Fact]
    public async Task Block_list_is_an_empty_blockedUserList_array()
    {
        await using var chat = new Chat();
        var (token, _) = await chat.LoginAsync();
        var reply = await chat.PostAsync("/gameClient/getBlockUserList", new JsonObject(), "Bearer " + token);

        Assert.Equal(0, (int)reply["error"]!);
        Assert.Empty(reply["blockedUserList"]!.AsArray());
    }

    [Theory]
    [InlineData("authorization", false)]
    [InlineData("authorization", true)]
    [InlineData("passcode", false)]
    [InlineData("passcode", true)]
    public async Task Stomp_connect_accepts_the_token_in_authorization_or_passcode_with_optional_bearer(string header, bool bearer)
    {
        await using var chat = new Chat();
        var (token, login) = await chat.LoginAsync();
        var value = header == "passcode" ? (string)login["subscriptionInfo"]!["passcode"]! : token;
        using var socket = await ConnectStompAsync(chat, header, bearer ? "Bearer " + value : value);

        var connected = await ReceiveFrameAsync(socket);
        Assert.Equal("CONNECTED", connected.Command);
        Assert.Equal("1.2", connected.Header("version"));
        Assert.Equal("0,30000", connected.Header("heart-beat"));
    }

    [Fact]
    public async Task Stomp_connect_with_an_unknown_token_is_refused()
    {
        await using var chat = new Chat();
        using var socket = await ConnectStompAsync(chat, "passcode", "mtalk-unknown");

        Assert.Equal("ERROR", (await ReceiveFrameAsync(socket)).Command);
    }

    [Fact]
    public async Task Message_json_data_matches_the_official_shape()
    {
        await using var chat = new Chat();
        var (token, login) = await chat.LoginAsync(locale: "ru-RU");
        using var socket = await ConnectStompAsync(chat, "passcode", "Bearer " + token);
        Assert.Equal("CONNECTED", (await ReceiveFrameAsync(socket)).Command);
        await SendFrameAsync(socket, "SUBSCRIBE", ("id", "sub-0"), ("destination", (string)login["subscriptionInfo"]!["topicName"]!));
        var joined = await chat.PostAsync("/gameClient/joinRoom", new JsonObject { ["gameRoomKeyInfo"] = Region.DeepClone() }, "Bearer " + token);
        await Task.Delay(200);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sent = await chat.PostAsync("/gameClient/sendMessage", new JsonObject
        {
            ["gameRoomKeyInfo"] = Region.DeepClone(),
            ["gameMessageInfo"] = new JsonObject { ["userName"] = "Saledar", ["type"] = "PUBLISH", ["subType"] = "NORMAL", ["content"] = "hello" },
        }, "Bearer " + token);
        Assert.Equal(0, (int)sent["error"]!);

        var message = await ReceiveFrameAsync(socket);
        Assert.Equal("MESSAGE", message.Command);
        var data = JsonNode.Parse(message.Body)!["jsonData"]!;
        Assert.Equal(JsonValueKind.String, data["dateCreated"]!.GetValueKind());
        Assert.InRange(long.Parse((string)data["dateCreated"]!), before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(((long)joined["gameRoomInfo"]!["gameRoomId"]!).ToString(), (string)data["roomId"]!);
        Assert.Equal("Saledar", (string)data["alias"]!);
        Assert.Equal("Saledar", (string)data["userName"]!);
        Assert.Equal("JOINER", (string)data["role"]!);
        Assert.Equal("aion2", (string)data["gameCode"]!);
        Assert.Equal("aion2", (string)data["senderGameCode"]!);
        Assert.Equal("ru-RU", (string)data["locale"]!);
        Assert.Equal("ru", (string)data["language"]!);
        Assert.Equal("RU", (string)data["country"]!);
        Assert.NotNull(data["userMetaData"]!["faction"]);
        Assert.Equal("hello", (string)data["content"]!);
    }

    [Fact]
    public async Task Logout_route_ends_the_session_and_closes_its_socket()
    {
        await using var chat = new Chat();
        var (token, _) = await chat.LoginAsync();
        using var socket = await ConnectStompAsync(chat, "authorization", token);
        await ReceiveFrameAsync(socket);

        var reply = await chat.PostAsync("/gameLogin/logoutWithJwtToken", new JsonObject(), "Bearer " + token);

        Assert.Equal(0, (int)reply["error"]!);
        Assert.Null(chat.Relay.Find(token));
        Assert.True(await ClosedWithinAsync(socket, 3));
        var after = await chat.PostAsync("/gameClient/getSubscriptionInfo", new JsonObject(), token);
        Assert.NotEqual(0, (int)after["error"]!);
    }

    [Fact]
    public async Task Closing_the_socket_ends_the_session()
    {
        await using var chat = new Chat();
        var (token, _) = await chat.LoginAsync();
        var socket = await ConnectStompAsync(chat, "authorization", token);
        await ReceiveFrameAsync(socket);
        Assert.NotNull(chat.Relay.Find(token));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Timeout());
        socket.Dispose();

        await WaitForAsync(() => chat.Relay.Find(token) is null);
        Assert.Null(chat.Relay.Find(token));
    }

    [Fact]
    public async Task A_second_socket_with_the_same_token_keeps_the_session_alive_when_the_first_closes()
    {
        await using var chat = new Chat();
        var (token, _) = await chat.LoginAsync();
        var first = await ConnectStompAsync(chat, "authorization", token);
        await ReceiveFrameAsync(first);
        using var second = await ConnectStompAsync(chat, "authorization", token);
        await ReceiveFrameAsync(second);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Timeout());
        first.Dispose();
        await Task.Delay(300);

        Assert.NotNull(chat.Relay.Find(token));
    }

    [Fact]
    public async Task Relogin_replaces_the_previous_session_of_the_same_character()
    {
        await using var chat = new Chat();
        var (first, _) = await chat.LoginAsync();
        var (second, _) = await chat.LoginAsync();
        var (other, _) = await chat.LoginAsync("1099511629057");

        Assert.Null(chat.Relay.Find(first));
        Assert.NotNull(chat.Relay.Find(second));
        Assert.NotNull(chat.Relay.Find(other));
    }

    [Fact]
    public async Task A_client_that_never_sends_connect_is_closed()
    {
        await using var chat = new Chat(new StompHubOptions(ConnectTimeout: TimeSpan.FromMilliseconds(200)));
        using var socket = await chat.ConnectAsync();

        Assert.True(await ClosedWithinAsync(socket, 5));
    }

    [Fact]
    public async Task A_silent_client_is_closed_after_the_negotiated_heartbeat_and_its_session_ends()
    {
        await using var chat = new Chat(new StompHubOptions(ServerReceiveMs: 100));
        var (token, _) = await chat.LoginAsync();
        using var socket = await ConnectStompAsync(chat, "authorization", token, "100,0");
        var connected = await ReceiveFrameAsync(socket);
        Assert.Equal("0,100", connected.Header("heart-beat"));

        Assert.True(await ClosedWithinAsync(socket, 5));
        await WaitForAsync(() => chat.Relay.Find(token) is null);
        Assert.Null(chat.Relay.Find(token));
    }

    [Fact]
    public async Task Client_heartbeats_keep_the_socket_open()
    {
        await using var chat = new Chat(new StompHubOptions(ServerReceiveMs: 400));
        var (token, _) = await chat.LoginAsync();
        using var socket = await ConnectStompAsync(chat, "authorization", token, "400,0");
        await ReceiveFrameAsync(socket);

        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100);
            await SendRawAsync(socket, "\n"u8.ToArray());
        }

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.NotNull(chat.Relay.Find(token));
    }

    [Fact]
    public async Task The_server_sends_heartbeats_when_both_sides_offer_them()
    {
        await using var chat = new Chat(new StompHubOptions(ServerSendMs: 100));
        var (token, _) = await chat.LoginAsync();
        using var socket = await ConnectStompAsync(chat, "authorization", token, "0,100");
        var connected = await ReceiveFrameAsync(socket);
        Assert.Equal("100,30000", connected.Header("heart-beat"));

        Assert.Equal("\n", await ReceiveTextAsync(socket));
    }

    static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
    }
}
