using System.Text;
using Waigoma.ClipboardHistory.Store;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class HistoryStoreRecoveryTests
{
    [Fact]
    public void GarbageDatabaseFile_IsMovedAside_AndTheStoreOpensEmpty()
    {
        using var temp = new TempStore();
        temp.Paths.EnsureCreated();
        byte[] garbage = Encoding.ASCII.GetBytes(new string('x', 4096));
        File.WriteAllBytes(temp.Paths.Db, garbage);

        using var store = new HistoryStore(temp.Paths, new FakeClock());

        Assert.True(store.RecoveredFromCorruption);
        Assert.Equal(0, store.TotalBytes());
        string kept = Assert.Single(Directory.GetFiles(temp.Root, "history.db.corrupt-*"));
        Assert.Equal(garbage, File.ReadAllBytes(kept));
    }

    [Fact]
    public void HealthyDatabase_IsNotRecovered()
    {
        using var temp = new TempStore();
        using (new HistoryStore(temp.Paths, new FakeClock()))
        {
        }

        using var reopened = new HistoryStore(temp.Paths, new FakeClock());

        Assert.False(reopened.RecoveredFromCorruption);
        Assert.Empty(Directory.GetFiles(temp.Root, "history.db.corrupt-*"));
    }
}
