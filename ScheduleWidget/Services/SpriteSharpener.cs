using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace ScheduleWidget
{
    /// <summary>
    /// Pets drawn larger than their sheet (1536 × 208·rows, 192 × 208 a frame) look soft: the page scales the sheet with the
    /// browser's bilinear filter. Each sheet is made again here once at twice the size — Lanczos3, then a mild unsharp mask —
    /// and kept in PetCache next to the Pet folder (%LocalAppData%\ScheduleWidget\PetCache; tests move it with
    /// CharacterCatalog.PetRoot), named by the sheet's path, size, time and <see cref="AlgorithmVersion"/>: one PNG per row of
    /// frames (sheet-KEY-00.png …, 3072 × 416 each) and a note naming the source (sheet-KEY.txt). Rows, because the page then
    /// holds only the row a pet is playing: a whole 2× sheet is 66 MB decoded, and the browser kept one per pet and per view
    /// (measured: +530 MB in its GPU process for 3 + 3 pets). The work runs on one background thread, one sheet at a time, a
    /// row at a time (about 50 MB). Until a sheet is ready the page shows the original; then <see cref="SheetReady"/> tells the
    /// pet windows, which hand the page the new address.
    /// Every step stays inside one frame (taps are clamped to its cell), so nothing bleeds between frames; the work is done on
    /// premultiplied pixels and the colors are kept within the alpha, so no halo reaches into the transparent parts, and the
    /// alpha is never sharpened (Lanczos ringing in it is clamped to its two nearest source pixels: no faint rims around the
    /// pet). A failure (no WebP codec, a busy file, no disk space) only writes a line to pet-log.txt: the original stays.
    /// </summary>
    public static class SpriteSharpener
    {
        /// <summary>Goes up whenever the result changes: every sheet is then made again (old copies are deleted by <see cref="Prune"/>).</summary>
        public const int AlgorithmVersion = 2;
        public const string Host = "petcache.example";
        // Unsharp mask on the 2× sheet, in its pixels. The page scales that sheet by another ~1.45× (pets at ~2.9×) with its
        // bilinear filter, which softens again: tuned so what shows on screen measures like Lanczos3 straight to the screen
        // size + unsharp σ 1.2 / amount 0.6, the look chosen from the comparison (Edge render of the pet at 556 × 603: mean edge
        // gradient 32.3 vs 32.5 for that, 22.7 for the original sheet; overshoot 2.7 vs 2.7).
        public const double UnsharpSigma = 1.0;
        public const double UnsharpAmount = 0.8;
        private const int CellWidth = 192, CellHeight = 208, SheetWidth = 1536;
        private const long MaxOutputPixels = 25000000; // the page's limit for a sheet (all rows together)

        /// <summary>Off: pages get no sharper sheets and nothing is made (tests).</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>Folder for the sheets; null = PetCache beside the Pet folder in use (so tests that move PetRoot move it too).</summary>
        public static string CacheRoot { get; set; }
        public static string CacheDirectory => CacheRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CharacterCatalog.PetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "PetCache");

        /// <summary>A sheet is ready (source image path, page address: see <see cref="Address"/>). Raised on the background thread.</summary>
        public static event Action<string, string> SheetReady;

        /// <summary>How many sheets were made by this process (tests: a second request is a cache hit).</summary>
        public static int SheetsMade => sheetsMade;
        private static int sheetsMade;

        /// <summary>How a sheet came out: made; not a sheet to sharpen (never tried again); or a passing failure (a busy file, a full disk).</summary>
        public enum Outcome { Made, Unsuitable, Passing }

        private static readonly object Gate = new object();
        private const string PruneJob = "";
        private static readonly List<string> Pending = new List<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // not tried again this run
        private static readonly Dictionary<string, DateTime> RetryAfter = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Forgotten = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // deleted pets
        private static TimeSpan RetryDelay = TimeSpan.FromMinutes(1); // tests shorten it
        private static Thread worker;
        // The job being made now: asking for it again meanwhile only notes that (RunningAskedAgain) — a second run would
        // announce the same sheet twice — unless the sheet changed while it was made, then it is made again afterwards.
        private static string running;
        private static bool runningAskedAgain;
        // A passing failure is tried again by itself after RetryDelay (no page reload needed), up to MaxAutoRetries times a path.
        private const int MaxAutoRetries = 5;
        private static readonly Dictionary<string, int> AutoRetries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Timer> RetryTimers = new HashSet<Timer>(); // keeps the waiting timers alive

        /// <summary>
        /// The page's address for a made sheet (<paramref name="cacheKey"/> from <see cref="CacheKey"/>): the start of its rows'
        /// addresses — row r is that + r as two digits + ".png".
        /// </summary>
        public static string Address(string cacheKey) => "https://" + Host + "/" + Uri.EscapeDataString(Path.GetFileName(cacheKey)) + "-";

        /// <summary>
        /// Where the sharper copy of this sheet is (or would be) kept: its path without the row and extension (…\sheet-KEY);
        /// null when the file does not exist.
        /// </summary>
        public static string CacheKey(string imagePath)
        {
            var file = new FileInfo(Path.GetFullPath(imagePath));
            if (!file.Exists) return null;
            string version = file.FullName.ToLowerInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|" + AlgorithmVersion + "|" +
                UnsharpSigma.ToString("R", CultureInfo.InvariantCulture) + "|" + UnsharpAmount.ToString("R", CultureInfo.InvariantCulture);
            string key;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(version)), 0, 12).Replace("-", "").ToLowerInvariant();
            return Path.Combine(CacheDirectory, "sheet-" + key);
        }

        /// <summary>Row <paramref name="row"/>'s file of a made sheet.</summary>
        public static string RowFile(string cacheKey, int row) => cacheKey + "-" + row.ToString("00", CultureInfo.InvariantCulture) + ".png";

        // Made: the rows go in place in row order, so the last one there marks the copy as made (the row count comes from the
        // sheet's header).
        private static bool IsMade(string cacheKey, string imagePath)
        {
            if (!CharacterCatalog.TryImageSize(imagePath, out int width, out int height) || width != SheetWidth || height <= 0 || height % CellHeight != 0) return false;
            return File.Exists(RowFile(cacheKey, height / CellHeight - 1));
        }

        /// <summary>
        /// For a pet window's page about to load (before Navigate: WebView2 applies a folder mapping on the next navigation):
        /// maps the cache folder and gives the sharper sheet's address when it is ready, else "" — and has it made in the
        /// background (<see cref="SheetReady"/> follows). Throws only what the view's own mapping throws.
        /// </summary>
        internal static string PageAddress(CoreWebView2 browser, string imagePath)
        {
            if (!Enabled || string.IsNullOrEmpty(imagePath)) return "";
            string folder, key;
            try
            {
                folder = CacheDirectory;
                Directory.CreateDirectory(folder);
                key = CacheKey(imagePath);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return ""; }
            browser.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            if (key == null) return "";
            if (IsMade(key, imagePath)) return Address(key);
            // A sheet already larger (or of another size) is left as it is; one whose header cannot be read is tried.
            bool other = CharacterCatalog.TryImageSize(imagePath, out int width, out int height) && (width != SheetWidth || height % CellHeight != 0);
            if (!other) Request(imagePath);
            return "";
        }

        /// <summary>Has the sheet made on the background thread (once; asking again while it waits does nothing).</summary>
        public static void Request(string imagePath)
        {
            string path;
            try { path = Path.GetFullPath(imagePath); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return; }
            Enqueue(path);
        }

        /// <summary>At app start: deletes the sheets of pets deleted or changed since (<see cref="Prune"/>), on the background thread.</summary>
        public static void PruneInBackground() => Enqueue(PruneJob);

        private static void Enqueue(string job)
        {
            lock (Gate)
            {
                if (job != PruneJob) Forgotten.Remove(job); // the same path imported again
                if (Pending.Contains(job, StringComparer.OrdinalIgnoreCase)) return;
                if (job != PruneJob && string.Equals(job, running, StringComparison.OrdinalIgnoreCase)) { runningAskedAgain = true; return; }
                Pending.Add(job);
                if (worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Pet sheet sharpener" };
                    worker.Start();
                }
                else Monitor.Pulse(Gate);
            }
        }

        /// <summary>
        /// A pet is being deleted: its sheet is not made (a request still waiting is dropped, one being made is not kept) and its
        /// sharper copies go now.
        /// </summary>
        public static void Forget(string imagePath)
        {
            string path;
            try { path = Path.GetFullPath(imagePath); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return; }
            lock (Gate)
            {
                Pending.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                Forgotten.Add(path);
                // Under the lock: a copy being finished now is either already in place (and deleted here) or not kept.
                try
                {
                    string folder = CacheDirectory;
                    if (!Directory.Exists(folder)) return;
                    foreach (string note in Directory.GetFiles(folder, "sheet-*.txt"))
                    {
                        try
                        {
                            if (string.Equals(File.ReadAllText(note).Trim(), path, StringComparison.OrdinalIgnoreCase))
                                DeleteCopy(Path.ChangeExtension(note, null));
                        }
                        catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { } // left to Prune
                    }
                }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            }
        }

        // Every file of one copy: its rows and note (a failure is left to Prune).
        private static void DeleteCopy(string cacheKey)
        {
            string prefix = Path.GetFileName(cacheKey);
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(cacheKey), prefix + "*"))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) TryDelete(file);
            }
        }

        private static void Work()
        {
            while (true)
            {
                string job;
                lock (Gate)
                {
                    while (Pending.Count == 0) Monitor.Wait(Gate);
                    job = Pending[0];
                    Pending.RemoveAt(0);
                    running = job == PruneJob ? null : job;
                    runningAskedAgain = false;
                }
                // Nothing here may end the app: this thread's exceptions would.
                try
                {
                    if (job == PruneJob) { Prune(); continue; }
                    string key = EnsureCached(job, out Outcome? outcome);
                    if (key != null) SheetReady?.Invoke(job, Address(key));
                    else if (outcome == Outcome.Passing) RetryLater(job);
                }
                catch (Exception ex) { ErrorLog.Write("pet-sharpen", ex); }
                finally { FinishRunning(job); }
            }
        }

        // Asked for again while it was being made: once more only when the sheet changed meanwhile (a new key).
        private static void FinishRunning(string job)
        {
            bool again;
            lock (Gate) { again = runningAskedAgain && job != PruneJob; running = null; runningAskedAgain = false; }
            if (!again) return;
            try
            {
                string key = CacheKey(job);
                if (key != null && !IsMade(key, job)) Enqueue(job);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
        }

        // A passing failure (busy file, full disk): asked for again once RetryDelay has passed, unless the pet was deleted.
        private static void RetryLater(string path)
        {
            Timer timer = null;
            lock (Gate)
            {
                AutoRetries.TryGetValue(path, out int tries);
                if (tries >= MaxAutoRetries) return;
                AutoRetries[path] = tries + 1;
                timer = new Timer(_ =>
                {
                    bool forgotten;
                    lock (Gate) { RetryTimers.Remove(timer); forgotten = Forgotten.Contains(path); }
                    timer.Dispose();
                    if (!forgotten && Enabled) Enqueue(path);
                });
                RetryTimers.Add(timer);
                // A little after RetryAfter, so the retry is not turned away as "too soon".
                timer.Change(RetryDelay + TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// The sharper sheet's <see cref="CacheKey"/>, made now when it is not there yet; null when it cannot be made — a sheet
        /// that is not suitable is not tried again this run, a passing failure (busy file, full disk) not before a minute has passed.
        /// </summary>
        public static string EnsureCached(string imagePath) => EnsureCached(imagePath, out _);

        // outcome: what a try made of it now; null when nothing was tried (already made, or not to be tried yet / again).
        private static string EnsureCached(string imagePath, out Outcome? outcome)
        {
            outcome = null;
            string key = CacheKey(imagePath);
            if (key == null) return null;
            if (IsMade(key, imagePath)) return key;
            lock (Gate)
            {
                if (Failed.Contains(key)) return null;
                if (RetryAfter.TryGetValue(key, out DateTime after) && DateTime.UtcNow < after) return null;
            }
            var made = Sharpen(imagePath, key);
            outcome = made;
            lock (Gate)
            {
                RetryAfter.Remove(key);
                if (made == Outcome.Unsuitable) Failed.Add(key);
                else if (made == Outcome.Passing) RetryAfter[key] = DateTime.UtcNow + RetryDelay;
                else AutoRetries.Remove(Path.GetFullPath(imagePath));
            }
            return made == Outcome.Made ? key : null;
        }

        /// <summary>
        /// Makes the 2× sheet of <paramref name="source"/> as rows at <paramref name="cacheKey"/> (written to temporary files,
        /// then renamed: a page never sees half a file). Unsuitable, with a line in pet-log.txt, when it is not a sprite sheet
        /// (1536 wide, whole 208-pixel rows, 2× at most 25 million pixels) or its colors would change (CMYK, more than 8 bits a
        /// channel, a color profile other than sRGB — the page shows those color-managed); Passing when it cannot be read or written.
        /// </summary>
        public static Outcome Sharpen(string source, string cacheKey)
        {
            var watch = Stopwatch.StartNew();
            string stamp = "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            var temps = new List<string>();
            try
            {
                BitmapFrame sheet;
                using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    sheet = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                int width = sheet.PixelWidth, height = sheet.PixelHeight;
                string unsuitable = width != SheetWidth || height <= 0 || height % CellHeight != 0 || (long)width * height * 4 > MaxOutputPixels ? "size" :
                    sheet.Format == PixelFormats.Cmyk32 || sheet.Format.BitsPerPixel > 32 ? "format " + sheet.Format :
                    ColorProfile(sheet) ? "color profile" : null;
                if (unsuitable != null)
                {
                    PetLog.Write("pet-sharpen-skip", source + " " + width + "×" + height + " (" + unsuitable + ")");
                    return Outcome.Unsuitable;
                }
                var pixels = new FormatConvertedBitmap(sheet, PixelFormats.Pbgra32, null, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(cacheKey));
                var upscaler = new Upscaler(width);
                var band = new byte[width * CellHeight * 4];
                int rows = height / CellHeight;
                for (int row = 0; row < rows; row++)
                {
                    string temp = RowFile(cacheKey, row) + stamp;
                    temps.Add(temp);
                    pixels.CopyPixels(new Int32Rect(0, row * CellHeight, width, CellHeight), band, width * 4, 0);
                    upscaler.Process(band);
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var png = new PngWriter(file, width * 2, CellHeight * 2))
                    {
                        for (int y = 0; y < CellHeight * 2; y++) png.WriteRow(upscaler.Output, y * width * 8);
                        png.Finish();
                    }
                }
                // What it was made from (Prune, Forget), written before the rows go in place. A note that cannot be written
                // costs only the cleanup: Prune then takes the copy for an orphan, and it is made again next time.
                string note = cacheKey + ".txt";
                try { File.WriteAllText(note, Path.GetFullPath(source)); }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { PetLog.Write("pet-sharpen-note", ex.GetType().Name + ": " + ex.Message); }
                lock (Gate)
                {
                    // The pet was deleted meanwhile: keep nothing of it.
                    if (Forgotten.Contains(Path.GetFullPath(source)) || !File.Exists(source)) { TryDelete(note); return Outcome.Unsuitable; }
                    // In row order: the last row in place marks the copy as made. One left by an earlier try is replaced.
                    for (int row = 0; row < rows; row++)
                    {
                        string target = RowFile(cacheKey, row);
                        TryDelete(target);
                        File.Move(temps[row], target);
                    }
                }
                Interlocked.Increment(ref sheetsMade);
                PetLog.Write("pet-sharpen", Path.GetFileName(cacheKey) + " from " + source + " (" + width + "×" + height + ", " + watch.ElapsedMilliseconds + " ms)");
                return Outcome.Made;
            }
            catch (Exception ex) when (ex is FileFormatException || ex is NotSupportedException || ex is ArgumentException || ex is OverflowException)
            {
                PetLog.Write("pet-sharpen-skip", source + ": " + ex.GetType().Name + ": " + ex.Message); // not a picture WIC can read
                return Outcome.Unsuitable;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException || ex is OutOfMemoryException)
            {
                PetLog.Write("pet-sharpen-failed", source + ": " + ex.GetType().Name + ": " + ex.Message);
                return Outcome.Passing;
            }
            finally { foreach (string temp in temps) TryDelete(temp); } // whatever happened, no half-written file stays
        }

        // A color profile (ICC) other than sRGB: the page color-manages the original, while the copy (plain sRGB PNG) would
        // lose it and shift the colors. (The WebP decoder reports sRGB for every sheet without a profile: that one is fine.)
        private static bool ColorProfile(BitmapFrame frame)
        {
            try
            {
                if (frame.ColorContexts == null) return false;
                foreach (var context in frame.ColorContexts)
                    using (var profile = context.OpenProfileStream())
                    {
                        var bytes = new byte[64 * 1024];
                        int read = 0, n;
                        while (read < bytes.Length && (n = profile.Read(bytes, read, bytes.Length - read)) > 0) read += n;
                        if (System.Text.Encoding.ASCII.GetString(bytes, 0, read).IndexOf("sRGB", StringComparison.Ordinal) < 0) return true;
                    }
                return false;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException || ex is IOException ||
                                       ex is System.Runtime.InteropServices.COMException) { return true; } // unreadable: keep the original
        }

        /// <summary>
        /// Deletes the copies whose pet is gone or has changed since (a changed sheet gets a new key), and what an interrupted
        /// write left behind. Cheap: one look at the folder, once per app start (<see cref="PruneInBackground"/>).
        /// </summary>
        public static int Prune()
        {
            int removed = 0;
            try
            {
                string folder = CacheDirectory;
                if (!Directory.Exists(folder)) return 0;
                var judged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // copy key → keep
                foreach (string file in Directory.GetFiles(folder))
                {
                    try
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-10)) { File.Delete(file); removed++; }
                            continue;
                        }
                        if (!name.StartsWith("sheet-", StringComparison.OrdinalIgnoreCase) || name.Length < 30) continue;
                        string key = Path.Combine(folder, name.Substring(0, 30)); // sheet- + 24 hex digits
                        if (!judged.TryGetValue(key, out bool keep))
                        {
                            string note = key + ".txt";
                            string source = File.Exists(note) ? File.ReadAllText(note).Trim() : "";
                            string current = source.Length > 0 ? CacheKey(source) : null;
                            judged[key] = keep = current != null && string.Equals(Path.GetFileName(current), Path.GetFileName(key), StringComparison.OrdinalIgnoreCase);
                        }
                        if (!keep) { File.Delete(file); removed++; }
                    }
                    catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { } // in use (a page reading it): next time
                }
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            return removed;
        }

        /// <summary>
        /// The 2× copy of premultiplied BGRA pixels (width a multiple of 192, height of 208) as straight RGBA (PNG order) —
        /// what <see cref="Sharpen"/> writes, for checks.
        /// </summary>
        public static byte[] Upscale(byte[] pbgra, int width, int height)
        {
            if (width <= 0 || width % CellWidth != 0 || height <= 0 || height % CellHeight != 0 || pbgra == null || pbgra.Length < width * height * 4)
                throw new ArgumentException("not whole 192 × 208 cells");
            var upscaler = new Upscaler(width);
            var result = new byte[width * height * 16];
            var band = new byte[width * CellHeight * 4];
            for (int top = 0; top < height; top += CellHeight)
            {
                Buffer.BlockCopy(pbgra, top * width * 4, band, 0, band.Length);
                upscaler.Process(band);
                Buffer.BlockCopy(upscaler.Output, 0, result, top * width * 16, upscaler.Output.Length);
            }
            return result;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
        }

        private static double Lanczos3(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-9) return 1;
            if (x >= 3) return 0;
            double px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }

        /// <summary>
        /// 2× Lanczos3 + unsharp of one band (one row of frames, 208 pixels high), one channel at a time: alpha first (the
        /// colors are kept within it), then B, G, R. Buffers are reused from band to band.
        /// </summary>
        private sealed class Upscaler
        {
            private const int OutCellWidth = CellWidth * 2, OutCellHeight = CellHeight * 2;
            private readonly int width, cells, outWidth, blurRadius;
            private readonly int[] tapsX, tapsY;
            private readonly float[] weightsX, weightsY, blurKernel;
            private readonly float[] source, across, alpha, color, blurAcross, blurred;
            /// <summary>The band's result: straight RGBA, (2 × width) × 416.</summary>
            public readonly byte[] Output;

            public Upscaler(int width)
            {
                this.width = width;
                cells = width / CellWidth;
                outWidth = width * 2;
                Taps(CellWidth, out tapsX, out weightsX);
                Taps(CellHeight, out tapsY, out weightsY);
                blurRadius = (int)Math.Ceiling(UnsharpSigma * 3);
                blurKernel = new float[blurRadius * 2 + 1];
                double sum = 0;
                for (int k = -blurRadius; k <= blurRadius; k++) sum += Math.Exp(-k * k / (2 * UnsharpSigma * UnsharpSigma));
                for (int k = -blurRadius; k <= blurRadius; k++) blurKernel[k + blurRadius] = (float)(Math.Exp(-k * k / (2 * UnsharpSigma * UnsharpSigma)) / sum);
                source = new float[width * CellHeight];
                across = new float[outWidth * CellHeight];
                int size = outWidth * OutCellHeight;
                alpha = new float[size]; color = new float[size]; blurAcross = new float[size]; blurred = new float[size];
                Output = new byte[size * 4];
            }

            // For each of the 2n results in a cell of n pixels: its 6 source taps (clamped inside the cell) and their weights.
            // Taps 2 and 3 are the two nearest source pixels.
            private static void Taps(int n, out int[] taps, out float[] weights)
            {
                taps = new int[n * 2 * 6];
                weights = new float[n * 2 * 6];
                for (int o = 0; o < n * 2; o++)
                {
                    double center = (o + 0.5) / 2 - 0.5, sum = 0;
                    int first = (int)Math.Floor(center) - 2;
                    var w = new double[6];
                    for (int k = 0; k < 6; k++) { w[k] = Lanczos3(center - (first + k)); sum += w[k]; }
                    for (int k = 0; k < 6; k++)
                    {
                        taps[o * 6 + k] = Math.Max(0, Math.Min(n - 1, first + k));
                        weights[o * 6 + k] = (float)(w[k] / sum);
                    }
                }
            }

            public void Process(byte[] band)
            {
                Load(band, 3);
                Resample(alpha, true);
                int size = alpha.Length;
                for (int i = 0; i < size; i++)
                {
                    float a = alpha[i] < 0 ? 0 : alpha[i] > 255 ? 255 : alpha[i];
                    alpha[i] = a;
                    Output[i * 4 + 3] = (byte)(a + 0.5f);
                }
                float amount = (float)UnsharpAmount;
                for (int channel = 0; channel < 3; channel++) // B, G, R in the source; R, G, B in the result
                {
                    Load(band, channel);
                    Resample(color, false);
                    for (int i = 0; i < size; i++) color[i] = color[i] < 0 ? 0 : color[i] > alpha[i] ? alpha[i] : color[i];
                    Blur(color, blurred);
                    int to = 2 - channel;
                    for (int i = 0; i < size; i++)
                    {
                        float a = alpha[i], v = color[i] + amount * (color[i] - blurred[i]);
                        v = v < 0 ? 0 : v > a ? a : v;
                        int straight = Output[i * 4 + 3] == 0 ? 0 : (int)(v * 255f / a + 0.5f);
                        Output[i * 4 + to] = (byte)(straight > 255 ? 255 : straight);
                    }
                }
            }

            private void Load(byte[] band, int channel)
            {
                for (int i = 0; i < source.Length; i++) source[i] = band[i * 4 + channel];
            }

            // Across (each cell's row to twice its width), then down (the band is one cell high: its edges are the cell's).
            // Alpha: each result is kept between its two nearest source pixels, so Lanczos ringing leaves no rim.
            private void Resample(float[] into, bool clampToNearest)
            {
                for (int y = 0; y < CellHeight; y++)
                    for (int cell = 0; cell < cells; cell++)
                    {
                        int from = y * width + cell * CellWidth, to = y * outWidth + cell * OutCellWidth;
                        for (int o = 0; o < OutCellWidth; o++)
                        {
                            int t = o * 6;
                            float v = weightsX[t] * source[from + tapsX[t]] + weightsX[t + 1] * source[from + tapsX[t + 1]] +
                                      weightsX[t + 2] * source[from + tapsX[t + 2]] + weightsX[t + 3] * source[from + tapsX[t + 3]] +
                                      weightsX[t + 4] * source[from + tapsX[t + 4]] + weightsX[t + 5] * source[from + tapsX[t + 5]];
                            if (clampToNearest)
                            {
                                float p = source[from + tapsX[t + 2]], q = source[from + tapsX[t + 3]];
                                float low = p < q ? p : q, high = p < q ? q : p;
                                v = v < low ? low : v > high ? high : v;
                            }
                            across[to + o] = v;
                        }
                    }
                for (int o = 0; o < OutCellHeight; o++)
                {
                    int t = o * 6, to = o * outWidth;
                    int r0 = tapsY[t] * outWidth, r1 = tapsY[t + 1] * outWidth, r2 = tapsY[t + 2] * outWidth,
                        r3 = tapsY[t + 3] * outWidth, r4 = tapsY[t + 4] * outWidth, r5 = tapsY[t + 5] * outWidth;
                    float w0 = weightsY[t], w1 = weightsY[t + 1], w2 = weightsY[t + 2], w3 = weightsY[t + 3], w4 = weightsY[t + 4], w5 = weightsY[t + 5];
                    for (int x = 0; x < outWidth; x++)
                    {
                        float v = w0 * across[r0 + x] + w1 * across[r1 + x] + w2 * across[r2 + x] + w3 * across[r3 + x] + w4 * across[r4 + x] + w5 * across[r5 + x];
                        if (clampToNearest)
                        {
                            float p = across[r2 + x], q = across[r3 + x];
                            float low = p < q ? p : q, high = p < q ? q : p;
                            v = v < low ? low : v > high ? high : v;
                        }
                        into[to + x] = v;
                    }
                }
            }

            // Gaussian blur inside each cell (its edge pixels repeat outward).
            private void Blur(float[] from, float[] into)
            {
                int r = blurRadius;
                for (int y = 0; y < OutCellHeight; y++)
                    for (int cell = 0; cell < cells; cell++)
                    {
                        int start = y * outWidth + cell * OutCellWidth;
                        for (int x = 0; x < OutCellWidth; x++)
                        {
                            float v = 0;
                            for (int k = -r; k <= r; k++)
                            {
                                int xx = x + k;
                                xx = xx < 0 ? 0 : xx >= OutCellWidth ? OutCellWidth - 1 : xx;
                                v += blurKernel[k + r] * from[start + xx];
                            }
                            blurAcross[start + x] = v;
                        }
                    }
                for (int y = 0; y < OutCellHeight; y++)
                {
                    int to = y * outWidth;
                    Array.Clear(into, to, outWidth);
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = y + k;
                        yy = yy < 0 ? 0 : yy >= OutCellHeight ? OutCellHeight - 1 : yy;
                        int row = yy * outWidth;
                        float w = blurKernel[k + r];
                        for (int x = 0; x < outWidth; x++) into[to + x] += w * blurAcross[row + x];
                    }
                }
            }
        }

        /// <summary>
        /// A PNG (8-bit RGBA, Paeth filter) written row by row, so a 16-million-pixel sheet never has to be in memory whole:
        /// the zlib stream (DeflateStream + its header and Adler-32) goes out in IDAT chunks of 256 KB.
        /// </summary>
        private sealed class PngWriter : IDisposable
        {
            private readonly Stream file;
            private readonly ChunkStream data;
            private readonly DeflateStream deflate;
            private readonly int rowBytes;
            private readonly byte[] previous, filtered;
            private uint adlerA = 1, adlerB;
            private bool finished;

            public PngWriter(Stream file, int width, int height)
            {
                this.file = file;
                file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new byte[13];
                BigEndian(header, 0, (uint)width);
                BigEndian(header, 4, (uint)height);
                header[8] = 8; header[9] = 6; // 8 bits, RGBA
                WriteChunk(file, "IHDR", header, header.Length);
                data = new ChunkStream(file);
                data.Write(new byte[] { 0x78, 0x9C }, 0, 2);
                deflate = new DeflateStream(data, CompressionLevel.Optimal, true);
                rowBytes = width * 4;
                previous = new byte[rowBytes];
                filtered = new byte[rowBytes + 1];
            }

            public void WriteRow(byte[] pixels, int offset)
            {
                filtered[0] = 4; // Paeth
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= 4 ? pixels[offset + i - 4] : 0, b = previous[i], c = i >= 4 ? previous[i - 4] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    filtered[i + 1] = (byte)(pixels[offset + i] - (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                Buffer.BlockCopy(pixels, offset, previous, 0, rowBytes);
                for (int i = 0; i < filtered.Length;)
                {
                    int end = Math.Min(filtered.Length, i + 5552); // Adler-32 sums stay below 2^32 for 5552 bytes
                    for (; i < end; i++) { adlerA += filtered[i]; adlerB += adlerA; }
                    adlerA %= 65521; adlerB %= 65521;
                }
                deflate.Write(filtered, 0, filtered.Length);
            }

            public void Finish()
            {
                deflate.Dispose();
                var adler = new byte[4];
                BigEndian(adler, 0, adlerB << 16 | adlerA);
                data.Write(adler, 0, 4);
                data.Flush();
                WriteChunk(file, "IEND", new byte[0], 0);
                finished = true;
            }

            public void Dispose() { if (!finished) deflate.Dispose(); }

            private static void BigEndian(byte[] buffer, int offset, uint value)
            {
                buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16);
                buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
            }

            private static readonly uint[] CrcTable = BuildCrcTable();

            private static uint[] BuildCrcTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                return table;
            }

            internal static void WriteChunk(Stream stream, string type, byte[] content, int length)
            {
                var head = new byte[8];
                BigEndian(head, 0, (uint)length);
                for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
                uint crc = 0xFFFFFFFFu;
                for (int i = 4; i < 8; i++) crc = CrcTable[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
                for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ content[i]) & 0xFF] ^ (crc >> 8);
                var tail = new byte[4];
                BigEndian(tail, 0, crc ^ 0xFFFFFFFFu);
                stream.Write(head, 0, 8);
                stream.Write(content, 0, length);
                stream.Write(tail, 0, 4);
            }

            /// <summary>Collects what is written and passes it on as IDAT chunks.</summary>
            private sealed class ChunkStream : Stream
            {
                private readonly Stream file;
                private readonly byte[] buffer = new byte[256 * 1024];
                private int used;
                public ChunkStream(Stream file) { this.file = file; }
                public override void Write(byte[] bytes, int offset, int count)
                {
                    while (count > 0)
                    {
                        int take = Math.Min(count, buffer.Length - used);
                        Buffer.BlockCopy(bytes, offset, buffer, used, take);
                        used += take; offset += take; count -= take;
                        if (used == buffer.Length) Flush();
                    }
                }
                public override void Flush()
                {
                    if (used == 0) return;
                    WriteChunk(file, "IDAT", buffer, used);
                    used = 0;
                }
                public override bool CanRead => false;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
            }
        }
    }
}
