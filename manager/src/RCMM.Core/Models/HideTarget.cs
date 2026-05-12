using RCMM.Core.Services;

namespace RCMM.Core.Models;

public sealed record HideTarget(HideKind Kind, RegistryHive Hive, string Path, string? ValueName);
