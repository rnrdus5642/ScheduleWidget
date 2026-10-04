using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    // Reads the public YouTube playlist page so every video in a playlist link can be added as its own track.
    public static class YouTubePlaylistService
    {
        private const int MaxPages = 60;
        private static volatile HttpClient current = CreateClient(null);
        private static HttpClient Client => current;

        /// <summary>
        /// Checks: answer every request with this handler instead of the network (null goes back to the network). The
        /// same headers and timeout apply; the handler is not disposed here.
        /// </summary>
        public static void UseHandler(HttpMessageHandler handler) => current = CreateClient(handler);

        private static HttpClient CreateClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }, disposeHandler: handler == null) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,en;q=0.8");
            // Skip the EU consent interstitial so the page contains the playlist data.
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "CONSENT=YES+1; SOCS=CAI");
            return client;
        }

        public sealed class Video
        {
            public string Id { get; set; }
            public string Title { get; set; }
        }

        /// <summary>Returns playable videos in playlist order. Throws InvalidOperationException when the list cannot be read.</summary>
        public static async Task<List<Video>> FetchAsync(string playlistId)
        {
            if (string.IsNullOrWhiteSpace(playlistId) || !Regex.IsMatch(playlistId, @"\A[A-Za-z0-9_-]{10,100}\z"))
                throw new InvalidOperationException("재생목록 ID가 올바르지 않습니다.");
            string html;
            try { html = await Client.GetStringAsync("https://www.youtube.com/playlist?list=" + playlistId + "&hl=ko").ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            { throw new InvalidOperationException("YouTube에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.", ex); }

            JObject initial = ExtractJson(html, "ytInitialData");
            if (initial == null) throw new InvalidOperationException("재생목록 정보를 읽을 수 없습니다.");

            var videos = new List<Video>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string continuation = Collect(initial, videos, seen, out _);

            string apiKey = Match(html, "\"INNERTUBE_API_KEY\"\\s*:\\s*\"([^\"]+)\"");
            string clientVersion = Match(html, "\"INNERTUBE_CLIENT_VERSION\"\\s*:\\s*\"([^\"]+)\"") ?? "2.20240101.00.00";
            if (continuation != null && apiKey == null)
                throw new InvalidOperationException("YouTube 재생목록의 다음 페이지를 읽을 수 없습니다. 페이지 정보를 다시 불러와 주세요.");

            for (int page = 0; continuation != null && apiKey != null && page < MaxPages; page++)
            {
                string body = JsonConvert.SerializeObject(new
                {
                    context = new { client = new { clientName = "WEB", clientVersion, hl = "ko" } },
                    continuation
                });
                JObject next;
                try
                {
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var response = await Client.PostAsync("https://www.youtube.com/youtubei/v1/browse?key=" + apiKey, content).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("YouTube 재생목록의 다음 페이지를 읽지 못했습니다. 잠시 후 다시 시도해 주세요.");
                        next = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
                { throw new InvalidOperationException("YouTube 재생목록의 다음 페이지를 읽지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.", ex); }
                continuation = Collect(next, videos, seen, out bool recognizedPlaylistPage);
                if (!recognizedPlaylistPage)
                    throw new InvalidOperationException("YouTube 재생목록의 다음 페이지 형식을 읽을 수 없습니다. 잠시 후 다시 시도해 주세요.");
            }

            if (continuation != null)
                throw new InvalidOperationException("YouTube 재생목록이 너무 길어 끝까지 읽지 못했습니다. 곡 수를 줄여 다시 시도해 주세요.");

            if (videos.Count == 0)
                throw new InvalidOperationException("재생목록이 비어 있거나 비공개입니다. 자동 생성 믹스는 곡별로 가져올 수 없습니다.");
            return videos;
        }

        /// <summary>Title of one public video via YouTube's oEmbed endpoint; null when it can't be read.</summary>
        public static async Task<string> FetchVideoTitleAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId) || !Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z")) return null;
            try
            {
                string url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + videoId);
                using (var response = await Client.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    var title = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))["title"] as JValue;
                    string text = title?.Type == JTokenType.String ? (string)title : null;
                    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                }
            }
            // Offline, blocked, an odd answer …: the title is only a nicety, the caller keeps its fallback name.
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { return null; }
        }

        private static string Collect(JContainer root, List<Video> videos, HashSet<string> seen, out bool recognizedPlaylistPage)
        {
            // Only arrays that hold the playlist's own entries: the first page's item list or a continuation append.
            // This keeps sidebar recommendations out of the imported tracks.
            string continuation = null;
            recognizedPlaylistPage = false;
            foreach (JArray items in root.DescendantsAndSelf().OfType<JArray>().ToList())
            {
                string owner = (items.Parent as JProperty)?.Name;
                if (owner != "contents" && owner != "continuationItems") continue;
                var entries = items.Children<JObject>().ToList();
                bool hasPlaylistEntry = entries.Any(o => o["lockupViewModel"] != null || o["playlistVideoRenderer"] != null ||
                    o["continuationItemViewModel"] != null || o["continuationItemRenderer"] != null);
                if (!hasPlaylistEntry && !(owner == "continuationItems" && entries.Count == 0 && IsContinuationItemsArray(items))) continue;
                recognizedPlaylistPage = true;
                foreach (JObject entry in entries)
                {
                    string id = null, title = null;
                    if (entry["playlistVideoRenderer"] is JObject video)
                    {
                        if (video["isPlayable"]?.Type == JTokenType.Boolean && !(bool)video["isPlayable"]) continue;
                        id = Text(video["videoId"]);
                        title = Text(video.SelectToken("title.runs[0].text")) ?? Text(video.SelectToken("title.simpleText"));
                    }
                    else if (entry["lockupViewModel"] is JObject lockup)
                    {
                        if (Text(lockup["contentType"]) != "LOCKUP_CONTENT_TYPE_VIDEO") continue;
                        id = Text(lockup["contentId"]);
                        title = Text(lockup.SelectToken("metadata.lockupMetadataViewModel.title.content"));
                    }
                    else if (entry["continuationItemViewModel"] != null || entry["continuationItemRenderer"] != null)
                    {
                        continuation = entry.Descendants().OfType<JProperty>()
                            .Where(p => p.Name == "token" && p.Value.Type == JTokenType.String)
                            .Select(p => (string)p.Value).FirstOrDefault() ?? continuation;
                        continue;
                    }
                    if (id == null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z") || !seen.Add(id)) continue;
                    videos.Add(new Video { Id = id, Title = string.IsNullOrWhiteSpace(title) ? "YouTube · " + id : title });
                }
            }
            return continuation;
        }

        private static bool IsContinuationItemsArray(JArray items)
        {
            // YouTube wraps these under appendContinuationItemsAction or reloadContinuationItemsCommand.
            JProperty owner = items.Parent as JProperty;
            JProperty action = owner?.Parent?.Parent as JProperty;
            return owner?.Name == "continuationItems" &&
                (action?.Name == "appendContinuationItemsAction" || action?.Name == "reloadContinuationItemsCommand");
        }

        // A text value of YouTube's page data, or null (a changed page may hold an object where text used to be).
        private static string Text(JToken token) => token is JValue value && value.Type == JTokenType.String ? (string)value : null;

        private static JObject ExtractJson(string html, string name)
        {
            int start = html.IndexOf(name, StringComparison.Ordinal);
            if (start < 0) return null;
            start = html.IndexOf('{', start);
            if (start < 0) return null;
            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(html.Substring(start))) { SupportMultipleContent = true })
                    return JObject.Load(reader);
            }
            catch (JsonException) { return null; }
        }

        private static string Match(string text, string pattern)
        {
            var match = Regex.Match(text, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
