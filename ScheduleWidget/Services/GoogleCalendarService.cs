using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    /// <summary>구글 캘린더 연동 상태 (schedules.json). The refresh token is DPAPI-protected like the other 연동 키.</summary>
    public sealed class GoogleCalendarSettings
    {
        public bool Enabled { get; set; }
        public string ProtectedRefreshToken { get; set; }
        public string Account { get; set; }
        // The account the schedules' GoogleEventIds belong to (kept after 로그아웃; another account starts fresh).
        public string LinkedAccount { get; set; }
        // When the last sync ended (this PC's clock): only what 설정 shows.
        public DateTime? LastSync { get; set; }
        // (UTC) When the last sync that went through started reading Google's list: the next one reads only what changed on
        // Google since then (null → the whole list). Taken before the read and in UTC, so a sync that runs long (hundreds of
        // new events sent slowly) or a clock / time zone / DST change never makes a later read skip an edit made on Google.
        public DateTime? ReadStartedUtc { get; set; }
        // Google event ids that were in sync last time; one that no longer has a local item was deleted here.
        public List<string> SyncedEventIds { get; set; } = new List<string>();
        // Of those, the events this app made that have no guests: deleting the schedule here deletes them on Google too.
        // Any other event (a shared meeting, one made in Google Calendar) only leaves the widget and stays on Google.
        public List<string> OwnedEventIds { get; set; } = new List<string>();
        // Google events taken out of the widget only (id → date, null when unknown): not brought back while they stay there.
        public Dictionary<string, string> HiddenEvents { get; set; } = new Dictionary<string, string>();
        // 1 once the schedules linked by older versions were sorted into OwnedEventIds (their first full read).
        public int OwnershipVersion { get; set; }
        // 캐릭터도 구글 드라이브에 보관: imported characters kept in the Drive folder "ScheduleWidget 캐릭터" and shared by every PC
        // signed in to the account. A character is deleted on Drive only when it was deleted here (CharacterCatalog.Delete).
        public bool PetsEnabled { get; set; }
        public List<string> SyncedPetIds { get; set; } = new List<string>();
        // Drive character files that could not be brought in ("file id|modified time"): skipped until the file changes there.
        public List<string> FailedPetFiles { get; set; } = new List<string>();
        // true once the characters an older version deleted here (it kept no record of them) were sorted out: done by the
        // first character sync of this version (see CharacterCatalog.RememberOldDeletions).
        public bool PetDeletionsSorted { get; set; }
        // Characters deleted here whose Drive file Drive refused to delete (no right to it, …): they stay deleted here — never
        // downloaded again — while that file is there. Forgotten once it is gone from Drive.
        public List<string> RefusedPetDeletes { get; set; } = new List<string>();
    }

    /// <summary>What one character sync does (pure, tested): upload, download, delete on Drive.</summary>
    public sealed class PetSyncPlan
    {
        public List<string> Upload { get; } = new List<string>();
        public List<string> Download { get; } = new List<string>();
        public List<string> DeleteRemote { get; } = new List<string>();
        // Drive characters named like a built-in one (and like no imported one here): brought in only when they are not just a
        // copy of that built-in (same picture). Built-ins themselves never go to Drive (only the imported ones are uploaded),
        // so such a file is always a pet the user imported somewhere — often one of their own that merely shares the name.
        public List<string> DownloadUnlessBuiltIn { get; } = new List<string>();
        // Skipped by the same-name rule (shown in the status, so a pet without a backup is never a surprise): here and not
        // uploaded / on Drive and not downloaded.
        public List<string> SameNameNotUploaded { get; } = new List<string>();
        public List<string> SameNameNotDownloaded { get; } = new List<string>();

        /// <summary>
        /// Same rules as the schedules: every character here goes to Drive and every one on Drive comes here. Only a character
        /// deleted here (캐릭터 선택 → 삭제, remembered in <paramref name="deletedIds"/>) is deleted on Drive; one that is just
        /// missing here (a wiped or restored Pet folder, a new PC) is downloaded again instead — the backup is kept exactly
        /// when it is needed. Nothing is deleted here automatically — a character removed from Drive stays on this PC and is
        /// not uploaded again. A character whose name is already on the other side (any character here, built-in ones too)
        /// is neither downloaded nor uploaded, so the same pet never appears twice.
        /// </summary>
        /// <param name="local">imported characters here that read fine: ID → name</param>
        /// <param name="remote">characters on Drive: ID → name</param>
        /// <param name="builtInNames">names of the built-in characters (기본 캐릭터, Codex, Dewey, …)</param>
        /// <param name="deletedIds">characters deleted here that Drive may still have</param>
        /// <param name="presentIds">every imported character folder here, also one that cannot be read now (left alone)</param>
        /// <param name="refusedIds">deleted here, but Drive refused to delete them: left alone (never downloaded again)</param>
        public static PetSyncPlan Build(IDictionary<string, string> local, IDictionary<string, string> remote, IEnumerable<string> syncedIds,
            IEnumerable<string> builtInNames = null, IEnumerable<string> deletedIds = null, IEnumerable<string> presentIds = null,
            IEnumerable<string> refusedIds = null)
        {
            var plan = new PetSyncPlan();
            // IDs are folder names here, and Windows does not tell "Cat" from "cat".
            var ids = StringComparer.OrdinalIgnoreCase;
            var synced = new HashSet<string>(syncedIds ?? new string[0], ids);
            var deleted = new HashSet<string>(deletedIds ?? new string[0], ids);
            var present = new HashSet<string>(presentIds ?? new string[0], ids);
            var refused = new HashSet<string>(refusedIds ?? new string[0], ids);
            var localIds = new HashSet<string>(local.Keys, ids);
            var remoteIds = new HashSet<string>(remote.Keys, ids);
            string Key(string name) => (name ?? "").Trim().ToLowerInvariant();
            var namesHere = new HashSet<string>(local.Values.Select(Key));
            var builtIns = new HashSet<string>((builtInNames ?? new string[0]).Select(Key));
            var namesThere = new HashSet<string>(remote.Values.Select(Key));
            foreach (var pet in local.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (remoteIds.Contains(pet.Key) || synced.Contains(pet.Key)) continue;
                if (namesThere.Add(Key(pet.Value))) plan.Upload.Add(pet.Key);
                else plan.SameNameNotUploaded.Add(pet.Key);
            }
            foreach (var pet in remote.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (localIds.Contains(pet.Key)) continue;
                if (deleted.Contains(pet.Key)) plan.DeleteRemote.Add(pet.Key);
                else if (refused.Contains(pet.Key)) continue; // deleted here; Drive would not delete it — it stays deleted here
                else if (present.Contains(pet.Key)) continue; // its folder is here but cannot be read now: neither side is touched
                else if (!CharacterCatalog.IsPetId(pet.Key)) continue;
                else if (!namesHere.Add(Key(pet.Value))) plan.SameNameNotDownloaded.Add(pet.Key);
                else if (builtIns.Contains(Key(pet.Value))) plan.DownloadUnlessBuiltIn.Add(pet.Key);
                else plan.Download.Add(pet.Key);
            }
            return plan;
        }
    }

    /// <summary>One event read from Google, in the app's terms (date, optional HH:mm, length, 완료).</summary>
    public sealed class GoogleEvent
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Period { get; set; }
        // 여러 날: the last day ("yyyy-MM-dd") of an event over more than one day (all-day: the day before Google's exclusive
        // end date; timed: the day it ends, an end at exactly midnight counting for the day before). Null for one day.
        public string EndPeriod { get; set; }
        public string Time { get; set; }
        public int EndMinutes { get; set; }
        // The last day it is on (for how long a hidden event is remembered).
        public string LastPeriod => EndPeriod ?? Period;
        public bool Completed { get; set; }
        // One date of a repeating event (only those from today through 8 weeks ahead are brought here).
        public bool Recurring { get; set; }
        // Made by this app (private property scheduleWidget=1). LegacyApp: written by an older version (완료 property only) —
        // which it wrote on every event it sent, also on a Google-made one it only edited, so on its own it proves nothing.
        public bool AppTag { get; set; }
        public bool LegacyApp { get; set; }
        // Has guests, or someone else organizes it: deleting it here never deletes it on Google.
        public bool Shared { get; set; }
    }

    /// <summary>What one sync does. Built by <see cref="GoogleCalendarSync.Plan"/> (pure, tested) and run by the service.</summary>
    public sealed class GoogleSyncPlan
    {
        public List<ScheduleItem> Insert { get; } = new List<ScheduleItem>();           // local only → create on Google
        public List<ScheduleItem> Patch { get; } = new List<ScheduleItem>();            // changed here → update on Google (what changed)
        public List<string> DeleteRemote { get; } = new List<string>();                 // an app-made event deleted here → delete on Google
        public Dictionary<string, string> Unlink { get; } = new Dictionary<string, string>(); // any other one deleted here → out of the widget (id → date)
        public List<KeyValuePair<ScheduleItem, GoogleEvent>> UpdateLocal { get; } = new List<KeyValuePair<ScheduleItem, GoogleEvent>>();
        public List<KeyValuePair<ScheduleItem, GoogleEvent>> UpdateLength { get; } = new List<KeyValuePair<ScheduleItem, GoogleEvent>>(); // only its length changed there
        public List<GoogleEvent> AddLocal { get; } = new List<GoogleEvent>();           // new on Google → add here
        public List<ScheduleItem> Prune { get; } = new List<ScheduleItem>();            // untouched repeating dates beyond 8 weeks (come back later)
        public HashSet<ScheduleItem> NewId { get; } = new HashSet<ScheduleItem>();      // a copy sharing another schedule's event gets its own
        public Dictionary<ScheduleItem, GoogleEvent> Remote { get; } = new Dictionary<ScheduleItem, GoogleEvent>(); // each linked schedule's event, when read
    }

    /// <summary>What one calendar sync did: the app saves and redraws only when something changed.</summary>
    public sealed class GoogleSyncResult
    {
        public bool DataChanged { get; set; }   // something kept in schedules.json changed
        public bool ListChanged { get; set; }   // a schedule shown in the lists was added, removed or changed
        public int Refused { get; set; }        // events Google refused this time (not sent again until edited here)
        public bool More { get; set; }          // more new schedules wait (sent in batches, the next one a few seconds later)
    }

    /// <summary>What one 캐릭터 보관 sync did.</summary>
    public sealed class GooglePetSyncResult
    {
        public int Downloaded { get; set; }
        public int Uploaded { get; set; }
        public int Deleted { get; set; }
        public int Failed { get; set; }
        public string FirstError { get; set; }
        public bool Changed { get; set; }       // something kept in schedules.json changed
        // Characters here not backed up because Drive already has one with the same name (the same-name rule).
        public List<string> SameNameNotBackedUp { get; } = new List<string>();
        // Shown under the character sync's status: "같은 이름이라 백업하지 않음: X, Y" (empty when none).
        public string Note => SameNameNotBackedUp.Count == 0 ? "" : "같은 이름이라 백업하지 않음: " + string.Join(", ", SameNameNotBackedUp.Take(5)) +
            (SameNameNotBackedUp.Count > 5 ? " 외 " + (SameNameNotBackedUp.Count - 5) + "개" : "");
    }

    public static class GoogleCalendarSync
    {
        // Google events read each sync: from 30 days ago through two years ahead; the dates of a repeating event only from
        // today through 8 weeks ahead (a daily meeting would otherwise become hundreds of schedules).
        public static DateTime WindowStart(DateTime today) => today.Date.AddDays(-30);
        public static DateTime WindowEnd(DateTime today) => today.Date.AddYears(2);
        public static DateTime RecurringEnd(DateTime today) => today.Date.AddDays(56);

        // Shown for an event without a title (never sent back to Google as its title).
        public const string Untitled = "(제목 없음)";

        // A new event's id is chosen here (Google allows a-v and 0-9): the first one from the schedule's own id, so a request
        // whose answer got lost cannot make a second copy — sent again, Google answers 409 and the schedule links to it.
        public static string EventIdFor(ScheduleItem item) => "sched" + item.Id.ToString("N");
        public static string NewEventId() => "sched" + Guid.NewGuid().ToString("N");

        // A 여러 날 schedule adds its last day as a fifth part; a one-day one hashes as before (so updating never makes every
        // schedule look changed).
        public static string Hash(string title, string period, string time, bool completed, string endPeriod = null) =>
            (title ?? "").Trim() + "\u001f" + (period ?? "") + "\u001f" + (time ?? "") + "\u001f" + (completed ? "1" : "0") +
            (string.IsNullOrEmpty(endPeriod) ? "" : "\u001f" + endPeriod);

        public static string Hash(ScheduleItem item) => Hash(item.Title, item.Period, item.Time, item.IsCompleted, EndOf(item));
        public static string Hash(GoogleEvent e) => Hash(e.Title, e.Period, e.Time, e.Completed, e.EndPeriod);

        /// <summary>A schedule's range end as Google gets it ("yyyy-MM-dd"), or null for one day.</summary>
        public static string EndOf(ScheduleItem item) => item.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static bool TryDate(string period, out DateTime date) =>
            DateTime.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        private static bool HasDate(string period) => TryDate(period, out _);

        /// <summary>Refused by Google as it is now (see <see cref="ScheduleItem.GoogleRefusedHash"/>).</summary>
        public static bool IsRefused(ScheduleItem item) => item.GoogleRefusedHash != null && item.GoogleRefusedHash == Hash(item);

        /// <summary>
        /// What deleting this schedule here does on Google: true = the event is deleted there too (this app made it and it has
        /// no guests); false = it stays on Google and only leaves the widget. For the 삭제 confirmation text.
        /// </summary>
        public static bool DeletesOnGoogle(GoogleCalendarSettings settings, ScheduleItem item) =>
            settings?.OwnedEventIds != null && !string.IsNullOrEmpty(item?.GoogleEventId) && settings.OwnedEventIds.Contains(item.GoogleEventId);

        /// <summary>One date of a repeating Google event, by the id Google gives it ("…_20261005" or "…_20261005T090000Z").</summary>
        public static bool IsRecurringInstanceId(string id, out DateTime date)
        {
            date = default(DateTime);
            var match = System.Text.RegularExpressions.Regex.Match(id ?? "", @"\A[a-v0-9]+_(\d{8})(T\d{6}Z?)?\z");
            return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        /// <summary>
        /// Keeps <paramref name="owned"/> (see <see cref="GoogleCalendarSettings.OwnedEventIds"/>) in step with the events just
        /// read: one with guests or a repeating one is never owned; one tagged by this app is. <paramref name="legacy"/> (the
        /// first full read after the update) sorts the events older versions linked, which carry no tag: of those made here
        /// (<paramref name="madeHere"/>, see <see cref="MadeHereIds"/>), one read now counts as this app's own when an older
        /// version wrote it (its 완료 property) and it has no guests and does not repeat; one not read (dated over 30 days
        /// ago or more than two years ahead) counts as its own on that alone (SyncAsync still looks at it on Google before
        /// deleting it there). An event brought here from Google never counts through this rule, even when an older version
        /// edited it (완료 ticked). A schedule deleted here before that first read is not made here any more: its event stays
        /// on Google.
        /// </summary>
        public static void UpdateOwnership(ICollection<string> owned, IEnumerable<GoogleEvent> remote, ICollection<string> madeHere, bool legacy)
        {
            var read = new HashSet<string>();
            foreach (var e in remote)
            {
                read.Add(e.Id);
                if (e.Shared || e.Recurring) owned.Remove(e.Id);
                else if ((e.AppTag || (legacy && e.LegacyApp && madeHere.Contains(e.Id))) && !owned.Contains(e.Id)) owned.Add(e.Id);
            }
            if (!legacy) return;
            foreach (string id in madeHere)
                if (!string.IsNullOrEmpty(id) && !read.Contains(id) && !owned.Contains(id)) owned.Add(id);
        }

        /// <summary>
        /// The events of the linked schedules this PC made itself (sent up by it, not brought here from Google), for the first
        /// read after the update: older versions set GoogleEndMinutes on every schedule they brought from Google (and when an
        /// event changed there) but never on one they sent up, so a linked schedule without it was made here. (One made here
        /// and changed on Google afterwards has it too: it then counts as Google's — the safe side, it is never deleted there.)
        /// </summary>
        public static HashSet<string> MadeHereIds(IEnumerable<ScheduleItem> local) =>
            new HashSet<string>(local.Where(s => !string.IsNullOrEmpty(s.GoogleEventId) && s.GoogleEndMinutes == null).Select(s => s.GoogleEventId));

        /// <summary>
        /// Two-way rules: every schedule here is added to the Google calendar and every Google event is brought here (a
        /// repeating one only for its next 8 weeks); a side that changed since the last sync wins (both changed → this PC
        /// wins for what it changed). Nothing is deleted automatically: a schedule deleted here (TODO list or mini window)
        /// deletes its Google event only when this app made it and it has no guests (<paramref name="ownedIds"/>) — any other
        /// event only leaves the widget and is not brought back (<paramref name="hidden"/>); an event deleted on Google leaves
        /// the schedule here as it is. Old completed schedules (over 30 days) are not uploaded, and a schedule Google refused
        /// is not sent again until it is edited.
        /// </summary>
        public static GoogleSyncPlan Plan(IList<ScheduleItem> local, IList<GoogleEvent> remote, ICollection<string> syncedIds, DateTime today,
            ICollection<string> ownedIds = null, IDictionary<string, string> hidden = null)
        {
            var plan = new GoogleSyncPlan();
            var synced = new HashSet<string>(syncedIds ?? new string[0]);
            var owned = new HashSet<string>(ownedIds ?? new string[0]);
            var localById = new Dictionary<string, ScheduleItem>();
            foreach (var item in local)
                if (!string.IsNullOrEmpty(item.GoogleEventId) && !localById.ContainsKey(item.GoogleEventId)) localById[item.GoogleEventId] = item;
            var remoteById = new Dictionary<string, GoogleEvent>();
            foreach (var e in remote) if (!string.IsNullOrEmpty(e.Id)) remoteById[e.Id] = e;
            DateTime oldest = today.Date.AddDays(-30), recurringEnd = RecurringEnd(today);

            foreach (string id in synced)
            {
                if (string.IsNullOrEmpty(id) || localById.ContainsKey(id)) continue;
                remoteById.TryGetValue(id, out GoogleEvent there);
                if (owned.Contains(id) && !(there?.Shared ?? false)) plan.DeleteRemote.Add(id);
                else plan.Unlink[id] = there?.LastPeriod ?? (IsRecurringInstanceId(id, out DateTime day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
            }

            foreach (var e in remoteById.Values)
            {
                if (synced.Contains(e.Id) && !localById.ContainsKey(e.Id)) continue; // deleted here (above)
                if (localById.TryGetValue(e.Id, out ScheduleItem item))
                {
                    plan.Remote[item] = e;
                    var pair = new KeyValuePair<ScheduleItem, GoogleEvent>(item, e);
                    if (Hash(item) != item.GoogleSyncedHash) { if (!IsRefused(item)) plan.Patch.Add(item); }
                    else if (Hash(e) != item.GoogleSyncedHash) plan.UpdateLocal.Add(pair);
                    else if (item.GoogleEndMinutes != e.EndMinutes) plan.UpdateLength.Add(pair);
                }
                else if (hidden != null && hidden.ContainsKey(e.Id)) continue; // taken out of the widget: stays out
                else if (!e.Recurring || (TryDate(e.Period, out DateTime date) && date >= today.Date && date <= recurringEnd)) plan.AddLocal.Add(e);
            }

            foreach (var item in local)
            {
                if (string.IsNullOrEmpty(item.GoogleEventId))
                {
                    // (A 여러 날 one counts as old from its last day.)
                    if (TryDate(item.Period, out DateTime date) && !(item.IsCompleted && (item.EndDate ?? date) < oldest) && !IsRefused(item)) plan.Insert.Add(item);
                    continue;
                }
                if (localById[item.GoogleEventId] != item)
                {
                    if (HasDate(item.Period) && !IsRefused(item)) { plan.Insert.Add(item); plan.NewId.Add(item); } // a duplicated id: the copy gets its own event
                    continue;
                }
                if (remoteById.ContainsKey(item.GoogleEventId)) continue;
                // Not in the list (outside the range, deleted on Google, or unchanged since the last read): only our own edits are
                // sent. If Google no longer has the event the edited schedule is added to it again; an untouched one stays here.
                if (item.GoogleSyncedHash == null)
                {
                    // Sent before but the answer never came: sent again under the same id (Google's 409 then links it).
                    if (HasDate(item.Period) && !IsRefused(item)) plan.Insert.Add(item);
                }
                else if (Hash(item) != item.GoogleSyncedHash) { if (!IsRefused(item)) plan.Patch.Add(item); }
                else if (IsRecurringInstanceId(item.GoogleEventId, out _) && TryDate(item.Period, out DateTime when) && when > recurringEnd && !item.HasCustomColor)
                    plan.Prune.Add(item); // a repeating date read by an older version for up to two years ahead: back within 8 weeks
            }
            return plan;
        }
    }

    /// <summary>
    /// 구글 캘린더 양방향 연동: Google sign-in in the browser (OAuth, PKCE, loopback), then the app's schedules and the primary
    /// calendar's events are kept in step. The OAuth client ("데스크톱 앱" client from Google Cloud Console) is built into the exe
    /// (encrypted google_client.bin); a google_client.json next to the app or saved in this PC's data takes precedence. Users
    /// only sign in.
    /// </summary>
    public sealed class GoogleCalendarService
    {
        // Sync uses the user's own primary calendar; no access to calendars shared by other owners is needed.
        private const string Scope = "https://www.googleapis.com/auth/calendar.events.owned https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.email";
        private const string DriveFiles = "https://www.googleapis.com/drive/v3/files";
        private const string PetFolderName = "ScheduleWidget 캐릭터";
        private const string EventsUrl = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
        private const string CompletedKey = "scheduleWidgetCompleted";
        private const string AppKey = "scheduleWidget"; // "1" on the events this app makes (see GoogleCalendarSettings.OwnedEventIds)
        private static readonly HttpClient SharedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Character files (up to 30 MB) go up and down on their own client: a slow link gets minutes, not 30 seconds.
        private static readonly HttpClient TransferClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly HttpClient client, transferClient;
        private string accessToken;
        private DateTime accessTokenExpires;
        // The whole event list is read at the first sync after start and once a day; the syncs in between read only what
        // changed on Google since the last one.
        private DateTime lastFullRead;

        /// <summary>Pause between two new events sent to Google (a first sync can add hundreds; Google limits how fast).</summary>
        public TimeSpan InsertPacing { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>New events sent in one sync at most; the rest go with the next sync a few seconds later.</summary>
        public int MaxInsertsPerSync { get; set; } = 100;

        public GoogleCalendarService(HttpClient client = null)
        {
            this.client = client ?? SharedClient;
            transferClient = client ?? TransferClient;
        }

        public static string ClientFile => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google_client.json");

        // A client file picked in 설정 (구글 로그인 → 파일 선택) is kept with this PC's data, so the app folder needs no write access.
        public static string SavedClientFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleWidget", "google_client.json");

        /// <summary>Checks only: the OAuth client to use instead of the files and the built-in one (null = the normal order).</summary>
        public static Tuple<string, string> ClientOverride { get; set; }

        /// <summary>(client id, client secret) from google_client.json (app folder, else this PC's data), or null when there is none yet.</summary>
        public static Tuple<string, string> LoadClient() => ClientOverride ?? ReadClient(ClientFile) ?? ReadClient(SavedClientFile) ?? EmbeddedClient.Value;

        // The app's own client, built in as an AES-encrypted resource (google_client.bin, made from the JSON by
        // tools/Protect-GoogleClient.ps1) so it is not a readable file next to the app. It only keeps casual eyes off:
        // Google treats a desktop app's client secret as not confidential; PKCE and the user's consent protect sign-in.
        private static readonly Lazy<Tuple<string, string>> EmbeddedClient = new Lazy<Tuple<string, string>>(() =>
        {
            try
            {
                using (var stream = typeof(GoogleCalendarService).Assembly.GetManifestResourceStream("ScheduleWidget.google_client.bin"))
                {
                    if (stream == null || stream.Length < 32 || stream.Length > 65536) return null;
                    var data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length) { int n = stream.Read(data, read, data.Length - read); if (n <= 0) return null; read += n; }
                    string json = Encoding.UTF8.GetString(DecryptClient(data));
                    var node = JObject.Parse(json);
                    var client = (node["installed"] ?? node["web"] ?? node) as JObject;
                    string id = (string)client?["client_id"], secret = (string)client?["client_secret"];
                    return string.IsNullOrWhiteSpace(id) ? null : Tuple.Create(id.Trim(), secret?.Trim() ?? "");
                }
            }
            catch (Exception ex) when (ex is CryptographicException || ex is JsonException || ex is IOException || ex is ArgumentException) { return null; }
        });

        // AES-256-CBC; the file is [16-byte IV][ciphertext]. Must match tools/Protect-GoogleClient.ps1.
        private const string ClientKeySeed = "ScheduleWidget|google-client|v1|7f3c9a2e5d8b41f6";

        public static byte[] DecryptClient(byte[] data)
        {
            using (var sha = SHA256.Create())
            using (var aes = Aes.Create())
            {
                aes.Key = sha.ComputeHash(Encoding.UTF8.GetBytes(ClientKeySeed));
                aes.IV = data.Take(16).ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(data, 16, data.Length - 16);
            }
        }

        public static byte[] EncryptClient(byte[] plain)
        {
            using (var sha = SHA256.Create())
            using (var aes = Aes.Create())
            {
                aes.Key = sha.ComputeHash(Encoding.UTF8.GetBytes(ClientKeySeed));
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var encryptor = aes.CreateEncryptor())
                    return aes.IV.Concat(encryptor.TransformFinalBlock(plain, 0, plain.Length)).ToArray();
            }
        }

        /// <summary>
        /// Reads a client file downloaded from Google Cloud Console; null if it is not one. Only a "데스크톱 앱" client works:
        /// a "웹 애플리케이션" one ("web") cannot answer on this PC's loopback port, so it is refused here instead of failing later.
        /// </summary>
        public static Tuple<string, string> ReadClient(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
                var json = JObject.Parse(File.ReadAllText(path));
                var node = json["installed"] as JObject ?? (json["web"] != null ? null : json);
                string id = (string)node?["client_id"], secret = (string)node?["client_secret"];
                return string.IsNullOrWhiteSpace(id) || !id.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                    ? null : Tuple.Create(id.Trim(), secret?.Trim() ?? "");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException) { return null; }
        }

        /// <summary>Keeps a picked client file for this PC. False when the file is not a Google OAuth client file.</summary>
        public static bool SaveClient(string path)
        {
            if (ReadClient(path) == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(SavedClientFile));
            File.Copy(path, SavedClientFile, true);
            return true;
        }

        // ---- sign-in ----

        /// <summary>Opens Google sign-in in the browser and waits (up to 5 minutes) for the answer on a loopback port.</summary>
        public async Task SignInAsync(GoogleCalendarSettings settings, CancellationToken cancel)
        {
            var app = LoadClient() ?? throw new InvalidOperationException(
                "이 앱에는 구글 연동용 OAuth 클라이언트가 들어 있지 않습니다. README의 '구글 캘린더 연동 준비'를 따라 한 번만 넣어 주세요.");
            string verifier = RandomToken(48), state = RandomToken(24);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true }; // no other program can share the port
            listener.Start();
            try
            {
                string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
                    "&client_id=" + Uri.EscapeDataString(app.Item1) + "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                    "&scope=" + Uri.EscapeDataString(Scope) + "&code_challenge=" + challenge +
                    "&code_challenge_method=S256&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                string code = await WaitForCodeAsync(listener, state, cancel);
                JObject token = await PostFormAsync("https://oauth2.googleapis.com/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
                    ["client_id"] = app.Item1, ["client_secret"] = app.Item2, ["code_verifier"] = verifier
                });
                string refresh = (string)token["refresh_token"];
                if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("구글이 연동 토큰을 주지 않았습니다. 다시 로그인해 주세요.");
                UseAccessToken(token);
                settings.ProtectedRefreshToken = SecretStore.Protect(refresh);
                // The account: the e-mail in Google's answer, else UserInfo using the same email permission; null when
                // neither can be read, so a passing error is never taken for another account. Signing in turns nothing on:
                // the switches in 설정 do.
                settings.Account = EmailOf(token["id_token"]) ?? await ReadAccountAsync();
                petFolderId = null; // another account has its own Drive folder
                lastFullRead = DateTime.MinValue;
            }
            finally { listener.Stop(); }
        }

        // The "email" claim of the ID token Google sends with the sign-in answer (straight from Google over HTTPS, so it is
        // read as is), or null.
        private static string EmailOf(JToken idToken)
        {
            try
            {
                string[] parts = (idToken?.Type == JTokenType.String ? (string)idToken : "").Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                string email = claims["email"]?.Type == JTokenType.String ? ((string)claims["email"]).Trim() : null;
                return string.IsNullOrEmpty(email) || email.IndexOf('@') <= 0 ? null : email;
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentException) { return null; }
        }

        private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken cancel)
        {
            var timeout = Task.Delay(TimeSpan.FromMinutes(5), cancel);
            while (true)
            {
                var accept = listener.AcceptTcpClientAsync();
                if (await Task.WhenAny(accept, timeout) != accept)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("구글 로그인이 5분 안에 끝나지 않았습니다. 다시 시도해 주세요.");
                }
                using (TcpClient tcp = accept.Result)
                using (NetworkStream stream = tcp.GetStream())
                {
                    // Browsers open spare connections that never send anything: give each a few seconds, never block. At most
                    // 8 KB is read, so another program on this PC cannot make the app swallow an endless line.
                    string requestLine = await ReadRequestLineAsync(stream, 8192, TimeSpan.FromSeconds(3));
                    if (requestLine == null) continue;
                    // "GET /?state=..&code=.. HTTP/1.1" — the browser may also ask for /favicon.ico.
                    string[] parts = requestLine.Split(' ');
                    string target = parts.Length >= 2 ? parts[1] : "";
                    int q = target.IndexOf('?');
                    var query = q < 0 ? new Dictionary<string, string>() : ParseQuery(target.Substring(q + 1));
                    bool ours = q >= 0 && query.TryGetValue("state", out string gotState) && gotState == state;
                    string message = !ours ? "" : query.ContainsKey("code") ? "구글 캘린더 연동이 끝났습니다. 이 창을 닫아도 됩니다." : "구글 로그인이 취소되었습니다.";
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>ScheduleWidget</title>" +
                        "<body style=\"font-family:sans-serif;padding:40px\"><h2>" + message + "</h2></body>");
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (ours ? "200 OK" : "404 Not Found") +
                        "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    try { await stream.WriteAsync(head, 0, head.Length); await stream.WriteAsync(body, 0, body.Length); }
                    catch (IOException) { }
                    if (!ours) continue;
                    if (query.TryGetValue("code", out string code) && !string.IsNullOrWhiteSpace(code)) return code;
                    throw new InvalidOperationException("구글 로그인이 취소되었습니다.");
                }
            }
        }

        // The first line of an HTTP request, read up to its '\n' within the limit and the wait; null otherwise.
        private static async Task<string> ReadRequestLineAsync(Stream stream, int limit, TimeSpan wait)
        {
            var buffer = new byte[limit];
            int length = 0;
            Task deadline = Task.Delay(wait);
            while (length < limit)
            {
                Task<int> read = stream.ReadAsync(buffer, length, limit - length);
                if (await Task.WhenAny(read, deadline) != read)
                {
                    _ = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); // fails once the socket closes
                    return null;
                }
                int count;
                try { count = read.Result; }
                catch (AggregateException) { return null; }
                if (count <= 0) return null;
                int newline = Array.IndexOf(buffer, (byte)'\n', length, count);
                length += count;
                if (newline >= 0) return Encoding.ASCII.GetString(buffer, 0, newline).TrimEnd('\r');
            }
            return null;
        }

        public async Task SignOutAsync(GoogleCalendarSettings settings)
        {
            string refresh = null;
            try { refresh = SecretStore.Unprotect(settings.ProtectedRefreshToken); } catch (InvalidOperationException) { }
            settings.ProtectedRefreshToken = null;
            settings.Account = null;
            settings.Enabled = false;
            settings.PetsEnabled = false;
            settings.LastSync = null;
            settings.ReadStartedUtc = null; // signed in again, the whole list is read first
            accessToken = null; // SyncedEventIds / LinkedAccount stay: the same account signing in again carries on
            petFolderId = null;
            lastFullRead = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(refresh)) return;
            // The token goes in the POST body, never in the URL (URLs end up in logs and proxies).
            try
            {
                using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }))
                using (await client.PostAsync("https://oauth2.googleapis.com/revoke", form)) { }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { } // local sign-out still happened
        }

        // ---- sync ----

        /// <summary>
        /// One two-way sync of <paramref name="schedules"/> with the primary calendar (rules: <see cref="GoogleCalendarSync.Plan"/>).
        /// Call on the UI thread: the list is only touched between awaits there (reading and parsing Google's list runs in the
        /// background), and items the user edits while a request is out stay 변경됨 for the next sync. Throws when the sync as
        /// a whole cannot go on (offline, signed out, Google's rate limit); one event Google refuses is skipped and counted.
        /// <paramref name="ownChanges"/> is called whenever this sync has changed the list itself (a new event's id, Google's
        /// answer taken in) and is about to wait, and once more at the end: a save meanwhile (a window move) can then tell
        /// those changes from the user's own edits.
        /// </summary>
        public async Task<GoogleSyncResult> SyncAsync(GoogleCalendarSettings settings, List<ScheduleItem> schedules, Action ownChanges = null)
        {
            var result = new GoogleSyncResult();
            string before = SettingsKey(settings);
            await EnsureAccessTokenAsync(settings);
            DateTime today = DateTime.Today;
            DateTime? readBefore = LastReadStart(settings);
            bool full = lastFullRead.Date != today || !readBefore.HasValue || settings.OwnershipVersion < 1;
            // Taken before reading: whatever changes on Google from now on is read by the next sync, however long this one runs.
            DateTime readStarted = DateTime.UtcNow;
            List<GoogleEvent> remote;
            // A few minutes back from the last read's start: this PC's clock and Google's may differ a little.
            try { remote = await ReadEventsAsync(today, full ? null : readBefore?.AddMinutes(-10)); }
            catch (GoogleRequestException ex) when (!full && ex.Status == HttpStatusCode.Gone)
            {
                full = true; // Google no longer answers "what changed" from that long ago: read everything
                remote = await ReadEventsAsync(today, null);
            }
            var linked = new HashSet<string>(schedules.Where(s => !string.IsNullOrEmpty(s.GoogleEventId)).Select(s => s.GoogleEventId));
            var owned = new HashSet<string>(settings.OwnedEventIds ?? new List<string>());
            GoogleCalendarSync.UpdateOwnership(owned, remote, GoogleCalendarSync.MadeHereIds(schedules), legacy: full && settings.OwnershipVersion < 1);
            var hidden = new Dictionary<string, string>(settings.HiddenEvents ?? new Dictionary<string, string>());
            var plan = GoogleCalendarSync.Plan(schedules, remote, settings.SyncedEventIds, today, owned, hidden);
            var synced = new HashSet<string>(settings.SyncedEventIds ?? new List<string>());
            // Ids of this PC's schedules while this sync runs: one the user deletes before it ends must stay "synced", so the
            // next sync handles the delete (dropping it here brought the deleted schedule back from Google).
            var ours = new HashSet<string>(linked);

            // A schedule waiting for its event: the id is chosen before the request and kept on it, so a lost answer never
            // makes a second copy (sent again, Google's 409 links it to the event made the first time).
            void Pending(ScheduleItem item, string id)
            {
                item.GoogleEventId = id;
                item.GoogleSyncedHash = null;
                item.GoogleSyncedPeriod = null;
                synced.Add(id);
                owned.Add(id);
                ours.Add(id);
                result.DataChanged = true;
            }
            void Drop(string id)
            {
                synced.Remove(id);
                owned.Remove(id);
                ours.Remove(id);
            }

            var read = new HashSet<string>(remote.Select(e => e.Id));
            foreach (string id in plan.DeleteRemote)
            {
                synced.Remove(id);
                owned.Remove(id);
                // Not read this time (outside the read range — e.g. an older version's event from months ago — or unchanged
                // since the last read): looked at first. One that got guests or became a repeating event on Google meanwhile
                // stays there, out of the widget only; one already gone there needs nothing.
                if (!read.Contains(id))
                {
                    JObject there = null;
                    bool looked = true;
                    try { there = await SendAsync(HttpMethod.Get, EventUrl(id), null, allowMissing: true); }
                    catch (GoogleRequestException ex) when (ex.IsRefusal) { looked = false; } // cannot look: deleted as planned
                    if (looked && (there == null || Text(there["status"]) == "cancelled")) continue;
                    if (looked && (IsShared(there) || there["recurrence"] != null || there["recurringEventId"] != null))
                    {
                        hidden[id] = TryParseEvent(there)?.LastPeriod;
                        continue;
                    }
                }
                try { await SendAsync(new HttpMethod("DELETE"), EventUrl(id), null, allowMissing: true); }
                catch (GoogleRequestException ex) when (ex.IsRefusal) { hidden[id] = null; result.Refused++; } // Google keeps it: out of the widget only
            }
            foreach (var pair in plan.Unlink)
            {
                synced.Remove(pair.Key);
                owned.Remove(pair.Key);
                hidden[pair.Key] = pair.Value;
            }

            var fresh = new HashSet<ScheduleItem>(plan.NewId);
            var insert = new List<ScheduleItem>(plan.Insert);
            foreach (var item in plan.Patch)
            {
                if (!schedules.Contains(item)) continue; // deleted meanwhile: the next sync takes care of it
                string hash = GoogleCalendarSync.Hash(item);
                if (plan.Remote.TryGetValue(item, out GoogleEvent there) && item.GoogleEndMinutes != there.EndMinutes)
                {
                    item.GoogleEndMinutes = there.EndMinutes; // its length on Google now: a move keeps it
                    result.DataChanged = true;
                }
                JObject answer;
                try
                {
                    JObject body = PatchBody(item, owned.Contains(item.GoogleEventId));
                    if (body.Count == 0) { MarkSynced(item, hash); result.DataChanged = true; continue; }
                    ownChanges?.Invoke();
                    answer = await SendAsync(new HttpMethod("PATCH"), EventUrl(item.GoogleEventId), body, allowMissing: true);
                }
                catch (Exception ex) when (IsRefusal(ex)) { Refuse(item, hash, result); continue; }
                if (answer == null || Text(answer["status"]) == "cancelled")
                {
                    // Gone on Google meanwhile: the edited schedule is added again, under a new id (a deleted one stays taken).
                    fresh.Add(item);
                    insert.Add(item);
                    continue;
                }
                Accept(item, answer, hash, result);
            }

            int sent = 0;
            foreach (var item in insert)
            {
                if (!schedules.Contains(item)) continue; // deleted meanwhile
                if (sent >= MaxInsertsPerSync) { result.More = true; break; }
                if (sent++ > 0 && InsertPacing > TimeSpan.Zero)
                {
                    ownChanges?.Invoke();
                    await Task.Delay(InsertPacing); // a first sync can add hundreds: Google limits how fast events are made
                    if (!schedules.Contains(item)) continue;
                }
                string hash = GoogleCalendarSync.Hash(item);
                string old = item.GoogleEventId;
                string id = fresh.Contains(item) ? GoogleCalendarSync.NewEventId() : old ?? GoogleCalendarSync.EventIdFor(item);
                if (old != null && old != id && !schedules.Any(s => s != item && s.GoogleEventId == old)) Drop(old);
                Pending(item, id);
                ownChanges?.Invoke();
                JObject answer = null;
                bool adopted = false;
                try { answer = await SendAsync(HttpMethod.Post, EventsUrl, InsertBody(item, id), allowMissing: false); }
                catch (GoogleRequestException ex) when (ex.Status == HttpStatusCode.Conflict)
                {
                    // The id is taken: by this app's own earlier request (its answer never came), or by an event deleted since.
                    answer = await SendAsync(HttpMethod.Get, EventUrl(id), null, allowMissing: true);
                    adopted = answer != null && Text(answer["status"]) != "cancelled";
                    if (!adopted) answer = null;
                }
                catch (Exception ex) when (IsRefusal(ex)) { Refuse(item, hash, result); continue; }
                if (answer == null)
                {
                    Drop(id);
                    id = GoogleCalendarSync.NewEventId();
                    Pending(item, id);
                    ownChanges?.Invoke();
                    try { answer = await SendAsync(HttpMethod.Post, EventsUrl, InsertBody(item, id), allowMissing: false); }
                    catch (Exception ex) when (IsRefusal(ex)) { Refuse(item, hash, result); continue; }
                }
                string given = Text(answer["id"]);
                if (!string.IsNullOrEmpty(given) && given != id) { Drop(id); id = given; Pending(item, id); } // Google chose its own id
                GoogleEvent made = TryParseEvent(answer);
                if (made != null && made.Shared) owned.Remove(id);
                if (adopted && made != null)
                {
                    // Linked to the event made the first time: whatever differs from it now goes up with the next sync.
                    item.GoogleRefusedHash = null;
                    item.GoogleEndMinutes = made.EndMinutes;
                    item.GoogleSyncedHash = GoogleCalendarSync.Hash(made);
                    item.GoogleSyncedPeriod = made.Period;
                }
                else Accept(item, answer, hash, result);
            }

            foreach (var pair in plan.UpdateLocal)
            {
                // Edited here while the requests above were out: this PC wins — keep the edit (it goes up next sync).
                if (GoogleCalendarSync.Hash(pair.Key) != pair.Key.GoogleSyncedHash) { synced.Add(pair.Value.Id); continue; }
                result.ListChanged |= ApplyRemote(pair.Key, pair.Value);
                result.DataChanged = true;
                synced.Add(pair.Value.Id);
            }
            foreach (var pair in plan.UpdateLength)
            {
                if (GoogleCalendarSync.Hash(pair.Key) != pair.Key.GoogleSyncedHash || pair.Key.GoogleEndMinutes == pair.Value.EndMinutes) continue;
                pair.Key.GoogleEndMinutes = pair.Value.EndMinutes; // lengthened or shortened on Google: a later move keeps the new length
                result.DataChanged = true;
            }
            // Inserts above may have adopted an event whose POST response was lost. It was already
            // included in the pre-request AddLocal plan, so keep it out if linked or still pending here.
            var linkedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in schedules)
                linkedIds.Add(string.IsNullOrEmpty(item.GoogleEventId) ? GoogleCalendarSync.EventIdFor(item) : item.GoogleEventId);
            foreach (var item in insert)
                if (!fresh.Contains(item)) linkedIds.Add(GoogleCalendarSync.EventIdFor(item)); // the schedule may have been deleted while a request was out
            foreach (var e in plan.AddLocal)
            {
                if (ours.Contains(e.Id) || !linkedIds.Add(e.Id)) continue;
                var item = new ScheduleItem { GoogleEventId = e.Id };
                ApplyRemote(item, e);
                schedules.Add(item);
                synced.Add(e.Id);
                result.DataChanged = result.ListChanged = true;
            }
            // Last, once every request went through (a sync cut short must not leave them looking "deleted here"): a
            // repeating date an older version read up to two years ahead leaves the list and comes back 8 weeks before.
            foreach (var item in plan.Prune)
            {
                if (GoogleCalendarSync.Hash(item) != item.GoogleSyncedHash || !schedules.Remove(item)) continue;
                Drop(item.GoogleEventId);
                result.DataChanged = result.ListChanged = true;
            }
            var now = new HashSet<string>(schedules.Where(s => !string.IsNullOrEmpty(s.GoogleEventId)).Select(s => s.GoogleEventId));
            synced.UnionWith(now);
            // Keep what is here now, plus what was here during this sync and got deleted meanwhile (handled next sync).
            settings.SyncedEventIds = synced.Where(id => now.Contains(id) || ours.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var kept = new HashSet<string>(settings.SyncedEventIds);
            settings.OwnedEventIds = owned.Where(kept.Contains).OrderBy(id => id, StringComparer.Ordinal).ToList();
            settings.HiddenEvents = TrimHidden(hidden, kept, remote, full, today);
            settings.LastSync = DateTime.Now;
            settings.ReadStartedUtc = readStarted; // only once everything went through: a sync cut short is read again from before
            if (full)
            {
                lastFullRead = DateTime.Now;
                settings.OwnershipVersion = 1;
            }
            if (SettingsKey(settings) != before) result.DataChanged = true;
            ownChanges?.Invoke();
            return result;
        }

        private static string EventUrl(string id) => EventsUrl + "/" + Uri.EscapeDataString(id);

        // Google will not take this one event as it is (bad data, no right to change that event, …), or its date cannot be
        // written: skip it and go on with the others.
        private static bool IsRefusal(Exception ex) => ex is GoogleRequestException request ? request.IsRefusal : ex is FormatException || ex is ArgumentException;

        private static void Refuse(ScheduleItem item, string hash, GoogleSyncResult result)
        {
            item.GoogleRefusedHash = hash; // not sent again until it is edited here
            result.Refused++;
            result.DataChanged = true;
        }

        // A PATCH / insert went through: the schedule takes the event Google sent back — which also brings what was changed
        // only on Google meanwhile (the fields this PC did not send) — unless the user edited it again during the request
        // (then that edit goes up next sync).
        private static void Accept(ScheduleItem item, JObject answer, string hash, GoogleSyncResult result)
        {
            item.GoogleRefusedHash = null;
            result.DataChanged = true;
            GoogleEvent there = TryParseEvent(answer);
            if (there != null) item.GoogleEndMinutes = there.EndMinutes;
            if (there != null && GoogleCalendarSync.Hash(item) == hash) result.ListChanged |= ApplyRemote(item, there);
            else MarkSynced(item, hash);
        }

        // Events taken out of the widget stay out while a read may still bring them: dropped once their date is over a month
        // past (no read reaches further back), or, date unknown, once a full read no longer has them.
        private static Dictionary<string, string> TrimHidden(Dictionary<string, string> hidden, ICollection<string> linked, IEnumerable<GoogleEvent> remote,
            bool full, DateTime today)
        {
            var read = new HashSet<string>(remote.Select(e => e.Id));
            DateTime past = today.Date.AddDays(-31);
            var kept = new List<KeyValuePair<string, string>>();
            foreach (var entry in hidden)
            {
                bool gone = entry.Value == null ? full && !read.Contains(entry.Key)
                    : !DateTime.TryParseExact(entry.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) || date < past;
                if (!gone && !linked.Contains(entry.Key)) kept.Add(entry);
            }
            // At most 5000 (the latest dates kept).
            return kept.OrderByDescending(e => e.Value ?? "9999").Take(5000).OrderBy(e => e.Key, StringComparer.Ordinal)
                .ToDictionary(e => e.Key, e => e.Value);
        }

        // What a sync may change in the settings (not LastSync or ReadStartedUtc, which every sync moves — they are saved with
        // the next save; the first sync after a start reads everything anyway): compared before and after to tell whether to save.
        private static string SettingsKey(GoogleCalendarSettings s) =>
            s.ProtectedRefreshToken + "\n" + s.Account + "\n" + s.OwnershipVersion + "\n" + s.PetDeletionsSorted + "\n" +
            string.Join(",", s.SyncedEventIds ?? new List<string>()) + "\n" + string.Join(",", s.OwnedEventIds ?? new List<string>()) + "\n" +
            string.Join(",", (s.HiddenEvents ?? new Dictionary<string, string>()).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value)) + "\n" +
            string.Join(",", s.SyncedPetIds ?? new List<string>()) + "\n" + string.Join(",", s.FailedPetFiles ?? new List<string>()) + "\n" +
            string.Join(",", s.RefusedPetDeletes ?? new List<string>());

        // The start of the last read that went through, as UTC; null when there is none, or when it lies ahead of this PC's
        // clock (the clock was set back): then the whole list is read.
        private static DateTime? LastReadStart(GoogleCalendarSettings settings)
        {
            if (!(settings.ReadStartedUtc is DateTime mark)) return null;
            mark = mark.Kind == DateTimeKind.Local ? mark.ToUniversalTime() : DateTime.SpecifyKind(mark, DateTimeKind.Utc);
            return mark > DateTime.UtcNow.AddMinutes(1) ? (DateTime?)null : mark;
        }

        // ---- 캐릭터 보관 (Google Drive, folder "ScheduleWidget 캐릭터", one .zip per imported character) ----

        private string petFolderId;
        private DateTime petFolderChecked;
        private static string lastSameNameLog = "|";

        /// <summary>
        /// Keeps the imported characters in step with Drive (see <see cref="PetSyncPlan.Build"/>). A character that fails on its
        /// own (a broken file, a slow upload, …) is skipped and counted; a Drive file that could not be brought in is tried
        /// again only once it changes there. Reading the characters and packing / unpacking zips runs in the background.
        /// </summary>
        public async Task<GooglePetSyncResult> SyncPetsAsync(GoogleCalendarSettings settings)
        {
            var result = new GooglePetSyncResult();
            string before = SettingsKey(settings);
            await EnsureAccessTokenAsync(settings);
            string folder = await PetFolderAsync();
            Dictionary<string, DrivePet> remote;
            try { remote = await ListPetFilesAsync(folder); }
            catch (GoogleRequestException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                petFolderId = null; // the folder is gone: found or made again
                folder = await PetFolderAsync();
                remote = await ListPetFilesAsync(folder);
            }
            // Older versions kept no record of a character deleted here (a synced one missing here was deleted on Drive by their
            // next sync). The first character sync of this version sorts those out once (CharacterCatalog.RememberOldDeletions);
            // from then on only a character deleted in 캐릭터 선택 is deleted on Drive.
            bool sortOld = !settings.PetDeletionsSorted;
            var syncedBefore = (settings.SyncedPetIds ?? new List<string>()).ToList();
            LocalPets here = await Task.Run(() =>
            {
                if (sortOld) CharacterCatalog.RememberOldDeletions(syncedBefore);
                return LocalPets.Read();
            });
            settings.PetDeletionsSorted = true;
            var ids = StringComparer.OrdinalIgnoreCase; // IDs are folder names here (Windows ignores case)
            var refused = new HashSet<string>(settings.RefusedPetDeletes ?? new List<string>(), ids);
            var plan = PetSyncPlan.Build(here.Names, remote.ToDictionary(r => r.Key, r => r.Value.Name), settings.SyncedPetIds, here.BuiltInNames,
                here.Deleted, here.Present, refused);
            foreach (string id in plan.SameNameNotUploaded) result.SameNameNotBackedUp.Add(here.Names[id]);
            string skipped = string.Join(",", plan.SameNameNotUploaded) + "|" + string.Join(",", plan.SameNameNotDownloaded);
            if (skipped != lastSameNameLog)
            {
                lastSameNameLog = skipped; // logged when it changes, not on every 5-minute sync
                if (skipped != "|") PetLog.Write("pet-sync-same-name", "not uploaded: " + string.Join(", ", plan.SameNameNotUploaded.Select(id => id + " (" + here.Names[id] + ")")) +
                    "; not downloaded: " + string.Join(", ", plan.SameNameNotDownloaded.Select(id => id + " (" + remote[id].Name + ")")));
            }
            var synced = new HashSet<string>(settings.SyncedPetIds ?? new List<string>(), ids);
            var failed = new HashSet<string>(settings.FailedPetFiles ?? new List<string>());
            // Deleted here and not on Drive (or here again): nothing left to delete.
            var settled = here.Deleted.Where(id => !remote.ContainsKey(id) || here.Names.ContainsKey(id)).ToList();

            foreach (string id in plan.DeleteRemote)
            {
                try
                {
                    await SendAsync(new HttpMethod("DELETE"), DriveFiles + "/" + Uri.EscapeDataString(remote[id].FileId), null, allowMissing: true);
                    result.Deleted++;
                }
                catch (GoogleRequestException ex) when (ex.IsRefusal)
                {
                    // Drive keeps it (not asked again): remembered, so it is never downloaded back here either.
                    Fail(result, ex);
                    refused.Add(id);
                }
                synced.Remove(id);
                settled.Add(id);
            }
            CharacterCatalog.ForgetDeleted(settled);
            foreach (string id in plan.Upload)
            {
                string zip = TempZip();
                try
                {
                    string name = await Task.Run(() =>
                    {
                        var entry = CharacterCatalog.Read(Path.Combine(CharacterCatalog.PetDirectory, id, "pet.json"));
                        CharacterCatalog.Export(entry, zip);
                        return entry.Name;
                    });
                    await UploadPetAsync(folder, id, name, zip);
                    synced.Add(id);
                    result.Uploaded++;
                }
                catch (Exception ex) when (IsPetFailure(ex)) { Fail(result, ex); }
                finally { TryDeleteFile(zip); }
            }
            var builtInNamed = new HashSet<string>(plan.DownloadUnlessBuiltIn, ids);
            foreach (string id in plan.Download.Concat(plan.DownloadUnlessBuiltIn))
            {
                DrivePet file = remote[id];
                if (failed.Contains(file.Mark)) continue; // failed before as it is: tried again once it changes on Drive
                string zip = TempZip();
                try
                {
                    await DownloadAsync(DriveFiles + "/" + Uri.EscapeDataString(file.FileId) + "?alt=media", zip, 30L * 1024 * 1024);
                    // A Codex character's very sheet (whatever its name) is never brought in: the Codex characters come only
                    // from the installed Codex. Skipped like a copy (not a failure; looked at again once that file changes).
                    // A default character's very sheet (기본 캐릭터, whatever its name) is already here: skipped the same way.
                    if (await Task.Run(() => CharacterCatalog.ZipHasCodexSheet(zip) || CharacterCatalog.ZipHasDefaultSheet(zip)))
                    {
                        failed.Add(file.Mark);
                        continue;
                    }
                    if (builtInNamed.Contains(id) && await Task.Run(() => CharacterCatalog.ZipCopiesBuiltIn(zip)))
                    {
                        // Just a copy of the built-in of that name: not brought in twice (not a failure; looked at again only
                        // once that file changes on Drive).
                        failed.Add(file.Mark);
                        continue;
                    }
                    await Task.Run(() => CharacterCatalog.Import(zip, null, id));
                    synced.Add(id);
                    result.Downloaded++;
                }
                catch (Exception ex) when (IsPetFailure(ex))
                {
                    Fail(result, ex);
                    failed.Add(file.Mark);
                }
                finally { TryDeleteFile(zip); }
            }
            foreach (string id in here.Names.Keys) if (remote.ContainsKey(id)) synced.Add(id);
            var nowLocal = new HashSet<string>(await Task.Run(() => CharacterCatalog.ImportedIds()), ids);
            var deletedThere = new HashSet<string>(plan.DeleteRemote, ids);
            // Keep an ID while the character is here or on Drive; forget it once it is gone from both.
            settings.SyncedPetIds = synced.Where(id => nowLocal.Contains(id) || (remote.ContainsKey(id) && !deletedThere.Contains(id)))
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            // A refused delete is kept while its file is on Drive and the character is not here again.
            settings.RefusedPetDeletes = refused.Where(id => remote.ContainsKey(id) && !nowLocal.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var marks = new HashSet<string>(remote.Values.Select(f => f.Mark));
            settings.FailedPetFiles = failed.Where(marks.Contains).OrderBy(m => m, StringComparer.Ordinal).ToList(); // only while that file is there
            result.Changed = result.Downloaded > 0 || SettingsKey(settings) != before;
            return result;
        }

        // A problem with one character (its file, its upload, …): counted, and the others go on. Signing in again, Google's
        // rate limit or no connection stop the whole character sync instead.
        private static bool IsPetFailure(Exception ex) => CharacterCatalog.IsImportError(ex) && !(ex is GoogleSignInRequiredException) &&
            !(ex is GoogleScopeException) && !(ex is GoogleRateLimitException) && !(ex is GoogleConnectionException);

        private static void Fail(GooglePetSyncResult result, Exception ex)
        {
            result.Failed++;
            if (result.FirstError == null) result.FirstError = ex.Message;
        }

        private static string TempZip() => Path.Combine(Path.GetTempPath(), "ScheduleWidgetPet-" + Guid.NewGuid().ToString("N") + ".zip");

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); } catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
        }

        // The characters here, read in the background: the imported ones that read fine (ID → name), every imported folder
        // (also one that cannot be read now), the built-in names and the ones deleted here.
        private sealed class LocalPets
        {
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Present, BuiltInNames, Deleted;

            public static LocalPets Read()
            {
                var characters = CharacterCatalog.Load(out _);
                var pets = new LocalPets { Present = CharacterCatalog.ImportedIds(), Deleted = CharacterCatalog.DeletedIds() };
                foreach (string id in pets.Present)
                {
                    var entry = characters.FirstOrDefault(c => c.Group == "가져온 캐릭터" &&
                        string.Equals(Path.GetFileName(Path.GetDirectoryName(c.ManifestPath)), id, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) pets.Names[id] = entry.Name;
                }
                pets.BuiltInNames = characters.Where(c => c.Group != "가져온 캐릭터").Select(c => c.Name).ToList();
                return pets;
            }
        }

        // One character file in the Drive folder.
        private sealed class DrivePet
        {
            public string FileId, Name, Modified;
            public string Mark => FileId + "|" + Modified; // changes whenever the file does
        }

        private async Task<string> PetFolderAsync()
        {
            // The folder found before is checked again every half hour: trashed or deleted meanwhile, it is found or made anew.
            if (petFolderId != null && DateTime.UtcNow - petFolderChecked < TimeSpan.FromMinutes(30)) return petFolderId;
            if (petFolderId != null)
            {
                JObject known = await SendAsync(HttpMethod.Get, DriveFiles + "/" + Uri.EscapeDataString(petFolderId) + "?fields=id,trashed", null, allowMissing: true);
                if (known != null && !IsTrue(known["trashed"])) { petFolderChecked = DateTime.UtcNow; return petFolderId; }
                petFolderId = null;
            }
            string query = "mimeType='application/vnd.google-apps.folder' and name='" + PetFolderName + "' and trashed=false";
            JObject found = await SendAsync(HttpMethod.Get, DriveFiles + "?pageSize=10&fields=files(id)&q=" + Uri.EscapeDataString(query), null, allowMissing: false);
            string id = Text((found["files"] as JArray)?.FirstOrDefault()?["id"]);
            if (id == null)
            {
                JObject created = await SendAsync(HttpMethod.Post, DriveFiles + "?fields=id",
                    new JObject { ["name"] = PetFolderName, ["mimeType"] = "application/vnd.google-apps.folder" }, allowMissing: false);
                id = Text(created["id"]) ?? throw new InvalidOperationException("구글 드라이브에 캐릭터 폴더를 만들지 못했습니다.");
            }
            petFolderId = id;
            petFolderChecked = DateTime.UtcNow;
            return petFolderId;
        }

        // petId → the character file in the folder (Drive file id, character name, last change).
        private async Task<Dictionary<string, DrivePet>> ListPetFilesAsync(string folder)
        {
            var result = new Dictionary<string, DrivePet>(StringComparer.OrdinalIgnoreCase); // IDs become folder names here
            string pageToken = null;
            do
            {
                string query = "'" + folder + "' in parents and trashed=false";
                JObject page = await SendAsync(HttpMethod.Get, DriveFiles + "?pageSize=1000&fields=nextPageToken,files(id,name,appProperties,modifiedTime)&q=" +
                    Uri.EscapeDataString(query) + (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), null, allowMissing: false);
                foreach (JObject file in (page["files"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var properties = file["appProperties"] as JObject;
                    string petId = Text(properties?["petId"]);
                    string name = Text(properties?["petName"]) ?? Path.GetFileNameWithoutExtension(Text(file["name"]) ?? "");
                    string fileId = Text(file["id"]);
                    if (CharacterCatalog.IsPetId(petId) && fileId != null && !result.ContainsKey(petId))
                        result[petId] = new DrivePet { FileId = fileId, Name = name, Modified = Text(file["modifiedTime"]) ?? "" };
                }
                pageToken = Text(page["nextPageToken"]);
            } while (!string.IsNullOrEmpty(pageToken));
            return result;
        }

        private async Task UploadPetAsync(string folder, string petId, string name, string zipPath)
        {
            string safeName = string.Concat((name ?? "캐릭터").Split(Path.GetInvalidFileNameChars())).Trim();
            var metadata = new JObject
            {
                ["name"] = (safeName.Length == 0 ? "캐릭터" : safeName) + ".zip",
                ["parents"] = new JArray(folder),
                // Drive keeps at most 124 bytes (UTF-8) per property, key included: the name is cut on a character boundary.
                ["appProperties"] = new JObject { ["petId"] = petId, ["petName"] = Utf8Prefix(name, 110) }
            };
            string location;
            using (var start = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id"))
            {
                start.Content = new StringContent(metadata.ToString(Formatting.None), Encoding.UTF8, "application/json");
                start.Headers.Add("X-Upload-Content-Type", "application/zip");
                using (var response = await SendRawAsync(start, transfer: false))
                    location = response.Headers.Location?.AbsoluteUri ?? throw new InvalidOperationException("구글 드라이브 업로드를 시작하지 못했습니다.");
            }
            using (var file = File.OpenRead(zipPath))
            using (var put = new HttpRequestMessage(HttpMethod.Put, location))
            {
                put.Content = new StreamContent(file);
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using (await SendRawAsync(put, transfer: true)) { }
            }
        }

        /// <summary>The longest start of <paramref name="text"/> that fits in <paramref name="maxBytes"/> UTF-8 bytes (never half a character).</summary>
        public static string Utf8Prefix(string text, int maxBytes)
        {
            text = text ?? "";
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
            int bytes = 0, length = 0;
            while (length < text.Length)
            {
                int size = char.IsHighSurrogate(text[length]) && length + 1 < text.Length && char.IsLowSurrogate(text[length + 1]) ? 2 : 1;
                int more = Encoding.UTF8.GetByteCount(text.Substring(length, size));
                if (bytes + more > maxBytes) break;
                bytes += more;
                length += size;
            }
            return text.Substring(0, length);
        }

        private async Task DownloadAsync(string url, string path, long limit)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await SendRawAsync(request, transfer: true))
            {
                if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("구글 드라이브의 캐릭터 파일이 너무 큽니다.");
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var target = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        // A transfer that stalls for a minute is given up (the response stream has no timeout of its own).
                        Task<int> reading = source.ReadAsync(buffer, 0, buffer.Length);
                        if (await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMinutes(1))) != reading)
                        {
                            _ = reading.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            throw new InvalidOperationException("구글 드라이브에서 캐릭터 파일을 받다가 멈췄습니다. 다음 동기화 때 다시 받습니다.");
                        }
                        int read = await reading;
                        if (read <= 0) break;
                        total += read;
                        if (total > limit) throw new InvalidOperationException("구글 드라이브의 캐릭터 파일이 너무 큽니다.");
                        await target.WriteAsync(buffer, 0, read);
                    }
                }
            }
        }

        // Sends with the access token; the caller disposes the (successful) response. transfer: a character file going up or
        // down (minutes allowed; a transfer that takes too long fails on its own, not the whole sync).
        private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, bool transfer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage response;
            try { response = await (transfer ? transferClient : client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead); }
            catch (HttpRequestException ex) { throw new GoogleConnectionException("구글에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.", ex); }
            catch (TaskCanceledException ex) { throw new InvalidOperationException("구글 드라이브 전송이 너무 오래 걸려 중단했습니다. 다음 동기화 때 다시 시도합니다.", ex); }
            if (response.IsSuccessStatusCode) return response;
            using (response) await ThrowForStatusAsync(response, request.RequestUri);
            return null;
        }

        // Turns an error answer into the right exception: sign-in expired (the next sync signs in again), Google's rate limit
        // (the sync waits), a permission not granted at sign-in (Calendar, or Drive for 캐릭터 보관 — named from the request's
        // path), an API switched off in the Cloud project, or one refused request.
        private async Task ThrowForStatusAsync(HttpResponseMessage response, Uri requestUri)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) { accessToken = null; throw new InvalidOperationException("구글 인증이 만료되었습니다. 잠시 후 다시 동기화합니다."); }
            string text = "";
            try { text = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException) { }
            string reasons = ErrorReasons(text);
            int status = (int)response.StatusCode;
            if (status == 429 || (status == 403 && ContainsAny(reasons, "rateLimitExceeded", "userRateLimitExceeded", "quotaExceeded", "dailyLimitExceeded",
                    "RATE_LIMIT_EXCEEDED", "RESOURCE_EXHAUSTED")))
                throw new GoogleRateLimitException(RetryAfter(response));
            if (status == 403)
            {
                // Only a missing permission (signed in before 캐릭터 보관 existed, or a permission left unticked on Google's
                // sign-in page) asks for signing in again — not any 403. Which one: the request's path says.
                if (ContainsAny(reasons, "ACCESS_TOKEN_SCOPE_INSUFFICIENT", "insufficientScopes") ||
                    text.IndexOf("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new GoogleScopeException(drive: IsDriveRequest(requestUri));
                if (text.IndexOf("accessNotConfigured", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("SERVICE_DISABLED", StringComparison.Ordinal) >= 0)
                {
                    // Name the API that is off (Google's error mentions its service name, else the request's path), so the fix is obvious.
                    string api = text.IndexOf("drive.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0 ? "Google Drive API"
                        : text.IndexOf("calendar", StringComparison.OrdinalIgnoreCase) >= 0 ? "Google Calendar API"
                        : IsDriveRequest(requestUri) ? "Google Drive API" : "Google Calendar API";
                    throw new InvalidOperationException("구글 Cloud 프로젝트에서 " + api + "가 꺼져 있습니다. 콘솔의 'API 및 서비스 → 라이브러리'에서 사용 설정한 뒤 몇 분 후 다시 동기화해 주세요.");
                }
            }
            throw new GoogleRequestException(response.StatusCode, reasons, "구글 요청 실패 (HTTP " + status + ").");
        }

        // The reason codes in Google's error JSON (error.errors[].reason, error.status, error.details[].reason).
        private static string ErrorReasons(string text)
        {
            try
            {
                var error = (string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text))?["error"] as JObject;
                if (error == null) return "";
                var reasons = new List<string>();
                if (Text(error["status"]) != null) reasons.Add(Text(error["status"]));
                foreach (string list in new[] { "errors", "details" })
                    foreach (JObject entry in (error[list] as JArray ?? new JArray()).OfType<JObject>())
                        if (Text(entry["reason"]) != null) reasons.Add(Text(entry["reason"]));
                return string.Join(" ", reasons);
            }
            catch (JsonException) { return ""; }
        }

        private static bool ContainsAny(string text, params string[] words) => words.Any(w => (text ?? "").IndexOf(w, StringComparison.Ordinal) >= 0);

        // A Drive request (캐릭터 보관: /drive/v3/…, uploads /upload/drive/v3/…); anything else here is the calendar (/calendar/v3/…).
        private static bool IsDriveRequest(Uri uri) => (uri?.AbsolutePath ?? "").IndexOf("/drive/", StringComparison.OrdinalIgnoreCase) >= 0;

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta != null) return header.Delta;
            if (header?.Date != null) return header.Date.Value > DateTimeOffset.UtcNow ? header.Date.Value - DateTimeOffset.UtcNow : TimeSpan.Zero;
            return null;
        }

        private static void MarkSynced(ScheduleItem item, string hash)
        {
            item.GoogleSyncedHash = hash;
            item.GoogleSyncedPeriod = item.Period;
        }

        // Returns whether what the lists show (title, date, time, 완료) changed.
        private static bool ApplyRemote(ScheduleItem item, GoogleEvent e)
        {
            bool shown = item.Title != e.Title || item.Period != e.Period || item.Time != e.Time || item.IsCompleted != e.Completed ||
                item.EndPeriod != e.EndPeriod;
            item.Title = e.Title;
            item.Period = e.Period;
            item.EndPeriod = e.EndPeriod; // 여러 날 on Google: a range here too (one day: none)
            item.Time = e.Time;
            item.IsCompleted = e.Completed;
            item.GoogleEndMinutes = e.EndMinutes;
            item.GoogleSyncedHash = GoogleCalendarSync.Hash(e);
            item.GoogleSyncedPeriod = e.Period;
            return shown;
        }

        /// <summary>Google event JSON for a schedule: all-day without a time, else a timed event keeping its length (1 hour new).</summary>
        public static JObject EventBody(ScheduleItem item)
        {
            var body = new JObject { ["summary"] = Summary(item.Title) };
            SetTimes(body, item, wasTimed: null);
            body["extendedProperties"] = new JObject { ["private"] = new JObject { [CompletedKey] = item.IsCompleted ? "1" : "0", [AppKey] = "1" } };
            return body;
        }

        // A new event: EventBody without the empty fields, under the id chosen here.
        private static JObject InsertBody(ScheduleItem item, string id)
        {
            var body = EventBody(item);
            foreach (var side in new[] { "start", "end" })
                foreach (var empty in ((JObject)body[side]).Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) empty.Remove();
            body["id"] = id;
            return body;
        }

        /// <summary>
        /// What a PATCH sends for a schedule changed here: only what differs from the last sync (title, date and time, 완료), so
        /// what changed on Google meanwhile — or is never shown here (length, guests, notes, seconds) — stays as it is there.
        /// <paramref name="owned"/>: made by this app (the mark goes along, so other PCs know it too).
        /// </summary>
        public static JObject PatchBody(ScheduleItem item, bool owned)
        {
            string[] was = (item.GoogleSyncedHash ?? "").Split('\u001f');
            bool known = was.Length == 4 || was.Length == 5; // 5: it was a 여러 날 schedule (its last day)
            string wasEnd = was.Length == 5 ? was[4] : "";
            var body = new JObject();
            if (!known || was[0] != (item.Title ?? "").Trim()) body["summary"] = Summary(item.Title);
            if (!known || was[1] != (item.Period ?? "") || was[2] != (item.Time ?? "") || wasEnd != (GoogleCalendarSync.EndOf(item) ?? ""))
                SetTimes(body, item, known ? was[2].Length > 0 : (bool?)null, known ? wasEnd.Length > 0 : (bool?)null);
            var properties = new JObject();
            if (!known || was[3] != (item.IsCompleted ? "1" : "0")) properties[CompletedKey] = item.IsCompleted ? "1" : "0";
            if (owned && (body.Count > 0 || properties.Count > 0)) properties[AppKey] = "1";
            if (properties.Count > 0) body["extendedProperties"] = new JObject { ["private"] = properties };
            return body;
        }

        // The title Google gets: "(제목 없음)" is only how an untitled event is shown here, never a title to send back.
        private static string Summary(string title) => title == GoogleCalendarSync.Untitled ? "" : title ?? "";

        // start / end of a schedule. wasTimed (what it was at the last sync; null = not known): an all-day event keeps its
        // number of days and a timed one its length; one that just got or lost its time becomes 1 hour / 1 day.
        // 여러 날 (EndPeriod): all-day → start = Period, end = the last day + 1 (Google's end date is exclusive); with a time →
        // start = Period + time, end = the last day at the same time (+1 hour if that is not after the start) — or, when it was
        // a timed range at the last sync, the last day at the clock time it ended then (from its length), so moving it keeps
        // its end time. A range turned back into one day (wasRange) becomes 1 day / 1 hour.
        private static void SetTimes(JObject body, ScheduleItem item, bool? wasTimed, bool? wasRange = null)
        {
            DateTime date = DateTime.ParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            DateTime? last = item.EndDate;
            int? length = item.GoogleEndMinutes > 0 ? item.GoogleEndMinutes : null;
            if (string.IsNullOrEmpty(item.Time) || !TimeSpan.TryParseExact(item.Time, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan time))
            {
                DateTime end = last.HasValue ? last.Value.AddDays(1)
                    : date.AddDays(wasTimed == true || wasRange == true ? 1 : Math.Max(1, (length ?? 1440) / 1440));
                body["start"] = new JObject { ["date"] = item.Period, ["dateTime"] = null };
                body["end"] = new JObject { ["date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["dateTime"] = null };
            }
            else if (last.HasValue)
            {
                DateTime startAt = date + time;
                DateTime endAt = last.Value + time;
                if (wasTimed == true && wasRange == true && length.HasValue)
                {
                    TimeSpan endTime = startAt.AddMinutes(length.Value).TimeOfDay;
                    endAt = endTime == TimeSpan.Zero ? last.Value.AddDays(1) : last.Value + endTime; // ended at midnight: still does
                }
                if (endAt <= startAt) endAt = startAt.AddHours(1);
                body["start"] = new JObject { ["dateTime"] = LocalStamp(startAt), ["date"] = null };
                body["end"] = new JObject { ["dateTime"] = LocalStamp(endAt), ["date"] = null };
            }
            else
            {
                int minutes = wasRange == true ? 60
                    : wasTimed == true ? length ?? 60
                    : wasTimed == false ? 60
                    : length.HasValue && length.Value < 1440 * 60 && length.Value % 1440 != 0 ? length.Value : 60;
                var startLocal = new DateTimeOffset(date + time, TimeZoneInfo.Local.GetUtcOffset(date + time));
                body["start"] = new JObject { ["dateTime"] = startLocal.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), ["date"] = null };
                body["end"] = new JObject { ["dateTime"] = startLocal.AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), ["date"] = null };
            }
        }

        // A local clock time with its own UTC offset (each end of a range may fall on either side of a DST change).
        private static string LocalStamp(DateTime local) =>
            new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary>A Google event → the app's date / time / length / 완료 (null for events the app does not show).</summary>
        public static GoogleEvent ParseEvent(JObject e)
        {
            string id = Text(e["id"]);
            if (string.IsNullOrEmpty(id) || Text(e["status"]) == "cancelled") return null;
            var start = e["start"] as JObject;
            var end = e["end"] as JObject;
            if (start == null || (Text(start["date"]) == null && start["dateTime"] == null)) return null;
            var properties = (e["extendedProperties"] as JObject)?["private"] as JObject;
            string summary = Text(e["summary"]);
            var result = new GoogleEvent
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(summary) ? GoogleCalendarSync.Untitled : summary.Trim(),
                Completed = Text(properties?[CompletedKey]) == "1",
                AppTag = Text(properties?[AppKey]) == "1",
                LegacyApp = properties?[CompletedKey] != null,
                Recurring = e["recurringEventId"] != null || e["recurrence"] != null,
                Shared = IsShared(e)
            };
            if (Text(start["date"]) != null)
            {
                DateTime day = DateTime.ParseExact(Text(start["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                DateTime endDay = Text(end?["date"]) != null ? DateTime.ParseExact(Text(end["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture) : day.AddDays(1);
                result.Period = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                result.EndMinutes = (int)Math.Max(1440, Math.Min(int.MaxValue, (endDay - day).TotalMinutes));
                // 여러 날: over more than one day → the day before Google's (exclusive) end date is the last one.
                if ((endDay - day).TotalDays > 1)
                    result.EndPeriod = ScheduleItem.NormalizeEndPeriod(result.Period, endDay.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else
            {
                DateTimeOffset startAt = ParseDateTime(start["dateTime"]).ToLocalTime();
                DateTimeOffset endAt = end?["dateTime"] != null ? ParseDateTime(end["dateTime"]) : startAt.AddHours(1);
                result.Period = startAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                result.Time = startAt.ToString("HH:mm", CultureInfo.InvariantCulture);
                result.EndMinutes = (int)Math.Max(1, Math.Min(int.MaxValue, (endAt - startAt).TotalMinutes));
                // 여러 날: ends on a later day here (an end at exactly midnight belongs to the day before).
                DateTime endLocal = endAt.ToLocalTime().DateTime;
                DateTime lastDay = endLocal.TimeOfDay == TimeSpan.Zero ? endLocal.Date.AddDays(-1) : endLocal.Date;
                if (lastDay > startAt.Date)
                    result.EndPeriod = ScheduleItem.NormalizeEndPeriod(result.Period, lastDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            return result;
        }

        private static GoogleEvent TryParseEvent(JObject e)
        {
            try { return e == null ? null : ParseEvent(e); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException || ex is OverflowException) { return null; }
        }

        // Guests besides this calendar (meeting rooms aside), or organized by someone else.
        private static bool IsShared(JObject e)
        {
            if (e["attendees"] is JArray attendees && attendees.OfType<JObject>().Any(a => !IsTrue(a["self"]) && !IsTrue(a["resource"]))) return true;
            return e["organizer"] is JObject organizer && !IsTrue(organizer["self"]);
        }

        private static bool IsTrue(JToken token) => token?.Type == JTokenType.Boolean && (bool)token;

        private static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;

        // Newtonsoft may already have turned the string into a DateTime; either way keep the offset.
        private static DateTimeOffset ParseDateTime(JToken token) =>
            token.Type == JTokenType.Date ? (token.Value<object>() is DateTimeOffset o ? o : new DateTimeOffset(token.Value<DateTime>()))
                : DateTimeOffset.Parse((string)token, CultureInfo.InvariantCulture);

        // What one sync reads, in the background: everything from 30 days ago through 8 weeks ahead with repeating events
        // listed date by date, then the one-off events from there to two years ahead (repeating ones are left out there).
        // updatedSince: only what changed on Google since then (deleted ones come as "cancelled" and are skipped).
        private Task<List<GoogleEvent>> ReadEventsAsync(DateTime today, DateTime? updatedSince) => Task.Run(async () =>
        {
            DateTime split = GoogleCalendarSync.RecurringEnd(today).AddDays(1);
            List<GoogleEvent> events = await ListEventsAsync(GoogleCalendarSync.WindowStart(today), split, true, updatedSince).ConfigureAwait(false);
            var seen = new HashSet<string>(events.Select(e => e.Id));
            foreach (GoogleEvent e in await ListEventsAsync(split.AddDays(-1), GoogleCalendarSync.WindowEnd(today), false, updatedSince).ConfigureAwait(false))
                if (!e.Recurring && seen.Add(e.Id)) events.Add(e);
            return events;
        });

        private async Task<List<GoogleEvent>> ListEventsAsync(DateTime from, DateTime to, bool singleEvents, DateTime? updatedSince)
        {
            var result = new List<GoogleEvent>();
            string pageToken = null;
            var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                string url = EventsUrl + "?singleEvents=" + (singleEvents ? "true" : "false") + "&maxResults=2500&eventTypes=default" +
                    "&timeMin=" + Uri.EscapeDataString(Rfc3339(from)) + "&timeMax=" + Uri.EscapeDataString(Rfc3339(to)) +
                    (updatedSince.HasValue ? "&updatedMin=" + Uri.EscapeDataString(Rfc3339(updatedSince.Value)) : "") +
                    (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
                JObject page = await SendAsync(HttpMethod.Get, url, null, allowMissing: false).ConfigureAwait(false);
                foreach (JObject e in (page["items"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    try { var parsed = ParseEvent(e); if (parsed != null) result.Add(parsed); }
                    catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException || ex is OverflowException) { } // skip one odd event
                }
                pageToken = Text(page["nextPageToken"]);
                if (!string.IsNullOrEmpty(pageToken) && !seenPageTokens.Add(pageToken))
                    throw new InvalidOperationException("구글 일정 조회에서 같은 페이지 토큰이 반복되어 동기화를 중단했습니다. 다음 동기화에서 다시 시도합니다.");
            } while (!string.IsNullOrEmpty(pageToken));
            return result;
        }

        private static string Rfc3339(DateTime time) => new DateTimeOffset(time).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        // Account display must work with email permission alone. Calendar metadata needs a broader, unused scope.
        private async Task<string> ReadAccountAsync()
        {
            try
            {
                JObject account = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo", null, allowMissing: false);
                string email = Text(account["email"])?.Trim();
                return !string.IsNullOrEmpty(email) && email.IndexOf('@') > 0 ? email : null;
            }
            catch (InvalidOperationException) { return null; }
        }

        private async Task EnsureAccessTokenAsync(GoogleCalendarSettings settings)
        {
            if (accessToken != null && DateTime.UtcNow < accessTokenExpires) return;
            var app = LoadClient() ?? throw new InvalidOperationException("이 앱에는 구글 연동용 OAuth 클라이언트가 들어 있지 않습니다.");
            string refresh;
            try { refresh = SecretStore.Unprotect(settings.ProtectedRefreshToken); }
            catch (InvalidOperationException) { throw new GoogleSignInRequiredException(); } // saved on another PC or Windows account: sign in here
            if (string.IsNullOrWhiteSpace(refresh)) throw new GoogleSignInRequiredException();
            JObject token;
            try
            {
                token = await PostFormAsync("https://oauth2.googleapis.com/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = app.Item1, ["client_secret"] = app.Item2
                });
            }
            catch (GoogleAuthRejectedException) { throw new GoogleSignInRequiredException(); }
            UseAccessToken(token);
            string rotated = Text(token["refresh_token"]);
            if (!string.IsNullOrWhiteSpace(rotated)) settings.ProtectedRefreshToken = SecretStore.Protect(rotated);
        }

        private void UseAccessToken(JObject token)
        {
            accessToken = Text(token["access_token"]) ?? throw new InvalidOperationException("구글 인증 응답을 읽지 못했습니다.");
            long seconds = token["expires_in"]?.Type == JTokenType.Integer ? (long)token["expires_in"] : 3600;
            accessTokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, Math.Min(86400, seconds) - 120));
        }

        private async Task<JObject> PostFormAsync(string url, Dictionary<string, string> form)
        {
            try
            {
                using (var content = new FormUrlEncodedContent(form))
                using (var response = await client.PostAsync(url, content))
                {
                    string text = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
                        throw new GoogleAuthRejectedException();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("구글 인증 실패 (HTTP " + (int)response.StatusCode + ").");
                    return JObject.Parse(text);
                }
            }
            catch (HttpRequestException ex) { throw new GoogleConnectionException("구글에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.", ex); }
            catch (TaskCanceledException ex) { throw new GoogleConnectionException("구글 응답이 늦어 중단했습니다.", ex); }
            catch (JsonException ex) { throw new InvalidOperationException("구글 인증 응답을 읽지 못했습니다.", ex); }
        }

        // One Google API request. The answer is read and parsed off the UI thread; the caller's code resumes where it was.
        private async Task<JObject> SendAsync(HttpMethod method, string url, JObject body, bool allowMissing)
        {
            try
            {
                using (var request = new HttpRequestMessage(method, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request).ConfigureAwait(false))
                    {
                        if (allowMissing && (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)) return null;
                        if (!response.IsSuccessStatusCode) await ThrowForStatusAsync(response, request.RequestUri).ConfigureAwait(false);
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        // Keep dates as the strings Google sent (with their offsets); ParseEvent reads them itself.
                        return string.IsNullOrWhiteSpace(text) ? new JObject()
                            : JsonConvert.DeserializeObject<JObject>(text, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                    }
                }
            }
            catch (HttpRequestException ex) { throw new GoogleConnectionException("구글에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.", ex); }
            catch (TaskCanceledException ex) { throw new GoogleConnectionException("구글 응답이 늦어 중단했습니다.", ex); }
            catch (JsonException ex) { throw new InvalidOperationException("구글 응답을 읽지 못했습니다.", ex); }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                result[Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return result;
        }

        private static string RandomToken(int bytes)
        {
            var data = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(data);
            return Base64Url(data);
        }

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The saved Google sign-in no longer works (revoked, expired in testing mode, …): sign in again.</summary>
    public sealed class GoogleSignInRequiredException : InvalidOperationException
    {
        public GoogleSignInRequiredException() : base("구글 로그인이 만료되었습니다. 설정에서 다시 로그인해 주세요.") { }
    }

    /// <summary>
    /// The sign-in lacks the permission a request needs: Drive (캐릭터 보관 — signed in before it existed) or Calendar (left
    /// unticked on Google's sign-in page). Sign in again once, allowing it.
    /// </summary>
    public sealed class GoogleScopeException : InvalidOperationException
    {
        public GoogleScopeException() : this(drive: true) { }

        public GoogleScopeException(bool drive) : base(drive
            ? "캐릭터 보관은 구글 드라이브 권한이 필요합니다. 로그아웃 후 다시 로그인해 주세요."
            : "구글 캘린더 연동은 캘린더 권한이 필요합니다. 로그아웃 후 다시 로그인하고, 구글 로그인 화면에서 캘린더 권한을 허용해 주세요.")
        {
            Drive = drive;
        }

        /// <summary>true: the Drive permission (캐릭터 보관) is missing; false: the Calendar one.</summary>
        public bool Drive { get; }
    }

    internal sealed class GoogleAuthRejectedException : InvalidOperationException
    {
        public GoogleAuthRejectedException() : base("구글이 인증을 거부했습니다.") { }
    }

    /// <summary>Google answered one request with an error (other than sign-in, permission and rate-limit problems).</summary>
    public sealed class GoogleRequestException : InvalidOperationException
    {
        public GoogleRequestException(HttpStatusCode status, string reasons, string message) : base(message)
        {
            Status = status;
            Reasons = reasons ?? "";
        }

        public HttpStatusCode Status { get; }
        public string Reasons { get; }

        /// <summary>Google will not take this one request as it is (bad data, no right to change that event, …): skip it and go on.</summary>
        public bool IsRefusal => (int)Status >= 400 && (int)Status < 500 && Status != HttpStatusCode.RequestTimeout;
    }

    /// <summary>Google asks to slow down (429 / rate limit): the sync stops and waits (as long as Google says, when it does).</summary>
    public sealed class GoogleRateLimitException : InvalidOperationException
    {
        public GoogleRateLimitException(TimeSpan? retryAfter) : base("구글 요청 한도에 걸렸습니다. 잠시 쉬었다가 다시 동기화합니다.") { RetryAfter = retryAfter; }
        public TimeSpan? RetryAfter { get; }
    }

    /// <summary>Google could not be reached (offline, no answer in time): the whole sync waits for the next try.</summary>
    public sealed class GoogleConnectionException : InvalidOperationException
    {
        public GoogleConnectionException(string message, Exception inner) : base(message, inner) { }
    }
}
