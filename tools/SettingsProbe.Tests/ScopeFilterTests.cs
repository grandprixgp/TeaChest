using SettingsProbe.Usn;
using static SettingsProbe.Tests.CannedRecords;

namespace SettingsProbe.Tests;

public class ScopeFilterTests
{
    private const ulong Root = 100;
    private const ulong Elsewhere = 900;

    [Fact]
    public void Keeps_records_under_the_root_and_skips_the_rest()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));

        Assert.True(filter.Add(Record(1, Root, UsnReason.FileCreate, "settings.json")));
        Assert.False(filter.Add(Record(2, Elsewhere, UsnReason.FileCreate, "someone-elses.docx")));

        Assert.Equal(1, filter.Kept);
        Assert.Equal(1, filter.Skipped);
        ScopedChange change = Assert.Single(filter.Changes());
        Assert.Equal("settings.json", change.RelativePath);
    }

    [Fact]
    public void Folders_created_inside_join_the_scope()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(10, Root, UsnReason.FileCreate, "Contoso", directory: true));
        filter.Add(Record(11, 10, UsnReason.FileCreate, "App", directory: true));
        filter.Add(Record(12, 11, UsnReason.FileCreate | UsnReason.DataExtend, "settings.json"));

        Assert.Equal(new[] { "Contoso", @"Contoso\App", @"Contoso\App\settings.json" }, filter.Changes().Select(c => c.RelativePath));
        Assert.True(filter.Changes()[0].IsDirectory);
    }

    [Fact]
    public void Folds_all_records_for_one_file_into_one_change()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(1, Root, UsnReason.FileCreate, "app.log"));
        filter.Add(Record(1, Root, UsnReason.FileCreate | UsnReason.DataExtend, "app.log"));
        filter.Add(Record(1, Root, UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close, "app.log"));

        ScopedChange change = Assert.Single(filter.Changes());
        Assert.Equal(3, change.RecordCount);
        Assert.True(change.Created);
        Assert.True(change.Written);
        Assert.False(change.Deleted);
    }

    [Fact]
    public void An_atomic_save_shows_up_under_its_final_name()
    {
        // Write settings.json.new, then rename it over settings.json: same file ID throughout.
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(5, Root, UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close, "settings.json.new"));
        filter.Add(Record(5, Root, UsnReason.RenameOldName, "settings.json.new"));
        filter.Add(Record(5, Root, UsnReason.RenameNewName, "settings.json"));

        ScopedChange change = Assert.Single(filter.Changes());
        Assert.Equal("settings.json", change.RelativePath);
        Assert.True(change.Renamed);
    }

    [Fact]
    public void Renaming_a_folder_updates_its_childrens_paths()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(10, Root, UsnReason.FileCreate, "Old", directory: true));
        filter.Add(Record(11, 10, UsnReason.FileCreate, "a.ini"));
        filter.Add(Record(10, Root, UsnReason.RenameNewName, "New", directory: true));

        Assert.Contains(@"New\a.ini", filter.Changes().Select(c => c.RelativePath));
    }

    [Fact]
    public void Moving_out_of_the_scope_never_records_the_new_name()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(1, Root, UsnReason.FileCreate, "export.json"));
        filter.Add(Record(1, Root, UsnReason.RenameOldName, "export.json"));
        filter.Add(Record(1, Elsewhere, UsnReason.RenameNewName, "private-destination-name.json"));

        ScopedChange change = Assert.Single(filter.Changes());
        Assert.Equal("export.json", change.RelativePath);
        Assert.True(change.MovedOut);
        Assert.DoesNotContain(filter.Changes(), c => c.RelativePath.Contains("private"));

        // And later writes to it, now outside, are skipped.
        Assert.False(filter.Add(Record(1, Elsewhere, UsnReason.DataExtend, "private-destination-name.json")));
    }

    [Fact]
    public void Moving_into_the_scope_starts_tracking_it()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        Assert.False(filter.Add(Record(7, Elsewhere, UsnReason.RenameOldName, "outside-name.txt")));
        Assert.True(filter.Add(Record(7, Root, UsnReason.RenameNewName, "inside.txt")));

        Assert.Equal("inside.txt", Assert.Single(filter.Changes()).RelativePath);
    }

    [Fact]
    public void A_deleted_file_keeps_its_last_path()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        filter.Add(Record(3, Root, UsnReason.FileCreate, "recover.tmp"));
        filter.Add(Record(3, Root, UsnReason.FileDelete | UsnReason.Close, "recover.tmp"));

        ScopedChange change = Assert.Single(filter.Changes());
        Assert.Equal("recover.tmp", change.RelativePath);
        Assert.True(change.Deleted);
    }

    [Fact]
    public void Files_in_a_folder_the_filter_never_saw_created_are_skipped()
    {
        // A folder that already existed before the window isn't known, so its files are skipped.
        // That's the safe direction: miss something rather than resolve a path outside the scope.
        var filter = new ScopeFilter(new UInt128(0, Root));
        Assert.False(filter.Add(Record(20, 55, UsnReason.DataExtend, "inner.json")));
        Assert.Empty(filter.Changes());
    }

    [Fact]
    public void Blank_names_from_the_unprivileged_read_produce_no_paths()
    {
        var filter = new ScopeFilter(new UInt128(0, Root));
        Assert.True(filter.Add(Record(1, Root, UsnReason.FileCreate, "")));
        Assert.Empty(filter.Changes());
    }
}
