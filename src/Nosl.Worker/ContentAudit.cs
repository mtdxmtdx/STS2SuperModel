using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

public static class ContentAudit
{
    public const string UpstreamCommit="5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0";
    public static void Write(string path)
    {
        ModelDb.Init(ContentRegistry.AllTypes);
        var entries=ContentRegistry.AllTypes.Select(t=>new
        {
            id=t.Name,category=ModelDb.GetCategory(t),
            source=$"https://github.com/iRyougi/sts2-sim/tree/{UpstreamCommit}/src/Sts2Sim.Core",
            ruleAuthority="PINNED_UPSTREAM_TRUSTED",
            status=typeof(CardModel).IsAssignableFrom(t)&&CardCoverage.Get(t.Name)?.MultiplayerOnly==true?"OutOfScope":"ImplementedUnverified",
            adapterAdmission=typeof(CardModel).IsAssignableFrom(t)?"REGISTRY_DRIVEN_SOLO_CARD_EXECUTION":
                typeof(PotionModel).IsAssignableFrom(t)||typeof(RelicModel).IsAssignableFrom(t)||typeof(MonsterModel).IsAssignableFrom(t)?"REGISTRY_DRIVEN_EXECUTION":"NATIVE_ENGINE_DEPENDENCY",
            targetReachability="CONSERVATIVE_REGISTRY_SUPERSET_NOT_A_PROVEN_NATURAL_RUN_DISTRIBUTION",
            clientDifferential="NOT_REQUIRED_BY_USER_TRUST_CHOICE_NOT_INDEPENDENTLY_VERIFIED",
        }).ToArray();
        var value=new
        {
            schema="nosl.coverage.v2",upstream=new {repository="iRyougi/sts2-sim",commit=UpstreamCommit,rules="0.111.0 / 41cef1ea / 222455745",license="MIT"},
            scope="Silent A10 single-player full pinned-upstream reachable combat content; direct rule-engine reuse",
            fullTargetComplete=false,readyForTraining=false,
            counts=new {registry=entries.Length,cards=CardCoverage.Entries.Count,soloCardSuperset=CardCoverage.SupportedCards.Length,
                officialSilentSoloCards=CardCoverage.SilentCards.Length,potions=EnvironmentCoverage.PotionIds.Length,relics=EnvironmentCoverage.RelicIds.Length,
                monsters=EncounterCoverage.AllMonsters.Count,naturalEncounters=EncounterCoverage.AllEncounters.Count(x=>!x.RequiresEventContext),forcedEventOwners=EncounterCoverage.AllForcedEvents.Count},
            entries,cards=CardCoverage.Entries,items=EnvironmentCoverage.Entries,monsters=EncounterCoverage.AllMonsters,
            encounters=EncounterCoverage.AllEncounters.Select(e=>new {e.Name,e.Act,e.ActIndex,roomType=e.RoomType.ToString(),e.RequiresEventContext,e.Source}),
            forcedEvents=EncounterCoverage.AllForcedEvents,
            adapterCapabilities=new[]{"Native rules/legality/generation/upgrades, no per-card rule reimplementation", "Native natural multi-enemy/elite/boss encounter factories", "Revisioned play/use/discard-potion/end-turn/selection tokens", "Public hand/generated/discard/exhaust choices and canonically unordered draw/deck reveals", "Public mutable card/relic/counter/gold/orb/pet snapshots", "Independent concrete native replay for projection-sensitive automatic rewards", "Bounded whole-setup rejection posterior conditioned on every public decision; explicit inconclusive exhaustion", "Five declared forced-event option paths retain native owner, return, reward and automatic settlement contexts", "Versioned reviewed native carry-in profiles cover stable entry, owned conditional card choices and untouched generation-potion entry; unsupported histories stay explicit"},
            remaining=new[]{
                "Registered execution is distinct from passing adapter tests; see actual verification report for tested fixtures",
                "Forced-event fixtures execute their legal native option path and retain owning event context; natural-run event posterior certification remains separate",
                "Fresh constructed Scenario and reviewed native-entry imports have distinct priors; arbitrary natural carry-in outside certified public entry/state profiles remains unsupported",
                "General replay posterior may exhaust its declared proposal budget; never reinterpret that as a game loss or drop it from an outcome denominator",
                "In-place exchangeable sampling has a bounded proven mechanism set; mixed prior provenance is rejected",
                "Unrevealed random generated draw-card identities require an explicit information-reveal model before public export",
                "Ordered choices exceeding100000 explicit candidates are reported as an unsupported action-space capability, never silently truncated",
                "Same-process parallel rule execution remains unverified; workers are sequential",
            },
            note="User accepts current upstream rule semantics. This does not authorize hidden-future policy inputs or turn registry counts into NOSL validation claims. No independent original-game fidelity gate is imposed.",
        };
        File.WriteAllText(path,PublicJson.Serialize(value));
    }
}
