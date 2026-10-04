using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void CharacterSettingScopes()
        {
            var data = new AppData { MiniCharacterVisible = true, MiniCharacterScale = 140, MiniCharacterSide = "Right", MiniCharacterGap = 30 };
            data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json", Scale = 190, Flipped = true, Animation = "walk" });
            data.MiniPetSpots.Add(new MiniPetSpot { Left = .2, Top = .3, Key = MiniWindow.SlotKeysOf(data)[0] });
            data.MiniPetSpots.Add(new MiniPetSpot { Left = .7, Top = .2, Key = MiniWindow.SlotKeysOf(data)[1] });
            int saves = 0;
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            var host = (IPetSettingsHost)mini;
            var personal = new PetSettingsWindow(host, 1);
            var global = new PetSettingsWindow(host, 0, embedded: true);
            try
            {
                Require(Control<FrameworkElement>(global, "PersonalAppearancePanel").Visibility == Visibility.Collapsed && Control<FrameworkElement>(global, "GlobalPlacementPanel").Visibility == Visibility.Visible,
                    "Global settings still expose selected-pet appearance.");
                Require(Control<FrameworkElement>(personal, "PetRosterSection").Visibility == Visibility.Collapsed && Control<FrameworkElement>(personal, "GlobalPlacementPanel").Visibility == Visibility.Collapsed,
                    "Personal settings still expose shared management.");
                string firstSpot = JsonConvert.SerializeObject(data.MiniPetSpots[0]);
                Call(personal, "Reset_Click", personal, ClickArgs());
                Call(personal, "Reset_Click", personal, ClickArgs());
                Require(host.PetScale(0) == 140 && JsonConvert.SerializeObject(data.MiniPetSpots[0]) == firstSpot && host.CharacterSide == "Right" && host.CharacterGap == 30,
                    "Resetting one pet changed another pet or shared defaults.");
                Require(host.PetScale(1) == 100 && !host.PetFlipped(1) && host.PetAnimation(1) == "idle", "Personal reset was incomplete.");
                host.SetPetScale(1, 170);
                host.SetPetFlipped(1, true);
                host.StartPetPlacement(1);
                var canDrag = typeof(MiniWindow).GetMethod("CanDragPet", BindingFlags.Instance | BindingFlags.NonPublic);
                Require((bool)canDrag.Invoke(mini, new object[] { 1 }) && !(bool)canDrag.Invoke(mini, new object[] { 0 }), "Personal move permits dragging other pets.");
                Call(mini, "PlacementReset_Click", mini, ClickArgs());
                Require(JsonConvert.SerializeObject(data.MiniPetSpots[0]) == firstSpot, "Placement reset moved another pet.");
                mini.EndPetPlacement();
                Call(global, "Reset_Click", global, ClickArgs());
                Call(global, "Reset_Click", global, ClickArgs());
                Require(host.CharacterSide == "Left" && host.CharacterGap == 8 && data.MiniPetSpots.Count == 0 && host.PetScale(1) == 170 && host.PetFlipped(1),
                    "Global placement reset overwrote personal appearance.");
                Call(mini, "FlushSave");
                var store = Store("pet-scopes"); store.SaveData(data); var restored = store.LoadData().Data;
                Require(restored.MiniExtraCharacters[0].Scale == 170 && restored.MiniExtraCharacters[0].Flipped && saves > 0, "Scoped changes did not persist.");
            }
            finally { personal.Close(); global.Close(); mini.Close(); }
        }

        private static void ProviderSettingScopes()
        {
            var data = new AppData();
            var store = Store("provider-scopes");
            int saves = 0;
            var contact = new ContactWindow(data, new CommunicationService(), () => { saves++; store.SaveData(data); return true; }, embedded: true);
            var google = new Border();
            contact.CreateEmbeddedViews(google);
            try
            {
                var tabs = Control<TabControl>(contact, "ConnectionTabs");
                Require(tabs.Items.Count == 4 && contact.SelectedConnection == ConnectionProvider.Google && google.Parent != null, "Provider navigation did not include Google.");
                Require(Control<Border>(contact, "MessageSection").Parent == null, "Google shows a messaging composer.");
                contact.SelectConnection(ConnectionProvider.Telegram);
                Control<PasswordBox>(contact, "TelegramToken").Password = "123:dummy_telegram";
                Control<TextBox>(contact, "TelegramChat").Text = "draft-chat";
                contact.SetMessage("Telegram draft");
                Require(Control<ComboBox>(contact, "SendChannel").SelectedIndex == 0, "Telegram targets a different service.");
                contact.SelectConnection(ConnectionProvider.Kakao);
                Require(Control<TextBox>(contact, "MessageInput").Text == "", "Telegram draft leaked into Kakao.");
                Control<PasswordBox>(contact, "KakaoToken").Password = "dummy-kakao";
                Control<TextBox>(contact, "KakaoLink").Text = "https://example.com";
                Control<TextBox>(contact, "MessageInput").Text = "Kakao draft";
                Control<ComboBox>(contact, "SendChannel").SelectedIndex = 2;
                contact.SelectConnection(ConnectionProvider.Phone);
                Control<TextBox>(contact, "PhoneNumber").Text = "01000000000";
                Control<TextBox>(contact, "MessageInput").Text = "Phone draft";
                Require(Control<ComboBox>(contact, "SendChannel").SelectedIndex == 3, "Phone targets a network message sender.");
                contact.SelectConnection(ConnectionProvider.Google);
                contact.SetMessage("Schedule draft");
                Require(contact.SelectedConnection == ConnectionProvider.Telegram && Control<TextBox>(contact, "MessageInput").Text == "Schedule draft", "Schedule sharing stayed hidden in Google settings.");
                contact.SelectConnection(ConnectionProvider.Kakao);
                Require(Control<TextBox>(contact, "MessageInput").Text == "Kakao draft" && Control<ComboBox>(contact, "SendChannel").SelectedIndex == 2, "Switching tabs lost the Kakao draft or recipient.");
                contact.SelectConnection(ConnectionProvider.Telegram);
                Require(Control<PasswordBox>(contact, "TelegramToken").Password == "123:dummy_telegram" && Control<TextBox>(contact, "TelegramChat").Text == "draft-chat", "Switching tabs lost credentials.");
                Require(saves == 0 && data.Communication.TelegramChatId == null, "Changing provider silently saved its draft.");
                Require(contact.SaveSettings() && saves == 1, "Separated pages did not save together once.");
                var restored = store.LoadData().Data.Communication;
                Require(restored.TelegramChatId == "draft-chat" && restored.PhoneNumber == "01000000000" && SecretStore.Unprotect(restored.ProtectedKakaoToken) == "dummy-kakao", "Provider settings did not survive reload.");
            }
            finally { contact.Close(); }
        }
    }
}
