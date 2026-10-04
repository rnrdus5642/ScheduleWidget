using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public static class SecretStore
    {
        public static string Protect(string value) => string.IsNullOrWhiteSpace(value) ? null :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));

        public static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            { throw new InvalidOperationException("이 Windows 계정에서 연동 키를 읽을 수 없습니다. 키를 다시 입력해 주세요."); }
        }
    }

    public sealed class CommunicationService
    {
        private static readonly HttpClient SharedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) };
        private readonly HttpClient client;
        // ponytail: one Kakao account; serialize token refresh, use per-account locks if multiple accounts are added.
        private readonly SemaphoreSlim kakaoLock = new SemaphoreSlim(1, 1);
        public CommunicationService(HttpClient client = null) { this.client = client ?? SharedClient; }

        public async Task SendTelegramAsync(CommunicationSettings settings, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
                throw new InvalidOperationException("텔레그램 메시지는 1~4096자로 입력해 주세요.");
            string token = SecretStore.Unprotect(settings.ProtectedTelegramToken);
            if (!Regex.IsMatch(token, @"\A[0-9]+:[A-Za-z0-9_-]+\z") || string.IsNullOrWhiteSpace(settings.TelegramChatId))
                throw new InvalidOperationException("텔레그램 봇 토큰과 Chat ID를 설정해 주세요.");
            JObject result = await PostAsync("https://api.telegram.org/bot" + token + "/sendMessage",
                new Dictionary<string, string> { ["chat_id"] = settings.TelegramChatId.Trim(), ["text"] = text }, null, "텔레그램");
            if (!(result["ok"]?.Type == JTokenType.Boolean && (bool)result["ok"])) throw new InvalidOperationException("텔레그램이 전송을 확인하지 못했습니다.");
        }

        // Answer fields read by their JSON type: an unexpected shape means "not confirmed", never a crash of the caller.
        private static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;

        public async Task SendKakaoAsync(CommunicationSettings settings, string text, bool toSelf = false)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200)
                throw new InvalidOperationException("카카오톡 메시지는 1~200자로 입력해 주세요.");
            if (!Uri.TryCreate(settings.KakaoLinkUrl, UriKind.Absolute, out Uri link) ||
                link.Scheme != "https" || !string.IsNullOrEmpty(link.UserInfo))
                throw new InvalidOperationException("카카오 개발자 앱에 등록한 HTTPS 웹 링크를 입력해 주세요.");
            await kakaoLock.WaitAsync();
            try
            {
                string token = SecretStore.Unprotect(settings.ProtectedKakaoToken);
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("카카오 액세스 토큰을 설정해 주세요.");
                bool friend = !toSelf && !string.IsNullOrWhiteSpace(settings.KakaoFriendUuid);
                var form = new Dictionary<string, string>
                {
                    ["template_object"] = JsonConvert.SerializeObject(new
                    {
                        object_type = "text", text,
                        link = new { web_url = link.AbsoluteUri, mobile_web_url = link.AbsoluteUri },
                        button_title = "자세히 보기"
                    })
                };
                if (friend) form["receiver_uuids"] = JsonConvert.SerializeObject(new[] { settings.KakaoFriendUuid.Trim() });
                string url = friend ? "https://kapi.kakao.com/v1/api/talk/friends/message/default/send" :
                    "https://kapi.kakao.com/v2/api/talk/memo/default/send";
                JObject result;
                try { result = await PostAsync(url, form, token, "카카오톡"); }
                catch (UnauthorizedAccessException)
                {
                    token = await RefreshKakaoAsync(settings);
                    result = await PostAsync(url, form, token, "카카오톡");
                }
                if (friend)
                {
                    var recipients = result["successful_receiver_uuids"] as JArray;
                    if (recipients == null || recipients.Count != 1 || Text(recipients[0]) != settings.KakaoFriendUuid.Trim())
                        throw new InvalidOperationException("카카오톡 친구 전송에 실패했습니다. 친구 UUID와 메시지 권한을 확인해 주세요.");
                }
                else if (!Equals((result["result_code"] as JValue)?.Value, 0L))
                    throw new InvalidOperationException("카카오톡이 전송을 확인하지 못했습니다.");
            }
            finally { kakaoLock.Release(); }
        }

        private async Task<string> RefreshKakaoAsync(CommunicationSettings settings)
        {
            string refresh = SecretStore.Unprotect(settings.ProtectedKakaoRefreshToken);
            if (string.IsNullOrWhiteSpace(refresh) || string.IsNullOrWhiteSpace(settings.KakaoAppKey))
                throw new InvalidOperationException("카카오 토큰이 만료되었습니다. 다시 발급하거나 갱신 토큰과 REST API 키를 설정해 주세요.");
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = settings.KakaoAppKey.Trim(), ["refresh_token"] = refresh
            };
            string secret = SecretStore.Unprotect(settings.ProtectedKakaoClientSecret);
            if (!string.IsNullOrEmpty(secret)) form["client_secret"] = secret;
            JObject result = await PostAsync("https://kauth.kakao.com/oauth/token", form, null, "카카오 인증");
            string token = Text(result["access_token"]);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("카카오 토큰 갱신에 실패했습니다.");
            settings.ProtectedKakaoToken = SecretStore.Protect(token);
            string refreshed = Text(result["refresh_token"]);
            if (!string.IsNullOrWhiteSpace(refreshed))
                settings.ProtectedKakaoRefreshToken = SecretStore.Protect(refreshed);
            return token;
        }

        private async Task<JObject> PostAsync(string url, Dictionary<string, string> form, string bearer, string service)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new FormUrlEncodedContent(form);
                    if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    using (var response = await client.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            throw new UnauthorizedAccessException(service + " 인증이 만료되었거나 키가 잘못되었습니다.");
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException(service + " 전송 실패 (HTTP " + (int)response.StatusCode +
                                "). 받는 사람, 권한, 전송 한도를 확인해 주세요.");
                        return JObject.Parse(await response.Content.ReadAsStringAsync());
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                // Request exception messages can include Telegram's token-bearing URL.
                throw new InvalidOperationException(service + " 응답을 확인할 수 없습니다. 네트워크와 수신 내역을 확인해 주세요.");
            }
        }
    }
}
