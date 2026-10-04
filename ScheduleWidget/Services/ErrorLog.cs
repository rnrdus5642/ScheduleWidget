using System;
using System.Globalization;
using System.IO;

namespace ScheduleWidget
{
    /// <summary>
    /// Unexpected errors the app caught at the top (a timer tick, an async handler …) in %LocalAppData%\ScheduleWidget\error-log.txt:
    /// the time, where it was caught and the full exception, so a "the app closed / something stopped" report can be traced.
    /// Capped at 64 KB (the previous part is kept as error-log.old.txt). Never throws.
    /// </summary>
    public static class ErrorLog
    {
        private const long MaxBytes = 64 * 1024;
        private static readonly object Gate = new object();

        /// <summary>Folder for the log; null = %LocalAppData%\ScheduleWidget. Tests point it at their own folder.</summary>
        public static string Root { get; set; }

        public static string FilePath => Path.Combine(Root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleWidget"), "error-log.txt");

        public static void Write(string source, Exception error)
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
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + source + "\t" +
                        (error == null ? "(no exception)" : error.ToString()) + Environment.NewLine + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is NotSupportedException || ex is System.Security.SecurityException) { }
        }
    }
}
