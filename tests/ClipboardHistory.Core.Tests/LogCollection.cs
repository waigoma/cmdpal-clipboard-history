using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

// Every test that calls Log.Initialize joins this collection. Log is process-wide, so two such tests running at once
// would point the log at each other's folders.
[CollectionDefinition(LogCollection.Name, DisableParallelization = true)]
public sealed class LogCollection
{
    public const string Name = "Log";
}
