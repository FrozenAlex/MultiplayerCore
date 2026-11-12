using BGNet.Core.GameLift;
using IPA.Utilities;
using MultiplayerCore.Networking;
using MultiplayerCore.Patchers;
using Newtonsoft.Json.Linq;
using OculusStudios.MetaNetworking.Utils;
using OculusStudios.Platform.Core;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MultiplayerCore.Objects
{
    public class CustomPlayerSessionProvider : GameLiftPlayerSessionProvider, IGameLiftPlayerSessionProvider
    {
        internal NetworkConfigPatcher? _customNetconfig;
        private readonly IPlatform _platform;
        private readonly IMetaApiUserAgentProvider _userAgent;
        private static readonly HttpClient CustomHttpClient = new(new HttpClientHandler { UseCookies = false });

        public CustomPlayerSessionProvider(INetworkConfig networkConfig,
            IMultiplayerStatusModel multiplayerStatusModel, IMetaApiUserAgentProvider metaApiUserAgentProvider,
            IPlatform platform) : base(networkConfig, multiplayerStatusModel, metaApiUserAgentProvider)
        {
            _platform = platform;
            _userAgent = metaApiUserAgentProvider;
        }

        public new async Task<PlayerSessionInfo> GetGameLiftPlayerSessionInfo(
            IAuthenticationTokenProvider authenticationTokenProvider, string userId,
            BeatmapLevelSelectionMask beatmapLevelSelectionMask, GameplayServerConfiguration gameplayServerConfiguration,
            string secret, string code, CancellationToken cancellationToken)
        {
            if (_customNetconfig?.IsOverridingApi != true)
                return await base.GetGameLiftPlayerSessionInfo(authenticationTokenProvider, userId,
                    beatmapLevelSelectionMask, gameplayServerConfiguration, secret, code, cancellationToken);

            var client = new GraphAPIClient(_customNetconfig.GraphUrl!, _networkConfig.graphAccessToken, _userAgent.SetHeader);
            client.GetField<HttpClient, GraphAPIClient>("_client").Dispose();
            client.SetField("_client", CustomHttpClient);
            var platform = authenticationTokenProvider.GetTokenPlatform(PlatformEnvironment.Production);
            var platformUserId = _platform.user.userId.ToString();
            // Steam provides a hex ticket; Oculus provides a proof, not the x-platform bearer token.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var token = timeout.Token;
            var proofTask = _platform.user.GetAccessTokenAsync();
            if (await Task.WhenAny(proofTask, Task.Delay(Timeout.Infinite, token)) != proofTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ConnectionFailedException(ConnectionFailedReason.Timeout);
            }
            var proof = await proofTask;
            string? ticketId = null;
            string? placementId = null;
            JObject Request() => LegacyMatchmakingRequest.AddCredentials(JObject.FromObject(
                new GetMultiplayerInstanceRequest(Application.version, _networkConfig.serviceEnvironment, userId,
                    beatmapLevelSelectionMask, gameplayServerConfiguration, platform, secret, code,
                    GetAverageLatencies(), ticketId, placementId, _networkConfig.customLocation)), platformUserId, proof);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var response = await client.Post<JObject, GetMultiplayerInstanceResponse>(
                        "beat_saber_get_multiplayer_instance", proof, Request(), default, token);
                    if (response.errorCode != MultiplayerPlacementErrorCode.Success && response.errorCode != MultiplayerPlacementErrorCode.RequestTimeout)
                        throw new ConnectionFailedException(response.errorCode.ToConnectionFailedReason());
                    if (response.playerSessionInfo != null && !string.IsNullOrEmpty(response.playerSessionInfo.gameSessionId)
                        && !string.IsNullOrEmpty(response.playerSessionInfo.playerSessionId))
                        return response.playerSessionInfo;
                    ticketId = response.ticketId;
                    placementId = response.placementId;
                    if (string.IsNullOrEmpty(ticketId) && string.IsNullOrEmpty(placementId))
                        throw new ConnectionFailedException(ConnectionFailedReason.Timeout);
                    await Task.Delay(Math.Max(100, response.pollIntervalMs), token);
                }
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrEmpty(ticketId) || !string.IsNullOrEmpty(placementId))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await client.Post<JObject, GetMultiplayerInstanceResponse>(
                            "beat_saber_multiplayer_cancel_matchmaking_ticket", proof, Request(), default, cleanup.Token);
                    }
                    catch (Exception) { /* Preserve cancellation even if cleanup fails. */ }
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new ConnectionFailedException(ConnectionFailedReason.Timeout);
            }
        }

        internal void UpdateServer()
        {
            var oldClient = _graphAPIClient;
            // BSIPA resolves private fields on the generic declaring type, not inherited fields.
            ((GameLiftPlayerSessionProvider)this).SetField(nameof(_graphAPIClient), new GraphAPIClient(_networkConfig.graphApiBaseUrl,
                _networkConfig.graphAccessToken, _userAgent.SetHeader));
            oldClient.GetField<HttpClient, GraphAPIClient>("_client").Dispose();
        }
    }
}
