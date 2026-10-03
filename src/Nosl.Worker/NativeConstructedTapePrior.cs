using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Public experimental setup, not a captured run or a natural reachability claim.
/// It contains no seed, private state, enemy state, materialized draw order or trace.
/// Named encounters retain their native formation, HP, AI and settlement rules.
/// </summary>
internal sealed record NativeConstructedSetup
{
    // Their pinned AfterObtained implementations call RelicModel.OfferRewards
    // or RewardsCmd.OfferCustom; NeowsBones also reads the current Ancient owner.
    // This source acquires inventory between rooms and does not manufacture an
    // event/merchant/rest owner or discard the native pending reward offers.
    private static readonly HashSet<string> OwnerDependentRelics = new(StringComparer.Ordinal)
    {
        nameof(NeowsBones), nameof(LostCoffer), nameof(SmallCapsule), nameof(Kaleidoscope),
        nameof(CallingBell), nameof(Cauldron), nameof(Orrery), nameof(GlassEye), nameof(ToyBox),
    };

    public string Encounter { get; init; } = "SludgeSpinnerWeak";
    public string[]? Deck { get; init; }
    public string[]? Potions { get; init; }
    public string[]? Relics { get; init; }
    // Base resources before native relic acquisition, not promised combat-entry
    // values. Actual H0/max-HP/gold and asset ledgers are captured after all pickup
    // effects. A missing HP/max-HP value retains native new-Silent startup values.
    public int? Hp { get; init; }
    public int? MaxHp { get; init; }
    public int? Gold { get; init; }

    internal NativeConstructedSetup Freeze()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = EncounterCoverage.Find(Encounter);
        if (encounter.RequiresEventContext)
            throw new NotSupportedException("constructed_forced_owner_required: " + Encounter
                + " must retain its native event owner; this declared source currently accepts ordinary named encounters only");
        if (Deck is { Length: 0 or > 512 } || Potions is { Length: > 16 } || Relics is { Length: > 64 }
            || Hp is <= 0 || MaxHp is <= 0 || Gold is < 0 || Hp is int hp && MaxHp is int max && hp > max)
            throw new ArgumentException("Invalid declared constructed inventory or player resources");
        foreach (string text in Deck ?? [])
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Constructed card ID is required");
            string id = text.TrimEnd('+');
            var card = CardCoverage.Get(id);
            int upgrades = text.Length - id.Length;
            if (card is null || card.MultiplayerOnly || upgrades > card.MaxUpgradeLevel)
                throw new ArgumentException("Unsupported constructed single-player card/upgrade: " + text);
        }
        foreach (string id in Potions ?? []) _ = Model<PotionModel>(id);
        foreach (string id in Relics ?? [])
        {
            var relic = Model<RelicModel>(id);
            if (OwnerDependentRelics.Contains(id))
                throw new NotSupportedException("constructed_relic_owner_required: " + id
                    + " requires a declared native event, rest-site or merchant acquisition owner");
            if (relic is SeaGlass)
                throw new NotSupportedException("constructed_relic_setup_required: SeaGlass requires a declared foreign-character choice");
        }
        int initialMax = MaxHp ?? ModelDb.Character<Silent>().StartingHp;
        if ((Hp ?? ModelDb.Character<Silent>().StartingHp) > initialMax)
            throw new ArgumentException("Constructed current HP must not exceed the declared initial maximum");
        return this with { Deck = Deck?.ToArray(), Potions = Potions?.ToArray(), Relics = Relics?.ToArray() };
    }

    private static T Model<T>(string id) where T : AbstractModel =>
        (T)(ModelDb.All<T>().SingleOrDefault(model => model.GetType().Name == id)
            ?? throw new ArgumentException("Unknown constructed " + typeof(T).Name + ": " + id)).MutableClone();

    internal async Task ApplyAsync(RunState run)
    {
        Player player = run.Players.Single();
        if (Deck is not null)
        {
            foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
            foreach (string text in Deck)
            {
                string id = text.TrimEnd('+');
                var card = Model<CardModel>(id);
                card.AssignOwner(player);
                for (int upgrade = 0; upgrade < text.Length - id.Length; upgrade++)
                {
                    if (!card.IsUpgradable) throw new ArgumentException("Illegal constructed upgrade: " + text);
                    card.Upgrade();
                }
                player.Deck.AddInternal(card);
            }
        }
        if (MaxHp is int max) player.Creature.SetMaxHpInternal(max);
        if (Hp is int hp)
        {
            if (hp > player.Creature.MaxHp) throw new ArgumentException("Constructed HP exceeds player maximum");
            player.Creature.SetCurrentHpInternal(hp);
        }
        if (Gold is int gold) player.Gold = gold;
        // This order is part of the declared setup law. Native acquisition hooks
        // and any outside-combat selections remain executed and publicly recorded.
        if ((Potions?.Length ?? 0) > player.PotionSlots.Count)
            throw new ArgumentException("Constructed potions exceed initial available slots");
        foreach (string id in Potions ?? [])
        {
            var potion = Model<PotionModel>(id);
            potion.AssignOwner(player); player.AddPotionInternal(potion);
        }
        foreach (string id in Relics ?? [])
        {
            var relic = Model<RelicModel>(id);
            if (relic is DustyTome tome) tome.SetupForPlayer(player);
            await RelicCmd.Obtain(relic, player);
        }
    }
}

/// <summary>
/// A fresh declared-setup prior with one native combat and a fixed public stopping
/// rule. All stochastic setup, startup and continuation effects use the explicit
/// Map/Rewards/state tape law. Actual source seeds are never part of this object.
/// </summary>
internal sealed record NativeConstructedTapePrior
{
    internal const string Version = "nosl.constructed-native-map-rewards-state-tape-prior.v1";
    internal const string SourceDrawDomain = "nosl-constructed-tape-source-draw-v1";
    public string SchemaVersion { get; init; } = Version;
    public NativeConstructedSetup Setup { get; init; } = new();
    public string SourcePolicyId { get; init; } = PublicContinuationPolicies.ReviewedId;
    public int SourceDecisionHorizon { get; init; } = 160;
    public string RootSelection { get; init; } = "opening";
    public int DecisionIndex { get; init; }
    public string PrimitiveLaw => "independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1";
    public string PrimitiveImplementation => "sha256-address-expansion-with-explicit-conditioned-overrides-v1";
    public string RootLaw => "one-declared-native-combat-fixed-public-stopping-rule-with-absence-v1";
    public string SetupLaw => "fixed-acts-base-deck-hp-maxhp-gold-potions-then-native-relic-acquisition-v1";

    [JsonIgnore] internal NativeRunExecutionOptions Execution => new(MaxFloors: 1,
        SourceDecisionHorizon: SourceDecisionHorizon, SourcePolicyId: SourcePolicyId,
        OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
        PublicContextProfile: PublicRunContext.Version,
        PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
        PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1);

    internal NativeConstructedTapePrior Freeze()
    {
        if (SchemaVersion != Version || Setup is null || SourceDecisionHorizon is < 1 or > 100000
            || DecisionIndex is < 0 or > 100000 || RootSelection is not
                ("opening" or "decision_index" or "first_player_turn_2" or "first_player_turn_3" or "first_pending_choice")
            || RootSelection != "decision_index" && DecisionIndex != 0)
            throw new ArgumentException("Invalid constructed tape prior or public root selection");
        _ = PublicContinuationPolicies.Create(SourcePolicyId);
        _ = Execution.EmitsPublicEvidence;
        return this with { Setup = Setup.Freeze() };
    }

    internal string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(this)))).ToLowerInvariant();
    internal NativeTapeRecipe Draw(Rng random) => new(random.NextUnsignedLong(), random.NextUnsignedLong(),
        random.NextUnsignedLong(), 0, DecisionIndex);

    internal bool Selects(DecisionPacket packet, int localDecision) => RootSelection switch
    {
        "opening" => localDecision == 0,
        "decision_index" => localDecision == DecisionIndex,
        "first_player_turn_2" => packet.Observation!.Turn >= 2,
        "first_player_turn_3" => packet.Observation!.Turn >= 3,
        "first_pending_choice" => packet.Status == "card_choice",
        _ => throw new InvalidOperationException("Unvalidated constructed root selection"),
    };
}
