using System.Runtime.CompilerServices;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class CommonResourceObjectiveTests
{
    private static AuditedOutcome Row(int hp = 12, string world = "w0") => new(new()
    {
        TerminalKind = TerminalKind.Win, PlayerAlive = true, HpAtCombatStart = 12, HpAfterSettlement = hp,
        MaxHpStart = 70, MaxHpAfterSettlement = 70, InventorySnapshotsComplete = true,
        InventoryStart = [new("SwiftPotion", 1)], InventoryEnd = [],
        ResourceEvents = [new("consumed", "SwiftPotion", 1, "public_potion_used")], ResourceProvenanceComplete = true,
        PermanentChangesComplete = true, PersistentAssetsAtStartJson = "{\"unchanged\":true}",
        PersistentAssetsAfterSettlementJson = "{\"unchanged\":true}", SettlementComplete = true,
        SettlementProfileId = EmptyPotionContinuationProof.Endpoint, ContinuationPolicyId = "frozen-test-policy-v1"
    }, new(world, "public-root", "combat-start", "no-controller", "independent-worlds-v1", true, "fixed-root-action"));
    private static RelativeOutcomeEvaluation Evaluate(AuditedOutcome a, AuditedOutcome b) => CommonResourceObjective.Evaluate(a, b);
    private static void Masked(AuditedOutcome a, AuditedOutcome b, string reason)
    {
        var result = Evaluate(a, b); Assert.False(result.RelativeValueMask); Assert.Null(result.FirstMinusSecondCost);
        Assert.Contains(reason, result.Reasons); Assert.False(result.FormalLabelsAllowed);
    }

    [Fact]
    public void CommonUnknownInventoryCancelsWithoutAssigningEitherAbsoluteValue()
    {
        var a = Row(7); var b = Row(); var result = Evaluate(a, b);
        Assert.True(result.RelativeValueMask); Assert.Equal(65.0 / 12, result.FirstMinusSecondCost!.Value, 12);
        Assert.False(result.FirstAbsoluteValueMask); Assert.False(result.SecondAbsoluteValueMask);
        Assert.Null(ObjectiveEvaluator.Evaluate(a.Outcome).Cost); Assert.Null(ObjectiveEvaluator.Evaluate(b.Outcome).Cost);
        Assert.False(result.FormalLabelsAllowed); Assert.Equal(CommonResourceObjective.LabelProfile, result.LabelProfile);
        Assert.Equal(new CancelledInventoryTerm("SwiftPotion", 1, true), Assert.Single(result.CancelledInventory));
        // Agreement with the actual absolute formula for multiple admissible prices, never one assumed price.
        foreach (double price in new[] { -100.0, -9, 0, 9, 100 })
        {
            var p = ObjectiveProfile.Candidate with { InventoryValues = new Dictionary<string, ResourceValue>
                { ["SwiftPotion"] = new(price, "test-only algebra check") } };
            Assert.Equal(ObjectiveEvaluator.Evaluate(a.Outcome, p).Cost!.Value - ObjectiveEvaluator.Evaluate(b.Outcome, p).Cost!.Value,
                result.FirstMinusSecondCost.Value, 10);
        }
    }

    [Fact]
    public void LossRetentionMaskCannotBeReplacedByMatchingRawConsumption()
    {
        var retained = Row() with { Outcome = Row().Outcome with { InventoryEnd = [new("SwiftPotion", 1)], ResourceEvents = [] } };
        var death = retained with { Outcome = retained.Outcome with { TerminalKind = TerminalKind.Loss, PlayerAlive = false, HpAfterSettlement = 0 } };
        Masked(retained, death, "loss_aware_inventory_coefficients_differ");
        var spentDeath = Row() with { Outcome = Row().Outcome with { TerminalKind = TerminalKind.Loss, PlayerAlive = false, HpAfterSettlement = 0 } };
        Assert.Equal(-1014.4, Evaluate(Row(), spentDeath).FirstMinusSecondCost!.Value, 10);
    }

    [Fact]
    public void MissingUnknownOrUnreconciledResourceLedgersRemainMaskedWithoutAnIndependentClosure()
    {
        var a = Row();
        foreach (var bad in new[] {
            a.Outcome with { ResourceProvenanceComplete = false },
            a.Outcome with { ResourceEvents = [] },
            a.Outcome with { ResourceEvents = [new("unknown_transform", "SwiftPotion", 1, "unknown")] },
            a.Outcome with { ResourceEvents = [new("consumed", "SwiftPotion", 1, "")] } })
            Masked(a, a with { Outcome = bad }, "complete_reconciled_resource_ledger_or_reviewed_closure_required");
        Masked(a, a with { Outcome = a.Outcome with { InventorySnapshotsComplete = false } }, "inventory_snapshots_incomplete");
        Masked(a, a with { Outcome = a.Outcome with { PermanentChangesComplete = false } }, "permanent_change_ledger_incomplete");
        Masked(a, a with { Outcome = a.Outcome with { TerminalKind = TerminalKind.ComputeTruncated, SettlementComplete = false } },
            "both_valid_actual_settled_outcomes_required");
    }

    [Fact]
    public void SameWorldPublicControllerStartAndEndpointEvidenceIsRequired()
    {
        var a = Row();
        foreach (var audit in new[] { a.Audit with { WorldId = "other" }, a.Audit with { PublicRootKey = "other" },
            a.Audit with { CombatStartAnchor = "other" }, a.Audit with { ControllerContext = "other" },
            a.Audit with { SamplerProfile = "other" }, a.Audit with { SharedWorldForkVerified = false } })
            Masked(a, a with { Audit = audit }, "same_world_root_anchor_controller_and_sampler_required");
        Masked(a, a with { Outcome = a.Outcome with { HpAtCombatStart = 13 } }, "combat_start_facts_mismatch");
        Masked(a, a with { Outcome = a.Outcome with { ContinuationPolicyId = "different" } }, "frozen_continuation_mismatch");
        Masked(a, a with { Outcome = a.Outcome with { SettlementProfileId = "different" } }, "settlement_endpoint_mismatch");
    }

    [Fact]
    public void EqualPermanentCountsNeverHideDifferentFutureOpportunitiesOrUnobservedAssets()
    {
        var a = Row();
        foreach (string kind in new[] { "earned_extra_reward_opportunity:CardReward", "permanent_deck_change", "relic_state_change" })
        {
            var changed = a with { Outcome = a.Outcome with { PermanentChanges = [new(kind, 1, "settled_snapshot")] } };
            Masked(changed, changed, "permanent_or_future_opportunity_changes_not_supported");
        }
        Masked(a, a with { Outcome = a.Outcome with { PersistentAssetsAfterSettlementJson = "{\"different\":true}" } },
            "exact_unchanged_persistent_assets_required");
        Masked(a, a with { Outcome = a.Outcome with { PersistentAssetsAtStartJson = null } }, "exact_unchanged_persistent_assets_required");
        var earned = a with { Outcome = a.Outcome with { EarnedBonus = true } };
        Masked(earned, earned, "bonus_or_finish_opportunity_facts_not_supported");
        Masked(a, a with { Outcome = a.Outcome with { SpecifiedFinishSuccess = true } }, "bonus_or_finish_opportunity_facts_not_supported");
    }

    [Fact]
    public void EqualUnknownTermsDoNotBypassWholeComponentCapsOrUnequalPricedTerms()
    {
        var a = Row();
        var two = a with { Outcome = a.Outcome with { InventoryStart = [new("SwiftPotion", 2)],
            ResourceEvents = [new("consumed", "SwiftPotion", 2, "public")] } };
        Masked(two, two, "common_inventory_component_bound_not_certified");
        var other = a with { Outcome = a.Outcome with { InventoryStart = [new("SwiftPotion", 1), new("FirePotion", 1)],
            ResourceEvents = [new("consumed", "SwiftPotion", 1, "public"), new("consumed", "FirePotion", 1, "public")] } };
        Masked(a, other, "loss_aware_inventory_coefficients_differ");
    }

    [Fact]
    public void FixedNRequiresEveryWorldAndNeverPromotesTheMeanWithoutSupport()
    {
        var first = Enumerable.Range(0, 16).Select(i => Row(7, "w" + i)).ToArray();
        var second = Enumerable.Range(0, 16).Select(i => Row(12, "w" + i)).ToArray();
        var plan = new RelativeEvaluationPlan(first.Select(x => x.Audit.WorldId).ToArray(), "fixed before sampling", true, 10);
        var noSupport = CommonResourceObjective.EvaluateBatch(first, second, plan);
        Assert.Equal(65.0 / 12, noSupport.EmpiricalMeanFirstMinusSecondCost!.Value, 10);
        Assert.Null(noSupport.CostDifferenceInterval); Assert.Null(noSupport.PreferredArm);
        var wide = CommonResourceObjective.EvaluateBatch(first, second, plan, support: new(-1014.4, 1014.4, "full support including losses"));
        Assert.True(wide.CostDifferenceInterval!.Value.Lower < 0); Assert.True(wide.CostDifferenceInterval.Value.Upper > 0);
        Assert.Null(wide.PreferredArm); Assert.False(wide.FormalLabelsAllowed);
        Assert.Throws<ArgumentException>(() => CommonResourceObjective.EvaluateBatch(first[..^1], second, plan));
        Assert.Throws<ArgumentException>(() => CommonResourceObjective.EvaluateBatch(first.Reverse().ToArray(), second, plan));
        Assert.Throws<ArgumentException>(() => CommonResourceObjective.EvaluateBatch(first, second, plan with { IndependentFinalEvaluation = false }));
        var mixedArm = first.ToArray(); mixedArm[0] = mixedArm[0] with { Audit = mixedArm[0].Audit with { RootActionIdentity = "adapted-after-seeing-world" } };
        Assert.Throws<ArgumentException>(() => CommonResourceObjective.EvaluateBatch(mixedArm, second, plan));
        first[0] = first[0] with { Outcome = first[0].Outcome with { ResourceProvenanceComplete = false } };
        var incomplete = CommonResourceObjective.EvaluateBatch(first, second, plan);
        Assert.Equal(16, incomplete.AssignedWorlds); Assert.Equal(15, incomplete.ResolvedWorlds);
        Assert.False(incomplete.RelativeValueMask); Assert.Null(incomplete.EmpiricalMeanFirstMinusSecondCost);
    }

    [Fact]
    public void ArchivedActualNativeEightyOutcomesHaveRelativeEvidenceAndUnchangedAbsoluteMasks()
    {
        var (fixture, root, candidates) = NativeFixture();
        using (fixture)
        {
            var closure = EmptyPotionContinuationProof.CertifyConstructedRoot(root);
            var worldIds = fixture.RootElement.GetProperty("worldIds").Deserialize<string[]>()!;
            Assert.Equal(5, candidates.Length); Assert.All(candidates, rows => Assert.Equal(16, rows.Length));
            Assert.All(candidates.SelectMany(x => x), x => {
                Assert.False(x.Outcome.ResourceProvenanceComplete); Assert.Null(ObjectiveEvaluator.Evaluate(x.Outcome).Cost);
                Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, ObjectiveEvaluator.Evaluate(x.Outcome).Status);
            });
            Assert.False(Evaluate(candidates[0][0], candidates[2][0]).RelativeValueMask);
            var plan = new RelativeEvaluationPlan(worldIds, "original fixed diagnostic plan:" +
                fixture.RootElement.GetProperty("sourcePlanSha256").GetString(), true, 10);
            for (int i = 0; i < 5; i++) for (int j = i + 1; j < 5; j++)
            {
                var result = CommonResourceObjective.EvaluateBatch(candidates[i], candidates[j], plan, closure: closure);
                Assert.True(result.RelativeValueMask); Assert.Equal(16, result.ResolvedWorlds);
                Assert.Null(result.PreferredArm); Assert.False(result.FormalLabelsAllowed);
            }
            var fumesMinusBackflip = CommonResourceObjective.EvaluateBatch(candidates[2], candidates[0], plan,
                closure: closure, support: EmptyPotionContinuationProof.ConstructedCostDifferenceSupport(root));
            Assert.Equal(65.0 / 12, fumesMinusBackflip.EmpiricalMeanFirstMinusSecondCost!.Value, 10);
            Assert.Null(fumesMinusBackflip.PreferredArm); Assert.True(fumesMinusBackflip.CostDifferenceInterval!.Value.Lower < 0);
            Assert.True(fumesMinusBackflip.CostDifferenceInterval.Value.Upper > 0);
            var endMinusBackflip = CommonResourceObjective.EvaluateBatch(candidates[4], candidates[0], plan, closure: closure);
            Assert.Equal(1014.4, endMinusBackflip.EmpiricalMeanFirstMinusSecondCost!.Value, 10);
        }
    }

    [Fact]
    public async Task RealNativeReplayReproducesTheOriginalPairedWorldsWithNoAbsoluteRelabeling()
    {
        var (fixture, root, expected) = NativeFixture();
        using (fixture)
        {
            var f = fixture.RootElement;
            await using var source = await CombatSession.CreateAsync(PublicJson.Read<Scenario>(f.GetProperty("scenario").GetRawText()));
            foreach (var action in f.GetProperty("prefixActions").EnumerateArray())
                await source.StepAsync(PublicJson.Read<PublicAction>(action.GetRawText()));
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(source.Observe()));
            var seeds = f.GetProperty("worldIds").EnumerateArray().Select(x => ulong.Parse(x.GetString()!)).ToArray();
            var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = seeds, MaxDecisions = 200 });
            Assert.Equal(80, result.Costs.WorldsCompleted);
            var closure = EmptyPotionContinuationProof.Certify(source);
            for (int i = 0; i < result.Candidates.Length; i++) for (int w = 0; w < seeds.Length; w++)
            {
                var recorded = result.Candidates[i].Outcomes[w];
                Assert.True(recorded.HpEventDiagnosticsComplete);
                Assert.True(recorded.ResourceProvenanceComplete);
                Assert.Equal(0, recorded.HealingReceived);
                Assert.Equal(0, recorded.OtherHpAdjustment);
                // The archived execution did not observe these diagnostics. Compare
                // its exact facts without retroactively filling unknown old fields.
                var legacyView = recorded with { HealingReceived = null, OtherHpAdjustment = null,
                    HpEventDiagnosticsComplete = false, ResourceProvenanceComplete = false,
                    ResourceEvents = recorded.ResourceEvents.Select(e => e.Kind == "consumed"
                        ? e with { PublicSource = "public_potion_used" } : e).ToArray() };
                Assert.Equal(PublicJson.Serialize(expected[i][w].Outcome), PublicJson.Serialize(legacyView));
                Assert.Null(result.Candidates[i].Evaluation.ExpectedCost);
                var actual = new AuditedOutcome(result.Candidates[i].Outcomes[w], expected[i][w].Audit);
                Assert.True(CommonResourceObjective.Evaluate(actual, expected[0][w], closure: closure).RelativeValueMask);
            }
            Assert.Empty(result.Ranking.Pairs); // Ordinary teacher behavior is untouched.
        }
    }

    [Fact]
    public void ReviewedClosureRejectsUnknownContentInventoryAndInvisibleDrawCards()
    {
        var (fixture, root, _) = NativeFixture();
        using (fixture)
        {
            var o = root.Observation!;
            foreach (var changed in new[] { o with { Potions = ["SwiftPotion"] }, o with { UnidentifiedDrawCount = 1 },
                o with { Relics = ["MeatOnTheBone"] }, o with { Hand = [o.Hand[0] with { Id = "Alchemize" }] },
                o with { Powers = [new("UnknownFutureRewardPower", 1)] }, o with { Enemies = [o.Enemies[0] with { Id = "UnknownEnemy" }] },
                o with { Schema = "nosl.public.v1" }, o with { Pets = null }, o with { Orbs = null },
                o with { Hand = [o.Hand[0] with { Enchantments = null }] }, o with { Hand = [o.Hand[0] with { Details = null }] },
                o with { Hand = [o.Hand[0] with { Keywords = ["UnknownReward"] }] },
                o with { Hand = [o.Hand[0] with { Details = o.Hand[0].Details! with { EnergyModifiers = [new("unknown", 1)] } }] },
                o with { RelicStates = [o.RelicStates![0] with { Details = new Dictionary<string, int>(o.RelicStates[0].Details) { ["unknownReward"] = 1 } }] } })
                Assert.Throws<NotSupportedException>(() => EmptyPotionContinuationProof.CertifyConstructedRoot(root with { Observation = changed }));
        }
    }

    private static (JsonDocument Fixture, DecisionPacket Root, AuditedOutcome[][] Rows) NativeFixture([CallerFilePath] string source = "")
    {
        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "../data/common-resource-relative-native-v1.json")));
        var f = fixture.RootElement; var root = PublicJson.Read<DecisionPacket>(f.GetProperty("publicRoot").GetRawText());
        string key = EmptyPotionContinuationProof.RootKey(root), assets = f.GetProperty("persistentAssetsJson").GetString()!;
        string[] ids = f.GetProperty("worldIds").Deserialize<string[]>()!;
        var rows = f.GetProperty("samples").EnumerateArray().Select(s => {
            var outcome = PublicJson.Read<RolloutOutcome>(s.GetProperty("outcome").GetRawText()) with
                { PersistentAssetsAtStartJson = assets, PersistentAssetsAfterSettlementJson = assets };
            return s.GetProperty("worldIndices").EnumerateArray().Select(i => new AuditedOutcome(outcome,
                new(ids[i.GetInt32()], key, f.GetProperty("anchor").GetString()!, "no-controller",
                    f.GetProperty("samplerProfile").GetString()!, true,
                    PublicJson.Serialize(root.Actions[s.GetProperty("actionIndex").GetInt32()])))).ToArray();
        }).ToArray();
        return (fixture, root, rows);
    }
}
