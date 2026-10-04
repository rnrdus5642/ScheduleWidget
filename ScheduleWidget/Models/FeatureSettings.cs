using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    public sealed class CommunicationSettings
    {
        public string ProtectedTelegramToken { get; set; }
        public string TelegramChatId { get; set; }
        public string ProtectedKakaoToken { get; set; }
        public string ProtectedKakaoRefreshToken { get; set; }
        public string KakaoAppKey { get; set; }
        public string ProtectedKakaoClientSecret { get; set; }
        public string KakaoLinkUrl { get; set; }
        public string KakaoFriendUuid { get; set; }
        public string PhoneNumber { get; set; }
    }

    public sealed class ReminderSettings
    {
        public bool Enabled { get; set; }
        public bool Desktop { get; set; } = true;
        public bool Telegram { get; set; }
        public bool Kakao { get; set; }
        public int DaysBefore { get; set; }
        public int Hour { get; set; } = 9;
        public int Minute { get; set; }
    }

    public sealed class MusicSettings
    {
        public List<MusicPlaylist> Playlists { get; set; } = new List<MusicPlaylist>();
        public Guid SelectedPlaylistId { get; set; }
        public double Volume { get; set; } = 0.5;
        // The last audible level, which the mini window's 음소거 해제 brings back — kept across restarts while muted.
        public double VolumeBeforeMute { get; set; } = 0.5;
        public bool Repeat { get; set; }
        public bool Shuffle { get; set; }
        // 한 곡 반복: 곡이 끝나면 같은 곡을 다시 재생합니다(랜덤·반복보다 우선).
        public bool RepeatOne { get; set; }
    }

    public sealed class MusicPlaylist
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "내 플레이리스트";
        public List<MusicTrack> Tracks { get; set; } = new List<MusicTrack>();
    }

    public sealed class MusicTrack
    {
        public string Title { get; set; }
        public string Source { get; set; }
        [JsonIgnore]
        public bool IsYouTube => FeatureRules.TryYouTube(Source, out _, out _);
        [JsonIgnore]
        public string Kind => IsYouTube ? "YouTube" : "파일";
    }

    public static class FeatureRules
    {
        public static bool TryScheduleTime(string value, out string normalized)
        {
            normalized = null;
            var match = Regex.Match((value ?? "").Trim(), @"\A([0-9]{1,2}):([0-9]{2})\z");
            if (!match.Success) return false;
            int hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (hour > 23 || minute > 59) return false;
            normalized = hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
            return true;
        }

        public static bool IsColor(string value) => value != null &&
            Regex.IsMatch(value, @"\A#(?:[0-9a-fA-F]{6}|FF[0-9a-fA-F]{6})\z", RegexOptions.IgnoreCase);

        public static string ReadableText(string value)
        {
            if (!IsColor(value)) return "#24243A";
            string rgb = value.Substring(value.Length - 6);
            double[] components = Enumerable.Range(0, 3).Select(i =>
            {
                double c = int.Parse(rgb.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }).ToArray();
            double luminance = 0.2126 * components[0] + 0.7152 * components[1] + 0.0722 * components[2];
            return luminance > 0.179 ? "#000000" : "#FFFFFF";
        }

        public static bool TryYouTube(string source, out string videoId, out string playlistId)
        {
            videoId = null;
            playlistId = null;
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (host != "youtube.com" && host != "www.youtube.com" && host != "m.youtube.com" &&
                host != "music.youtube.com" && host != "youtu.be" && host != "www.youtu.be" &&
                host != "www.youtube-nocookie.com") return false;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string[] path = uri.AbsolutePath.Trim('/').Split('/');
            if (host == "youtu.be" || host == "www.youtu.be") videoId = path[0];
            else if (path.Length == 2 && new[] { "shorts", "embed", "live" }.Contains(path[0])) videoId = path[1];
            else if (path[0] == "watch") videoId = query["v"];
            // A watch URL is one track, even when copied from a playlist.
            if (!string.IsNullOrEmpty(videoId))
            {
                if (Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z")) return true;
                videoId = null;
                return false;
            }
            if (path[0] != "playlist") return false;
            playlistId = query["list"];
            if (playlistId != null && Regex.IsMatch(playlistId, @"\A[A-Za-z0-9_-]{10,100}\z")) return true;
            playlistId = null;
            return false;
        }

        // Playlist ID carried by any YouTube link, including watch links copied while a playlist was playing.
        public static bool TryYouTubePlaylistId(string source, out string playlistId)
        {
            playlistId = null;
            if (!TryYouTube(source, out _, out _) || !Uri.TryCreate(source, UriKind.Absolute, out Uri uri)) return false;
            string list = System.Web.HttpUtility.ParseQueryString(uri.Query)["list"];
            if (list == null || !Regex.IsMatch(list, @"\A[A-Za-z0-9_-]{10,100}\z")) return false;
            playlistId = list;
            return true;
        }

        public static bool IsReminderDue(ScheduleItem item, ReminderSettings settings, DateTime now)
        {
            if (!settings.Enabled || item.IsCompleted ||
                !DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date)) return false;
            int days = Math.Max(0, Math.Min(30, settings.DaysBefore));
            if (date < DateTime.MinValue.AddDays(days)) return false;
            int hour = Math.Max(0, Math.Min(23, settings.Hour));
            int minute = Math.Max(0, Math.Min(59, settings.Minute));
            if (TryScheduleTime(item.Time, out string time))
            {
                hour = int.Parse(time.Substring(0, 2), CultureInfo.InvariantCulture);
                minute = int.Parse(time.Substring(3, 2), CultureInfo.InvariantCulture);
            }
            DateTime start = date.AddDays(-days).AddHours(hour).AddMinutes(minute);
            return now >= start && now.Date <= date;
        }

        public static string ReminderKey(ScheduleItem item, ReminderSettings settings) =>
            item.Period + "|" + settings.DaysBefore + "|" +
            (TryScheduleTime(item.Time, out string time) ? "at" + time : settings.Hour + ":" + settings.Minute);

        public static string ReminderMessage(ScheduleItem item) =>
            "[할 일 알림] " + (item.Title ?? "").Substring(0, Math.Min(150, (item.Title ?? "").Length)) +
            "\n마감: " + item.Period + (TryScheduleTime(item.Time, out string time) ? " " + time : "") +
            (item.IsMultiDay ? " ~ " + item.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "") + " (" + item.DDay + ")";
    }
}
