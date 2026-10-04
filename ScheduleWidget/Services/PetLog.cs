using System;
using System.Globalization;
using System.IO;

namespace ScheduleWidget
{
    /// <summary>
    /// One line per pet-view recovery (WebView process gone, capture nudged after a display / power / session change) in
    /// %LocalAppData%\ScheduleWidget\pet-log.txt, so a "the pets vanished" report can be matched to what happened.
    /// Capped at 64 KB (the previous part is kept as pet-log.old.txt). Never throws.
    /// </summary>
    public static class PetLog
    {
        private const long MaxBytes = 64 * 1024;
        private static readonly object Gate = new object();

        /// <summary>Folder for the log; null = %LocalAppData%\ScheduleWidget. Tests point it at their own folder.</summary>
        public static string Root { get; set; }

        public static string FilePath => Path.Combine(Root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleWidget"), "pet-log.txt");

        public static void Write(string kind, string detail = null)
        {
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        string old = Path.ChangeExtension(path, ".old.txt");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + kind +
                        (string.IsNullOrEmpty(detail) ? "" : "\t" + detail.Replace('\r', ' ').Replace('\n', ' ')) + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { }
        }
    }
}
