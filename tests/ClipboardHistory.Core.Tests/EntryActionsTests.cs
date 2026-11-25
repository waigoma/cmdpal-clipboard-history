using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class EntryActionsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly EntryAction[] TextOrder =
    [
        EntryAction.Paste, EntryAction.Copy, EntryAction.PastePlain, EntryAction.Edit, EntryAction.Rename,
        EntryAction.TogglePin, EntryAction.ExcludeApp, EntryAction.Delete, EntryAction.BulkDelete, EntryAction.DeleteAll,
    ];

    [Fact]
    public void Text_ListsActionsInSpecOrder()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Text), pasteIsPrimary: true, formatCount: 1);

        Assert.Equal(TextOrder, actions);
    }

    [Fact]
    public void CopyIsPrimary_SwapsTheFirstTwoActions()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Text), pasteIsPrimary: false, formatCount: 1);

        Assert.Equal(EntryAction.Copy, actions[0]);
        Assert.Equal(EntryAction.Paste, actions[1]);
        Assert.Equal(TextOrder.Skip(2), actions.Skip(2));
    }

    [Fact]
    public void PasteFormat_AppearsOnlyWhenTwoOrMoreFormats()
    {
        IReadOnlyList<EntryAction> one = EntryActions.For(Summary(EntryKind.Text), true, formatCount: 1);
        IReadOnlyList<EntryAction> two = EntryActions.For(Summary(EntryKind.Text), true, formatCount: 2);

        Assert.DoesNotContain(EntryAction.PasteFormat, one);
        Assert.Equal(
            new[] { EntryAction.Paste, EntryAction.Copy, EntryAction.PastePlain, EntryAction.PasteFormat },
            two.Take(4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoKnownSource_OmitsExcludeApp(string? source)
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Text, source: source), true, 1);

        Assert.DoesNotContain(EntryAction.ExcludeApp, actions);
    }

    [Fact]
    public void Link_OffersEditAndOpen_ButNotOpenLocation()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Link), true, 1);

        Assert.Equal(
            new[]
            {
                EntryAction.Paste, EntryAction.Copy, EntryAction.PastePlain, EntryAction.Edit, EntryAction.Rename,
                EntryAction.TogglePin, EntryAction.Open, EntryAction.ExcludeApp, EntryAction.Delete,
                EntryAction.BulkDelete, EntryAction.DeleteAll,
            },
            actions);
    }

    [Fact]
    public void Email_OffersEditAndOpen()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Email), true, 1);

        Assert.Contains(EntryAction.Edit, actions);
        Assert.Contains(EntryAction.Open, actions);
        Assert.DoesNotContain(EntryAction.OpenLocation, actions);
    }

    [Fact]
    public void Color_OffersEditButNotOpen()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Color), true, 1);

        Assert.Contains(EntryAction.Edit, actions);
        Assert.DoesNotContain(EntryAction.Open, actions);
    }

    [Fact]
    public void Image_WithQr_OffersOcrQrAndLocation_AndNoText()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Image, hasQr: true), true, formatCount: 2);

        Assert.Equal(
            new[]
            {
                EntryAction.Paste, EntryAction.Copy, EntryAction.PasteFormat, EntryAction.Rename,
                EntryAction.TogglePin, EntryAction.CopyOcr, EntryAction.CopyQr, EntryAction.Open,
                EntryAction.OpenLocation, EntryAction.ExcludeApp, EntryAction.Delete, EntryAction.BulkDelete,
                EntryAction.DeleteAll,
            },
            actions);
        Assert.DoesNotContain(EntryAction.PastePlain, actions);
        Assert.DoesNotContain(EntryAction.Edit, actions);
    }

    [Fact]
    public void Image_WithoutQr_OmitsCopyQr()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Image, hasQr: false), true, formatCount: 1);

        Assert.Contains(EntryAction.CopyOcr, actions);
        Assert.DoesNotContain(EntryAction.CopyQr, actions);
        Assert.DoesNotContain(EntryAction.PasteFormat, actions);
    }

    [Fact]
    public void Files_OffersPlainPasteAndLocation_ButNotEdit()
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Files), true, formatCount: 2);

        Assert.Equal(
            new[]
            {
                EntryAction.Paste, EntryAction.Copy, EntryAction.PastePlain, EntryAction.PasteFormat,
                EntryAction.Rename, EntryAction.TogglePin, EntryAction.Open, EntryAction.OpenLocation,
                EntryAction.ExcludeApp, EntryAction.Delete, EntryAction.BulkDelete, EntryAction.DeleteAll,
            },
            actions);
    }

    [Fact]
    public void PrimaryIsAlwaysFirst_WhateverTheFormatCount()
    {
        for (int formats = 0; formats <= 3; formats++)
        {
            IReadOnlyList<EntryAction> actions = EntryActions.For(Summary(EntryKind.Image, hasQr: true), true, formats);

            Assert.Equal(EntryAction.Paste, actions[0]);
            Assert.Equal(EntryAction.Copy, actions[1]);
        }
    }

    private static EntrySummary Summary(EntryKind kind, string? source = "notepad.exe", bool hasQr = false)
    {
        return new EntrySummary(
            Id: 1,
            Kind: kind,
            Preview: "preview",
            Name: null,
            SourceApp: source,
            Pinned: false,
            CreatedAt: T0,
            LastCopiedAt: T0,
            Width: 0,
            Height: 0,
            ByteSize: 1,
            CharCount: 1,
            LineCount: 1,
            ImageState: kind == EntryKind.Image ? ImageState.Done : ImageState.None,
            HasOcr: false,
            HasQr: hasQr,
            ThumbPath: null);
    }
}
