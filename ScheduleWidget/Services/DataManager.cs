using System;
using System.IO;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    public class DataManager
    {
        private readonly string jsonPath;
        private const string AppName = "ScheduleWidget";

        public DataManager()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            jsonPath = Path.Combine(exeDir, "schedules.json");
        }

        public AppData LoadData()
        {
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                return JsonConvert.DeserializeObject<AppData>(json) ?? new AppData();
            }
            return new AppData();
        }

        public void SaveData(AppData data)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(jsonPath, json);
        }

        public void EnableStartup()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                key?.SetValue(AppName, exePath);
            }
        }
    }
}