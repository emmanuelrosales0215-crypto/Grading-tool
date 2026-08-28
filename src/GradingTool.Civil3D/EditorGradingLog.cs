using Autodesk.AutoCAD.EditorInput;
using GradingTool.Diagnostics;

namespace GradingTool.Civil3D
{
    /// <summary>
    /// Routes engine diagnostics to the Civil 3D command line. This is the add-in's
    /// implementation of <see cref="IGradingLog"/> - the reason the engine never calls
    /// <c>Console.WriteLine</c> (there is no console in Civil 3D) but logs through the
    /// abstraction instead, so warnings like a metre-to-feet conversion or a stale config
    /// actually reach the engineer.
    /// </summary>
    public sealed class EditorGradingLog : IGradingLog
    {
        private readonly Editor _editor;

        /// <summary>Construct over the active document's editor.</summary>
        public EditorGradingLog(Editor editor) => _editor = editor;

        /// <inheritdoc />
        public void Log(GradingLogLevel level, string message)
            => _editor.WriteMessage($"\n[{level.ToString().ToUpperInvariant()}] {message}");
    }
}
