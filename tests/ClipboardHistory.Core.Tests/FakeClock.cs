namespace Waigoma.ClipboardHistory.Tests;

// Lets tests move time explicitly so timestamp assertions do not depend on the wall clock.
internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
