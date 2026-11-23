using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Application.Chat;

namespace Aion.Protocol.Lime;

/// <summary>
/// JSON shapes of the client's Lime chat SDK (reflected types, see
/// research/local/lime-api-schema-20261014.txt; flow in research/chat-and-gm.md).
/// Responses are BaseResponse {error, module, line, text, defined} plus route fields, camelCase.
/// </summary>
public static class LimeWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The game code the official service shows (login, rooms and messages).</summary>
    public const string GameCode = "aion2";

    /// <summary>
    /// The mtalk token from an Authorization header or STOMP header: the official client sends
    /// "Bearer {token}" on some routes and the bare token on others.
    /// </summary>
    public static string Token(string? value)
    {
        var text = (value ?? "").Trim();
        return text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? text[7..].Trim() : text;
    }

    /// <summary>
    /// The numeric room ID (gameRoomId in joinRoom, roomId in messages), derived from the full
    /// room key so both always agree without server-side room state.
    /// </summary>
    public static long RoomId(ChatRoomKey room)
    {
        var hash = 2166136261u;
        foreach (var c in $"{room.Type}/{room.ServerKey}/{room.RoomKey}")
            hash = (hash ^ c) * 16777619u;
        return 100 + (hash & 0x7FFFFFFF) % 1_000_000_000;
    }

    public static JsonObject Ok() => new() { ["error"] = 0, ["module"] = 0, ["line"] = 0, ["text"] = "", ["defined"] = "" };

    public static JsonObject Error(int code, string text)
    {
        var reply = Ok();
        reply["error"] = code;
        reply["text"] = text;
        return reply;
    }

    /// <summary>ResponseLoginWithToken: session token plus where to subscribe over STOMP.</summary>
    public static JsonObject LoginReply(string token, ChatIdentity identity, string stompUrl)
    {
        var reply = Ok();
        reply["mtalkToken"] = token;
        reply["characterId"] = identity.CharacterId;
        reply["subscriptionInfo"] = new JsonObject
        {
            ["topicName"] = UserTopic(identity),
            ["subscribeUrl"] = stompUrl,
            ["login"] = identity.CharacterId,
            ["passcode"] = token,
            ["worldTopic"] = "/topic/world." + identity.ServerKey,
            ["serverAppDest"] = "/app/chat",
        };
        reply["gameRoomInfoList"] = new JsonArray();
        return reply;
    }

    public static string UserTopic(ChatIdentity identity) => $"/topic/user.{identity.ServerKey}.{identity.CharacterId}";

    /// <summary>
    /// joinRoom's gameRoomInfo as the official service sends it: dateCreated, gameRoomId and
    /// adminGameUserId are integers; a GUILD room also carries the last-message fields.
    /// </summary>
    public static JsonObject RoomInfo(ChatRoomKey room, int memberCount = 1, long dateCreated = 0)
    {
        var info = new JsonObject
        {
            ["gameRoomKeyInfo"] = RoomKey(room), ["gameCode"] = GameCode, ["name"] = room.RoomKey,
            ["memberCount"] = memberCount, ["voiceMemberCount"] = 0, ["maxMemberCount"] = 10000,
            ["dateCreated"] = dateCreated, ["deleted"] = false,
            ["gameRoomId"] = RoomId(room), ["adminGameUserId"] = 0,
        };
        if (room.Type == "GUILD")
        {
            info["lastMessageContent"] = "";
            info["dateLastMessagePublished"] = 0;
            info["lastMessageGuid"] = "";
        }
        return info;
    }

    /// <summary>The route reply for room calls: gameRoomInfo plus the caller's numeric gameUserId.</summary>
    public static JsonObject RoomReply(ChatRoomKey room, ChatIdentity? caller, int memberCount, long dateCreated)
    {
        var reply = Ok();
        reply["gameRoomInfo"] = RoomInfo(room, memberCount, dateCreated);
        reply["gameUserId"] = long.TryParse(caller?.CharacterId, out var id) ? id : 0;
        return reply;
    }

    /// <summary>getBlockUserList: the official reply is {"blockedUserList":[]} for a player with no blocks.</summary>
    public static JsonObject BlockedUsers()
    {
        var reply = Ok();
        reply["blockedUserList"] = new JsonArray();
        return reply;
    }

    public static JsonObject RoomKey(ChatRoomKey room) =>
        new() { ["type"] = room.Type, ["serverKey"] = room.ServerKey, ["roomKey"] = room.RoomKey };

    public static JsonObject SendReply(string guid)
    {
        var reply = Ok();
        reply["guid"] = guid;
        reply["restrictInfo"] = new JsonObject
        {
            ["restrictStep"] = 0, ["startTime"] = "", ["expirationTime"] = "", ["blockedType"] = "", ["reason"] = "",
        };
        return reply;
    }

    /// <summary>
    /// STOMP MESSAGE body for a received message. OnReceiveStompMessage (0x7cde300) reads
    /// {type, tid, method, jsonData}: type NOTI/MESSAGE goes to the notification handler
    /// (0x7ce0020), which requires method "GAME" and routes on jsonData.type (PUBLISH falls
    /// through to receive-message). An empty method is dropped (0x7cde948).
    /// </summary>
    public static string NotificationEnvelope(ChatNotification n)
    {
        // The official locale is "ru-RU" style, with language and country alongside.
        var locale = n.Locale.Replace('_', '-');
        var parts = locale.Split('-', 2);
        var language = parts[0];
        var country = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
        return new JsonObject
        {
            ["type"] = "NOTI",
            ["tid"] = n.Guid,
            ["method"] = "GAME",
            ["jsonData"] = new JsonObject
            {
                ["guid"] = n.Guid, ["dateCreated"] = n.DateCreated, ["seq"] = n.Seq, ["roomId"] = RoomId(n.Room).ToString(),
                ["gameRoomKeyInfo"] = RoomKey(n.Room), ["type"] = n.Type, ["subType"] = n.SubType,
                ["attribute"] = "", ["content"] = n.Content, ["initialContent"] = n.Content, ["optional"] = n.Optional,
                ["hasReplacedContent"] = false, ["replacedMessageInfoList"] = new JsonArray(), ["isFromGame"] = true,
                ["gameCode"] = GameCode, ["gameUserId"] = n.GameUserId, ["senderGameCode"] = GameCode,
                ["serverId"] = n.ServerId, ["playNcCharId"] = n.GameUserId, ["userName"] = n.UserName, ["alias"] = n.UserName,
                ["hasCastle"] = false, ["ranking"] = 0, ["guildId"] = "",
                ["role"] = "JOINER", ["locale"] = locale, ["language"] = language, ["country"] = country,
                ["receiverCharacterId"] = n.ReceiverCharacterId, ["receiverGameUserId"] = n.ReceiverCharacterId,
                ["receiverUserName"] = n.ReceiverUserName, ["receiverServerId"] = n.ReceiverServerId,
                ["externalAccounts"] = new JsonArray(),
                ["userMetaData"] = new JsonObject
                {
                    ["authorityEffectiveFrom"] = "", ["authorityEffectiveTo"] = "", ["faction"] = n.Faction,
                },
            },
        }.ToJsonString();
    }

    public static ChatRoomKey ReadRoom(JsonNode? node) => new(
        Str(node, "type"), Str(node, "serverKey"), Str(node, "roomKey"));

    public static ChatMessage ReadMessage(JsonNode? node) => new(
        Str(node, "userName"), Str(node, "type"), Str(node, "subType"), Str(node, "content"), Str(node, "optional"),
        Str(node, "receiverCharacterId"), Str(node, "receiverUserName"), Str(node, "receiverServerId"));

    /// <summary>
    /// sendWhisper (RequestSendWhisper: GameUserKey, GameMessageInfo, ValidationInfo). The
    /// target name is ValidationInfo.TargetCharacterName (observed in the reflected schema; no
    /// live whisper request has been captured). GameUserKey's fields are not in the schema dump;
    /// a characterId there is used only when the message names no receiver ID (CANDIDATE).
    /// </summary>
    public static ChatMessage ReadWhisper(JsonNode? body)
    {
        var message = ReadMessage(body?["gameMessageInfo"]) with
        {
            TargetCharacterName = Str(body?["validationInfo"], "targetCharacterName"),
        };
        return message.ReceiverCharacterId.Length > 0 ? message
            : message with { ReceiverCharacterId = Str(body?["gameUserKey"], "characterId") };
    }

    public static ChatLoginRequest ReadLogin(JsonNode? node) => new(
        Str(node, "authnToken"), Str(node, "gameCode"), Str(node, "deviceId"), Str(node, "clientType"),
        Str(node, "characterId"), Str(node, "serverKey"), Str(node, "locale"));

    static string Str(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue v
            && v.TryGetValue<string>(out var text) ? text : "";
}
