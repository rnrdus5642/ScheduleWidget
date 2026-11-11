using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace ScheduleWidget
{
    public partial class CharacterHelpWindow : Window
    {
        internal const string GalleryUrl = "https://codexpets.org/gallery";
        internal const string ExampleUrl = "https://codexpets.org/pets/tater";
        internal const string BasicManifest = "{\n  \"displayName\": \"내 캐릭터\",\n  \"spritesheetPath\": \"spritesheet.png\",\n  \"spriteVersionNumber\": 1\n}";
        private static readonly (string Label, int Frames)[] BasicRows =
        {
            ("대기", 6), ("오른쪽 걷기", 8), ("왼쪽 걷기", 8), ("인사", 4), ("점프", 5),
            ("당황", 8), ("기다리기", 6), ("활동", 6), ("집중", 6)
        };
        private readonly AuxiliaryWindowPlacement placement;
        private readonly AuxiliaryWindowDismissal dismissal;

        public CharacterHelpWindow()
        {
            InitializeComponent();
            ChromelessWindow.Apply(this, addCloseButton: false);
            placement = new AuxiliaryWindowPlacement(this);
            dismissal = new AuxiliaryWindowDismissal(this, Close);
            ManifestExample.Text = BasicManifest;
            AnimationRows.ItemsSource = BasicRows.Select((row, i) => new { Number = i + 1, row.Label, Frames = row.Frames + "장" }).ToList();
            OpenGalleryButton.ToolTip = GalleryUrl;
            OpenExampleButton.ToolTip = ExampleUrl;
        }

        internal static string CreationPrompt =>
            "ScheduleWidget에서 사용할 움직이는 캐릭터를 만들어 주세요. 캐릭터의 모습과 스타일은 제가 지정하겠습니다.\n" +
            "투명 배경 PNG 스프라이트시트: 1536×1872 px, 가로 8칸 × 세로 9줄, 한 칸 192×208 px.\n" +
            "모든 프레임의 캐릭터 크기와 기준 위치를 맞추고, 각 동작은 왼쪽부터 자연스럽게 이어지는 연속 그림으로 그려 주세요. 남는 칸은 투명하게 비워 주세요.\n" +
            string.Join("\n", BasicRows.Select((row, i) => (i + 1) + "번째 줄: " + row.Label + " " + row.Frames + "장")) +
            "\n이미지는 spritesheet.png로 저장하고, 아래 pet.json과 같은 폴더에 넣어 주세요. 이미지 크기와 파일 경로가 맞는지도 확인해 주세요.\n" + BasicManifest;

        private void OpenGallery_Click(object sender, RoutedEventArgs e) => OpenWebsite(GalleryUrl);
        private void OpenExample_Click(object sender, RoutedEventArgs e) => OpenWebsite(ExampleUrl);

        private void OpenWebsite(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            { GuideStatus.Text = "브라우저를 열지 못했습니다. 주소: " + url; }
        }

        private void CopyManifest_Click(object sender, RoutedEventArgs e) => CopyText(BasicManifest, "JSON을 복사했습니다.");
        private void CopyPrompt_Click(object sender, RoutedEventArgs e) => CopyText(CreationPrompt, "제작 요청문을 복사했습니다. 원하는 캐릭터 설명을 덧붙여 사용하세요.");

        private void CopyText(string text, string message)
        {
            try { Clipboard.SetText(text); GuideStatus.Text = message; }
            catch (ExternalException) { GuideStatus.Text = "복사하지 못했습니다. 잠시 후 다시 눌러 주세요."; }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Help_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }
    }
}
