using System.Collections.ObjectModel;
using Nosl.Contracts;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class LabelShuffleAbortTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void NoopParticipatingScopePreservesNativePermutationBytesAndCounter(int count)
    {
        var original = new Rng(678912); var observed = new Rng(678912);
        int[] expected = Enumerable.Range(0, count).ToArray(), actual = expected.ToArray();
        original.Shuffle(expected);
        SpyScope? spy = null;
        using (LabelRandomScope.Enter(NativeWord, beginShuffle: (_, _) => spy = new(null)))
            observed.Shuffle(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(PublicJson.Serialize(original.ToSerializable()), PublicJson.Serialize(observed.ToSerializable()));
        Assert.Equal(Math.Max(0, count - 1), observed.Counter);
        Assert.NotNull(spy); Assert.True(spy.Disposed); Assert.Null(spy.NativeError);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)] // Native failure after the final index word, before its last swap.
    public void NativeFailureSurvivesAbortAndCleanupErrorsAndRestoresOuterScope(int throwOnSet, int draws)
    {
        var nativeError = new FormatException("native shuffle list write failed");
        var list = new ThrowingList([0, 1, 2, 3], throwOnSet, nativeError);
        var rng = new Rng(91724); SpyScope? spy = null; int outerWords = 0;
        using (LabelRandomScope.Enter(_ => { outerWords++; return 170; }, beginShuffle: (_, _) =>
        {
            var inner = LabelRandomScope.Enter(_ => ulong.MaxValue);
            return spy = new(inner, throwOnAbort: true, throwOnDispose: true);
        }))
        {
            Assert.Same(nativeError, Assert.Throws<FormatException>(() => rng.Shuffle(list)));
            Assert.Equal(draws, rng.Counter);
            Assert.Same(nativeError, spy!.NativeError); Assert.True(spy.Disposed);
            Assert.Equal(170UL, rng.NextUnsignedLong()); // The failed nested word scope was removed.
            Assert.Equal(1, outerWords);
        }
        var sequential = new Rng(rng.ToSerializable());
        Assert.Equal(sequential.NextUnsignedLong(), rng.NextUnsignedLong()); // The outer scope was removed too.
    }

    private static ulong NativeWord(LabelRandomState state) => new MegaRandom(new SerializableRng
    { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong();

    private sealed class SpyScope(IDisposable? inner, bool throwOnAbort = false, bool throwOnDispose = false)
        : IAbortableLabelShuffleBoundary
    {
        internal Exception? NativeError { get; private set; }
        internal bool Disposed { get; private set; }
        public void Abort(Exception nativeError)
        {
            NativeError = nativeError;
            if (throwOnAbort) throw new InvalidOperationException("secondary abort error");
        }
        public void Dispose()
        {
            if (Disposed) return; Disposed = true; inner?.Dispose();
            if (throwOnDispose) throw new InvalidOperationException("secondary cleanup error");
        }
    }
    private sealed class ThrowingList(IList<int> values, int throwOnSet, Exception error) : Collection<int>(values)
    {
        private int _sets;
        protected override void SetItem(int index, int value)
        {
            if (++_sets == throwOnSet) throw error;
            base.SetItem(index, value);
        }
    }
}
