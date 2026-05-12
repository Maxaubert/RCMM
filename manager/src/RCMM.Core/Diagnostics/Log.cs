using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RCMM.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Thread-safe append-only file logger. Used by RCMM services and view-models to record
/// unexpected behaviour (COM failures, capture timeouts, hide/unhide errors) so the user
/// can attach a log when reporting a problem.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();
    private static string _folder = DefaultFolder();
    private static string _file = Path.Combine(_folder, "rcmm.log");
    private static LogLevel _minLevel = LogLevel.Debug;
    private const long MaxBytes = 1_000_000;

    public static string Folder => _folder;
    public static string FilePath => _file;

    public static LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    public static void Configure(string folder)
    {
        lock (_gate)
        {
            _folder = folder;
            _file = Path.Combine(_folder, "rcmm.log");
        }
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message, null);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    public static void Hr(string category, string operation, int hr, string? extra = null)
    {
        var level = hr < 0 ? LogLevel.Warn : LogLevel.Debug;
        var sb = new StringBuilder();
        sb.Append(operation).Append(" hr=0x").Append(hr.ToString("X8"));
        if (extra != null) sb.Append(' ').Append(extra);
        Write(level, category, sb.ToString(), null);
    }

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _minLevel) return;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_folder);
                Rotate();
                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
                sb.Append(" [t").Append(Thread.CurrentThread.ManagedThreadId.ToString("D2")).Append(']');
                sb.Append(' ').Append(category).Append(": ").Append(message);
                if (ex != null) sb.Append(" | ex=").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                sb.Append("\r\n");
                File.AppendAllText(_file, sb.ToString());
            }
        }
        catch
        {
            // Logging must never throw into the caller's hot path.
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length < MaxBytes) return;
            var old = _file + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(_file, old);
        }
        catch { }
    }

    private static string DefaultFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RCMM", "logs");
}
