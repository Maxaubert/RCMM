using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class FormatConverterTests
{
    [Theory]
    [InlineData("photo.png", ConvertCategory.Image)]
    [InlineData("PHOTO.JPG", ConvertCategory.Image)]     // case-insensitive
    [InlineData("art.webp", ConvertCategory.Image)]
    [InlineData("clip.mp4", ConvertCategory.Video)]
    [InlineData("movie.MKV", ConvertCategory.Video)]
    [InlineData("notes.txt", ConvertCategory.Unknown)]
    [InlineData("noext", ConvertCategory.Unknown)]
    public void Detect_maps_extension_to_category(string path, ConvertCategory expected)
        => Assert.Equal(expected, FormatConverter.Detect(path));

    [Fact]
    public void PlanFor_image_uses_imagemagick_and_offers_common_targets()
    {
        var plan = FormatConverter.PlanFor(@"C:\a\photo.png");
        Assert.NotNull(plan);
        Assert.Equal(ConvertCategory.Image, plan!.Category);
        Assert.Equal("magick", plan.Tool);
        Assert.Equal("ImageMagick.ImageMagick", plan.WingetId);
        var exts = plan.Targets.Select(t => t.Extension).ToList();
        Assert.Contains(".webp", exts);
        Assert.Contains(".jpg", exts);
        Assert.Contains(".ico", exts);
        // The source format is not offered as a target.
        Assert.DoesNotContain(".png", exts);
    }

    [Fact]
    public void PlanFor_video_uses_ffmpeg_and_includes_audio_extract()
    {
        var plan = FormatConverter.PlanFor(@"C:\a\clip.mov");
        Assert.NotNull(plan);
        Assert.Equal(ConvertCategory.Video, plan!.Category);
        Assert.Equal("ffmpeg", plan.Tool);
        Assert.Equal("Gyan.FFmpeg", plan.WingetId);
        Assert.Contains(plan.Targets, t => t.Extension == ".mp4");
        // "extract audio" is a target carrying ffmpeg args.
        Assert.Contains(plan.Targets, t => t.Extension == ".mp3" && t.ExtraArgs != null && t.ExtraArgs.Contains("-vn"));
    }

    [Fact]
    public void PlanFor_unknown_type_returns_null()
        => Assert.Null(FormatConverter.PlanFor(@"C:\a\notes.txt"));

    [Fact]
    public void OutputPathFor_changes_extension_and_avoids_overwriting_input()
    {
        var webp = new ConvertTarget("WebP", ".webp");
        Assert.Equal(@"C:\a\photo.webp", FormatConverter.OutputPathFor(@"C:\a\photo.png", webp));

        // If the target extension equals the input's, don't clobber the source.
        var png = new ConvertTarget("PNG", ".png");
        Assert.Equal(@"C:\a\photo (converted).png", FormatConverter.OutputPathFor(@"C:\a\photo.png", png));
    }

    [Fact]
    public void BuildArguments_image_quotes_input_and_output()
    {
        var plan = FormatConverter.PlanFor(@"C:\a\photo.png")!;
        var webp = new ConvertTarget("WebP", ".webp");
        Assert.Equal("\"C:\\a\\photo.png\" \"C:\\a\\photo.webp\"",
                     FormatConverter.BuildArguments(plan, @"C:\a\photo.png", webp));
    }

    [Fact]
    public void BuildArguments_video_uses_ffmpeg_input_flag_and_extra_args()
    {
        var plan = FormatConverter.PlanFor(@"C:\a\clip.mov")!;
        var mp4 = new ConvertTarget("MP4", ".mp4");
        var args = FormatConverter.BuildArguments(plan, @"C:\a\clip.mov", mp4);
        Assert.StartsWith("-y -i \"C:\\a\\clip.mov\"", args);
        Assert.EndsWith("\"C:\\a\\clip.mp4\"", args);

        var mp3 = new ConvertTarget("Audio (MP3)", ".mp3", "-vn -c:a libmp3lame");
        var audioArgs = FormatConverter.BuildArguments(plan, @"C:\a\clip.mov", mp3);
        Assert.Contains("-vn -c:a libmp3lame", audioArgs);
    }
}
