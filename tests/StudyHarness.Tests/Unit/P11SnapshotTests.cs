using System.Collections.Specialized;
using StudyHarness.Desktop;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class P11SnapshotTests
{
    [Fact]
    public void BatchPublishesOnlyCompleteSnapshotAndUnchangedValuesAreSilent()
    {
        var collection = new SnapshotCollection<int>();
        var observed = new List<int[]>();
        collection.CollectionChanged += (_, e) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
            observed.Add(collection.ToArray());
        };
        collection.Replace(Enumerable.Range(0, 300));
        collection.Replace(Enumerable.Range(0, 300));
        Assert.Single(observed); Assert.Equal(Enumerable.Range(0, 300), observed[0]);
        collection.Replace([7, 9]);
        Assert.Equal(2, observed.Count); Assert.Equal(new[] { 7, 9 }, observed[1]);
    }
    [Fact]
    public void FailedSnapshotEnumerationLeavesExistingCollectionIntact()
    {
        var collection = new SnapshotCollection<int>(); collection.Replace([1, 2]);
        Assert.Throws<InvalidOperationException>(() => collection.Replace(Fail()));
        Assert.Equal(new[] { 1, 2 }, collection);
        static IEnumerable<int> Fail() { yield return 3; throw new InvalidOperationException(); }
    }
}
