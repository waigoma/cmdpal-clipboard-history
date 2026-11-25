namespace Waigoma.ClipboardHistory.Store;

// The whole store lives under one root so that a single folder can be moved or deleted as a unit.
public sealed record StorePaths(string Root)
{
    public string Db => Path.Combine(Root, "history.db");

    public string Files => Path.Combine(Root, "files");

    public string Thumbs => Path.Combine(Root, "thumbs");

    public string Temp => Path.Combine(Root, "tmp");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(Thumbs);
        Directory.CreateDirectory(Temp);
    }
}
