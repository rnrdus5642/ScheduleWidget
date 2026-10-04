using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public partial class CharacterWindow : Window
    {
        private List<CharacterEntry> characters;
        private int selected = -1;
        private bool closed, ready;
        private string currentManifest;
        private string preferred;
        private string galleryPayload;
        private string galleryUri;
        private CharacterEntry pendingDelete;
        private bool deferredCodexReload;
        private readonly List<string> otherSlots; // the characters the other pets use (not the one being chosen here)
        public string Result { get; private set; }
        /// <summary>True when the user chose 드래그로 위치 설정 (the mini window then lets the pets be dragged).</summary>
        public bool PlacementRequested { get; private set; }
        /// <summary>True when a character was deleted here (even if the window was then cancelled): the pets reload, since
        /// one of them may have been that character.</summary>
        public bool DeletedAny { get; private set; }

        /// <param name="otherSlots">The characters the other pets show, so deleting one of them can say so.</param>
        public CharacterWindow(string current, bool offerPlacement = false, IEnumerable<string> otherSlots = null)
        {
            CharacterCatalog.MigrateLegacyPets(); // an old app-folder Pet dropped in while the app runs is picked up here too
            // Codex installed, updated or removed since: its characters are brought in step (in the background; the list
            // is read again when that changed anything).
            CodexPets.Changed += OnCodexPetsChanged;
            CodexPets.RefreshInBackground();
            currentManifest = preferred = CharacterCatalog.ResolveSelection(current);
            this.otherSlots = (otherSlots ?? Enumerable.Empty<string>()).Select(CharacterCatalog.ResolveSelection).ToList();
            InitializeComponent();
            ChromelessWindow.Apply(this, rounded: true); // no title bar or taskbar button; X in the top-right corner; rounded corners
            PlacePets.Visibility = offerPlacement ? Visibility.Visible : Visibility.Collapsed;
            Loaded += async (s, e) =>
            {
                try
                {
                    await EmbeddedBrowser.InitializeAsync(Gallery);
                    if (closed) return;
                    Gallery.CoreWebView2.WebMessageReceived += (sender, args) =>
                    {
                        // A page message must never take the app down: odd values are ignored, a view that just went away too.
                        try { OnGalleryMessage(args.Source, args.WebMessageAsJson); }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
                        { }
                    };
                    Gallery.CoreWebView2.NewWindowRequested += (sender, args) => args.Handled = true;
                    Reload();
                }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException || ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
                { if (!closed) CharacterStatus.Text = "미리보기를 열 수 없습니다. Edge WebView2 Runtime 설치를 확인해 주세요. " + ex.Message; }
            };
            // The gallery's browser goes with the window, here on the window's own thread — never later from the finalizer.
            Closed += (s, e) =>
            {
                closed = true;
                CodexPets.Changed -= OnCodexPetsChanged;
                try { Gallery.Dispose(); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { }
                GC.SuppressFinalize(Gallery);
            };
        }

        private void OnGalleryMessage(string source, string json)
        {
            if (closed || source == null || !source.StartsWith(EmbeddedBrowser.Origin, StringComparison.Ordinal) ||
                !string.Equals(source, galleryUri, StringComparison.OrdinalIgnoreCase)) return;
            JObject message;
            try { message = JObject.Parse(json); } catch (JsonException) { return; }
            string type = PageMessage.Text(message, "type");
            if (type == "ready")
            {
                ready = true;
                var core = Gallery.CoreWebView2;
                if (core != null && galleryPayload != null) core.PostWebMessageAsJson(galleryPayload);
            }
            else if (type == "selected")
            {
                int index = PageMessage.Index(message, characters?.Count ?? 0);
                if (index < 0) return;
                selected = index;
                ApplyCharacter.IsEnabled = true;
                DeleteCharacter.IsEnabled = CharacterCatalog.CanDelete(characters[index]);
                ExportCharacter.IsEnabled = CharacterCatalog.CanExport(characters[index]); // not the Codex characters (Codex's own)
                CharacterStatus.Text = characters[index].Name + " · 선택됨";
            }
            // Esc pressed inside the gallery (the browser keeps its keys from the window): close like 취소. While the delete
            // question shows, the gallery is hidden and Esc reaches CharacterWindow_KeyDown instead.
            else if (type == "escape" && pendingDelete == null) Close();
        }

        // Old characters an earlier version kept next to the app that could not be brought over: said once (they stay there).
        private static int migrationFailuresShown;
        private bool migrationNoticeChecked;

        internal static string TakeMigrationNotice()
        {
            int failures = CharacterCatalog.LastMigrationFailures;
            if (failures == 0) { migrationFailuresShown = 0; return null; }
            if (failures == migrationFailuresShown) return null;
            migrationFailuresShown = failures;
            return CharacterCatalog.MigrationWarning;
        }

        private void Reload()
        {
            characters = CharacterCatalog.Load(out string warning);
            selected = -1; ApplyCharacter.IsEnabled = false; DeleteCharacter.IsEnabled = false; ExportCharacter.IsEnabled = false;
            string status = string.IsNullOrEmpty(warning) ? "Codex pet.json + 스프라이트시트, 내보낸 캐릭터(.zip) 또는 PNG · WebP · GIF · JPG를 가져올 수 있습니다." : warning;
            if (!characters.Any(c => c.Group == CharacterCatalog.CodexGroup)) status = CharacterCatalog.NoCodexNote + (string.IsNullOrEmpty(warning) ? "" : " " + warning);
            if (!migrationNoticeChecked)
            {
                migrationNoticeChecked = true;
                string notice = TakeMigrationNotice();
                if (notice != null) status = string.IsNullOrEmpty(warning) ? notice : notice + " " + warning;
            }
            CharacterStatus.Text = status;
            int index = characters.FindIndex(c => SameManifest(c.ManifestPath, preferred));
            try
            {
                var core = Gallery.CoreWebView2;
                if (core == null) return; // the preview is still starting: it loads this list once it is ready
                galleryPayload = JsonConvert.SerializeObject(new
                {
                    action = "load", selected = index >= 0 ? index : 0,
                    characters = characters.Select((c, i) => CharacterCatalog.BrowserEntry(core, c, i)).ToArray()
                });
                // WebView2 applies newly mapped image folders on the next navigation.
                ready = false;
                galleryUri = EmbeddedBrowser.Origin + "character.html?catalog=" + Guid.NewGuid().ToString("N");
                core.Navigate(galleryUri);
            }
            // The gallery's browser process is gone (after an import or a delete): say so instead of crashing the app.
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { CharacterStatus.Text = "미리보기를 다시 불러오지 못했습니다. 이 창을 닫았다가 다시 열어 주세요."; }
        }
        // The Codex characters changed (Codex installed, updated or removed): the list is read again, unless a delete
        // question is showing (then it is read when that is answered).
        private void OnCodexPetsChanged() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed) return;
            if (pendingDelete != null)
            {
                deferredCodexReload = true;
                return;
            }
            try
            {
                if (Gallery.CoreWebView2 == null) return; // initial Loaded path reloads the latest catalog
                Reload();
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException) { }
        }));

        private void PlacePets_Click(object sender, RoutedEventArgs e)
        {
            PlacementRequested = true;
            DialogResult = false; // no character change; the caller starts the drag placement
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!ready) { CharacterStatus.Text = "미리보기가 준비된 후 가져올 수 있습니다."; return; }
            var dialog = new OpenFileDialog { Title = "캐릭터 가져오기", Filter = "캐릭터 또는 이미지|pet.json;*.zip;*.png;*.webp;*.gif;*.jpg;*.jpeg;*.bmp|내보낸 캐릭터 (*.zip)|*.zip|Codex 캐릭터|pet.json" };
            if (dialog.ShowDialog(this) != true) return;
            try { preferred = CharacterCatalog.Import(dialog.FileName); Reload(); }
            catch (DuplicateCharacterException ex)
            {
                // Already here (a default character's very sheet): nothing added, that character is selected instead.
                preferred = ex.ExistingManifest;
                Reload();
                CharacterStatus.Text = ex.Message;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { CharacterStatus.Text = "가져오기 실패: " + ex.Message; }
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (selected < 0) return;
            var entry = characters[selected];
            string fileName = string.Concat((entry.Name ?? "캐릭터").Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
            var dialog = new SaveFileDialog
            {
                Title = "캐릭터 내보내기", Filter = "캐릭터 파일 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
                FileName = (fileName.Length == 0 ? "캐릭터" : fileName) + ".zip"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                CharacterCatalog.Export(entry, dialog.FileName);
                CharacterStatus.Text = entry.Name + " · 내보냈습니다: " + dialog.FileName + " (다른 PC에서 '캐릭터 가져오기'로 이 파일을 고르면 됩니다)";
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { CharacterStatus.Text = "내보내기 실패: " + ex.Message; }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (selected < 0) return;
            try
            {
                var entry = characters[selected];
                Result = CharacterCatalog.SelectionManifest(entry);
                DialogResult = true;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { CharacterStatus.Text = ex.Message; }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (selected < 0 || !CharacterCatalog.CanDelete(characters[selected])) return;
            pendingDelete = characters[selected];
            DeleteName.Text = pendingDelete.Name;
            string manifest = pendingDelete.ManifestPath;
            // The pets showing it get 흰 모찌 (기본 캐릭터); without the default characters' files they show no character.
            string fallback = CharacterCatalog.IsUnavailable(CharacterCatalog.DefaultManifest) ? "캐릭터 없이 남습니다." : "기본 캐릭터(흰 모찌)로 바뀝니다.";
            DeleteDescription.Text = SameManifest(manifest, currentManifest)
                ? "이 캐릭터와 가져온 이미지를 삭제합니다. 사용 중인 캐릭터는 " + fallback
                : otherSlots.Any(other => SameManifest(manifest, other))
                    ? "이 캐릭터와 가져온 이미지를 삭제합니다. 함께 띄운 다른 캐릭터로 쓰고 있어, 그 캐릭터도 " + fallback
                    : "이 캐릭터와 가져온 이미지를 삭제합니다.";
            DeleteError.Text = string.Empty;
            Gallery.Visibility = CharacterStatus.Visibility = CharacterActions.Visibility = Visibility.Hidden;
            DeleteConfirmation.Visibility = Visibility.Visible;
            CancelDelete.Focus();
        }

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            CharacterEntry entry = pendingDelete;
            if (entry == null) return;
            try
            {
                bool deletingCurrent = SameManifest(entry.ManifestPath, currentManifest);
                CharacterCatalog.Delete(entry);
                DeletedAny = true;
                if (deletingCurrent)
                {
                    Result = CharacterCatalog.DefaultManifest;
                    DialogResult = true;
                    return;
                }
                DismissDelete(reloadDeferred: false);
                preferred = currentManifest;
                Reload();
                CharacterStatus.Text = "캐릭터를 삭제했습니다.";
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { DeleteError.Text = "삭제 실패: " + ex.Message; }
        }

        private void CancelDelete_Click(object sender, RoutedEventArgs e) => DismissDelete();

        private void DismissDelete(bool reloadDeferred = true)
        {
            pendingDelete = null;
            DeleteConfirmation.Visibility = Visibility.Collapsed;
            Gallery.Visibility = CharacterStatus.Visibility = CharacterActions.Visibility = Visibility.Visible;
            bool shouldReload = deferredCodexReload;
            deferredCodexReload = false;
            if (reloadDeferred && shouldReload && !closed)
            {
                try { if (Gallery.CoreWebView2 != null) Reload(); }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
                { CharacterStatus.Text = "캐릭터 목록을 다시 불러오지 못했습니다. 이 창을 닫았다가 다시 열어 주세요."; }
            }
            DeleteCharacter.Focus();
        }

        // Esc: cancels the delete question when it is showing, else closes the window (like 취소 — nothing changes).
        private void CharacterWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (pendingDelete != null) DismissDelete();
            else Close();
        }

        private static bool SameManifest(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            string firstPath = CharacterCatalog.FullManifestPath(first);
            string secondPath = CharacterCatalog.FullManifestPath(second);
            return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Typed reads of a page's web message: a value of the wrong kind or out of range reads as "none", never throws.</summary>
    internal static class PageMessage
    {
        public static string Text(JObject message, string name) =>
            message?[name] is JValue value && value.Type == JTokenType.String ? (string)value : null;

        /// <summary>The message's "index" when it is a whole number from 0 to count - 1, else -1.</summary>
        public static int Index(JObject message, int count) =>
            message?["index"] is JValue value && value.Type == JTokenType.Integer && value.Value is long index && index >= 0 && index < count ? (int)index : -1;
    }
}
