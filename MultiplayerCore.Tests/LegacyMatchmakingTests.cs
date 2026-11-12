using MultiplayerCore.Networking;
using Newtonsoft.Json.Linq;
using Xunit;

public class LegacyMatchmakingTests
{
    [Theory]
    [InlineData("76561198000000000", "01020304")]
    [InlineData("12345", "nonce-with-quote-\"-and-unicode-π")]
    public void AddsRawPlatformCredentialsWithoutChangingGamePayload(string id, string proof)
    {
        var original = JObject.Parse("{\"user_id\":\"hashed-id\",\"version\":\"1.45.3_30054\",\"gameplay_server_configuration\":{\"max_player_count\":8},\"ticket_id\":\"ticket\"}");
        var request = LegacyMatchmakingRequest.AddCredentials(original, id, proof);
        var parsed = JObject.Parse(request.ToString());
        Assert.Equal(id, (string?)parsed["auth_user_id"]);
        Assert.Equal(proof, (string?)parsed["single_use_auth_token"]);
        parsed.Remove("auth_user_id"); parsed.Remove("single_use_auth_token");
        Assert.True(JToken.DeepEquals(original, parsed));
        Assert.Null(original["auth_user_id"]);
    }
    [Theory]
    [InlineData("", "proof")]
    [InlineData("raw-id", " ")]
    public void RejectsMissingCredentials(string id, string proof)
        => Assert.Throws<ArgumentException>(() => LegacyMatchmakingRequest.AddCredentials(new JObject(), id, proof));
}
