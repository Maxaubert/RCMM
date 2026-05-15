using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>One section of the icon picker — a labelled group of icon names.</summary>
public sealed record IconCategory(string Name, IReadOnlyList<string> Icons);
