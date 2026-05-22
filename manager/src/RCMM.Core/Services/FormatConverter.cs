using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RCMM.Core.Services;

public enum ConvertCategory { Unknown, Image, Video }

/// <summary>A format the user can convert a file into. <paramref name="ExtraArgs"/>
/// carries tool-specific flags (e.g. ffmpeg's <c>-vn</c> for audio extraction).</summary>
public sealed record ConvertTarget(string Label, string Extension, string? ExtraArgs = null);

/// <summary>What the helper needs to convert a given file: the category, the
/// external tool, the winget package to offer if that tool is missing, and the
/// list of target formats.</summary>
public sealed record ConvertPlan(
    ConvertCategory Category,
    string Tool,
    string WingetId,
    IReadOnlyList<ConvertTarget> Targets);

/// <summary>
/// Pure logic behind the "Convert / Change format" smart action: maps a file's
/// extension to a category, the tool that handles it, the winget package to
/// offer if the tool isn't installed, and the available target formats. This
/// class is side-effect-free (no process launches, no IO) so it is unit-tested;
/// the helper UI owns running the tool / winget.
/// </summary>
public static class FormatConverter
{
    private static readonly HashSet<string> _imageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> _videoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv" };

    private static readonly IReadOnlyList<ConvertTarget> _imageTargets = new[]
    {
        new ConvertTarget("PNG",  ".png"),
        new ConvertTarget("JPG",  ".jpg"),
        new ConvertTarget("WebP", ".webp"),
        new ConvertTarget("ICO",  ".ico"),
    };

    private static readonly IReadOnlyList<ConvertTarget> _videoTargets = new[]
    {
        new ConvertTarget("MP4",  ".mp4"),
        new ConvertTarget("MKV",  ".mkv"),
        new ConvertTarget("MOV",  ".mov"),
        new ConvertTarget("WebM", ".webm"),
        new ConvertTarget("GIF",  ".gif"),
        new ConvertTarget("Audio (MP3)", ".mp3", "-vn -c:a libmp3lame"),
    };

    public static ConvertCategory Detect(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return ConvertCategory.Unknown;
        if (_imageExts.Contains(ext)) return ConvertCategory.Image;
        if (_videoExts.Contains(ext)) return ConvertCategory.Video;
        return ConvertCategory.Unknown;
    }

    public static ConvertPlan? PlanFor(string path)
    {
        var ext = Path.GetExtension(path);
        return Detect(path) switch
        {
            ConvertCategory.Image => new ConvertPlan(ConvertCategory.Image, "magick",
                "ImageMagick.ImageMagick", TargetsExcludingSource(_imageTargets, ext)),
            ConvertCategory.Video => new ConvertPlan(ConvertCategory.Video, "ffmpeg",
                "Gyan.FFmpeg", TargetsExcludingSource(_videoTargets, ext)),
            _ => null,
        };
    }

    // No point offering to convert PNG→PNG, so drop the target matching the
    // source extension. The audio-extract target survives (its ext never
    // coincides with a video source).
    private static IReadOnlyList<ConvertTarget> TargetsExcludingSource(IReadOnlyList<ConvertTarget> targets, string sourceExt)
        => targets.Where(t => !string.Equals(t.Extension, sourceExt, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Where the converted file is written: same folder + name, new
    /// extension. If that would overwrite the source (target ext == source ext),
    /// a " (converted)" suffix is added instead.</summary>
    public static string OutputPathFor(string inputPath, ConvertTarget target)
    {
        var candidate = Path.ChangeExtension(inputPath, target.Extension);
        if (string.Equals(candidate, inputPath, StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(inputPath);
            candidate = Path.Combine(dir, $"{name} (converted){target.Extension}");
        }
        return candidate;
    }

    /// <summary>Argument string passed to <see cref="ConvertPlan.Tool"/>.
    /// magick: <c>"in" "out"</c>. ffmpeg: <c>-y -i "in" [extra] "out"</c>.</summary>
    public static string BuildArguments(ConvertPlan plan, string inputPath, ConvertTarget target)
    {
        var output = OutputPathFor(inputPath, target);
        return plan.Tool switch
        {
            "ffmpeg" => $"-y -i \"{inputPath}\"{(string.IsNullOrEmpty(target.ExtraArgs) ? string.Empty : " " + target.ExtraArgs)} \"{output}\"",
            _ => $"\"{inputPath}\" \"{output}\"",
        };
    }
}
