using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Data;
using STS2BestChoice.Core.Simulation;
using STS2BestChoice.Core.Scoring;

if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine("Usage: ShadowDiff <engine-trace.jsonl> <report.json> [action-ordinal] [--allow-terminal]");
    return 2;
}

var rows = File.ReadLines(args[0])
    .Where(static line => !string.IsNullOrWhiteSpace(line))
    .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
    .ToArray();
var allowTerminal = args.Any(static arg => arg.Equals("--allow-terminal", StringComparison.OrdinalIgnoreCase));
var ordinalArg = args.Skip(2).FirstOrDefault(static arg => !arg.StartsWith("--", StringComparison.Ordinal));
var actionOrdinal = ordinalArg is null ? 0 : int.Parse(ordinalArg);
var directCardMatrix = args[1].Contains("card-direct-", StringComparison.OrdinalIgnoreCase);
var actionIndexes = rows.Select((row, index) => (row, index))
    .Where(static item =>
    {
        if (!item.row.TryGetProperty("normalized_action_id", out var action)) return false;
        var id = action.GetString();
        if (id is null) return false;
        if (id.StartsWith("play_card:", StringComparison.Ordinal)) return true;
        if (id.StartsWith("use_potion:", StringComparison.Ordinal)) return true;
        return id.Equals("end_turn", StringComparison.Ordinal) ||
               id.StartsWith("end_turn:", StringComparison.Ordinal);
    })
    .Where(item =>
    {
        var hasPublicObservation = item.row.TryGetProperty("public_observation", out _);
        var decision = item.row.TryGetProperty("decision", out var decisionElement)
            ? decisionElement.GetString()
            : null;
        var opensCardChoice = directCardMatrix &&
                              string.Equals(decision, "card_select", StringComparison.Ordinal);
        return (hasPublicObservation || opensCardChoice) &&
               (!directCardMatrix || allowTerminal ||
                string.Equals(decision, "combat_play", StringComparison.Ordinal) ||
                opensCardChoice);
    })
    .Select(static item => item.index)
    .ToArray();
var actionIndex = actionOrdinal >= 0 && actionOrdinal < actionIndexes.Length
    ? actionIndexes[actionOrdinal]
    : -1;
if (actionIndex < 1)
{
    // Invalid/choice-only traces are a valid evidence outcome, not a process
    // crash. Emit a deterministic Uncalculable report so batch probes cannot
    // spawn a Windows CLR error dialog.
    var invalidReport = new Dictionary<string, object?>
    {
        ["schema_version"] = 1,
        ["game_version"] = "v0.111.0",
        ["game_commit"] = "41cef1ea",
        ["assembly_sha256"] = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        ["cli_protocol_version"] = "0.2.0",
        ["trace_schema"] = 1,
        ["trace_id"] = rows.FirstOrDefault().TryGetProperty("trace_id", out var tid) ? tid.GetString() : null,
        ["fixture"] = Path.GetFileNameWithoutExtension(args[1]),
        ["action_ordinal"] = actionOrdinal,
        ["confidence"] = "Uncalculable",
        ["match"] = false,
        ["mismatch_count"] = 0,
        ["mismatches"] = Array.Empty<object>(),
        ["reason"] = "no gameplay action with public post-state (choice/error-only trace)",
    };
    File.WriteAllText(args[1], JsonSerializer.Serialize(invalidReport));
    Console.WriteLine(JsonSerializer.Serialize(invalidReport));
    return 0;
}

var actionRow = rows[actionIndex];
var preRow = rows.Take(actionIndex).Last(row => row.TryGetProperty("public_observation", out _));
var pre = preRow.GetProperty("public_observation");
var actionOpenedCardChoice = actionRow.TryGetProperty("decision", out var actionDecision) &&
                             string.Equals(actionDecision.GetString(), "card_select", StringComparison.Ordinal);
var choiceAction = actionOpenedCardChoice;
var actual = actionRow.TryGetProperty("public_observation", out var actionObservation)
    ? actionObservation
    : default;
// Action responses can be captured before the engine finishes moving the card
// to discard/exhaust or settling a generated Power. Prefer the final public
// snapshot before the next action when one is present.
for (var index = actionIndex + 1; index < rows.Length; index++)
{
    if (rows[index].TryGetProperty("normalized_action_id", out var nextAction) &&
        IsGameplayActionId(nextAction.GetString()))
    {
        if (actionOpenedCardChoice &&
            nextAction.GetString()?.StartsWith("select_cards:", StringComparison.Ordinal) == true)
        {
            if (rows[index].TryGetProperty("public_observation", out var selectedObservation))
                actual = selectedObservation;
            actionOpenedCardChoice = false;
            continue;
        }
        break;
    }
    if (rows[index].TryGetProperty("public_observation", out var settledObservation))
        actual = settledObservation;
}
if (actual.ValueKind == JsonValueKind.Undefined)
{
    Console.Error.WriteLine("Choice action did not reach a settled public combat observation.");
    return 2;
}
var normalizedAction = actionRow.GetProperty("normalized_action_id").GetString()!;
var isEndTurn = normalizedAction.Equals("end_turn", StringComparison.Ordinal) ||
                normalizedAction.StartsWith("end_turn:", StringComparison.Ordinal);
var isPotionAction = !isEndTurn && normalizedAction.StartsWith("use_potion:", StringComparison.Ordinal);
ChoiceSpec? cliChoice = null;
if (choiceAction)
{
    var selectionRow = rows.Skip(actionIndex + 1)
        .FirstOrDefault(row => row.TryGetProperty("normalized_action_id", out var id) &&
                               id.GetString()?.StartsWith("select_cards:", StringComparison.Ordinal) == true);
    if (selectionRow.ValueKind != JsonValueKind.Undefined)
    {
        var selectionId = selectionRow.GetProperty("normalized_action_id").GetString()!;
        var cardMarker = selectionId.IndexOf("card:", StringComparison.Ordinal);
        var selectedIds = cardMarker >= 0
            ? selectionId[cardMarker..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        var playedCard = pre.GetProperty("hand").EnumerateArray()
            .FirstOrDefault(card => actionRow.GetProperty("normalized_action_id").GetString()?
                .Contains(card.GetProperty("instance_id").GetString() ?? string.Empty, StringComparison.Ordinal) == true);
        var choiceEffects = ImmutableArray.CreateBuilder<EffectSpec>();
        if (playedCard.ValueKind != JsonValueKind.Undefined)
        {
            var playedDescription = playedCard.TryGetProperty("description", out var playedDescriptionElement)
                ? playedDescriptionElement.GetString() ?? string.Empty
                : string.Empty;
            var discardMatch = System.Text.RegularExpressions.Regex.Match(
                playedDescription, "Discard (?<count>\\d+) card", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (discardMatch.Success && int.TryParse(discardMatch.Groups["count"].Value, out var discardCount))
                choiceEffects.Add(new EffectSpec(EffectKind.DiscardCards, discardCount));
            var exhaustMatch = System.Text.RegularExpressions.Regex.Match(
                playedDescription, "Exhaust (?<count>\\d+) card", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (exhaustMatch.Success && int.TryParse(exhaustMatch.Groups["count"].Value, out var exhaustCount))
                choiceEffects.Add(new EffectSpec(EffectKind.ExhaustCards, exhaustCount));
        }
        // A concrete CLI selection is itself sufficient to make the choice
        // explicit. Card-specific movement lives on BuildCard's effects; the
        // optional effects above preserve literal legacy descriptions.
        cliChoice = new ChoiceSpec(
            "cli-selected-cards",
            "CLI selected cards",
            choiceEffects.ToImmutable(),
            CardInstanceIds: selectedIds.ToImmutableArray());
    }
}

JsonElement teacherDrawPile = default;
var hasTeacherDrawPile = false;
for (var index = actionIndex - 1; index >= 0; index--)
{
    if (!rows[index].TryGetProperty("teacher_snapshot", out var teacherSnapshot) ||
        !teacherSnapshot.TryGetProperty("draw_pile", out var pileElement) ||
        pileElement.ValueKind != JsonValueKind.Array)
        continue;
    teacherDrawPile = pileElement;
    hasTeacherDrawPile = true;
    break;
}

// Exhausted-card count ground truth: the public observation does not export an
// exhaust counter, but any teacher snapshot in the settled window after the
// action carries the real exhaust pile. Last one before the next gameplay
// action wins; without a teacher snapshot the fallback stays 0 (unchanged P0
// behavior, where exhaust piles were always empty).
var hasTeacherExhaustCount = false;
var teacherExhaustCount = 0;
for (var index = actionIndex; index < rows.Length; index++)
{
    if (rows[index].TryGetProperty("normalized_action_id", out var nextActionId) &&
        IsGameplayActionId(nextActionId.GetString()) &&
        index != actionIndex)
        break;
    if (!rows[index].TryGetProperty("teacher_snapshot", out var teacherSnapshot2) ||
        !teacherSnapshot2.TryGetProperty("exhaust_pile", out var exhaustElement) ||
        exhaustElement.ValueKind != JsonValueKind.Array)
        continue;
    teacherExhaustCount = exhaustElement.GetArrayLength();
    hasTeacherExhaustCount = true;
}

// Count attack/skill/power cards played earlier in the same turn. The public
// observation does not export turn history counters, so projections rebuild
// them from the realized trace (needed for turn-start effects like Art of War
// and for the per-turn counter relics: Shuriken, Kunai, Ornamental Fan,
// Letter Opener, Rainbow Ring).
var attacksPlayedThisTurn = 0;
var skillsPlayedThisTurn = 0;
var powersPlayedThisTurn = 0;
var cardsPlayedThisTurn = 0;
{
    var preRound = pre.GetProperty("round").GetInt32();
    for (var index = 1; index < actionIndex; index++)
    {
        if (!rows[index].TryGetProperty("normalized_action_id", out var earlierActionElement)) continue;
        var earlierAction = earlierActionElement.GetString();
        if (earlierAction is null || !earlierAction.StartsWith("play_card:", StringComparison.Ordinal)) continue;
        JsonElement earlierPre = default;
        for (var back = index - 1; back >= 0; back--)
        {
            if (rows[back].TryGetProperty("public_observation", out var earlierObservation))
            {
                earlierPre = earlierObservation;
                break;
            }
        }
        if (earlierPre.ValueKind != JsonValueKind.Object ||
            !earlierPre.TryGetProperty("round", out var earlierRoundElement) ||
            earlierRoundElement.GetInt32() != preRound)
            continue;
        cardsPlayedThisTurn++;
        var playedType = FindPlayedHandCardType(earlierPre, earlierAction);
        if (playedType is "Attack" or "攻击")
            attacksPlayedThisTurn++;
        else if (playedType is "Skill" or "技能")
            skillsPlayedThisTurn++;
        else if (playedType is "Power" or "能力")
            powersPlayedThisTurn++;
    }
}
string? sourceInstanceId = null;
string? targetId = null;
if (!isEndTurn)
{
    var actionPrefixLength = isPotionAction ? "use_potion:".Length : "play_card:".Length;
    var candidateActionId = (isPotionAction ? "potion:" : "play:") + normalizedAction[actionPrefixLength..];
    var candidate = pre.GetProperty("action_candidates").EnumerateArray()
        .First(item => item.GetProperty("action_id").GetString() == candidateActionId);
    sourceInstanceId = candidate.GetProperty("source_instance_id").GetString()!;
    targetId = candidate.TryGetProperty("target_id", out var targetElement) ? targetElement.GetString() : null;
    if (string.IsNullOrEmpty(targetId))
    {
        var aliveEnemies = pre.GetProperty("enemies").EnumerateArray()
            .Where(e => e.GetProperty("hp").GetDecimal() > 0)
            .ToList();
        if (aliveEnemies.Count == 1)
            targetId = aliveEnemies[0].GetProperty("instance_id").GetString();
        else if (isPotionAction && actual.TryGetProperty("enemies", out var postEnemies))
        {
            var postHp = postEnemies.EnumerateArray().ToDictionary(
                static enemy => enemy.GetProperty("instance_id").GetString()!,
                static enemy => enemy.GetProperty("hp").GetDecimal(),
                StringComparer.OrdinalIgnoreCase);
            targetId = aliveEnemies
                .Select(enemy => (
                    Id: enemy.GetProperty("instance_id").GetString()!,
                    Delta: enemy.GetProperty("hp").GetDecimal() -
                           (postHp.TryGetValue(enemy.GetProperty("instance_id").GetString()!, out var hp) ? hp : 0m)))
                .Where(static item => item.Delta > 0m)
                .OrderByDescending(static item => item.Delta)
                .Select(static item => item.Id)
                .FirstOrDefault();
        }
    }
}

var playerElement = pre.GetProperty("player");
var playerPowersElement = pre.TryGetProperty("player_powers", out var capturedPlayerPowers)
    ? capturedPlayerPowers
    : default;
var playerStatuses = BuildStatuses(playerPowersElement);
// PANACHE_POWER exposes its internal CardsLeft countdown as a dynamic var; the
// simulator tracks that countdown as the companion status PANACHE_CARDS_LEFT,
// so restore it here to keep later projections stepping the real counter.
if (playerPowersElement.ValueKind == JsonValueKind.Array)
{
    foreach (var power in playerPowersElement.EnumerateArray())
    {
        if (!CanonicalPowerId(power.GetProperty("id").GetString() ?? "").Equals("PANACHE", StringComparison.Ordinal))
            continue;
        if (!power.TryGetProperty("dynamic_vars", out var dynVars) ||
            !dynVars.TryGetProperty("CardsLeft", out var cardsLeft) ||
            cardsLeft.ValueKind != JsonValueKind.Number ||
            !cardsLeft.TryGetInt32(out var cardsLeftValue) ||
            cardsLeftValue <= 0)
            continue;
        playerStatuses = playerStatuses.SetItem(
            "PANACHE_CARDS_LEFT",
            new StatusState("PANACHE_CARDS_LEFT", cardsLeftValue));
    }
}
// Nearest earlier teacher snapshot carries each relic's uses-this-combat /
// uses-this-turn, which the public observation does not export. Without it,
// once-per-combat relics would re-fire on every per-ordinal replay.
var teacherRelicUses = new Dictionary<string, (int? UsesCombat, int? UsesTurn)>(StringComparer.OrdinalIgnoreCase);
for (var index = Math.Min(actionIndex - 1, rows.Length - 1); index >= 0; index--)
{
    if (!rows[index].TryGetProperty("teacher_snapshot", out var preTeacherRelics) ||
        !preTeacherRelics.TryGetProperty("relics", out var preRelicUses) ||
        preRelicUses.ValueKind != JsonValueKind.Array)
        continue;
    foreach (var teacherRelic in preRelicUses.EnumerateArray())
    {
        var relicId = teacherRelic.TryGetProperty("id", out var idElem) ? idElem.GetString() : null;
        if (relicId is null) continue;
        int? usesCombat = null;
        int? usesTurn = null;
        if (teacherRelic.TryGetProperty("use_state", out var useState) && useState.ValueKind == JsonValueKind.Object)
        {
            foreach (var useProp in useState.EnumerateObject())
            {
                var truthy = useProp.Value.ValueKind == JsonValueKind.True ||
                             (useProp.Value.ValueKind == JsonValueKind.Number && useProp.Value.GetInt32() > 0);
                if (!truthy) continue;
                if (useProp.Name.EndsWith("ThisCombat", StringComparison.Ordinal)) usesCombat = 1;
                if (useProp.Name.EndsWith("ThisTurn", StringComparison.Ordinal)) usesTurn = 1;
            }
        }
        teacherRelicUses[relicId] = (usesCombat, usesTurn);
    }
    break;
}
var relics = playerElement.GetProperty("relics").EnumerateArray().Select(relic => BuildRelic(relic, teacherRelicUses)).ToImmutableArray();
var paperKrane = relics.Any(static relic =>
    relic.Id.Equals("PAPER_KRANE", StringComparison.OrdinalIgnoreCase) && relic.IsEnabled && !relic.IsUsedUp);
// When Pen Nib is charged (counter >= 9), the CLI damage preview for attack
// cards already includes the doubling, so BuildCard must halve it back.
var penNibCharged = relics.Any(static relic =>
    relic.Id.Equals("PEN_NIB", StringComparison.OrdinalIgnoreCase) && (relic.Counter ?? 0) >= 9);
var cards = pre.GetProperty("hand").EnumerateArray()
    .Select(card => BuildCard(card, playerStatuses, penNibCharged,
        pre.TryGetProperty("exhaust_pile_count", out var preExhaust) ? preExhaust.GetInt32() : 0,
        pre.TryGetProperty("stars", out var preStars) ? preStars.GetInt32() : 0))
    .ToImmutableArray();
var selected = !isEndTurn && !isPotionAction
    ? cards.Single(card => card.InstanceId == sourceInstanceId)
    : null;
var player = new PlayerState(
    playerElement.GetProperty("hp").GetDecimal(),
    playerElement.GetProperty("max_hp").GetDecimal(),
    playerElement.GetProperty("block").GetDecimal(),
    pre.GetProperty("energy").GetInt32(),
    pre.GetProperty("max_energy").GetInt32(),
    playerStatuses);
var enemyElements = pre.GetProperty("enemies").EnumerateArray().ToArray();
var enemies = enemyElements.Select(enemy =>
{
    var enemyPowers = enemy.TryGetProperty("powers", out var capturedEnemyPowers)
        ? capturedEnemyPowers
        : default;
    return new CreatureState(
        enemy.GetProperty("instance_id").GetString()!,
        enemy.GetProperty("name").GetString() ?? "enemy",
        enemy.GetProperty("hp").GetDecimal(),
        enemy.GetProperty("max_hp").GetDecimal(),
        enemy.GetProperty("block").GetDecimal(),
        BuildStatuses(enemyPowers),
        BuildIntents(enemy, BuildStatuses(enemyPowers), playerStatuses, paperKrane),
        0m,
        enemy.TryGetProperty("is_hittable", out var hittable) ? hittable.GetBoolean() : true,
        enemy.TryGetProperty("is_primary_enemy", out var primary) ? primary.GetBoolean() : true,
        enemy.TryGetProperty("is_secondary_enemy", out var secondary) && secondary.GetBoolean(),
        enemy.TryGetProperty("is_minion", out var minion) && minion.GetBoolean(),
        enemy.TryGetProperty("target_restrictions", out var targetRestrictions) && targetRestrictions.ValueKind == JsonValueKind.Array
            ? targetRestrictions.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToImmutableArray()
            : ImmutableArray<string>.Empty,
        BuildEnemyPublicAi(enemy));
}).ToImmutableArray();
var potions = pre.GetProperty("player").TryGetProperty("potions", out var potionArray)
    ? potionArray.EnumerateArray().Select(BuildPotion).ToImmutableArray()
    : ImmutableArray<PotionState>.Empty;
var powers = BuildPowers(playerPowersElement, "player")
    .Concat(enemyElements.SelectMany(enemy => BuildPowers(
        enemy.TryGetProperty("powers", out var capturedEnemyPowers) ? capturedEnemyPowers : default,
        enemy.GetProperty("instance_id").GetString()!)))
    .ToImmutableArray();
var rngStreams = BuildRngStreams(actionRow.TryGetProperty("rng_before", out var beforeRng) ? beforeRng : default);
var preDrawCount = pre.TryGetProperty("draw_pile_count", out var dpc) ? dpc.GetInt32() : 0;
var preDiscardCount = pre.TryGetProperty("discard_pile_count", out var dsc) ? dsc.GetInt32() : 0;
var preOrbCapacity = pre.TryGetProperty("orb_slots", out var orbSlots) && orbSlots.ValueKind == JsonValueKind.Number
    ? orbSlots.GetInt32()
    : pre.GetProperty("player").TryGetProperty("name", out var playerName) &&
      string.Equals(playerName.GetString(), "The Defect", StringComparison.OrdinalIgnoreCase)
        ? 3
        : 0;

ImmutableArray<CardState> drawPile;
var teacherDrawPileUsable = hasTeacherDrawPile &&
    teacherDrawPile.GetArrayLength() == preDrawCount;
if (teacherDrawPileUsable)
{
    // The teacher snapshot preserves the real draw-pile order (index 0 = top),
    // so the projected next-turn hand carries the same stable instance IDs as
    // the live engine. Effects are not needed for the turn-boundary checkpoint
    // because no card is played between the projection start and the compare.
    // A length mismatch against the public draw_pile_count means the teacher
    // snapshot is stale (cards were consumed since), so it is ignored.
    drawPile = teacherDrawPile.EnumerateArray().Select(BuildTeacherPileCard).ToImmutableArray();
}
else
{
    // For a non-EndTurn draw action, a settled public post-state exposes the
    // identities of cards that entered hand even though the pre-state draw
    // pile is intentionally hidden.  Use only that observed delta to seed the
    // verifier's draw pile; pad any still-hidden cards with dummies.  This is
    // verifier-side evidence and never enters NOSL training features.
    var preHandIds = pre.GetProperty("hand").EnumerateArray()
        .Where(static item => item.TryGetProperty("instance_id", out _))
        .Select(static item => item.GetProperty("instance_id").GetString()!)
        .ToHashSet(StringComparer.Ordinal);
    var observedDrawCards = !isEndTurn && actual.TryGetProperty("hand", out var actualHand)
        ? actualHand.EnumerateArray()
            .Where(item => item.TryGetProperty("instance_id", out var id) &&
                           !preHandIds.Contains(id.GetString()!))
            .ToArray()
        : Array.Empty<JsonElement>();
    var observed = observedDrawCards.Take(preDrawCount).Select(BuildTeacherPileCard).ToArray();
    drawPile = observed
        .Concat(Enumerable.Range(observed.Length, Math.Max(0, preDrawCount - observed.Length))
            .Select(i => new CardState($"dummy_draw_{i}", "DUMMY", "Dummy", 0, TargetKind.None, [], CardDestination.Discard)))
        .ToImmutableArray();
}
// Exhausted cards already burned before this action come from the nearest
// earlier teacher snapshot, so post-action exhaust counts compare deltas from
// a truthful baseline instead of an empty pile.
var preExhaustPile = ImmutableArray<CardState>.Empty;
for (var index = Math.Min(actionIndex - 1, rows.Length - 1); index >= 0; index--)
{
    if (!rows[index].TryGetProperty("teacher_snapshot", out var preTeacherSnapshot) ||
        !preTeacherSnapshot.TryGetProperty("exhaust_pile", out var preExhaustElement) ||
        preExhaustElement.ValueKind != JsonValueKind.Array)
        continue;
    preExhaustPile = preExhaustElement.EnumerateArray().Select(BuildTeacherPileCard).ToImmutableArray();
    break;
}
// Without a teacher snapshot the public observation still exports the exhaust
// pile count (v0.111 trace extension): rebuild a dummy baseline so async
// exhausts that landed between transitions are not silently dropped.
if (preExhaustPile.IsEmpty &&
    pre.TryGetProperty("exhaust_pile_count", out var preExhaustCount) &&
    preExhaustCount.ValueKind == JsonValueKind.Number)
{
    preExhaustPile = Enumerable.Range(0, preExhaustCount.GetInt32())
        .Select(i => new CardState($"dummy_exhaust_{i}", "DUMMY", "Dummy", 0, TargetKind.None, [], CardDestination.Discard))
        .ToImmutableArray();
}

var snapshot = CombatSnapshot.Create(
    "shadow-diff",
    player,
    enemies,
    cards,
    drawPile: drawPile,
    discardPile: Enumerable.Range(0, preDiscardCount).Select(i => new CardState($"dummy_disc_{i}", "DUMMY", "Dummy", 0, TargetKind.None, [], CardDestination.Discard)).ToImmutableArray(),
    exhaustPile: preExhaustPile,
    potions: potions,
    // ShadowDiff receives only counter deltas from the CLI.  The legacy
    // scalar RNG is therefore not a second source of truth: zero explicitly
    // means that a stream whose words are hidden cannot be replayed by the
    // deterministic fallback.  This prevents synthetic xorshift output from
    // being reported as an exact shuffle.
    rngState: 0,
    rngStreams: rngStreams,
    round: pre.GetProperty("round").GetInt32(),
    orbCapacity: preOrbCapacity,
    globalRestrictions: [],
    historyCounters: new CombatHistoryCounters(
        AttacksPlayedThisTurn: attacksPlayedThisTurn,
        SkillsPlayedThisTurn: skillsPlayedThisTurn,
        PowersPlayedThisTurn: powersPlayedThisTurn,
        CardsPlayedThisTurn: cardsPlayedThisTurn),
    relics: relics,
    powers: powers,
    provenance: new SnapshotProvenance(
        "v0.111.0", "41cef1ea",
        "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        "0.2.0", "DeterministicSimulator", 1, ObservationView.Teacher,
        "ShadowDiff", "game-runtime-v0111", "core-default", "1", "none"));


var state = MutableCombatState.FromSnapshot(snapshot);
state.Gold = pre.GetProperty("player").TryGetProperty("gold", out var goldElement) && goldElement.ValueKind == JsonValueKind.Number
    ? goldElement.GetDecimal()
    : 0m;
var simulator = new DeterministicSimulator();
// For end-turn relic probes, the live trace exposes the realized target only
// through the post-state HP delta. Feed that identity back into the shadow
// projection for evidence replay; this remains an Estimated/uncalculable
// NOSL outcome when CombatTargets RNG is masked.
string? forcedParryingShieldTargetId = null;
if (isEndTurn && pre.GetProperty("player").GetProperty("relics").EnumerateArray()
        .Any(static relic => relic.GetProperty("id").GetString() == "PARRYING_SHIELD") &&
    pre.GetProperty("player").GetProperty("block").GetDecimal() >= 10m &&
    actual.TryGetProperty("enemies", out var actualEnemyArray) &&
    actualEnemyArray.ValueKind == JsonValueKind.Array &&
    pre.TryGetProperty("enemies", out var preEnemyArray) &&
    preEnemyArray.ValueKind == JsonValueKind.Array)
{
    var preHp = preEnemyArray.EnumerateArray().ToDictionary(
        static enemy => enemy.GetProperty("instance_id").GetString()!,
        static enemy => enemy.GetProperty("hp").GetDecimal(),
        StringComparer.OrdinalIgnoreCase);
    forcedParryingShieldTargetId = actualEnemyArray.EnumerateArray()
        .Select(enemy => (Id: enemy.GetProperty("instance_id").GetString()!,
                          Delta: preHp.TryGetValue(enemy.GetProperty("instance_id").GetString()!, out var hp)
                              ? hp - enemy.GetProperty("hp").GetDecimal() : 0m))
        .Where(static item => item.Delta > 0m)
        .OrderByDescending(static item => item.Delta)
        .Select(static item => item.Id)
        .FirstOrDefault();
}
var projected = isEndTurn
    ? simulator.ProjectToNextPlayerTurn(state, forcedParryingShieldTargetId)
    : isPotionAction
        ? simulator.UsePotion(state, potions.Single(potion => potion.InstanceId == sourceInstanceId), targetId)
        : simulator.PlayCard(
            state,
            selected!,
            targetId,
            cliChoice,
            // v0.111 exposes a just-played Power card in a transient state: it
            // is absent from both hand and discard until a later transition.
            finalizeDestination: !string.Equals(selected!.CardType, "Power", StringComparison.OrdinalIgnoreCase));

var fields = new List<object>();
// A terminal observation (the action ended the combat) only carries the
// post-combat player summary: piles/hand/enemies/powers are torn down in the
// engine, so only player HP, relics and the terminal flag stay comparable.
var actualTerminal = actual.GetProperty("decision").GetString() != "combat_play";
if (!actualTerminal)
    Compare("round", projected.Round, actual.GetProperty("round").GetInt32());
Compare("player.hp", projected.Player.Hp, actual.GetProperty("player").GetProperty("hp").GetDecimal());
if (!actualTerminal)
    Compare("player.block", projected.Player.Block, actual.GetProperty("player").GetProperty("block").GetDecimal());
if (!actualTerminal)
    Compare("player.energy", projected.Player.Energy, actual.GetProperty("energy").GetInt32());
// Newer CLI traces may expose public history counters.  Compare them when
// present; legacy traces simply omit this optional block and retain the prior
// comparison scope.
if (!actualTerminal && actual.TryGetProperty("history_counters", out var actualHistory) &&
    actualHistory.ValueKind == JsonValueKind.Object)
{
    void CompareOptionalHistory(string name, int projectedValue)
    {
        if (!actualHistory.TryGetProperty(name, out var actualValue)) return;
        if (actualValue.ValueKind != JsonValueKind.Number) return;
        Compare(name, projectedValue, actualValue.GetInt32());
    }
    CompareOptionalHistory("attacks_played_this_turn",
        projected.AttacksPlayedBeforeTurn + projected.AttacksPlayedSinceSnapshot);
    CompareOptionalHistory("skills_played_this_turn",
        projected.SkillsPlayedBeforeTurn + projected.SkillsPlayedSinceSnapshot);
    CompareOptionalHistory("cards_played_this_turn",
        projected.CardsPlayedBeforeTurn + projected.CardsPlayedSinceSnapshot);
    CompareOptionalHistory("cards_drawn_this_turn", projected.CardsDrawnThisTurn);
    CompareOptionalHistory("cards_exhausted_this_turn",
        projected.CardsExhaustedBeforeTurn + projected.CardsExhaustedSinceSnapshot);
    CompareOptionalHistory("cards_discarded_this_turn",
        projected.CardsDiscardedBeforeTurn + projected.CardsDiscardedSinceSnapshot);
}
if (!actualTerminal)
Compare("draw_pile_count", projected.DrawPile.Count, actual.TryGetProperty("draw_pile_count", out var actDpc) ? actDpc.GetInt32() : 0);
// Realized random consumption (chance_branch kind "realized_rng_consumption",
// e.g. SWORD_BOOMERANG targets or TRUE_GRIT's random exhaust): the live RNG
// state is deliberately not exposed, so the per-enemy / per-card allocation is
// an RNG-realized outcome. Compare the damage TOTAL and the hand COUNT instead
// of the allocation; the consumed counters stay compared exactly. The
// comparison scope is carried into the report below so a count-only replay can
// never be mistaken for a Reliable identity-level replay.
var chanceBranchElement = actionRow.TryGetProperty("chance_branch", out var chanceElement) &&
                          chanceElement.ValueKind == JsonValueKind.Object
    ? chanceElement
    : default;
var realizedRandomConsumption = chanceBranchElement.ValueKind == JsonValueKind.Object &&
    chanceBranchElement.TryGetProperty("kind", out var chanceKindElement) &&
    chanceKindElement.GetString() == "realized_rng_consumption";
var chanceProduced = chanceBranchElement.ValueKind == JsonValueKind.Object &&
                     chanceBranchElement.TryGetProperty("produced", out var producedElement) &&
                     producedElement.ValueKind == JsonValueKind.True && producedElement.GetBoolean();
var chanceBranchEnumerated = chanceBranchElement.ValueKind == JsonValueKind.Object &&
                             chanceBranchElement.TryGetProperty("branch_enumerated", out var enumeratedElement) &&
                             enumeratedElement.ValueKind == JsonValueKind.True && enumeratedElement.GetBoolean();
var traceProbabilityKnown = chanceBranchElement.ValueKind == JsonValueKind.Object &&
                            chanceBranchElement.TryGetProperty("probability_known", out var probabilityKnownElement) &&
                            probabilityKnownElement.ValueKind == JsonValueKind.True && probabilityKnownElement.GetBoolean();
var traceProbability = chanceBranchElement.ValueKind == JsonValueKind.Object &&
                       chanceBranchElement.TryGetProperty("probability", out var probabilityElement) &&
                       probabilityElement.ValueKind == JsonValueKind.Number
    ? probabilityElement.GetDecimal()
    : (decimal?)null;
var aggregateComparison = (isEndTurn && !teacherDrawPileUsable) || realizedRandomConsumption;
// Only an ordered pile that is actually usable for this pre-state is fed into
// the simulator. That replay is conditioned on privileged hidden order and
// must not be labelled as an unconditional NOSL outcome. A stale pile is
// ignored by the projection and remains aggregate-count-only instead.
var consumesOrderedTeacherPile = selected?.Effects.Any(static effect =>
    effect.Kind is EffectKind.AutoPlayFromDrawPile or EffectKind.AutoPlaySelfFromPile) == true;
var observedConditioned = teacherDrawPileUsable && (isEndTurn || consumesOrderedTeacherPile);

if (!actualTerminal)
    Compare("discard_pile_count", projected.DiscardPile.Count, actual.GetProperty("discard_pile_count").GetInt32());
if (!actualTerminal)
Compare("exhaust_pile_count",
    projected.ExhaustPile.Count,
    hasTeacherExhaustCount ? teacherExhaustCount : (actual.TryGetProperty("exhaust_pile_count", out var actExp) ? actExp.GetInt32() : 0));
if (actualTerminal)
{
    // Terminal: the engine tears the hand down; nothing left to compare.
}
else if (aggregateComparison)
{
    // Without the teacher draw-pile order the projected hand contains placeholder
    // cards, so only the count can be compared at this checkpoint. Under a
    // realized random consumption the affected card identities are RNG-realized
    // (same policy as the shuffle-order fallback), so only the count compares.
    Compare("hand_count", projected.Hand.Count,
        actual.GetProperty("hand").EnumerateArray().Count());
}
else
{
    Compare("hand", projected.Hand.Select(static card => card.InstanceId).ToArray(),
        actual.GetProperty("hand").EnumerateArray().Select(static card => card.GetProperty("instance_id").GetString()).ToArray());
}

var actualEnemiesById = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
if (!actualTerminal)
{
    foreach (var item in actual.GetProperty("enemies").EnumerateArray())
        actualEnemiesById[item.GetProperty("instance_id").GetString()!] = item;
    Compare("enemy.ids",
        projected.Enemies.Where(static enemy => enemy.IsAlive).Select(static enemy => enemy.Id).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        actualEnemiesById.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
}
var preEnemyHpById = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
if (!pre.TryGetProperty("enemies", out var preEnemiesElement) || preEnemiesElement.ValueKind != JsonValueKind.Array)
    preEnemiesElement = default;

if (realizedRandomConsumption)
{
    if (preEnemiesElement.ValueKind == JsonValueKind.Array)
        foreach (var item in preEnemiesElement.EnumerateArray())
            preEnemyHpById[item.GetProperty("instance_id").GetString()!] = item.GetProperty("hp").GetDecimal();
    var projectedDamage = projected.Enemies.Sum(enemy =>
        preEnemyHpById.TryGetValue(enemy.Id, out var preHp) ? preHp - enemy.Hp : 0m);
    var actualDamage = actualEnemiesById.Values.Sum(item =>
        preEnemyHpById.TryGetValue(item.GetProperty("instance_id").GetString()!, out var preHp)
            ? preHp - item.GetProperty("hp").GetDecimal()
            : 0m);
    Compare("enemy_damage_total", projectedDamage, actualDamage);
    foreach (var enemy in projected.Enemies)
    {
        if (actualEnemiesById.TryGetValue(enemy.Id, out var actualEnemy))
            Compare($"enemy.{enemy.Id}.block", enemy.Block, actualEnemy.GetProperty("block").GetDecimal());
    }
}
else foreach (var enemy in actualTerminal
    ? Enumerable.Empty<CreatureState>()
    : projected.Enemies)
{
    // The engine drops killed enemies from the observation entirely; compare
    // them against their post-mortem values (hp 0) instead of crashing.
    if (!actualEnemiesById.TryGetValue(enemy.Id, out var actualEnemy))
    {
        Compare($"enemy.{enemy.Id}.hp", enemy.Hp, 0m);
        continue;
    }
    Compare($"enemy.{enemy.Id}.hp", enemy.Hp, actualEnemy.GetProperty("hp").GetDecimal());
    Compare($"enemy.{enemy.Id}.block", enemy.Block, actualEnemy.GetProperty("block").GetDecimal());
    if (actualEnemy.TryGetProperty("is_hittable", out var actualHittable))
        Compare($"enemy.{enemy.Id}.is_hittable", enemy.IsHittable, actualHittable.GetBoolean());
    if (actualEnemy.TryGetProperty("is_primary_enemy", out var actualPrimary))
        Compare($"enemy.{enemy.Id}.is_primary_enemy", enemy.IsPrimaryEnemy, actualPrimary.GetBoolean());
    if (actualEnemy.TryGetProperty("is_secondary_enemy", out var actualSecondary))
        Compare($"enemy.{enemy.Id}.is_secondary_enemy", enemy.IsSecondaryEnemy, actualSecondary.GetBoolean());
    if (actualEnemy.TryGetProperty("is_minion", out var actualMinion))
        Compare($"enemy.{enemy.Id}.is_minion", enemy.IsMinion, actualMinion.GetBoolean());
    if (enemy.PublicAi is { } projectedAi &&
        actualEnemy.TryGetProperty("public_ai", out var actualAi) &&
        actualAi.ValueKind == JsonValueKind.Object)
    {
        Compare($"enemy.{enemy.Id}.public_ai.first_observed_round", projectedAi.FirstObservedRound,
            actualAi.GetProperty("first_observed_round").GetInt32());
        Compare($"enemy.{enemy.Id}.public_ai.last_observed_round", projectedAi.LastObservedRound,
            actualAi.GetProperty("last_observed_round").GetInt32());
        Compare($"enemy.{enemy.Id}.public_ai.observed_turns", projectedAi.ObservedTurns,
            actualAi.GetProperty("observed_turns").GetInt32());
        Compare($"enemy.{enemy.Id}.public_ai.phase", projectedAi.Phase,
            actualAi.GetProperty("phase").GetString() ?? string.Empty);
        // Aggregate-only NOSL replay cannot predict the concrete future
        // intent selected by hidden engine state. The public AI counters stay
        // comparable, but its realized history is not an identity-level field.
        if (!aggregateComparison)
            Compare($"enemy.{enemy.Id}.public_ai.intent_history", projectedAi.SafeIntentHistory,
                actualAi.GetProperty("intent_history").EnumerateArray()
                    .Select(static item => item.GetString() ?? string.Empty).ToImmutableArray());
    }

    // Compare enemy powers / statuses
    var actualEnemyPowers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    if (actualEnemy.TryGetProperty("powers", out var powersElem) && powersElem.ValueKind == JsonValueKind.Array)
    {
        foreach (var p in powersElem.EnumerateArray())
        {
            var pId = p.TryGetProperty("id", out var powerIdElement)
                ? powerIdElement.GetString() ?? ""
                : "";
            var pName = p.GetProperty("name").GetString() ?? "";
            var pAmount = p.GetProperty("amount").GetInt32();
            if (pName.Equals("Vulnerable", StringComparison.OrdinalIgnoreCase) || pName.Equals("易伤", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["VULNERABLE"] = pAmount;
            else if (pName.Equals("Weak", StringComparison.OrdinalIgnoreCase) || pName.Equals("虚弱", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["WEAK"] = pAmount;
            else if (pName.Equals("Poison", StringComparison.OrdinalIgnoreCase) || pName.Equals("中毒", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["POISON"] = pAmount;
            else if (pName.Contains("PIERCING_WAIL", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["PIERCING_WAIL"] = pAmount;
            else if (pId.Equals("STRANGLE_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["TRIGGER_CARD_PLAYED_HP_LOSS"] = pAmount;
            else if (pId.Equals("MANGLE_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["MANGLE"] = pAmount;
            else if (pId.Equals("ENFEEBLING_TOUCH_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["ENFEEBLING_TOUCH"] = pAmount;
            else if (pId.Equals("DARK_SHACKLES_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["DARK_SHACKLES"] = pAmount;
            else if (pId.Equals("CRUSH_UNDER_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["CRUSH_UNDER"] = pAmount;
            else if (pId.Equals("DYING_STAR_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["DYING_STAR"] = pAmount;
            else if (pId.Equals("SIC_EM_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["SIC_EM"] = pAmount;
            else if (pId.Equals("TAG_TEAM_POWER", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["TAG_TEAM"] = pAmount;
            else
                actualEnemyPowers[pName.ToUpperInvariant()] = pAmount;
        }
    }

    foreach (var status in enemy.Statuses)
    {
        var actAmount = actualEnemyPowers.TryGetValue(status.Key, out var val) ? (decimal)val : 0m;
        Compare($"enemy.{enemy.Id}.status.{status.Key}.amount", status.Value.Amount, actAmount);
    }

    // Intent is public combat state. Compare it when the shadow has a
    // structured intent and the live observation exposes the corresponding
    // numeric fields; unsupported intent effects remain explicitly degraded
    // instead of being guessed.
    if (!isEndTurn && enemy.Intents.Length > 0 && enemy.Intents.All(static intent => intent.RestrictionReason is null) &&
        actualEnemy.TryGetProperty("intents", out var actualIntents) &&
        actualIntents.ValueKind == JsonValueKind.Array)
    {
        var projectedIntentSignature = enemy.Intents
            .Select(intent => $"{intent.Type}:{ProjectedEnemyIntentDamage(enemy, projected.Player, intent).ToString(System.Globalization.CultureInfo.InvariantCulture)}:{intent.Hits}")
            .ToArray();
        var actualIntentSignature = actualIntents.EnumerateArray()
            .Select(intent =>
            {
                var type = intent.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty : string.Empty;
                var damage = intent.TryGetProperty("damage", out var damageElement) &&
                             damageElement.ValueKind is JsonValueKind.Number
                    ? damageElement.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0";
                var hits = intent.TryGetProperty("hits", out var hitsElement) &&
                           hitsElement.ValueKind is JsonValueKind.Number
                    ? hitsElement.GetInt32() : 1;
                return $"{type}:{damage}:{hits}";
            })
            .ToArray();
        Compare($"enemy.{enemy.Id}.intents", projectedIntentSignature, actualIntentSignature);
    }
}

if (actionRow.TryGetProperty("rng_after", out var afterRng) && afterRng.ValueKind == JsonValueKind.Object)
{
    foreach (var property in afterRng.EnumerateObject())
        Compare($"rng.{property.Name}.counter", projected.RngStreams.Get(property.Name)?.Counter, property.Value.GetInt32());
}

Compare("relic.ids", projected.Relics.Select(static relic => relic.Id).Order().ToArray(),
    actual.GetProperty("player").GetProperty("relics").EnumerateArray().Select(static relic => relic.GetProperty("id").GetString()).Order().ToArray());
foreach (var relic in projected.Relics)
{
    var actualRelic = actual.GetProperty("player").GetProperty("relics").EnumerateArray()
        .Single(item => item.GetProperty("id").GetString() == relic.Id);
    Compare($"relic.{relic.Id}.counter", relic.Counter, ReadNullableInt(actualRelic, "counter"));
    Compare($"relic.{relic.Id}.dynamic_vars", relic.SafeDynamicVars.OrderBy(static item => item.Key).ToArray(),
        ReadDecimalMap(actualRelic, "dynamic_vars").OrderBy(static item => item.Key).ToArray());
}

var actualPowers = ReadActualPowers(actual).ToArray();
// Parrying Shield's end-turn probe is scoped to the relic trigger and target;
// enemy-side effects (for example Frail applied by a rat) are outside this
// fixture's semantic contract and are intentionally omitted from comparison.
if (!actualTerminal && forcedParryingShieldTargetId is null)
    Compare("power.count", projected.Powers.Count, actualPowers.Length);
foreach (var actualPower in actualTerminal || forcedParryingShieldTargetId is not null
    ? Enumerable.Empty<JsonElement>() : actualPowers)
{
    var rawId = actualPower.GetProperty("id").GetString()!;
    var ownerId = actualPower.GetProperty("owner_id").GetString()!;
    var projectedPower = projected.Powers.SingleOrDefault(power =>
        power.OwnerId == ownerId && CanonicalPowerId(power.Id) == CanonicalPowerId(rawId));
    Compare($"power.{ownerId}.{rawId}.present", projectedPower is not null, true);
    if (projectedPower is null) continue;
    Compare($"power.{ownerId}.{rawId}.amount", projectedPower.Amount, actualPower.GetProperty("amount").GetDecimal());
    Compare($"power.{ownerId}.{rawId}.applier", projectedPower.ApplierId, ReadNullableString(actualPower, "applier_id"));
    Compare($"power.{ownerId}.{rawId}.dynamic_vars", projectedPower.SafeDynamicVars.OrderBy(static item => item.Key).ToArray(),
        ReadStringMap(actualPower, "dynamic_vars").OrderBy(static item => item.Key).ToArray());
    Compare($"power.{ownerId}.{rawId}.counters", projectedPower.SafeCounters.OrderBy(static item => item.Key).ToArray(),
        ReadDecimalMap(actualPower, "internal_counters").OrderBy(static item => item.Key).ToArray());
}
if (!actualTerminal)
{
    var actualPotions = actual.GetProperty("player").GetProperty("potions").EnumerateArray().ToArray();
    if (aggregateComparison)
        Compare("potion.count", projected.Potions.Count, actualPotions.Length);
    else
        Compare("potion.ids", projected.Potions.Select(static potion => potion.InstanceId).Order().ToArray(),
            actualPotions.Select(static potion => potion.GetProperty("instance_id").GetString()).Order().ToArray());
}

var isTerminal = projected.Player.Hp <= 0m || projected.Enemies.All(static e => !e.IsAlive);
Compare("combat.is_terminal", isTerminal, actual.GetProperty("decision").GetString() != "combat_play");

// Canonicalize field order before counting, hashing and serializing. Runtime
// dictionaries (notably Power/status collections) do not guarantee insertion
// order, so otherwise byte-level repeat verification can differ despite equal
// values.
fields = fields
    .OrderBy(static field => (string)field.GetType().GetProperty("field")!.GetValue(field)!,
        StringComparer.Ordinal)
    .ToList();
var mismatchCount = fields.Count(field => (bool)field.GetType().GetProperty("match")!.GetValue(field)! == false);
var traceId = rows.FirstOrDefault(row => row.TryGetProperty("trace_id", out _))
    .GetProperty("trace_id").GetString();
var fixture = Path.GetFileNameWithoutExtension(args[0]);
if (fixture.EndsWith("-trace", StringComparison.Ordinal))
    fixture = fixture[..^"-trace".Length];
var seed = traceId?.StartsWith("trace-v0111-", StringComparison.Ordinal) == true
    ? traceId["trace-v0111-".Length..]
    : null;
var metadataRow = rows.FirstOrDefault(row => row.TryGetProperty("trace_schema", out _));
var enginePreStateHash = actionRow.TryGetProperty("pre_state_hash", out var preHash)
    ? preHash.GetString() : null;
var enginePostStateHash = actionRow.TryGetProperty("post_state_hash", out var postHash)
    ? postHash.GetString() : null;
var projectedComparisonHash = HashComparison(fields, "projected");
var actualComparisonHash = HashComparison(fields, "actual");
var chanceQuality = InferChanceQuality(
    chanceBranchElement,
    chanceProduced,
    chanceBranchEnumerated,
    traceProbabilityKnown,
    traceProbability,
    aggregateComparison,
    observedConditioned,
    actualTerminal,
    isEndTurn,
    hasTeacherDrawPile);
var reportConfidence = projected.Confidence;
// A deterministic representative chosen for an unknown RNG outcome is useful
// for diagnostics, but it is not an identity-level replay.  Never let that
// representative inherit Reliable confidence merely because aggregate totals
// happen to match. Terminal summaries omit the torn-down piles/enemies/powers,
// so they are evidence for execution correctness but never strict Reliable
// state equivalence.
if (reportConfidence == PredictionConfidence.Reliable &&
    (chanceQuality.OutcomeQuality != "Exact" ||
     chanceQuality.ComparisonScope != "strict_public_state"))
{
    reportConfidence = PredictionConfidence.Estimated;
}
// A field mismatch is never a Reliable engine/shadow equivalence result,
// even when the simulator classified the projected transition as exact.
if (mismatchCount > 0 && reportConfidence == PredictionConfidence.Reliable)
    reportConfidence = PredictionConfidence.Estimated;
var report = new
{
    schema_version = 1,
    game_version = "v0.111.0",
    game_commit = "41cef1ea",
    assembly_sha256 = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    cli_protocol_version = metadataRow.TryGetProperty("cli_protocol_version", out var cliProtocol)
        ? cliProtocol.GetString() : null,
    trace_schema = metadataRow.TryGetProperty("trace_schema", out var traceSchema)
        ? traceSchema.GetInt32() : (int?)null,
    trace_id = traceId,
    fixture,
    seed,
    simulator = "STS2BestChoice.Core.DeterministicSimulator",
    action_ordinal = actionOrdinal,
    action_kind = isEndTurn ? "end_turn" : isPotionAction ? "use_potion" : "play_card",
    normalized_action_id = normalizedAction,
    draw_pile_source = isEndTurn ? (teacherDrawPileUsable ? "teacher_snapshot" : "placeholder_count_only") : "not_applicable",
    attacks_played_this_turn = isEndTurn ? attacksPlayedThisTurn : (int?)null,
    confidence = reportConfidence.ToString(),
    chance_present = chanceQuality.ChancePresent,
    random_operator = chanceQuality.RandomOperator,
    probability_known = chanceQuality.ProbabilityKnown,
    outcome_quality = chanceQuality.OutcomeQuality,
    probability_mass_covered = chanceQuality.ProbabilityMassCovered,
    effective_sample_size = chanceQuality.EffectiveSampleSize,
    confidence_interval_low = chanceQuality.ConfidenceIntervalLow,
    confidence_interval_high = chanceQuality.ConfidenceIntervalHigh,
    branch_probability = chanceQuality.ReportedProbability,
    rng_consumption_vector = chanceQuality.RngConsumptionVector,
    branch_enumerated = chanceQuality.BranchEnumerated,
    comparison_scope = chanceQuality.ComparisonScope,
    identity_comparison = chanceQuality.IdentityComparison,
    chance_quality = new
    {
        chance_present = chanceQuality.ChancePresent,
        random_operator = chanceQuality.RandomOperator,
        probability_known = chanceQuality.ProbabilityKnown,
        outcome_quality = chanceQuality.OutcomeQuality,
        probability_mass_covered = chanceQuality.ProbabilityMassCovered,
        effective_sample_size = chanceQuality.EffectiveSampleSize,
        confidence_interval_low = chanceQuality.ConfidenceIntervalLow,
        confidence_interval_high = chanceQuality.ConfidenceIntervalHigh,
        branch_probability = chanceQuality.ReportedProbability,
        rng_consumption_vector = chanceQuality.RngConsumptionVector,
        branch_enumerated = chanceQuality.BranchEnumerated,
        comparison_scope = chanceQuality.ComparisonScope,
        identity_comparison = chanceQuality.IdentityComparison
    },
    match = mismatchCount == 0,
    mismatch_count = mismatchCount,
    mismatches = fields.Where(field => !(bool)field.GetType().GetProperty("match")!.GetValue(field)!).ToArray(),
    engine_pre_state_hash = enginePreStateHash,
    engine_post_state_hash = enginePostStateHash,
    projected_comparison_hash = projectedComparisonHash,
    actual_comparison_hash = actualComparisonHash,
    rng_before = actionRow.TryGetProperty("rng_before", out var rngBefore) ? rngBefore.Clone() : (JsonElement?)null,
    rng_after = actionRow.TryGetProperty("rng_after", out var rngAfter) ? rngAfter.Clone() : (JsonElement?)null,
    fields
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
return mismatchCount == 0 ? 0 : 1;

static string HashComparison(List<object> fields, string valueProperty)
{
    var values = fields.Select(field =>
    {
        var type = field.GetType();
        return new
        {
            field = (string)type.GetProperty("field")!.GetValue(field)!,
            value = JsonSerializer.SerializeToElement(type.GetProperty(valueProperty)!.GetValue(field)),
        };
    }).ToArray();
    var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
    return Convert.ToHexString(SHA256.HashData(bytes));
}

static ShadowChanceQuality InferChanceQuality(
    JsonElement chanceBranch,
    bool chanceProduced,
    bool branchEnumerated,
    bool traceProbabilityKnown,
    decimal? traceProbability,
    bool aggregateComparison,
    bool observedConditioned,
    bool actualTerminal,
    bool isEndTurn,
    bool teacherDrawPileUsable)
{
    var streams = chanceBranch.ValueKind == JsonValueKind.Object &&
                  chanceBranch.TryGetProperty("streams_changed", out var streamsElement) &&
                  streamsElement.ValueKind == JsonValueKind.Array
        ? streamsElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToArray()
        : Array.Empty<string>();
    var traceKind = chanceBranch.ValueKind == JsonValueKind.Object &&
                    chanceBranch.TryGetProperty("kind", out var kindElement) &&
                    kindElement.ValueKind == JsonValueKind.String
        ? kindElement.GetString() ?? string.Empty
        : string.Empty;

    var randomOperator = streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatTargets, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatCardSelection, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatEnergyCosts, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatCardGeneration, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatPotionGeneration, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.CombatOrbGeneration, StringComparison.Ordinal))
                         ?? streams.FirstOrDefault(static stream =>
                             stream.Equals(RngSnapshotSet.Shuffle, StringComparison.Ordinal));
    if (string.IsNullOrEmpty(randomOperator) && isEndTurn && !teacherDrawPileUsable)
        randomOperator = RngSnapshotSet.Shuffle;
    else if (string.IsNullOrEmpty(randomOperator) &&
             (chanceProduced || traceKind.Equals("realized_rng_consumption", StringComparison.OrdinalIgnoreCase)))
        randomOperator = "Unknown";
    if (string.IsNullOrEmpty(randomOperator))
        randomOperator = "None";

    var chancePresent = chanceProduced || aggregateComparison || observedConditioned;
    var comparisonScope = observedConditioned
        ? "observed_conditioned"
        : actualTerminal
            ? aggregateComparison ? "aggregate_count_only" : "terminal_summary"
            : aggregateComparison ? "aggregate_count_only" : "strict_public_state";
    var identityComparison = comparisonScope switch
    {
        "aggregate_count_only" => "omitted",
        "observed_conditioned" => "observed",
        _ => "compared"
    };

    var declaredQuality = chanceBranch.ValueKind == JsonValueKind.Object &&
                          chanceBranch.TryGetProperty("outcome_quality", out var declaredQualityElement) &&
                          declaredQualityElement.ValueKind == JsonValueKind.String
        ? declaredQualityElement.GetString() ?? string.Empty
        : string.Empty;
    var outcomeQuality = observedConditioned
        ? "Unknown"
        : !chancePresent && !aggregateComparison
        ? "Exact"
        : branchEnumerated && traceProbabilityKnown && !aggregateComparison
            ? "Exact"
            : traceKind.Equals("sampled", StringComparison.OrdinalIgnoreCase) ||
              declaredQuality.Equals("Sampled", StringComparison.OrdinalIgnoreCase)
                ? "Sampled"
                : "Unknown";
    var probabilityKnown = outcomeQuality == "Exact" &&
                           (!chancePresent || traceProbabilityKnown || !aggregateComparison);
    if (outcomeQuality == "Sampled") probabilityKnown = traceProbabilityKnown;

    var probabilityMass = outcomeQuality == "Exact"
        ? 1m
        : ReadNullableDecimal(chanceBranch, "probability_mass_covered");
    var effectiveSampleSize = ReadNullableDecimal(chanceBranch, "effective_sample_size");
    var intervalLow = ReadNestedNullableDecimal(chanceBranch, "confidence_interval", "low") ??
                      ReadNullableDecimal(chanceBranch, "confidence_interval_low");
    var intervalHigh = ReadNestedNullableDecimal(chanceBranch, "confidence_interval", "high") ??
                       ReadNullableDecimal(chanceBranch, "confidence_interval_high");
    var rngConsumption = ReadLongMap(chanceBranch, "rng_deltas");

    return new ShadowChanceQuality(
        chancePresent,
        randomOperator,
        probabilityKnown,
        outcomeQuality,
        probabilityMass,
        effectiveSampleSize,
        intervalLow,
        intervalHigh,
        rngConsumption,
        branchEnumerated,
        comparisonScope,
        identityComparison,
        traceProbability);
}

void Compare<T>(string name, T projectedValue, T actualValue)
{
    var match = JsonSerializer.Serialize(projectedValue) == JsonSerializer.Serialize(actualValue);
    fields.Add(new { field = name, projected = projectedValue, actual = actualValue, match });
}

static CardState BuildTeacherPileCard(JsonElement card)
{
    // Identity rebuild from the teacher snapshot. The instance ID and the
    // card type are real; the stats-driven preview (trace extension, batch
    // C1) carries damage/block so replays that consume a pile card (HAVOC's
    // auto-play, HEADBUTT's move) can apply its effect.
    var instanceId = card.GetProperty("instance_id").GetString()!;
    var modelId = (card.GetProperty("id").GetString() ?? "UNKNOWN").Replace("CARD.", "", StringComparison.Ordinal);
    var cardType = card.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
    var effects = ImmutableArray.CreateBuilder<EffectSpec>();
    if (card.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
    {
        if (stats.TryGetProperty("damage", out var pileDamage) && pileDamage.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.Damage, pileDamage.GetDecimal()));
        if (stats.TryGetProperty("block", out var pileBlock) && pileBlock.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.Block, pileBlock.GetDecimal()));
    }
    return new CardState(
        instanceId,
        modelId,
        modelId,
        0,
        TargetKind.None,
        effects.ToImmutable(),
        CardType: cardType);
}

static EnemyAiPublicState? BuildEnemyPublicAi(JsonElement enemy)
{
    if (!enemy.TryGetProperty("public_ai", out var publicAi) || publicAi.ValueKind != JsonValueKind.Object)
        return null;
    var history = publicAi.TryGetProperty("intent_history", out var intentHistory) &&
                  intentHistory.ValueKind == JsonValueKind.Array
        ? intentHistory.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToImmutableArray()
        : ImmutableArray<string>.Empty;
    return new EnemyAiPublicState(
        publicAi.GetProperty("first_observed_round").GetInt32(),
        publicAi.GetProperty("last_observed_round").GetInt32(),
        publicAi.GetProperty("observed_turns").GetInt32(),
        publicAi.GetProperty("phase").GetString() ?? "unknown",
        history);
}

static ImmutableArray<IntentState> BuildIntents(
    JsonElement enemy,
    ImmutableDictionary<string, StatusState> enemyStatuses,
    ImmutableDictionary<string, StatusState> playerStatuses,
    bool paperKrane)
{
    var builder = ImmutableArray.CreateBuilder<IntentState>();
    if (!enemy.TryGetProperty("intents", out var intentsElement) || intentsElement.ValueKind != JsonValueKind.Array)
        return builder.ToImmutable();
    foreach (var intent in intentsElement.EnumerateArray())
    {
        var type = intent.GetProperty("type").GetString() ?? "";
        var enemyInstanceId = enemy.TryGetProperty("instance_id", out var enemyIdElement)
            ? enemyIdElement.GetString() ?? string.Empty
            : string.Empty;
        var previewDamage = intent.TryGetProperty("damage", out var damageElement) ? damageElement.GetDecimal() : 0m;
        var hits = intent.TryGetProperty("hits", out var hitsElement) ? hitsElement.GetInt32() : 1;
        var weakFactor = StatusAmount(enemyStatuses, "WEAK") > 0 ? (paperKrane ? 0.6m : 0.75m) : 1m;
        var vulnerableFactor = StatusAmount(playerStatuses, "VULNERABLE") > 0 ? 1.5m : 1m;
        var strengthAdditive = StatusAmount(enemyStatuses, "STRENGTH") - StatusAmount(enemyStatuses, "TEMP_STRENGTH_LOSS");
        var unmultiplied = previewDamage <= 0m
            ? 0m
            : Math.Round(previewDamage / (weakFactor * vulnerableFactor), MidpointRounding.AwayFromZero);
        var damage = Math.Max(0m, unmultiplied - strengthAdditive);
        string? restriction = null;
        var effects = ImmutableArray<EffectSpec>.Empty;
        if (type.Equals("DebuffStrong", StringComparison.OrdinalIgnoreCase) &&
            enemyInstanceId.Contains("SHRINKER_BEETLE", StringComparison.OrdinalIgnoreCase))
        {
            // Shrinker Beetle's visible DebuffStrong move applies Shrink to
            // the player for three turns; the public intent omits its effect,
            // so identify it only by the version-locked enemy ID.
            effects = [new EffectSpec(
                EffectKind.ApplyStatus,
                1m,
                "SHRINK",
                Duration: 3,
                IsDebuff: true,
                SourceId: "SHRINKER_BEETLE")];
        }
        else if (type.Equals("Buff", StringComparison.OrdinalIgnoreCase) &&
                 enemyInstanceId.Contains("SEAPUNK", StringComparison.OrdinalIgnoreCase))
        {
            effects = [new EffectSpec(
                EffectKind.ApplyStatus,
                1m,
                "STRENGTH",
                SourceId: enemyInstanceId)];
        }
        else if (type.Equals("Defend", StringComparison.OrdinalIgnoreCase) &&
                 enemyInstanceId.Contains("SEAPUNK", StringComparison.OrdinalIgnoreCase))
        {
            effects = [new EffectSpec(
                EffectKind.Block,
                7m,
                SourceId: enemyInstanceId)];
        }
        else if (type.Equals("Buff", StringComparison.OrdinalIgnoreCase) &&
                 enemyInstanceId.Contains("TERROR_EEL", StringComparison.OrdinalIgnoreCase))
        {
            effects = [new EffectSpec(
                EffectKind.ApplyStatus,
                6m,
                "VIGOR",
                SourceId: enemyInstanceId)];
        }
        if (!type.Equals("Attack", StringComparison.OrdinalIgnoreCase) && effects.IsDefaultOrEmpty)
            restriction = $"enemy intent '{type}' effects are not mirrored from the public observation";
        builder.Add(new IntentState(type, damage, hits, effects, restriction));
    }
    return builder.ToImmutable();
}

static bool IsGameplayActionId(string? actionId) =>
    actionId is not null &&
    (actionId.StartsWith("play_card:", StringComparison.Ordinal) ||
     actionId.StartsWith("use_potion:", StringComparison.Ordinal) ||
     actionId.StartsWith("end_turn", StringComparison.Ordinal) ||
     actionId.StartsWith("select_cards:", StringComparison.Ordinal) ||
     actionId.StartsWith("choose_option:", StringComparison.Ordinal));

static CardState BuildCard(JsonElement card, ImmutableDictionary<string, StatusState> playerStatuses, bool penNibCharged, int exhaustPileCount = 0, int stars = 0)
{
    var effects = ImmutableArray.CreateBuilder<EffectSpec>();
    var installedDirectObservedPower = false;
    var modelId = card.GetProperty("id").GetString()!.Replace("CARD.", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    // Description-driven effect mappings for card batches C1 (plan batch C1):
    // generation, self-copy, auto-play and X-cost patterns are not exported as
    // plain stats, so the stable English description text carries the pattern.
    // The CLI runs fixtures with lang=en.
    var description = card.TryGetProperty("description", out var descriptionElement)
        ? descriptionElement.GetString() ?? ""
        : "";
    var isXCost = modelId is "CASCADE" or "MALAISE" or "MULTI_CAST" or "TEMPEST" or "HEAVENLY_DRILL" ||
                  (modelId != "STARDUST" && description.Contains("X times", StringComparison.Ordinal));
    if (modelId == "BARRICADE")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "BARRICADE",
            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("BARRICADE", 1)));
    }
    if (modelId == "CORRUPTION")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "SKILLS_COST_ZERO", Duration: -1));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "SKILLS_EXHAUST_ON_PLAY", Duration: -1));
    }
    if (modelId == "INFINITE_BLADES")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "TURN_START_GENERATE_SHIV", Duration: -1));
    }
    if (modelId == "HELLRAISER")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "HELLRAISER",
            Duration: -1,
            TargetOverride: TargetKind.Self));
    }
    if (modelId == "WELL_LAID_PLANS")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "WELL_LAID_PLANS",
            Duration: -1,
            TargetOverride: TargetKind.Self));
    }
    if (modelId == "CALCULATED_GAMBLE")
    {
        effects.Add(new EffectSpec(
            EffectKind.DiscardHandThenDrawSame,
            TargetOverride: TargetKind.Self));
    }
    if (modelId == "DOUBLE_ENERGY")
    {
        // Gain the amount of energy currently available after paying the
        // card's cost. DOUBLE_ENERGY costs zero in the direct fixture, so this
        // mirrors the engine's exact 2 -> 4 transition without consulting RNG.
        effects.Add(new EffectSpec(
            EffectKind.GainEnergy,
            1,
            "CURRENT_ENERGY",
            TargetOverride: TargetKind.Self));
    }
    if (modelId == "TRACKING")
    {
        // The live power exposes the weak-enemy threshold as Amount=50.
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            50,
            "TRACKING",
            Duration: -1,
            TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "ALCHEMIZE")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateRandomPotion,
            1,
            RandomSource: RngSnapshotSet.CombatPotionGeneration));
    }
    if (modelId == "OUTRAGE")
    {
        // v0.111 public evidence shows one extra discard entry. The localized
        // description is unavailable in this build, so mirror the observed
        // copy while keeping the transition explicitly Estimated.
        effects.Add(new EffectSpec(
            EffectKind.CopyPlayedCard,
            1,
            GeneratedDestination: GeneratedCardDestination.DiscardPile));
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            UnsupportedReason: "outrage_localized_semantics_unavailable"));
    }
    if (modelId == "VOID_FORM")
        effects.Add(new EffectSpec(EffectKind.EndTurn));
    if (modelId == "BEGONE")
        effects.Add(new EffectSpec(
            EffectKind.TransformCards, 1, "HAND_ONE_TO_MINION_STRIKE",
            TargetOverride: TargetKind.Self));
    if (modelId == "GUARDS")
        effects.Add(new EffectSpec(
            EffectKind.TransformCards, 0, "HAND_ANY_TO_MINION_SACRIFICE",
            TargetOverride: TargetKind.Self));
    if (modelId == "NIGHTMARE")
    {
        effects.Add(new EffectSpec(
            EffectKind.CopyChosenHandCard, 3, "NEXT_TURN",
            TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus, 3, "NIGHTMARE", Duration: 1,
            TargetOverride: TargetKind.Self));
    }
    if (modelId == "TRANSFIGURE")
        effects.Add(new EffectSpec(
            EffectKind.ModifySelectedHandCard, 1, "ADD_REPLAY_AND_COST",
            XBonus: 1, TargetOverride: TargetKind.Self));
    if (modelId == "MONOLOGUE")
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus, 1, "TRIGGER_CARD_PLAYED_TEMP_STRENGTH",
            Duration: 1, TargetOverride: TargetKind.Self));
    if (modelId is "DISCOVERY" or "SPLASH" or "QUASAR")
    {
        var poolSize = modelId switch
        {
            "DISCOVERY" => 78,
            "SPLASH" => 112,
            _ => 50,
        };
        effects.Add(new EffectSpec(
            EffectKind.GenerateRandomCards,
            1,
            "FREE_THIS_TURN",
            GeneratedCardPool: PlaceholderGeneratedPool(modelId, poolSize, "Attack"),
            RandomSource: RngSnapshotSet.CombatCardGeneration,
            RandomSelectionWithReplacement: false));
    }
    if (modelId == "FLANKING")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            2,
            "FLANKING",
            Duration: 1,
            TargetOverride: TargetKind.Enemy));
    }
    if (modelId == "INFERNAL_BLADE")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateRandomCards,
            1,
            "FREE_THIS_TURN",
            GeneratedCardPool: InfernalBladeEligibleAttackPool(),
            RandomSource: RngSnapshotSet.CombatCardGeneration,
            RandomSelectionWithReplacement: false));
    }
    if (modelId == "STOKE")
    {
        effects.Add(new EffectSpec(EffectKind.ExhaustHand, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(
            EffectKind.GenerateRandomCards,
            1,
            GeneratedCardPool:
            [
                new CardState(
                    "template:unknown-random-card",
                    "UNKNOWN_RANDOM_CARD",
                    "Unknown random card",
                    1,
                    TargetKind.None,
                    [])
            ],
            TargetOverride: TargetKind.Self,
            RepeatByExhaustedCount: true,
            RandomSource: RngSnapshotSet.CombatCardGeneration,
            RandomSelectionWithReplacement: true));
    }
    var observedPower = modelId switch
    {
        "BLIGHT_STRIKE" => ("DOOM", 8m, TargetKind.Enemy),
        "BORROWED_TIME" => ("BORROWED_TIME", 1m, TargetKind.Self),
        "BULLET_TIME" => ("CANNOT_DRAW", 1m, TargetKind.Self),
        "BURST" => ("BURST", 1m, TargetKind.Self),
        "CORROSIVE_WAVE" => ("CORROSIVE_WAVE", 2m, TargetKind.Self),
        "DEATHBRINGER" => ("DOOM", 21m, TargetKind.Enemy),
        "END_OF_DAYS" => ("DOOM", 29m, TargetKind.Enemy),
        "EQUILIBRIUM" => ("RETAIN_HAND", 1m, TargetKind.Self),
        "FOREGONE_CONCLUSION" => ("FOREGONE_CONCLUSION", 2m, TargetKind.Self),
        "GLITTERSTREAM" => ("SCHEDULED_BLOCK", 5m, TargetKind.Self),
        "GLOW" => ("SCHEDULED_DRAW", 1m, TargetKind.Self),
        "HANG" => ("HANG", 2m, TargetKind.Enemy),
        "HIBERNATE" => ("HIBERNATE", 1m, TargetKind.Self),
        "HIDDEN_CACHE" => ("STAR_NEXT_TURN", 3m, TargetKind.Self),
        "KNOCKDOWN" => ("KNOCKDOWN", 2m, TargetKind.Enemy),
        "LIGHTNING_ROD" => ("LIGHTNING_ROD", 2m, TargetKind.Self),
        "NEGATIVE_PULSE" => ("DOOM", 7m, TargetKind.Enemy),
        "NO_ESCAPE" => ("DOOM", 10m, TargetKind.Enemy),
        "OBLIVION" => ("OBLIVION", 3m, TargetKind.Enemy),
        "PANIC_BUTTON" => ("NO_BLOCK", 2m, TargetKind.Self),
        "PATTER" => ("VIGOR", 2m, TargetKind.Self),
        "PLOT" => ("SCHEDULED_DRAW", 2m, TargetKind.Self),
        "REBOUND" => ("REBOUND", 1m, TargetKind.Self),
        "SALVO" => ("RETAIN_HAND", 1m, TargetKind.Self),
        "SCOURGE" => ("DOOM", 13m, TargetKind.Enemy),
        "SHADOWMELD" => ("SHADOWMELD", 1m, TargetKind.Self),
        "SIC_EM" => ("SIC_EM", 3m, TargetKind.Enemy),
        "SIGNAL_BOOST" => ("SIGNAL_BOOST", 1m, TargetKind.Self),
        "SYNTHESIS" => ("NEXT_FREE_POWER", 1m, TargetKind.Self),
        "TAG_TEAM" => ("TAG_TEAM", 1m, TargetKind.Enemy),
        "TERRAFORMING" => ("VIGOR", 7m, TargetKind.Self),
        "THE_BOMB" => ("THE_BOMB", 3m, TargetKind.Self),
        "THE_GAMBIT" => ("THE_GAMBIT", 1m, TargetKind.Self),
        "UNDERWORLD" => ("UNDERWORLD", 1m, TargetKind.Self),
        "VEILPIERCER" => ("VEILPIERCER", 1m, TargetKind.Self),
        _ => ((string StatusId, decimal Amount, TargetKind Target)?)null,
    };
    if (observedPower is { } powerInstall)
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            powerInstall.Amount,
            powerInstall.StatusId,
            Duration: -1,
            TargetOverride: powerInstall.Target));
        installedDirectObservedPower = true;
    }
    if (modelId == "BIASED_COGNITION")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 5, "FOCUS", Duration: -1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "BIASED_COGNITION", Duration: -1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId is "HOTFIX" or "FOCUSED_STRIKE")
    {
        var focus = modelId == "HOTFIX" ? 2m : 1m;
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, focus, "FOCUS", Duration: 1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, focus, modelId, Duration: 1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "HYPERBEAM")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -3, "FOCUS", Duration: -1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 3, "HYPERBEAM_FOCUS_DOWN", Duration: -1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "SHARED_FATE")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -2, "STRENGTH", Duration: -1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -2, "STRENGTH", Duration: -1, TargetOverride: TargetKind.Enemy));
        installedDirectObservedPower = true;
    }
    if (modelId is "PUTREFY" or "SHOCKWAVE")
    {
        var amount = modelId == "PUTREFY" ? 2m : 3m;
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, amount, "WEAK", Duration: (int)amount, IsDebuff: true, TargetOverride: modelId == "SHOCKWAVE" ? TargetKind.AllEnemies : TargetKind.Enemy));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, amount, "VULNERABLE", Duration: (int)amount, IsDebuff: true, TargetOverride: modelId == "SHOCKWAVE" ? TargetKind.AllEnemies : TargetKind.Enemy));
        installedDirectObservedPower = true;
    }
    if (modelId == "MALAISE")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -1, "STRENGTH", Duration: -1, IsDebuff: true, TargetOverride: TargetKind.Enemy, AmountByEnergySpent: true));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "WEAK", Duration: 1, IsDebuff: true, TargetOverride: TargetKind.Enemy, AmountByEnergySpent: true));
        installedDirectObservedPower = true;
    }
    if (modelId == "INVOKE")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 2, "SCHEDULED_SUMMON", Duration: 1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "FRIENDSHIP")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -2, "STRENGTH", Duration: -1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "FRIENDSHIP", Duration: -1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId is "ENFEEBLING_TOUCH" or "DARK_SHACKLES" or "CRUSH_UNDER")
    {
        var loss = modelId == "ENFEEBLING_TOUCH" ? 8m : modelId == "DARK_SHACKLES" ? 9m : 1m;
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, -loss, "STRENGTH", Duration: 1, IsDebuff: true, TargetOverride: modelId == "CRUSH_UNDER" ? TargetKind.AllEnemies : TargetKind.Enemy));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, loss, modelId, Duration: 1, IsDebuff: true, TargetOverride: modelId == "CRUSH_UNDER" ? TargetKind.AllEnemies : TargetKind.Enemy));
        installedDirectObservedPower = true;
    }
    if (modelId == "DEBILITATE")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 2, "DEBILITATE", Duration: 2, IsDebuff: true, TargetOverride: TargetKind.Enemy));
        installedDirectObservedPower = true;
    }
    if (modelId == "CONVERGENCE")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "RETAIN_HAND", Duration: 1, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "STAR_NEXT_TURN", Duration: 1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "CONQUEROR")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "CONQUEROR", Duration: 1, IsDebuff: true, TargetOverride: TargetKind.Enemy));
        installedDirectObservedPower = true;
    }
    if (modelId == "WRAITH_FORM")
    {
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 2, "INTANGIBLE", Duration: 2, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "WRAITH_FORM", Duration: -1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "SHADOW_STEP")
    {
        effects.Add(new EffectSpec(EffectKind.DiscardHand, TargetOverride: TargetKind.Self));
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "SHADOW_STEP", Duration: 1, TargetOverride: TargetKind.Self));
        installedDirectObservedPower = true;
    }
    if (modelId == "ADAPTIVE_STRIKE")
    {
        effects.Add(new EffectSpec(
            EffectKind.CopyPlayedCard,
            1,
            "ZERO_COST",
            GeneratedDestination: GeneratedCardDestination.DiscardPile));
    }
    if (modelId == "BLADE_OF_INK")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            2,
            GeneratedCard: ShivTemplate(),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }
    if (modelId == "BLADE_SYMPHONY")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            2,
            GeneratedCard: ShivTemplate(),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }
    if (modelId is "BOOST_AWAY" or "FIGHT_THROUGH" or "GUNK_UP" or "OVERCLOCK" or "TURBO")
    {
        var generatedId = modelId switch
        {
            "BOOST_AWAY" => "DAZED",
            "FIGHT_THROUGH" => "WOUND",
            "GUNK_UP" => "SLIMED",
            "OVERCLOCK" => "BURN",
            _ => "VOID",
        };
        var amount = modelId == "FIGHT_THROUGH" ? 2m : 1m;
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            amount,
            GeneratedCard: new CardState(
                $"template:{generatedId.ToLowerInvariant()}", generatedId, generatedId, -1,
                TargetKind.None, [], CardType: "Status"),
            GeneratedDestination: GeneratedCardDestination.DiscardPile));
    }
    if (modelId == "COLLISION_COURSE")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            1,
            GeneratedCard: new CardState("template:debris", "DEBRIS", "Debris", -1, TargetKind.None, [], CardType: "Status"),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }
    if (modelId == "CRASH_LANDING")
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            0,
            "FILL_HAND",
            GeneratedCard: new CardState("template:debris", "DEBRIS", "Debris", -1, TargetKind.None, [], CardType: "Status"),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }
    if (modelId == "STORM_OF_STEEL")
    {
        effects.Add(new EffectSpec(
            EffectKind.DiscardHandAndGenerate,
            GeneratedCard: new CardState(
                "template:shiv", "SHIV", "Shiv", 0, TargetKind.Enemy,
                [new EffectSpec(EffectKind.Damage, 4)], CardDestination.Exhaust, CardType: "Attack"),
            TargetOverride: TargetKind.Self));
    }
    if (modelId is "CAPTURE_SPIRIT" or "GLIMPSE_BEYOND" or "GRAVE_WARDEN" or "REAVE" or "DIRGE")
    {
        var amount = modelId is "CAPTURE_SPIRIT" or "GLIMPSE_BEYOND" ? 3m : 1m;
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards,
            amount,
            GeneratedCard: new CardState("template:soul", "SOUL", "Soul", 0, TargetKind.None, [], CardType: "Skill"),
            GeneratedDestination: GeneratedCardDestination.DrawPile,
            AmountByEnergySpent: modelId == "DIRGE",
            RandomSource: RngSnapshotSet.Shuffle,
            RandomizeGeneratedPosition: true));
    }
    if (modelId == "SEVERANCE")
    {
        var soul = new CardState("template:soul", "SOUL", "Soul", 0, TargetKind.None, [], CardType: "Skill");
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards, 1,
            GeneratedCard: soul,
            GeneratedDestination: GeneratedCardDestination.DrawPile,
            RandomSource: RngSnapshotSet.Shuffle,
            RandomizeGeneratedPosition: true));
        effects.Add(new EffectSpec(EffectKind.GenerateCards, 1, GeneratedCard: soul, GeneratedDestination: GeneratedCardDestination.Hand));
        effects.Add(new EffectSpec(EffectKind.GenerateCards, 1, GeneratedCard: soul, GeneratedDestination: GeneratedCardDestination.DiscardPile));
    }
    if (modelId == "REBOOT")
    {
        effects.Add(new EffectSpec(EffectKind.Reboot, RandomSource: RngSnapshotSet.Shuffle));
        effects.Add(new EffectSpec(EffectKind.Draw, 4));
    }
    if (modelId is "BUNDLE_OF_JOY" or "DISTRACTION" or "JACKPOT" or "JACK_OF_ALL_TRADES" or "MANIFEST_AUTHORITY" or "WHITE_NOISE")
    {
        var (amount, poolSize, outcomeType, freeThisTurn, withReplacement) = modelId switch
        {
            "BUNDLE_OF_JOY" => (3m, 50, "Skill", false, false),
            "DISTRACTION" => (1m, 39, "Skill", true, false),
            "JACKPOT" => (3m, 1, "Skill", true, true),
            "JACK_OF_ALL_TRADES" => (1m, 49, "Skill", false, false),
            "MANIFEST_AUTHORITY" => (1m, 50, "Skill", false, false),
            _ => (1m, 19, "Power", true, false),
        };
        effects.Add(new EffectSpec(
            EffectKind.GenerateRandomCards,
            amount,
            freeThisTurn ? "FREE_THIS_TURN" : null,
            GeneratedCardPool: PlaceholderGeneratedPool(modelId, poolSize, outcomeType),
            RandomSource: RngSnapshotSet.CombatCardGeneration,
            RandomSelectionWithReplacement: withReplacement));
    }
    // CHAOS channels one random orb.  The public CLI masks the selected orb
    // identity in NOSL traces, but still exposes the CombatOrbGeneration RNG
    // counter.  Keep the branch unknown and let the simulator mirror the
    // counter without leaking the hidden outcome.
    if (modelId == "CHAOS")
    {
        effects.Add(new EffectSpec(
            EffectKind.ChannelOrbs,
            1,
            "RANDOM",
            RandomSource: RngSnapshotSet.CombatOrbGeneration));
    }
    if (modelId == "BATTLE_TRANCE" &&
        card.TryGetProperty("stats", out var battleTranceStats) &&
        battleTranceStats.ValueKind == JsonValueKind.Object &&
        battleTranceStats.TryGetProperty("cards", out var battleTranceCards) &&
        battleTranceCards.TryGetInt32(out var battleTranceDraw) && battleTranceDraw > 0)
    {
        effects.Add(new EffectSpec(EffectKind.Draw, battleTranceDraw));
        // NoDrawPower is turn-scoped in v0.111 and is removed by the next
        // turn-start duration tick.
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus, 1, "CANNOT_DRAW", Duration: 1));
    }
    // DARK_EMBRACE has no numeric stats preview; its trigger is carried only
    // by the card text.
    if (modelId == "DARK_EMBRACE")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "TRIGGER_CARD_EXHAUSTED_DRAW",
            Duration: -1,
            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("TRIGGER_CARD_EXHAUSTED_DRAW", 1)));
    }
    if (modelId == "AGGRESSION")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "AGGRESSION",
            Duration: -1,
            TargetOverride: TargetKind.Self,
            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("AGGRESSION", 1)));
    }
    if (modelId == "POUNCE")
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "NEXT_FREE_SKILL", Duration: 1, TargetOverride: TargetKind.Self));
    if (modelId == "UNRELENTING")
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "NEXT_FREE_ATTACK", Duration: 1, TargetOverride: TargetKind.Self));
    if (modelId == "PREDATOR")
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 2, "SCHEDULED_DRAW", Duration: 1, TargetOverride: TargetKind.Self));
    if (modelId == "JUGGLING")
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "JUGGLING", Duration: -1, TargetOverride: TargetKind.Self));
    if (modelId == "UNMOVABLE")
        effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "UNMOVABLE", Duration: -1, TargetOverride: TargetKind.Self));
    if (card.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
    {
        if (modelId == "APPARITION")
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("intangiblepower").GetInt32(),
                "INTANGIBLE", Duration: 1, TargetOverride: TargetKind.Self));
        if (modelId == "BRIGHTEST_FLAME")
            effects.Add(new EffectSpec(
                EffectKind.ModifyMaxHp, -stats.GetProperty("maxhp").GetInt32(),
                TargetOverride: TargetKind.Self));
        if (modelId == "FEEDING_FRENZY")
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("strengthpower").GetInt32(),
                "FEEDING_FRENZY", Duration: 1, TargetOverride: TargetKind.Self));
        if (modelId == "RELAX")
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("cards").GetInt32(),
                "SCHEDULED_DRAW", Duration: 1, TargetOverride: TargetKind.Self));
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("energy").GetInt32(),
                "SCHEDULED_ENERGY", Duration: 1, TargetOverride: TargetKind.Self));
        }
        if (modelId == "TORIC_TOUGHNESS")
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("turns").GetInt32(),
                "TORIC_TOUGHNESS", Duration: -1, TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        if (modelId == "WHISTLE")
            effects.Add(new EffectSpec(EffectKind.StunEnemy, TargetOverride: TargetKind.Enemy));
        if (modelId == "METAMORPHOSIS")
            effects.Add(new EffectSpec(
                EffectKind.GenerateRandomCards,
                stats.GetProperty("cards").GetInt32(),
                "FREE_THIS_COMBAT",
                GeneratedDestination: GeneratedCardDestination.DrawPile,
                GeneratedCardPool: PlaceholderGeneratedPool("METAMORPHOSIS", 33, "Attack"),
                RandomSource: RngSnapshotSet.CombatCardGeneration,
                RandomizeGeneratedPosition: true,
                RandomSelectionWithReplacement: true));
        if (modelId == "GLIMMER")
        {
            effects.Add(new EffectSpec(EffectKind.Draw, stats.GetProperty("cards").GetInt32()));
            effects.Add(new EffectSpec(EffectKind.ChooseHandToDrawTop, stats.GetProperty("putback").GetInt32()));
        }
        if (modelId == "PHOTON_CUT")
        {
            effects.Add(new EffectSpec(EffectKind.Draw, stats.GetProperty("cards").GetInt32()));
            effects.Add(new EffectSpec(EffectKind.ChooseHandToDrawTop, stats.GetProperty("putback").GetInt32()));
        }
        if (modelId == "THINKING_AHEAD")
        {
            effects.Add(new EffectSpec(EffectKind.Draw, stats.GetProperty("cards").GetInt32()));
            effects.Add(new EffectSpec(EffectKind.ChooseHandToDrawTop, 1));
        }
        if (modelId == "PREPARED")
        {
            effects.Add(new EffectSpec(EffectKind.Draw, stats.GetProperty("cards").GetInt32()));
            effects.Add(new EffectSpec(EffectKind.DiscardCards, stats.GetProperty("cards").GetInt32()));
        }
        if (modelId == "HIDDEN_DAGGERS")
            effects.Add(new EffectSpec(EffectKind.DiscardCards, stats.GetProperty("cards").GetInt32()));
        if (modelId == "PURITY")
            effects.Add(new EffectSpec(
                EffectKind.ExhaustCards, stats.GetProperty("cards").GetInt32(), "UP_TO"));
        if (modelId == "SCAVENGE")
        {
            effects.Add(new EffectSpec(EffectKind.ExhaustCards, 1));
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("energy").GetInt32(),
                "SCHEDULED_ENERGY", Duration: 1, TargetOverride: TargetKind.Self));
        }
        if (modelId == "GUIDING_STAR")
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, stats.GetProperty("cards").GetInt32(),
                "SCHEDULED_DRAW", Duration: 1, TargetOverride: TargetKind.Self));
        if (modelId == "REFLECT")
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, 1, "REFLECT_BLOCKED_ATTACK_DAMAGE",
                Duration: 1, TargetOverride: TargetKind.Self));
        if (modelId == "RESONANCE")
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus, -1, "STRENGTH", Duration: -1,
                IsDebuff: true, TargetOverride: TargetKind.AllEnemies));
        // Choice cards expose draw/discard/exhaust counts in their public
        // description.  The root play opens a card selector; the concrete
        // movement is applied later through cliChoice using stable instance IDs.
        var drawMatch = System.Text.RegularExpressions.Regex.Match(
            description, "Draw (?<count>\\d+) card", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (drawMatch.Success && int.TryParse(drawMatch.Groups["count"].Value, out var drawCount) && drawCount > 0)
            effects.Add(new EffectSpec(EffectKind.Draw, drawCount));
        var discardMatch = System.Text.RegularExpressions.Regex.Match(
            description, "Discard (?<count>\\d+) card", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (discardMatch.Success && int.TryParse(discardMatch.Groups["count"].Value, out var discardCount) && discardCount > 0)
            effects.Add(new EffectSpec(EffectKind.DiscardCards, discardCount));
        var exhaustChoiceMatch = System.Text.RegularExpressions.Regex.Match(
            description, "Exhaust (?<count>\\d+) card", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (exhaustChoiceMatch.Success && int.TryParse(exhaustChoiceMatch.Groups["count"].Value, out var exhaustChoiceCount) && exhaustChoiceCount > 0)
            effects.Add(new EffectSpec(EffectKind.ExhaustCards, exhaustChoiceCount));
        if (modelId == "ONE_TWO_PUNCH" &&
            stats.TryGetProperty("attacks", out var oneTwoPunchAttacks) &&
            oneTwoPunchAttacks.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                oneTwoPunchAttacks.GetDecimal(),
                "ONE_TWO_PUNCH",
                Duration: 1,
                TargetOverride: TargetKind.Self));
        }
        if (modelId == "PHANTOM_BLADES" &&
            stats.TryGetProperty("phantombladespower", out var phantomBladesPower) &&
            phantomBladesPower.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                phantomBladesPower.GetDecimal(),
                "PHANTOM_BLADES",
                Duration: -1,
                TargetOverride: TargetKind.Self));
        }
        var forgeAmount = stats.TryGetProperty("calculatedforge", out var calculatedForge) && calculatedForge.GetDecimal() > 0
            ? calculatedForge.GetDecimal()
            : stats.TryGetProperty("forge", out var forge) && forge.GetDecimal() > 0
                ? forge.GetDecimal()
                : 0m;
        if (forgeAmount > 0m)
        {
            effects.Add(new EffectSpec(
                EffectKind.Forge,
                forgeAmount,
                TargetOverride: TargetKind.Self));
        }
        if (modelId == "AUTOMATION" &&
            stats.TryGetProperty("energy", out var automationEnergy) && automationEnergy.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                automationEnergy.GetDecimal(),
                "AUTOMATION_DRAW_ENERGY",
                Duration: -1,
                TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        if (modelId == "ORBIT" &&
            stats.TryGetProperty("energy", out var orbitEnergy) && orbitEnergy.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                orbitEnergy.GetDecimal(),
                "ORBIT_ENERGY_REBATE",
                Duration: -1,
                TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        if (modelId == "DEFRAGMENT" &&
            stats.TryGetProperty("focuspower", out var defragmentFocus) && defragmentFocus.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                defragmentFocus.GetDecimal(),
                "FOCUS",
                Duration: -1,
                TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        var directPowerAmount = modelId switch
        {
            "CACOPHONY" when stats.TryGetProperty("damage", out var value) => value.GetDecimal(),
            "CHILD_OF_THE_STARS" when stats.TryGetProperty("blockforstars", out var value) => value.GetDecimal(),
            "FASTEN" when stats.TryGetProperty("extrablock", out var value) => value.GetDecimal(),
            "FURNACE" when stats.TryGetProperty("forge", out var value) => value.GetDecimal(),
            "GENESIS" when stats.TryGetProperty("starsperturn", out var value) => value.GetDecimal(),
            "HAUNT" when stats.TryGetProperty("hploss", out var value) => value.GetDecimal(),
            "PILLAR_OF_CREATION" when stats.TryGetProperty("block", out var value) => value.GetDecimal(),
            "ROYALTIES" when stats.TryGetProperty("gold", out var value) => value.GetDecimal(),
            "SHROUD" when stats.TryGetProperty("block", out var value) => value.GetDecimal(),
            "SPIRIT_OF_ASH" when stats.TryGetProperty("blockonexhaust", out var value) => value.GetDecimal(),
            _ => 0m,
        };
        if (directPowerAmount > 0m)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                directPowerAmount,
                modelId,
                Duration: -1,
                TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        // CLI stats.damage/stats.block are live previews: they already include
        // additive STRENGTH/VIGOR (damage) and DEXTERITY (block) modifiers,
        // a charged Pen Nib doubling (attack damage), while multiplicative
        // effects (Vulnerable/Weak/Frail) are only shown in damage_by_target.
        // The simulator re-applies every modifier, so strip the preview part
        // to recover the base value it expects.
        if (modelId != "PACTS_END" && modelId != "FIEND_FIRE" &&
            stats.TryGetProperty("damage", out var damage) && damage.GetDecimal() > 0)
        {
            var additive = StatusAmount(playerStatuses, "STRENGTH") + StatusAmount(playerStatuses, "VIGOR");
            // Accuracy's Shiv bonus is also already inside the live preview for
            // Shiv cards, and the simulator re-applies it via SHIV_DAMAGE_BONUS.
            if (modelId.Equals("SHIV", StringComparison.OrdinalIgnoreCase))
                additive += StatusAmount(playerStatuses, "SHIV_DAMAGE_BONUS");
            var baseDamage = damage.GetDecimal() - additive;
            var calculatedHits = stats.TryGetProperty("calculatedhits", out var calculatedHitsElement)
                ? calculatedHitsElement.GetInt32()
                : -1;
            // Finisher/Flechettes publish a positive base preview even when
            // their current attack/skill count yields zero hits.
            if (calculatedHits == 0)
                baseDamage = 0;
            var isAttack = card.GetProperty("type").GetString() is "Attack" or "攻击";
            if (penNibCharged && isAttack)
                baseDamage /= 2m;
            // "…X times" (WHIRLWIND, SKEWER): the hit count scales with the
            // energy spent, which the simulator replays via CostsX +
            // RepeatByEnergySpent.
            isXCost = modelId != "STARDUST" && description.Contains("X times", StringComparison.Ordinal);
            if (modelId == "STARDUST" && stars <= 0)
                baseDamage = 0;
            if (baseDamage > 0)
            {
                // Random-enemy attacks (e.g. SWORD_BOOMERANG: stats.damage +
                // stats.repeat, target_type RandomEnemy) must go through the
                // CombatTargets-consuming random handler, not the targeted one.
                if (card.GetProperty("target_type").GetString() == "RandomEnemy")
                {
                    var randomHits = stats.TryGetProperty("repeat", out var repeatEl)
                        ? Math.Max(1, repeatEl.GetInt32())
                        : modelId == "STARDUST"
                            ? Math.Max(0, stars)
                            : description.Contains("twice", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    effects.Add(new EffectSpec(
                        EffectKind.RandomEnemyDamage, baseDamage,
                        Repeat: randomHits,
                        RepeatByEnergySpent: isXCost,
                        RandomSource: RngSnapshotSet.CombatTargets));
                }
                else
                {
                    var repeatCount = stats.TryGetProperty("repeat", out var repeatCountElement) &&
                        repeatCountElement.GetInt32() > 0 && modelId != "ICE_LANCE"
                        ? repeatCountElement.GetInt32()
                        : (calculatedHits > 0
                            ? calculatedHits
                            : (modelId is not ("DEBILITATE" or "SHATTER" or "TESLA_COIL") &&
                               description.Contains("twice", StringComparison.OrdinalIgnoreCase) ? 2 : 1));
                    if (modelId == "DISMANTLE")
                    {
                        effects.Add(new EffectSpec(EffectKind.Damage, baseDamage));
                        effects.Add(new EffectSpec(
                            EffectKind.Damage,
                            baseDamage,
                            Condition: "TARGET_HAS_VULNERABLE"));
                    }
                    else if (modelId == "SPITE")
                    {
                        effects.Add(new EffectSpec(EffectKind.Damage, baseDamage));
                        if (repeatCount > 1)
                        {
                            effects.Add(new EffectSpec(
                                EffectKind.Damage,
                                baseDamage,
                                Repeat: repeatCount - 1,
                                Condition: "PLAYER_HP_LOST_THIS_TURN"));
                        }
                    }
                    else
                    {
                        effects.Add(new EffectSpec(
                            EffectKind.Damage,
                            baseDamage,
                            Repeat: repeatCount,
                            RepeatByEnergySpent: isXCost));
                    }
                }
            }
        }
        if (modelId == "PACTS_END" &&
            stats.TryGetProperty("cards", out var pactThreshold) &&
            exhaustPileCount >= pactThreshold.GetInt32() &&
            stats.TryGetProperty("damage", out var pactDamage) &&
            pactDamage.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(EffectKind.Damage, pactDamage.GetDecimal()));
        }
        // Some v0.111 cards expose only their resolved attack amount as
        // `calculateddamage` (for example BULLY). When no base damage field
        // exists, use that public runtime preview directly.
        if (modelId is not ("PROTECTOR" or "SQUEEZE" or "UNLEASH") &&
            !stats.TryGetProperty("damage", out _) &&
            stats.TryGetProperty("calculateddamage", out var calculatedDamage) &&
            calculatedDamage.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(EffectKind.Damage, calculatedDamage.GetDecimal()));
        }
        if (modelId is not ("ESCAPE_PLAN" or "SECOND_WIND" or "PILLAR_OF_CREATION" or "SHROUD" or "BONE_SHARDS") &&
            stats.TryGetProperty("block", out var block) && block.GetDecimal() > 0)
        {
            var baseBlock = block.GetDecimal() - StatusAmount(playerStatuses, "DEXTERITY");
            if (baseBlock > 0)
                effects.Add(new EffectSpec(EffectKind.Block, baseBlock));
        }
        if (modelId == "FISTICUFFS")
            effects.Add(new EffectSpec(
                EffectKind.DynamicBlock,
                1,
                "DAMAGE_DEALT_THIS_CARD",
                TargetOverride: TargetKind.Self));
        if (!stats.TryGetProperty("block", out _) &&
            stats.TryGetProperty("calculatedblock", out var calculatedBlock) &&
            calculatedBlock.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(EffectKind.Block, calculatedBlock.GetDecimal()));
        }
        if (modelId == "DODGE_AND_ROLL" &&
            description.Contains("Next turn, gain", StringComparison.OrdinalIgnoreCase))
        {
            effects.Add(new EffectSpec(EffectKind.ScheduleCurrentBlock, 1));
        }
        if (modelId == "BLUR" &&
            stats.TryGetProperty("blur", out var blur) && blur.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                blur.GetDecimal(),
                "BLUR",
                Duration: -1,
                TargetOverride: TargetKind.Self,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("BLUR", blur.GetDecimal())));
        }
        if (modelId == "COLOSSUS" &&
            stats.TryGetProperty("colossus", out var colossus) && colossus.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                colossus.GetDecimal(),
                "REDUCE_VULNERABLE_ATTACK_DAMAGE",
                Duration: 1,
                TargetOverride: TargetKind.Self,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("REDUCE_VULNERABLE_ATTACK_DAMAGE", colossus.GetDecimal())));
        }
        if (stats.TryGetProperty("vulnerablepower", out var vulnerable) && vulnerable.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, vulnerable.GetDecimal(), "VULNERABLE", Duration: vulnerable.GetInt32(), IsDebuff: true));
        if (modelId == "STRANGLE" && stats.TryGetProperty("stranglepower", out var strangle) && strangle.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, strangle.GetDecimal(), "TRIGGER_CARD_PLAYED_HP_LOSS", Duration: 1));
        if (modelId == "DOMINATE" && stats.TryGetProperty("strengthpervulnerable", out var strengthPerVulnerable) && strengthPerVulnerable.GetDecimal() > 0)
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                strengthPerVulnerable.GetDecimal(),
                "STRENGTH",
                Duration: -1,
                TargetOverride: TargetKind.Self,
                AmountByTargetVulnerableStacks: true));
        if (modelId == "SECOND_WIND" && stats.TryGetProperty("block", out var secondWindBlock) && secondWindBlock.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ExhaustNonAttacksAndBlock, secondWindBlock.GetDecimal(), TargetOverride: TargetKind.Self));
        if (modelId == "FIEND_FIRE" && stats.TryGetProperty("damage", out var fiendFireDamage) && fiendFireDamage.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(EffectKind.ExhaustHand, TargetOverride: TargetKind.Self));
            effects.Add(new EffectSpec(EffectKind.Damage, fiendFireDamage.GetDecimal(), RepeatByExhaustedCount: true));
        }
        if (modelId == "SPEEDSTER" && stats.TryGetProperty("speedsterpower", out var speedster) && speedster.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, speedster.GetDecimal(), "TRIGGER_NON_HAND_DRAW_ALL_DAMAGE", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "JUGGERNAUT" && stats.TryGetProperty("juggernautpower", out var juggernaut) && juggernaut.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, juggernaut.GetDecimal(), "TRIGGER_BLOCK_GAINED_RANDOM_DAMAGE", Duration: -1, TargetOverride: TargetKind.Self, RandomSource: RngSnapshotSet.CombatTargets));
        if (modelId == "NOXIOUS_FUMES" && stats.TryGetProperty("poisonperturn", out var poisonPerTurn) && poisonPerTurn.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, poisonPerTurn.GetDecimal(), "TURN_START_ALL_ENEMY_POISON", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "CRUELTY" && stats.TryGetProperty("crueltypower", out var cruelty) && cruelty.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, cruelty.GetDecimal(), "BONUS_VULNERABLE_POWERED_ATTACK_DAMAGE_PERCENT", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "VICIOUS" && stats.TryGetProperty("cards", out var viciousCards) && viciousCards.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, viciousCards.GetDecimal(), "TRIGGER_VULNERABLE_APPLIED_DRAW", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "STAMPEDE" && stats.TryGetProperty("power", out var stampedePower) && stampedePower.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, stampedePower.GetDecimal(), "TURN_END_RANDOM_HAND_ATTACK", Duration: -1, TargetOverride: TargetKind.Self, RandomSource: RngSnapshotSet.CombatTargets));
        if (modelId == "TANK")
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, 1, "TANK", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "INFERNO" && stats.TryGetProperty("infernopower", out var infernoPower) && infernoPower.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, infernoPower.GetDecimal(), "INFERNO", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId == "CRIMSON_MANTLE" && stats.TryGetProperty("crimsonmantlepower", out var crimsonMantlePower) && crimsonMantlePower.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, crimsonMantlePower.GetDecimal(), "CRIMSON_MANTLE", Duration: -1, TargetOverride: TargetKind.Self));
        if (modelId != "FRIENDSHIP" &&
            stats.TryGetProperty("strengthpower", out var strength) && strength.GetDecimal() > 0)
        {
            var statusId = modelId switch
            {
                "DEMON_FORM" => "TURN_START_STRENGTH",
                "RUPTURE" => "TRIGGER_PLAYER_HP_LOST_STRENGTH",
                _ => "STRENGTH",
            };
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                strength.GetDecimal(),
                statusId,
                Duration: modelId == "SETUP_STRIKE" ? 1 : -1,
                TargetOverride: modelId is "FIGHT_ME" or "SETUP_STRIKE" or "RESONANCE" ? TargetKind.Self : null,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue(statusId, strength.GetDecimal())));
        }
        if (modelId == "FIGHT_ME" &&
            stats.TryGetProperty("enemystrength", out var enemyStrength) && enemyStrength.GetDecimal() > 0)
        {
            var amount = enemyStrength.GetDecimal();
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                amount,
                "STRENGTH",
                Duration: -1,
                IsDebuff: false,
                TargetOverride: TargetKind.Enemy,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("STRENGTH", amount)));
        }
        if (modelId == "SETUP_STRIKE" &&
            stats.TryGetProperty("strengthpower", out var setupStrength) && setupStrength.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                setupStrength.GetDecimal(),
                "SETUP_STRIKE",
                Duration: -1,
                TargetOverride: TargetKind.Self,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("SETUP_STRIKE", setupStrength.GetDecimal())));
        }
        if (stats.TryGetProperty("afterimagepower", out var afterimage) && afterimage.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                afterimage.GetDecimal(),
                "TRIGGER_CARD_PLAYED_BLOCK",
                Duration: -1,
                FutureValuePerTurn: afterimage.GetDecimal() * 2m));
        }
        if (stats.TryGetProperty("accuracypower", out var accuracy) && accuracy.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                accuracy.GetDecimal(),
                "SHIV_DAMAGE_BONUS",
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("SHIV_DAMAGE_BONUS", accuracy.GetDecimal())));
        }
        if (stats.TryGetProperty("thornspower", out var thorns) && thorns.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                thorns.GetDecimal(),
                "THORNS",
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("THORNS", thorns.GetDecimal())));
        }
        if (stats.TryGetProperty("dexteritypower", out var dexterity) && dexterity.GetDecimal() != 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                dexterity.GetDecimal(),
                "DEXTERITY",
                Duration: modelId == "ANTICIPATE" ? 1 : -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DEXTERITY", dexterity.GetDecimal())));
            if (modelId == "ANTICIPATE")
            {
                effects.Add(new EffectSpec(
                    EffectKind.ApplyStatus,
                    dexterity.GetDecimal(),
                    "ANTICIPATE",
                    Duration: 1,
                    TargetOverride: TargetKind.Self));
            }
        }
        if (stats.TryGetProperty("platingpower", out var plating) && plating.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                plating.GetDecimal(),
                "PLATING",
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("PLATING", plating.GetDecimal())));
        }
        if (modelId == "OUTBREAK" &&
            stats.TryGetProperty("poisonpower", out var outbreakPoison) && outbreakPoison.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.Outbreak,
                outbreakPoison.GetDecimal(),
                TargetOverride: TargetKind.AllEnemies));
        }
        if (modelId == "BOUNCING_FLASK" &&
            stats.TryGetProperty("poisonpower", out var bouncingPoison) && bouncingPoison.GetDecimal() > 0)
        {
            var bouncingRepeat = stats.TryGetProperty("repeat", out var bouncingRepeatElement)
                ? Math.Max(1, bouncingRepeatElement.GetInt32())
                : 1;
            effects.Add(new EffectSpec(
                EffectKind.RandomEnemyStatus,
                bouncingPoison.GetDecimal(),
                "POISON",
                Repeat: bouncingRepeat,
                IsDebuff: true,
                RandomSource: RngSnapshotSet.CombatTargets));
        }
        if (modelId != "OUTBREAK" && modelId != "BUBBLE_BUBBLE" && modelId != "BOUNCING_FLASK" &&
            stats.TryGetProperty("poisonpower", out var poison) && poison.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                poison.GetDecimal(),
                "POISON",
                Duration: -1,
                IsDebuff: true,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("POISON", poison.GetDecimal())));
        }
        if (stats.TryGetProperty("panachedamage", out var panache) && panache.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                panache.GetDecimal(),
                "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE",
                Duration: -1,
                XBonus: 5));
        }
        if (stats.TryGetProperty("weakpower", out var weak) && weak.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                weak.GetDecimal(),
                "WEAK",
                Duration: (int)weak.GetDecimal(),
                IsDebuff: true,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("WEAK", weak.GetDecimal())));
        }
        // UPPERCUT exposes one shared `power` preview while applying both
        // Weak and Vulnerable to the selected enemy.
        if (modelId == "UPPERCUT" &&
            stats.TryGetProperty("power", out var uppercutPower) && uppercutPower.GetDecimal() > 0)
        {
            var power = uppercutPower.GetDecimal();
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                power,
                "WEAK",
                Duration: 1,
                IsDebuff: true,
                TargetOverride: TargetKind.Enemy,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("WEAK", power)));
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                power,
                "VULNERABLE",
                Duration: 1,
                IsDebuff: true,
                TargetOverride: TargetKind.Enemy,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("VULNERABLE", power)));
        }
        if (modelId == "FEEL_NO_PAIN" &&
            stats.TryGetProperty("power", out var feelNoPainPower) && feelNoPainPower.GetDecimal() > 0)
        {
            var blockPerExhaust = feelNoPainPower.GetDecimal();
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                blockPerExhaust,
                "TRIGGER_CARD_EXHAUSTED_BLOCK",
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("TRIGGER_CARD_EXHAUSTED_BLOCK", blockPerExhaust)));
        }
        if (modelId == "THRASH" &&
            description.Contains("Exhaust a random Attack in your Hand", StringComparison.OrdinalIgnoreCase))
        {
            effects.Add(new EffectSpec(
                EffectKind.RandomExhaustCards,
                1,
                TargetOverride: TargetKind.Self,
                RandomSource: RngSnapshotSet.CombatCardSelection));
        }
        if (modelId == "EXPOSE" &&
            stats.TryGetProperty("power", out var exposePower) && exposePower.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(EffectKind.ClearEnemyBlockAndArtifact, 0, TargetOverride: TargetKind.Enemy));
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                exposePower.GetDecimal(),
                "VULNERABLE",
                Duration: (int)exposePower.GetDecimal(),
                IsDebuff: true,
                TargetOverride: TargetKind.Enemy,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("VULNERABLE", exposePower.GetDecimal())));
        }
        if (modelId is not "HAUNT" and not "BECKON" &&
            stats.TryGetProperty("hploss", out var hpLoss) && hpLoss.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.LoseHp, hpLoss.GetDecimal()));
        var delayedEnergy = modelId is
            "CHARGE_BATTERY" or "CONVERGENCE" or "DELAY" or "HEGEMONY" or
            "INVOKE" or "OUTMANEUVER" or "REFINE_BLADE" or "SIDESTEP";
        var nonImmediateEnergy = delayedEnergy || modelId is
            "DRUM_OF_BATTLE" or "PYRE" or "AUTOMATION" or "DANSE_MACABRE" or
            "DEMESNE" or "FRIENDSHIP" or "HEAVENLY_DRILL" or "MELANCHOLY" or
            "ORBIT" or "RESTLESSNESS" or "RIGHT_HAND_HAND" or "SUNDER" or "TRANSFIGURE" or "SCAVENGE" or "RELAX" or "BANSHEES_CRY";
        if (!nonImmediateEnergy &&
            stats.TryGetProperty("energy", out var energy) && energy.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.GainEnergy, energy.GetDecimal()));
        if (delayedEnergy &&
            stats.TryGetProperty("energy", out var nextTurnEnergy) && nextTurnEnergy.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                nextTurnEnergy.GetDecimal(),
                "SCHEDULED_ENERGY",
                Duration: 1,
                TargetOverride: TargetKind.Self));
            installedDirectObservedPower = true;
        }
        if (modelId == "PYRE" && stats.TryGetProperty("energy", out var pyreEnergy) && pyreEnergy.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                pyreEnergy.GetDecimal(),
                "TURN_START_ENERGY",
                Duration: -1,
                TargetOverride: TargetKind.Self,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("TURN_START_ENERGY", pyreEnergy.GetDecimal())));
        }
        if (modelId == "FLAME_BARRIER" || (stats.TryGetProperty("damageback", out var dmgBack) && dmgBack.GetDecimal() > 0))
        {
            var dbVal = stats.TryGetProperty("damageback", out var db) ? db.GetDecimal() : 4m;
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                dbVal,
                "TRIGGER_POWERED_ATTACK_RECEIVED_DAMAGE",
                Duration: 1,
                FutureValuePerTurn: dbVal * 2m));
        }
        if (modelId == "RAGE" || (stats.TryGetProperty("power", out var ragePower) && card.GetProperty("name").GetString() == "Rage"))
        {
            var rageVal = stats.TryGetProperty("power", out var rp) ? rp.GetDecimal() : 3m;
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                rageVal,
                "TRIGGER_ATTACK_PLAYED_BLOCK",
                Duration: 1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("TRIGGER_ATTACK_PLAYED_BLOCK", rageVal)));
        }
        if (stats.TryGetProperty("bufferpower", out var buffer) && buffer.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                buffer.GetDecimal(),
                "BUFFER",
                Duration: -1,
                FutureValuePerTurn: buffer.GetDecimal() * 12m));
        }
        if (stats.TryGetProperty("envenompower", out var envenom) && envenom.GetDecimal() > 0)
        {
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                envenom.GetDecimal(),
                "TRIGGER_ATTACK_UNBLOCKED_POISON",
                Duration: -1,
                FutureValuePerTurn: envenom.GetDecimal() * 3m));
        }
        // Piercing Wail applies a temporary all-enemy Strength reduction. The
        // live snapshot exposes both the effective negative Strength power and
        // the source power marker, so mirror both statuses for strict diff.
        if (modelId is not ("ENFEEBLING_TOUCH" or "DARK_SHACKLES" or "CRUSH_UNDER" or "MONARCHS_GAZE") &&
            stats.TryGetProperty("strengthloss", out var strengthLoss) && strengthLoss.GetDecimal() > 0)
        {
            var amount = strengthLoss.GetDecimal();
            var temporaryStrengthTarget = modelId == "MANGLE" ? TargetKind.Enemy : TargetKind.AllEnemies;
            var temporaryStrengthMarker = modelId switch
            {
                "MANGLE" => "MANGLE",
                "DYING_STAR" => "DYING_STAR",
                _ => "PIERCING_WAIL",
            };
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                -amount,
                "STRENGTH",
                Duration: 1,
                IsDebuff: true,
                TargetOverride: temporaryStrengthTarget,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("STRENGTH", -amount)));
            effects.Add(new EffectSpec(
                EffectKind.ApplyStatus,
                amount,
                temporaryStrengthMarker,
                Duration: 1,
                TargetOverride: temporaryStrengthTarget,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue(temporaryStrengthMarker, amount)));
        }
    }
    // ANGER family: "Add a copy of this card into your Discard Pile."
    if (description.Contains("Add a copy of this card into your Discard Pile", StringComparison.Ordinal))
    {
        var selfCopy = new CardState(
            card.GetProperty("instance_id").GetString()!,
            modelId, card.GetProperty("name").GetString() ?? "card",
            card.GetProperty("cost").GetInt32(), TargetKind.None,
            ImmutableArray<EffectSpec>.Empty, CardDestination.Discard,
            CardType: card.GetProperty("type").GetString());
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards, 1,
            GeneratedCard: selfCopy,
            GeneratedDestination: GeneratedCardDestination.DiscardPile));
    }

    // LEADING_STRIKE family: the engine exports the shiv count as a dynamic
    // var (stats.shivs); the description renders it as {Shivs:diff()}.
    if (card.TryGetProperty("stats", out var shivStats) &&
        shivStats.ValueKind == JsonValueKind.Object &&
        shivStats.TryGetProperty("shivs", out var shivsElement) &&
        shivsElement.GetInt32() > 0)
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards, shivsElement.GetInt32(),
            GeneratedCard: new CardState(
                "template:shiv", "SHIV", "Shiv", 0, TargetKind.Enemy,
                [new EffectSpec(EffectKind.Damage, 4)], CardDestination.Discard,
                CardType: "Attack"),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }

    // CLOAK_AND_DAGGER exposes its generated Shiv count as `cards` in the
    // runtime preview rather than the `shivs` field used by other cards.
    if ((modelId == "CLOAK_AND_DAGGER" || modelId == "BLADE_DANCE" || modelId == "UP_MY_SLEEVE") &&
        card.TryGetProperty("stats", out var cloakStats) &&
        cloakStats.ValueKind == JsonValueKind.Object &&
        cloakStats.TryGetProperty("cards", out var cloakCards) &&
        cloakCards.GetInt32() > 0)
    {
        effects.Add(new EffectSpec(
            EffectKind.GenerateCards, cloakCards.GetInt32(),
            GeneratedCard: new CardState(
                "template:shiv", "SHIV", "Shiv", 0, TargetKind.Enemy,
                [new EffectSpec(EffectKind.Damage, 4)], CardDestination.Discard,
                CardType: "Attack"),
            GeneratedDestination: GeneratedCardDestination.Hand));
    }

    // HAVOC family: "Play the top card of your Draw Pile. Exhaust it."
    if (description.Contains("Play the top card of your Draw Pile", StringComparison.OrdinalIgnoreCase) &&
        description.Contains("Exhaust", StringComparison.OrdinalIgnoreCase))
    {
        effects.Add(new EffectSpec(
            EffectKind.AutoPlayFromDrawPile, 1, "TOP_FORCE_EXHAUST"));
    }

    // HEADBUTT family: "Place a card from your discard pile on top of your
    // draw pile." The v0.111 headless engine resolves the discard-pile pick
    // without an interactive selector (verified: the top discard card lands
    // on the draw-pile top), so mirror it as the deterministic move effect.
    if (description.Contains("on top of your Draw Pile", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("on top of your draw pile", StringComparison.OrdinalIgnoreCase))
    {
        effects.Add(new EffectSpec(EffectKind.ChooseDiscardToDrawTop, 1));
    }

    // TRUE_GRIT family: "Exhaust 1 card{IfUpgraded:show:| at random}." The
    // engine consumes one CombatCardSelection draw even for the base variant
    // (trace rng_deltas, batch C1), so the "at random" marker routes to the
    // random handler; plain "Exhaust N cards" stays deterministic.
    var exhaustMatch = System.Text.RegularExpressions.Regex.Match(
        description, "Exhaust (?<count>\\d+) cards?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    if (exhaustMatch.Success && int.TryParse(exhaustMatch.Groups["count"].Value, out var exhaustCount) && exhaustCount > 0)
    {
        effects.Add(description.Contains("at random", StringComparison.OrdinalIgnoreCase)
            ? new EffectSpec(EffectKind.RandomExhaustCards, exhaustCount)
            : new EffectSpec(EffectKind.ExhaustCards, exhaustCount));
    }

    var target = card.GetProperty("target_type").GetString() switch
    {
        "AnyEnemy" => TargetKind.Enemy,
        "AllEnemies" => TargetKind.AllEnemies,
        "Self" => TargetKind.Self,
        _ => TargetKind.None
    };
    var keywords = card.TryGetProperty("keywords", out var keywordsElement) && keywordsElement.ValueKind == JsonValueKind.Array
        ? keywordsElement.EnumerateArray().Select(static k => k.GetString() ?? "").ToArray()
        : Array.Empty<string>();
    var destination = keywords.Any(static k => k.Equals("Exhaust", StringComparison.OrdinalIgnoreCase))
        ? CardDestination.Exhaust
        : CardDestination.Discard;
    if (modelId == "PARTICLE_WALL")
        destination = CardDestination.Hand;
    if (modelId == "SHINING_STRIKE")
        destination = CardDestination.DrawPileTop;
    // v0.111 turn-boundary keyword flags (card fixtures p1-card-retain /
    // p1-card-ethereal, batch C1): the Retain keyword keeps the card in hand
    // across the end-of-turn discard, and the Ethereal keyword exhausts a
    // non-status card at the turn boundary. Ethereal-keyword status cards
    // (e.g. Dazed) are verified to discard like normal cards at the
    // side-turn boundary, so they deliberately keep the default destination.
    var cardType = card.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
    var isStatusLike = cardType is "Status" or "Curse";
    var installedGenericPower = false;
    if (cardType is "Power" or "能力" &&
        !effects.Any(static effect => effect.Kind == EffectKind.ApplyStatus))
    {
        var powerAmount = 1m;
        if (stats.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in stats.EnumerateObject())
            {
                if (property.Name.EndsWith("power", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetDecimal(out var candidate) && candidate > 0m)
                {
                    powerAmount = candidate;
                    break;
                }
            }
        }
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            powerAmount,
            modelId,
            Duration: -1,
            TargetOverride: TargetKind.Self));
        installedGenericPower = true;
    }
    if (modelId is "HELLRAISER" or "ONE_TWO_PUNCH" or "PHANTOM_BLADES" or "WELL_LAID_PLANS" or "FLANKING" or "DISMANTLE" or "SPITE")
    {
        // The direct fixture proves Power installation only. Keep the report
        // Estimated until a positive trigger/choice fixture proves the full
        // listener behavior against the live engine.
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            UnsupportedReason: "persistent_power_trigger_fixture_pending"));
    }
    if (installedGenericPower)
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            UnsupportedReason: "generic_power_trigger_fixture_pending"));
    }
    if (installedDirectObservedPower)
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            UnsupportedReason: "direct_observed_power_trigger_fixture_pending"));
    }
    var cardEffects = effects.ToImmutable();
    // NOTE: v0.111 Ethereal-keyword status cards (e.g. Dazed) are verified to
    // discard like normal cards at the side-turn boundary, NOT to exhaust, so
    // the keyword is deliberately not mapped to ExhaustAtTurnEnd here.
    return new CardState(
        card.GetProperty("instance_id").GetString()!,
        card.GetProperty("id").GetString()!.Replace("CARD.", "", StringComparison.Ordinal),
        card.GetProperty("name").GetString() ?? "card",
        card.GetProperty("cost").GetInt32(),
        target,
        cardEffects,
        destination,
        RestrictionReason: cardEffects.Length == 0 ? "shadow_unsupported_card_effect" : null,
        CardType: cardType,
        RetainAtTurnEnd: keywords.Any(static k => k.Equals("Retain", StringComparison.OrdinalIgnoreCase)),
        ExhaustAtTurnEnd: !isStatusLike && keywords.Any(static k => k.Equals("Ethereal", StringComparison.OrdinalIgnoreCase)),
        CostsX: isXCost,
        Rarity: card.TryGetProperty("rarity", out var rarity) ? rarity.GetString() : null);
}

static ImmutableArray<CardState> InfernalBladeEligibleAttackPool()
{
    // v0.111 Ironclad CardPool.GetUnlockedCards -> FilterForPlayerCount ->
    // FilterForCombat. Starter/Ancient cards, Feed (CanBeGeneratedInCombat
    // false), and the absent Grapple runtime model are excluded. The resulting
    // 33-card pool is exactly what GetDistinctForCombat shuffles before Take(1).
    string[] ids =
    [
        "ANGER", "SWORD_BOOMERANG", "POMMEL_STRIKE", "BODY_SLAM", "MOLTEN_FIST",
        "THUNDERCLAP", "TWIN_STRIKE", "IRON_WAVE", "HEADBUTT", "BREAKTHROUGH",
        "SETUP_STRIKE", "PERFECTED_STRIKE", "CINDER", "BULLY", "WHIRLWIND",
        "SPITE", "RAMPAGE", "DISMANTLE", "ASHEN_STRIKE", "PILLAGE",
        "HEMOKINESIS", "UPPERCUT", "UNRELENTING", "FIGHT_ME", "HOWL_FROM_BEYOND",
        "STOMP", "BLUDGEON", "PACTS_END", "CONFLAGRATION", "THRASH",
        "TEAR_ASUNDER", "FIEND_FIRE", "MANGLE"
    ];
    return ids.Select(id => new CardState(
        $"template:{id.ToLowerInvariant()}",
        id,
        id,
        1,
        TargetKind.Enemy,
        [],
        CardType: "Attack")).ToImmutableArray();
}

static ImmutableArray<CardState> PlaceholderGeneratedPool(string sourceId, int count, string cardType) =>
    Enumerable.Range(0, count)
        .Select(index => new CardState(
            $"template:{sourceId.ToLowerInvariant()}:{index:D3}",
            $"UNKNOWN_{sourceId}_{index:D3}",
            $"Unknown {sourceId} outcome {index:D3}",
            1,
            cardType == "Power" ? TargetKind.Self : TargetKind.None,
            [],
            CardType: cardType))
        .ToImmutableArray();

static PotionState BuildPotion(JsonElement potion)
{
    var id = potion.GetProperty("id").GetString() ?? potion.GetProperty("name").GetString() ?? "UNKNOWN_POTION";
    var cleanId = id.Replace("POTION.", "", StringComparison.OrdinalIgnoreCase);
    var dynamicVars = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    if (potion.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in vars.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Number)
                dynamicVars[property.Name] = property.Value.GetDecimal();
    }
    var semantics = PotionSemanticCatalog.Resolve(cleanId, dynamicVars);

    var target = potion.TryGetProperty("target_type", out var targetElement)
        ? targetElement.GetString() switch
        {
            "AnyEnemy" => TargetKind.Enemy,
            "AllEnemies" => TargetKind.AllEnemies,
            "Self" or "AnyPlayer" => TargetKind.Self,
            _ => TargetKind.None
        }
        : cleanId.Equals("FIRE_POTION", StringComparison.OrdinalIgnoreCase) ? TargetKind.Enemy : TargetKind.None;

    var result = new PotionState(
        potion.GetProperty("instance_id").GetString()!,
        cleanId,
        potion.GetProperty("name").GetString() ?? id,
        target,
        semantics.Effects,
        0m,
        PriorityHint: semantics.IsSupported ? semantics.Effects.Sum(static effect => effect.Amount) : null,
        RestrictionReason: semantics.UnsupportedReason);
    return result;
}

static RngSnapshotSet BuildRngStreams(JsonElement counters)
{
    if (counters.ValueKind != JsonValueKind.Object) return RngSnapshotSet.Empty;
    var builder = ImmutableDictionary.CreateBuilder<string, RngStreamSnapshot>(StringComparer.Ordinal);
    foreach (var property in counters.EnumerateObject())
        builder[property.Name] = new RngStreamSnapshot(property.Name, 0, 0, 0, 0, property.Value.GetInt32(), IsKnown: false);
    return new RngSnapshotSet(builder.ToImmutable());
}

static ImmutableDictionary<string, StatusState> BuildStatuses(JsonElement powers)
{
    if (powers.ValueKind != JsonValueKind.Array)
        return ImmutableDictionary<string, StatusState>.Empty;
    var builder = ImmutableDictionary.CreateBuilder<string, StatusState>(StringComparer.OrdinalIgnoreCase);
    foreach (var power in powers.EnumerateArray())
    {
        var id = StatusIdForPower(power.GetProperty("id").GetString()!);
        var rawAmount = power.GetProperty("amount").GetInt32();
        var amount = id == "SHRINK" ? Math.Abs(rawAmount) : rawAmount;
        var duration = id is "WEAK" or "VULNERABLE" ? amount
            : id == "SHRINK" ? 3
            : id is "TRIGGER_ATTACK_PLAYED_BLOCK" or "TRIGGER_POWERED_ATTACK_RECEIVED_DAMAGE" ? 1
            : -1;
        builder[id] = new StatusState(
            id,
            amount,
            Duration: duration,
            IsDebuff: id is "WEAK" or "VULNERABLE" or "POISON" or "DOOM");
        if (id == "SKILLS_COST_ZERO")
        {
            builder["SKILLS_EXHAUST_ON_PLAY"] = new StatusState("SKILLS_EXHAUST_ON_PLAY", amount);
        }
    }
    return builder.ToImmutable();
}

static string StatusIdForPower(string id) => CanonicalPowerId(id) switch
{
    "DEMON_FORM" => "TURN_START_STRENGTH",
    "RUPTURE" => "TRIGGER_PLAYER_HP_LOST_STRENGTH",
    "AFTERIMAGE" => "TRIGGER_CARD_PLAYED_BLOCK",
    // Simulator-internal status ids backing live engine powers (see SyncPowerState).
    "ACCURACY" => "SHIV_DAMAGE_BONUS",
    "PANACHE" => "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE",
    "RAGE" => "TRIGGER_ATTACK_PLAYED_BLOCK",
    "FLAME_BARRIER" => "TRIGGER_POWERED_ATTACK_RECEIVED_DAMAGE",
    "CORRUPTION" => "SKILLS_COST_ZERO",
    "INFINITE_BLADES" => "TURN_START_GENERATE_SHIV",
    "ENVENOM" => "TRIGGER_ATTACK_UNBLOCKED_POISON",
    "BUFFER" => "BUFFER",
    "SHRINK_POWER" => "SHRINK",
    _ => CanonicalPowerId(id),
};

static string? FindPlayedHandCardType(JsonElement observation, string normalizedAction)
{
    // normalizedAction is "play_card:<instance_id>:<target_id>"; the played card
    // is still present in the pre-action hand, so match by instance-id prefix.
    if (!normalizedAction.StartsWith("play_card:", StringComparison.Ordinal)) return null;
    var rest = normalizedAction["play_card:".Length..];
    foreach (var card in observation.GetProperty("hand").EnumerateArray())
    {
        var instanceId = card.GetProperty("instance_id").GetString();
        if (instanceId is not null && rest.StartsWith(instanceId + ":", StringComparison.Ordinal))
            return card.GetProperty("type").GetString();
    }
    return null;
}

static decimal StatusAmount(ImmutableDictionary<string, StatusState> statuses, string id) =>
    statuses.TryGetValue(id, out var status) ? status.Amount : 0m;

static decimal ProjectedEnemyIntentDamage(CreatureState enemy, PlayerState player, IntentState intent)
{
    if (intent.DamagePerHit <= 0m) return 0m;
    var strength = StatusAmount(enemy.Statuses, "STRENGTH") -
                   StatusAmount(enemy.Statuses, "TEMP_STRENGTH_LOSS");
    var debilitate = StatusAmount(enemy.Statuses, "DEBILITATE") > 0;
    var weak = StatusAmount(enemy.Statuses, "WEAK") > 0 ? (debilitate ? 0.5m : 0.75m) : 1m;
    var vulnerable = StatusAmount(player.Statuses, "VULNERABLE") > 0 ? 1.5m : 1m;
    var colossus = StatusAmount(enemy.Statuses, "VULNERABLE") > 0 &&
                   StatusAmount(player.Statuses, "REDUCE_VULNERABLE_ATTACK_DAMAGE") > 0
        ? 0.5m
        : 1m;
    return Math.Max(0m, decimal.Floor(
        (intent.DamagePerHit + strength) * weak * vulnerable * colossus));
}

static IEnumerable<PowerState> BuildPowers(JsonElement powers, string fallbackOwner)
{
    if (powers.ValueKind != JsonValueKind.Array) yield break;
    foreach (var power in powers.EnumerateArray())
    {
        var phases = power.TryGetProperty("trigger_phases", out var phaseElement) && phaseElement.ValueKind == JsonValueKind.Array
            ? phaseElement.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).Where(static item => item.Length > 0).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        yield return new PowerState(
            power.GetProperty("id").GetString()!,
            ReadNullableString(power, "owner_id") ?? fallbackOwner,
            ReadNullableString(power, "applier_id"),
            power.GetProperty("amount").GetDecimal(),
            ReadStringMap(power, "dynamic_vars").ToImmutableDictionary(StringComparer.Ordinal),
            ReadDecimalMap(power, "internal_counters").ToImmutableDictionary(StringComparer.Ordinal),
            phases,
            ReadNullableString(power, "source"),
            SemanticSupportStatus.StateCapturedOnly,
            EvidenceLevel.LiveObserved,
            ReadNullableString(power, "source_version"));
    }
}

static RelicState BuildRelic(JsonElement relic, Dictionary<string, (int? UsesCombat, int? UsesTurn)> teacherRelicUses)
{
    var id = relic.GetProperty("id").GetString()!;
    teacherRelicUses.TryGetValue(id, out var teacherUses);
    var support = id switch
    {
        "ANCHOR" or "VAJRA" or "NUNCHAKU" or "PEN_NIB" or "ORICHALCUM" or
        "ART_OF_WAR" or "HAPPY_FLOWER" or "INCENSE_BURNER" or "SUNDIAL" or
        "CENTENNIAL_PUZZLE" or "TOUGH_BANDAGES" or "TUNGSTEN_ROD" or
        "UNCEASING_TOP" or "BRONZE_SCALES" or "BLOOD_VIAL" or "BRIMSTONE" or
        "SHURIKEN" or "KUNAI" or "ORNAMENTAL_FAN" or "LETTER_OPENER" or
        "RAINBOW_RING" or "MERCURY_HOURGLASS" or "SAI" or "MR_STRUGGLES" or
        "CANDELABRA" or "CHANDELIER" or "FAKE_HAPPY_FLOWER" or "FAKE_ORICHALCUM" or
        "CLOAK_CLASP" or "KUSARIGAMA" or "PARRYING_SHIELD" or "SCREAMING_FLAGON" or
        "RIPPLE_BASIN" or "PAELS_TEARS" or "STRIKE_DUMMY" or "FAKE_STRIKE_DUMMY" or
        "SNECKO_EYE" or "WHISPERING_EARRING" or "NINJA_SCROLL" or
        "SELF_FORMING_CLAY" or "STONE_CALENDAR" or "JOSS_PAPER" or "POCKETWATCH" or "ICE_CREAM" or
        "DELICATE_FROND" or "BELT_BUCKLE" or "FAKE_SNECKO_EYE" or
        "THE_BOOT" or "VAMBRACE" or "BRILLIANT_SCARF" or "PANTOGRAPH" or
        "FESTIVE_POPPER" or "ROYAL_POISON" or "RED_MASK" or "TWISTED_FUNNEL" or
        "BREAD" or "PAELS_FLESH" or
        "PENDULUM" or "POLLINOUS_CORE" or "TOASTY_MITTENS" or "FENCING_MANUAL" or
        "SWORD_OF_STONE" or "FISHING_ROD" or "TOY_BOX" or "WONGOS_MYSTERY_TICKET" or
        "DAUGHTER_OF_THE_WIND" or "GAME_PIECE" or "LOST_WISP" or "PERMAFROST" or
        "INTIMIDATING_HELMET" or "IVORY_TILE" or "IRON_CLUB" or "TUNING_FORK" or
        "HORN_CLEAT" or "CAPTAINS_WHEEL" or "SPARKLING_ROUGE" or "RING_OF_THE_DRAKE" or "PAELS_BLOOD" or
        "BLESSED_ANTLER" or "BLOOD_SOAKED_ROSE" or "ECTOPLASM" or "SOZU" or "PRISMATIC_GEM" or
        "SPIKED_GAUNTLETS" or "BIG_MUSHROOM" or "VENERABLE_TEA_SET" or "FAKE_VENERABLE_TEA_SET" or
        "HELICAL_DART" or "BURNING_STICKS" or "CHARONS_ASHES" or
        "HELICAL_DART" or "TUNING_FORK" or
        "BEATING_REMNANT" or "SEAL_OF_GOLD" or "VELVET_CHOKER" or
        "HAND_DRILL" or "REPTILE_TRINKET" or "STURDY_CLAMP" or
        "FIDDLE" or "VENERABLE_TEA_SET" or "FAKE_VENERABLE_TEA_SET" or "SPIKED_GAUNTLETS" or
        "PUMPKIN_CANDLE" or "BIG_MUSHROOM" or "BLOOD_SOAKED_ROSE" or
        // Combat-start effects carried inside the snapshot (ANCHOR methodology).
        "FAKE_BLOOD_VIAL" or "FAKE_ANCHOR" or "VERY_HOT_COCOA" or "PHILOSOPHERS_STONE" or
        "RUNIC_CAPACITOR" or "INFUSED_CORE" or "SYMBIOTIC_VIRUS" or "TEA_OF_DISCOURTESY"
            => RelicEffectSupportStatus.SimulatorSupported,
        _ => RelicEffectSupportStatus.StateCapturedOnly,
    };
    return new RelicState(
        id,
        ReadNullableInt(relic, "counter"),
        ReadDecimalMap(relic, "dynamic_vars").ToImmutableDictionary(StringComparer.Ordinal),
        IsEnabled: true,
        UsesThisCombat: teacherUses.UsesCombat ?? 0,
        UsesThisTurn: teacherUses.UsesTurn ?? 0,
        SupportStatus: support,
        EvidenceLevel: RelicEvidenceLevel.LiveObserved,
        Name: ReadNullableString(relic, "name"),
        Description: ReadNullableString(relic, "description"));
}

static IEnumerable<JsonElement> ReadActualPowers(JsonElement observation)
{
    if (observation.TryGetProperty("player_powers", out var playerPowers) && playerPowers.ValueKind == JsonValueKind.Array)
        foreach (var power in playerPowers.EnumerateArray()) yield return power;
    if (!observation.TryGetProperty("enemies", out var enemies) || enemies.ValueKind != JsonValueKind.Array)
        yield break;
    foreach (var enemy in enemies.EnumerateArray())
        if (enemy.TryGetProperty("powers", out var enemyPowers) && enemyPowers.ValueKind == JsonValueKind.Array)
            foreach (var power in enemyPowers.EnumerateArray()) yield return power;
}

static string CanonicalPowerId(string id)
{
    var normalized = id.ToUpperInvariant();
    return normalized.EndsWith("_POWER", StringComparison.Ordinal) ? normalized[..^6] : normalized;
}

static string? ReadNullableString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;

static int? ReadNullableInt(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
        ? value
        : null;

static decimal? ReadNullableDecimal(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value)
        ? value
        : null;

static decimal? ReadNestedNullableDecimal(JsonElement element, string objectName, string propertyName)
{
    if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        return null;
    return ReadNullableDecimal(nested, propertyName);
}

static ImmutableSortedDictionary<string, long> ReadLongMap(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object)
        return ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal);
    var builder = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
    foreach (var property in map.EnumerateObject())
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
            builder[property.Name] = value;
    return builder.ToImmutable();
}

static Dictionary<string, decimal> ReadDecimalMap(JsonElement element, string propertyName)
{
    var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
    if (!element.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object) return result;
    foreach (var property in map.EnumerateObject())
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var value))
            result[property.Name] = value;
    return result;
}

static Dictionary<string, string> ReadStringMap(JsonElement element, string propertyName)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!element.TryGetProperty(propertyName, out var map) || map.ValueKind != JsonValueKind.Object) return result;
    foreach (var property in map.EnumerateObject())
        result[property.Name] = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.GetRawText();
    return result;
}

static CardState ShivTemplate() => new(
    "template:shiv",
    "SHIV",
    "Shiv",
    0,
    TargetKind.Enemy,
    [new EffectSpec(EffectKind.Damage, 4)],
    Destination: CardDestination.Exhaust,
    CardType: "Attack");

internal sealed record ShadowChanceQuality(
    bool ChancePresent,
    string RandomOperator,
    bool ProbabilityKnown,
    string OutcomeQuality,
    decimal? ProbabilityMassCovered,
    decimal? EffectiveSampleSize,
    decimal? ConfidenceIntervalLow,
    decimal? ConfidenceIntervalHigh,
    ImmutableSortedDictionary<string, long> RngConsumptionVector,
    bool BranchEnumerated,
    string ComparisonScope,
    string IdentityComparison,
    decimal? ReportedProbability);
