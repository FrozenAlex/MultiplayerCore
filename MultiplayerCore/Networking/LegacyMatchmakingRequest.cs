using System;
using Newtonsoft.Json.Linq;
namespace MultiplayerCore.Networking
{
    internal static class LegacyMatchmakingRequest
    {
        // 1.45 removed these fields. BeatTogether verifies the proof against the raw platform ID.
        internal static JObject AddCredentials(JObject request, string platformUserId, string proof)
        {
            if (string.IsNullOrWhiteSpace(platformUserId) || string.IsNullOrWhiteSpace(proof))
                throw new ArgumentException("Platform credentials are required for custom matchmaking.");
            var result = (JObject)request.DeepClone();
            result["auth_user_id"] = platformUserId;
            result["single_use_auth_token"] = proof;
            return result;
        }
    }
}
