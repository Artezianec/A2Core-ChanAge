using System.Text.Json;
using Aion.Host.Platform;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformGateEmptyRepliesTests
{
    [Theory]
    [InlineData("GET", "/local-api-gate/trade/v1.0/policies")]
    [InlineData("GET", "/local-api-gate/trade/v1.0/me/earnings")]
    [InlineData("GET", "/local-api-gate/warehouse/v2.0/me/items")]
    [InlineData("POST", "/local-api-gate/mobile_warehouse/v1.0/my/items")]
    [InlineData("POST", "/local-api-gate/fizz/v2.0/tokens")]
    [InlineData("GET", "/local-api-gate/role/v1.0/apps/B91B66D7-4B0A-4E3C-A767-DADCE0B52962/roles")]
    [InlineData("GET", "/local-api-gate/profileimage/v1.0/game_profile_images/10001")]
    public void Shop_side_calls_get_valid_json(string method, string path)
    {
        Assert.True(PlatformApiGateResponses.TryReply(method, path, QueryCollection.Empty, null, out var body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Warehouse_items_returns_a_paged_empty_200_body_and_echoes_client_context()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["page_index"] = "1",
            ["page_size"] = "80",
            ["game_server_id"] = "1361",
            ["char_key"] = "6",
            ["inventory_type"] = "1",
            ["is_delivery_confirmation"] = "1",
        });

        Assert.True(PlatformApiGateResponses.TryReply(
            "GET", "/local-api-gate/warehouse/v2.0/me/items", query, null, out var body));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("0", root.GetProperty("total_count").GetString());
        Assert.Equal("1", root.GetProperty("page_index").GetString());
        Assert.Equal("80", root.GetProperty("page_size").GetString());
        Assert.Equal("1361", root.GetProperty("game_server_id").GetString());
        Assert.Equal("6", root.GetProperty("char_key").GetString());
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
        Assert.Equal(0, root.GetProperty("warehouse_items").GetArrayLength());
    }
}
