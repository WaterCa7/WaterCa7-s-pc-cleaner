using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class CheckedSetTests
{
    private sealed record Row(int Pid, string Name);

    /// <summary>A set plus what it reported: PropertyChanged names and how many times Changed fired.</summary>
    private sealed class Spy
    {
        public CheckedSet<int> Set { get; } = new();
        public List<string?> Properties { get; } = new();
        public int Changed { get; private set; }

        public Spy()
        {
            Set.PropertyChanged += (_, e) => Properties.Add(e.PropertyName);
            Set.Changed += (_, _) => Changed++;
        }

        public void Reset()
        {
            Properties.Clear();
            Changed = 0;
        }
    }

    private const string CountName = nameof(CheckedSet<int>.Count);

    [Fact]
    public void NewSet_IsEmpty()
    {
        var set = new CheckedSet<int>();

        Assert.Equal(0, set.Count);
        Assert.False(set.IsChecked(1));
        Assert.Empty(set.Keys);
    }

    [Fact]
    public void Set_ChecksAndUnchecks_AndCounts()
    {
        var set = new CheckedSet<string>();

        set.Set("a", true);
        set.Set("b", true);
        Assert.Equal(2, set.Count);
        Assert.True(set.IsChecked("a"));
        Assert.True(set.IsChecked("b"));
        Assert.False(set.IsChecked("c"));

        set.Set("a", false);
        Assert.Equal(1, set.Count);
        Assert.False(set.IsChecked("a"));
        Assert.Equal(new[] { "b" }, set.Keys);
    }

    [Fact]
    public void Set_RaisesChangedAndCountOnlyWhenTheSetChanges()
    {
        var spy = new Spy();

        spy.Set.Set(1, true);
        Assert.Equal(1, spy.Changed);
        Assert.Equal(new string?[] { CountName }, spy.Properties);

        spy.Set.Set(1, true);   // already checked
        spy.Set.Set(2, false);  // already unchecked
        Assert.Equal(1, spy.Changed);
        Assert.Single(spy.Properties);

        spy.Set.Set(1, false);
        Assert.Equal(2, spy.Changed);
        Assert.Equal(2, spy.Properties.Count);
    }

    [Fact]
    public void Clear_DropsEveryCheck_AndRaisesOnceOnlyIfSomethingWasChecked()
    {
        var spy = new Spy();
        spy.Set.Clear();
        Assert.Equal(0, spy.Changed);
        Assert.Empty(spy.Properties);

        spy.Set.Set(1, true);
        spy.Set.Set(2, true);
        spy.Reset();

        spy.Set.Clear();

        Assert.Equal(0, spy.Set.Count);
        Assert.False(spy.Set.IsChecked(1));
        Assert.Equal(1, spy.Changed);
        Assert.Equal(new string?[] { CountName }, spy.Properties);
    }

    [Fact]
    public void StateOf_IsTrueWhenAllVisibleAreChecked_FalseWhenNone_NullWhenSome()
    {
        var set = new CheckedSet<int>();
        var visible = new[] { 1, 2, 3 };

        Assert.False(set.StateOf(visible));

        set.Set(1, true);
        Assert.Null(set.StateOf(visible));

        set.Set(2, true);
        Assert.Null(set.StateOf(visible));

        set.Set(3, true);
        Assert.True(set.StateOf(visible));
    }

    [Fact]
    public void StateOf_NoVisibleKeys_IsFalse_EvenWithChecksElsewhere()
    {
        var set = new CheckedSet<int>();
        Assert.False(set.StateOf(Array.Empty<int>()));

        set.Set(7, true);
        Assert.False(set.StateOf(Array.Empty<int>()));
    }

    [Fact]
    public void StateOf_LooksOnlyAtTheVisibleKeys()
    {
        var set = new CheckedSet<int>();
        set.Set(1, true);
        set.Set(99, true); // filtered out of the list, still checked

        Assert.True(set.StateOf(new[] { 1 }));
        Assert.False(set.StateOf(new[] { 2, 3 }));
        Assert.Null(set.StateOf(new[] { 1, 2 }));
    }

    [Fact]
    public void StateOf_EnumeratesALazySequenceOnce()
    {
        var set = new CheckedSet<int>();
        set.Set(1, true);
        var passes = 0;
        IEnumerable<int> Visible()
        {
            passes++;
            yield return 1;
            yield return 2;
        }

        Assert.Null(set.StateOf(Visible()));
        Assert.Equal(1, passes);
    }

    [Fact]
    public void SetAll_TouchesOnlyTheVisibleKeys()
    {
        var set = new CheckedSet<int>();
        set.Set(99, true); // checked, but filtered out of the list

        set.SetAll(new[] { 1, 2, 3 }, true);
        Assert.Equal(4, set.Count);
        Assert.True(set.IsChecked(99));
        Assert.True(set.StateOf(new[] { 1, 2, 3 }));

        set.SetAll(new[] { 1, 2, 3 }, false);
        Assert.Equal(new[] { 99 }, set.Keys);
        Assert.False(set.StateOf(new[] { 1, 2, 3 }));
    }

    [Fact]
    public void SetAll_RaisesOnceForTheWholeBatch_AndNotAtAllWhenNothingChanges()
    {
        var spy = new Spy();

        spy.Set.SetAll(new[] { 1, 2, 3 }, true);
        Assert.Equal(1, spy.Changed);
        Assert.Equal(new string?[] { CountName }, spy.Properties);

        spy.Set.SetAll(new[] { 1, 2, 3 }, true);   // nothing new
        spy.Set.SetAll(new[] { 7, 8 }, false);     // nothing to uncheck
        spy.Set.SetAll(Array.Empty<int>(), true);
        Assert.Equal(1, spy.Changed);
        Assert.Single(spy.Properties);
    }

    [Fact]
    public void KeepOnly_ForgetsKeysThatLeftTheList_AndRaisesChanged()
    {
        var spy = new Spy();
        spy.Set.SetAll(new[] { 1, 2, 3, 4 }, true);
        spy.Reset();

        spy.Set.KeepOnly(new[] { 2, 4, 5 }); // 1 and 3 vanished; 5 was never checked and must stay unchecked

        Assert.Equal(2, spy.Set.Count);
        Assert.False(spy.Set.IsChecked(1));
        Assert.True(spy.Set.IsChecked(2));
        Assert.False(spy.Set.IsChecked(3));
        Assert.True(spy.Set.IsChecked(4));
        Assert.False(spy.Set.IsChecked(5));
        Assert.Equal(1, spy.Changed);
        Assert.Equal(new string?[] { CountName }, spy.Properties);
    }

    [Fact]
    public void KeepOnly_DoesNothingWhenEveryCheckedKeyIsStillThere()
    {
        var spy = new Spy();
        spy.Set.SetAll(new[] { 1, 2 }, true);
        spy.Reset();

        spy.Set.KeepOnly(new[] { 1, 2, 3 });

        Assert.Equal(2, spy.Set.Count);
        Assert.Equal(0, spy.Changed);
        Assert.Empty(spy.Properties);
    }

    [Fact]
    public void KeepOnly_WithAnEmptyList_ForgetsEverything()
    {
        var set = new CheckedSet<int>();
        set.SetAll(new[] { 1, 2 }, true);

        set.KeepOnly(Array.Empty<int>());

        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Checks_SurviveARebuildOfTheRows_BecauseTheyAreKeptByKey()
    {
        var set = new CheckedSet<int>();
        var before = new[] { new Row(10, "a.exe"), new Row(20, "b.exe"), new Row(30, "c.exe") };
        set.Set(before[1].Pid, true);

        // a refresh replaces every row object with a new one carrying the same key
        var after = before.Select(r => new Row(r.Pid, r.Name)).Append(new Row(40, "d.exe")).ToArray();
        set.KeepOnly(after.Select(r => r.Pid));

        Assert.False(ReferenceEquals(before[1], after[1]));
        Assert.True(set.IsChecked(after[1].Pid));
        Assert.False(set.IsChecked(after[0].Pid));
        Assert.Null(set.StateOf(after.Select(r => r.Pid)));
    }

    [Fact]
    public void Keys_IsASnapshot_SoTheCallerCanChangeTheSetWhileWalkingIt()
    {
        var set = new CheckedSet<int>();
        set.SetAll(new[] { 1, 2, 3 }, true);

        var keys = set.Keys;
        foreach (var key in keys)
            set.Set(key, false); // would throw if Keys were the live collection

        Assert.Equal(3, keys.Count);
        Assert.Equal(0, set.Count);
    }
}
