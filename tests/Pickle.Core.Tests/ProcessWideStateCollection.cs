namespace Pickle.Core.Tests;

/// <summary>
/// For tests that assert on process-wide PowerShell state — notably <c>$PSStyle</c>, a single static instance that
/// every started runtime rewrites from its theme. Tests in this collection run alone, after the parallel ones.
/// Use: <c>[Collection(ProcessWideStateCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideStateCollection
{
    public const string Name = "Process-wide PowerShell state";
}
