using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace EPDiag
{
    /// <summary>
    /// Writes to its own file, not the BepInEx log.
    ///
    /// BepInEx truncates LogOutput.log on every launch, so restarting after a problem destroys
    /// the record of it, and the shared log carries thousands of unrelated exceptions from other
    /// mods. One file per launch, named for its start time, so nothing is overwritten. Flushed
    /// per line, because the last line before a crash is usually the one that matters.
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
