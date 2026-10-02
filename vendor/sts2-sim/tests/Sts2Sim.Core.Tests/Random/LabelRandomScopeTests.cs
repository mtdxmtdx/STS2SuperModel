namespace Sts2Sim.Core.Tests.Random;

using Sts2Sim.Core.Random;
using Sts2Sim.Core.Saves;

public class LabelRandomScopeTests
{
    [Theory]
    [InlineData(0uL, 0, -10, int.MinValue, 0)]
    [InlineData(0x8000000000000000uL, 5, 0, -1, 1073741823)]
    [InlineData(ulong.MaxValue, 9, 9, int.MaxValue - 1, int.MaxValue - 1)]
    public void IntegerConversions_ConsumeOneProvidedWord_AndAdvanceNativeState(
        ulong word, int bounded, int range, int wideRange, int defaultBound)
    {
        var rng = new Rng(42);
        var mega = new MegaRandom(42);
        int supplied = 0;
        using (LabelRandomScope.Enter(_ => { supplied++; return word; }))
        {
            Check(() => rng.NextInt(10), bounded);
            Check(() => rng.NextInt(-10, 10), range);
            Check(() => rng.NextInt(int.MinValue, int.MaxValue), wideRange);
            Check(() => rng.NextInt(), defaultBound);
            Check(() => mega.Next(10), bounded);
            Check(() => mega.Next(-10, 10), range);
            Check(() => mega.Next(int.MinValue, int.MaxValue), wideRange);
        }

        var expected = new Rng(42);
        for (int i = 0; i < 4; i++) expected.NextUnsignedLong();
        AssertSameState(expected.ToSerializable(), rng.ToSerializable());
        var expectedMega = new MegaRandom(42);
        for (int i = 0; i < 3; i++) expectedMega.NextULong();
        Assert.Equal(expectedMega.NextULong(), mega.NextULong());

        void Check(Func<int> draw, int value)
        {
            int before = supplied;
            Assert.Equal(value, draw());
            Assert.Equal(before + 1, supplied);
        }
    }

    [Fact]
    public void AllPrimitiveConversions_UseProvidedWord()
    {
        var rng = new Rng(1);
        var mega = new MegaRandom(1);
        int supplied = 0;
        using (LabelRandomScope.Enter(_ => { supplied++; return 0x8000000000000000uL; }))
        {
            Check(mega.NextDouble, 0.5);
            Check(mega.NextFloat, 0.5f);
            Check(mega.NextInt, 1073741824);
            Check(mega.NextUInt, 0u);
            Check(mega.NextULong, 0x8000000000000000uL);
            Check(mega.NextBool, true);
            Check(rng.NextBool, false);
            Check(rng.NextDouble, 0.5);
            Check(() => rng.NextDouble(-1, 1), 0.0);
            Check(() => rng.NextFloat(), 0.5f);
            Check(() => rng.NextFloat(-1, 1), 0.0f);
            Check(() => rng.NextUnsignedInt(10), 5u);
            Check(() => rng.NextUnsignedInt(10, 20), 15u);
            Check(rng.NextUnsignedLong, 0x8000000000000000uL);
            Check(() => rng.NextUnsignedLong(10), 5uL);
            Check(() => rng.NextUnsignedLong(10, 20), 15uL);
        }
        Assert.Equal(10, rng.Counter);

        void Check<T>(Func<T> draw, T value)
        {
            int before = supplied;
            Assert.Equal(value, draw());
            Assert.Equal(before + 1, supplied);
        }
    }

    [Fact]
    public void PreDrawKeys_PreserveClonesAndRecreatedSeeds_WithoutSamplerRecursion()
    {
        var sampler = new MegaRandom(123);
        var expectedSampler = new MegaRandom(123);
        var original = new Rng(42);
        var cells = new Dictionary<LabelRandomState, ulong>();
        var keys = new List<LabelRandomState>();
        SerializableRng initial = original.ToSerializable();

        using (LabelRandomScope.Enter(state =>
        {
            keys.Add(state);
            if (!cells.TryGetValue(state, out ulong word))
                cells.Add(state, word = sampler.NextULong());
            return word;
        }))
        {
            ulong first = original.NextUnsignedLong();
            Assert.Equal(first, new Rng(42).NextUnsignedLong());
            Rng clone = original.CloneExact();
            Assert.Equal(original.NextUnsignedLong(), clone.NextUnsignedLong());
        }

        Assert.Equal(new LabelRandomState(initial.state0, initial.state1, initial.state2, initial.state3), keys[0]);
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[2], keys[3]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.Equal(2, cells.Count);
        Assert.Equal(expectedSampler.NextULong(), cells[keys[0]]);
        Assert.Equal(expectedSampler.NextULong(), cells[keys[2]]);
        Assert.Equal(expectedSampler.NextULong(), sampler.NextULong());
    }

    [Fact]
    public async Task NestedAndConcurrentScopes_RestoreAcrossAwaitAndExceptions()
    {
        var parent = new MegaRandom(5);
        var native = new MegaRandom(5);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (LabelRandomScope.Enter(_ => 11))
        {
            Task first = DrawInChild(22, firstEntered, secondEntered);
            Task second = DrawInChild(33, secondEntered, firstEntered);
            await Task.WhenAll(first, second);
            Assert.Equal(11uL, parent.NextULong());

            Assert.Throws<InvalidOperationException>(() =>
            {
                using var nested = LabelRandomScope.Enter(_ => throw new InvalidOperationException("sample failed"));
                parent.NextULong();
            });
            Assert.Equal(11uL, parent.NextULong());
        }

        // Failed callback substitution still advances the native generator once.
        for (int i = 0; i < 3; i++) native.NextULong();
        Assert.Equal(native.NextULong(), parent.NextULong());

        async Task DrawInChild(ulong word, TaskCompletionSource entered, TaskCompletionSource other)
        {
            var child = new MegaRandom(word);
            using (LabelRandomScope.Enter(_ => word))
            {
                entered.SetResult();
                await other.Task;
                Assert.Equal(word, child.NextULong());
                using (LabelRandomScope.Enter(_ => word + 1))
                {
                    await Task.Yield();
                    Assert.Equal(word + 1, child.NextULong());
                }
                Assert.Equal(word, child.NextULong());
            }
            Assert.Equal(11uL, child.NextULong());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void ForcedShuffle_KeepsNativeSwapsCountersObserversAndState(int count)
    {
        object[] items = Enumerable.Range(0, count).Select(_ => new object()).ToArray();
        var list = items.ToList();
        var observed = new List<(int Counter, string Operation, string Value)>();
        var rng = Rng.CreateObserved(7, (counter, operation, value) => observed.Add((counter, operation, value)));
        var native = new Rng(7);
        var words = new Queue<ulong>(count == 4 ? [0, ulong.MaxValue, 0] : []);
        int shuffles = 0;
        int outerDraws = 0;

        using (LabelRandomScope.Enter(_ => { outerDraws++; return 17; }, (algorithmRng, originalItems) =>
        {
            shuffles++;
            Assert.Same(rng, algorithmRng);
            AssertSameState(native.ToSerializable(), algorithmRng.ToSerializable());
            Assert.Equal(items.Length, originalItems.Count);
            for (int i = 0; i < items.Length; i++) Assert.Same(items[i], originalItems[i]);
            return LabelRandomScope.Enter(_ => words.Dequeue());
        }))
        {
            rng.Shuffle(list);
            Assert.Equal(0, outerDraws);
            Assert.Equal(17uL, new MegaRandom(0).NextULong());
        }

        Assert.Equal(1, shuffles);
        Assert.Empty(words);
        int draws = Math.Max(0, count - 1);
        Assert.Equal(draws, rng.Counter);
        Assert.Equal(draws, observed.Count);
        for (int i = 0; i < draws; i++) native.NextUnsignedLong();
        AssertSameState(native.ToSerializable(), rng.ToSerializable());
        if (count == 4)
        {
            Assert.Equal(new[] { items[1], items[3], items[2], items[0] }, list);
            Assert.Equal(new[]
            {
                (1, "NextInt(maxExclusive=4)", "0"),
                (2, "NextInt(maxExclusive=3)", "2"),
                (3, "NextInt(maxExclusive=2)", "0")
            }, observed);
        }
        else Assert.Equal(items, list);
    }

    private static void AssertSameState(SerializableRng expected, SerializableRng actual)
    {
        Assert.Equal(expected.counter, actual.counter);
        Assert.Equal(expected.state0, actual.state0);
        Assert.Equal(expected.state1, actual.state1);
        Assert.Equal(expected.state2, actual.state2);
        Assert.Equal(expected.state3, actual.state3);
    }
}
