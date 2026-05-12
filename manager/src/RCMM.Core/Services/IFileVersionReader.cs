namespace RCMM.Core.Services;

public sealed record FileVersion(string? FileDescription, string? CompanyName, string? ProductName);

public interface IFileVersionReader
{
    FileVersion Read(string path);
}
