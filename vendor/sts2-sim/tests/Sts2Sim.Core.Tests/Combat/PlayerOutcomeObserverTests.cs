using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.Core.Tests.Combat;

file sealed class FailingOutcomePotion : PotionModel
{
    public override PotionRarity Rarity => PotionRarity.Common;
    public override PotionUsage Usage => PotionUsage.AnyTime;
    public override TargetType TargetType => TargetType.Self;
    protected override Task OnUse(Creature? target) => throw new InvalidOperationException("native effect failed");
}

[Collection("ModelDb")]
public sealed class PlayerOutcomeObserverTests : IDisposable
{
    public PlayerOutcomeObserverTests()
    {
        ModelDb.ResetForTests();
        ModelDb.Init(ContentRegistry.AllTypes.Append(typeof(FailingOutcomePotion)));
    }
    public void Dispose() => ModelDb.ResetForTests();

    [Fact]
    public async Task PotionCommitNotificationsSurviveEffectFailureAndIgnoreRejectedAttempts()
    {
        var run = new RunState("potion-commit-observer", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        var observer = new OutcomeObserver();
        player.OutcomeObserver = observer;
        var potion = (FailingOutcomePotion)ModelDb.Potion<FailingOutcomePotion>().MutableClone();
        Assert.True(await PotionCmd.TryToProcure(potion, player));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PotionCmd.Use(potion, player, null));
        Assert.Equal(new[] { PotionMutationKind.Acquired }, observer.Movements);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PotionCmd.Use(potion, player, player.Creature));
        Assert.Equal("native effect failed", failure.Message);
        Assert.DoesNotContain(potion, player.PotionSlots);
        Assert.Equal(new[] { PotionMutationKind.Acquired, PotionMutationKind.Consumed }, observer.Movements);
        var fire = (FirePotion)ModelDb.Potion<FirePotion>().MutableClone();
        Assert.True(await PotionCmd.TryToProcure(fire, player));
        await PotionCmd.Discard(fire);
        player.RemovePotionInternal(fire); // Removing an absent item does not invent another event.
        Assert.Equal(new[] { PotionMutationKind.Acquired, PotionMutationKind.Consumed,
            PotionMutationKind.Acquired, PotionMutationKind.Discarded }, observer.Movements);
    }

    private sealed class OutcomeObserver : IPlayerOutcomeObserver
    {
        internal readonly List<PotionMutationKind> Movements = [];
        public void HpChanged(Player player, HpMutationKind kind, int before, int after, int maxBefore, int maxAfter) { }
        public void PotionChanged(Player player, string potionId, PotionMutationKind kind) => Movements.Add(kind);
    }
}
