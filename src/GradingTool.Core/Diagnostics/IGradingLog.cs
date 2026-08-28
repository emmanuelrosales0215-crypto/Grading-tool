using System;
using System.Collections.Generic;

namespace GradingTool.Diagnostics
{
    /// <summary>Severity of a message emitted by the grading engine.</summary>
    public enum GradingLogLevel
    {
        /// <summary>Routine progress or a recorded conversion.</summary>
        Info,

        /// <summary>Something the engineer must read before trusting the output.</summary>
        Warning,

        /// <summary>A failure. Usually accompanied by a thrown exception.</summary>
        Error
    }

    /// <summary>
    /// Sink for engine diagnostics.
    /// <para>
    /// The engine must never write to <see cref="Console"/>. Inside Civil 3D there is no
    /// console, so a <c>Console.WriteLine</c> warning is silently lost - and several of
    /// the messages routed through here are ones the brief requires the engineer to see:
    /// a stale municipality config, and every unit conversion applied on ingestion
    /// ("converted, logged, never silently assumed").
    /// </para>
    /// </summary>
    public interface IGradingLog
    {
        /// <summary>Record one message.</summary>
        void Log(GradingLogLevel level, string message);
    }

    /// <summary>Convenience wrappers over <see cref="IGradingLog.Log"/>.</summary>
    public static class GradingLogExtensions
    {
        /// <summary>Record an informational message.</summary>
        public static void Info(this IGradingLog? log, string message)
            => log?.Log(GradingLogLevel.Info, message);

        /// <summary>Record a warning.</summary>
        public static void Warning(this IGradingLog? log, string message)
            => log?.Log(GradingLogLevel.Warning, message);

        /// <summary>Record an error.</summary>
        public static void Error(this IGradingLog? log, string message)
            => log?.Log(GradingLogLevel.Error, message);
    }

    /// <summary>Discards everything. Use where diagnostics genuinely do not matter.</summary>
    public sealed class NullGradingLog : IGradingLog
    {
        /// <summary>Shared instance.</summary>
        public static readonly NullGradingLog Instance = new NullGradingLog();

        /// <inheritdoc />
        public void Log(GradingLogLevel level, string message) { }
    }

    /// <summary>Writes to the console. For CLI tooling and tests, not for the add-in.</summary>
    public sealed class ConsoleGradingLog : IGradingLog
    {
        /// <inheritdoc />
        public void Log(GradingLogLevel level, string message)
            => Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {message}");
    }

    /// <summary>
    /// Keeps messages in memory so tests can assert that a required warning was actually
    /// emitted - for instance that a metre-to-foot conversion was recorded rather than
    /// applied silently.
    /// </summary>
    public sealed class CollectingGradingLog : IGradingLog
    {
        private readonly List<(GradingLogLevel Level, string Message)> _entries
            = new List<(GradingLogLevel, string)>();

        /// <summary>Everything recorded so far, in order.</summary>
        public IReadOnlyList<(GradingLogLevel Level, string Message)> Entries => _entries;

        /// <inheritdoc />
        public void Log(GradingLogLevel level, string message) => _entries.Add((level, message));

        /// <summary>True if any recorded message at <paramref name="level"/> contains <paramref name="fragment"/>.</summary>
        public bool Contains(GradingLogLevel level, string fragment)
            => _entries.Exists(e => e.Level == level
                && e.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
