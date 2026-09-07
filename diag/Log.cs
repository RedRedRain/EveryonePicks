using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace EPDiag
{
    /// <summary>
    /// Writes to its own file, not the BepInEx log.
    ///
    /// Two reasons. BepInEx truncates LogOutput.log on every launch, so restarting the game after
    /// a problem destroys the only record of it - that has already cost us one investigation. And
    /// the shared log is drowning in thousands of unrelated exceptions from other mods, which
    /// makes the round-loop sequence almost impossible to read.
    ///
    /// One file per launch, named for the moment it started, so nothing ever overwrites anything.
    /// Every line is flushed immediately: if the game hard-crashes or is killed, the last line
    /// before it died is the most valuable line in the file and must not be sitting in a buffer.
    /// </summary>
    internal static class Log
    {
        private static StreamWriter writer;
        private static readonly object gate = new object();
        private static DateTime started;

        internal static string Path { get; private set; }

        internal static void Open(string playerLabel)
        {
            try
            {
                started = DateTime.Now;

                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "EPDiag");
                Directory.CreateDirectory(dir);

                string safe = Sanitise(playerLabel);
                Path = System.IO.Path.Combine(
                    dir, "EPDiag-" + started.ToString("yyyyMMdd-HHmmss") + "-" + safe + ".log");

                writer = new StreamWriter(Path, false, new UTF8Encoding(false));
                writer.AutoFlush = true;

                Line("=== EPDiag " + EPDiagPlugin.Version + " opened " +
                     started.ToString("yyyy-MM-dd HH:mm:ss") + " for " + playerLabel + " ===");
                Line("Send this whole file. Clocks are local wall time so two players' files line up.");
            }
            catch
            {
                writer = null;   // never let logging break the game
            }
        }

        internal static void Line(string text)
        {
            var w = writer;
            if (w == null) return;

            try
            {
                lock (gate)
                {
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                + "  " + text);
                }
            }
            catch { }
        }

        internal static void Close()
        {
            try
            {
                lock (gate)
                {
                    if (writer == null) return;
                    writer.WriteLine("=== closed " + DateTime.Now.ToString("HH:mm:ss") + " ===");
                    writer.Dispose();
                    writer = null;
                }
            }
            catch { }
        }

        private static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "player";
            var sb = new StringBuilder();
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length == 0 ? "player" : sb.ToString();
        }
    }
}
