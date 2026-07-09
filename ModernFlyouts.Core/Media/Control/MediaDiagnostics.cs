using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ModernFlyouts.Core.Media.Control
{
    public static class MediaDiagnostics
    {
        private const int MaxEntries = 256;
        private static readonly object Gate = new();
        private static readonly Queue<string> Entries = new();

        public static bool TraceEnabled { get; set; }

        public static string ExportText()
        {
            lock (Gate)
            {
                return string.Join(Environment.NewLine, Entries.ToArray());
            }
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (Gate)
            {
                return Entries.ToArray();
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warning(string message) => Write("WARN", message);

        public static void Trace(string message)
        {
            if (TraceEnabled)
            {
                Write("TRACE", message);
            }
        }

        public static void Exception(string context, Exception exception)
        {
            Write("WARN", $"{context}: {exception.GetType().Name}: {exception.Message}");
        }

        private static void Write(string level, string message)
        {
            string entry = $"{DateTimeOffset.Now:O} [{level}] {message}";

            lock (Gate)
            {
                Entries.Enqueue(entry);
                while (Entries.Count > MaxEntries)
                {
                    Entries.Dequeue();
                }
            }

            Debug.WriteLine($"{nameof(MediaDiagnostics)}: {entry}");
        }
    }
}
