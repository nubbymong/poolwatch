using System.Collections;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// C6 (review round 2): ComScope.Each walks a collection and always ends its enumerator, also on an early break (with a
/// real COM collection that enumerator wraps the IEnumVARIANT that is then released). A plain collection stands in for
/// Task Scheduler here: no test touches the real one.
/// </summary>
public class ComScopeTests
{
    private sealed class Collection(params object[] items) : IEnumerable
    {
        public int Disposed;
        public IEnumerator GetEnumerator() => new Enumerator(this, items);

        private sealed class Enumerator(Collection owner, object[] items) : IEnumerator, IDisposable
        {
            private int _i = -1;
            public object Current => items[_i];
            public bool MoveNext() => ++_i < items.Length;
            public void Reset() => _i = -1;
            public void Dispose() => owner.Disposed++;
        }
    }

    [Fact]
    public void EachYieldsEveryItemAndEndsTheEnumerator()
    {
        var c = new Collection("a", "b", "c");
        using var com = new ComScope();
        var got = new List<object>();
        foreach (var x in com.Each(c)) got.Add(x);
        Assert.Equal(["a", "b", "c"], got);
        Assert.Equal(1, c.Disposed);

        foreach (var x in com.Each(c)) if ((string)x == "b") break; // an early break (the actions loop does this)
        Assert.Equal(2, c.Disposed);
    }
}
