using System.Collections.Immutable;
using System.Text.Json;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Simulation;
using STS2BestChoice.Core.Scoring;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: ShadowDiff <engine-trace.jsonl> <report.json> [action-ordinal]");
    return 2;
}

var rows = File.ReadLines(args[0])
    .Where(static line => !string.IsNullOrWhiteSpace(line))
    .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
    .ToArray();
var actionOrdinal = args.Length == 3 ? int.Parse(args[2]) : 0;
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
    .Where(static item => item.row.TryGetProperty("public_observation", out _))
    .Select(static item => item.index)
    .ToArray();
var actionIndex = actionOrdinal >= 0 && actionOrdinal < actionIndexes.Length
    ? actionIndexes[actionOrdinal]
    : -1;
if (actionIndex < 1)
    throw new InvalidOperationException("Trace does not contain a play_card, use_potion or end_turn row with a public post-state.");

var actionRow = rows[actionIndex];
var preRow = rows.Take(actionIndex).Last(row => row.TryGetProperty("public_observation", out _));
var pre = preRow.GetProperty("public_observation");
var actual = actionRow.GetProperty("public_observation");
// Action responses can be captured before the engine finishes moving the card
// to discard/exhaust or settling a generated Power. Prefer the final public
// snapshot before the next action when one is present.
for (var index = actionIndex + 1; index < rows.Length; index++)
{
    if (rows[index].TryGetProperty("normalized_action_id", out var nextAction) &&
        IsGameplayActionId(nextAction.GetString()))
        break;
    if (rows[index].TryGetProperty("public_observation", out var settledObservation))
        actual = settledObservation;
}
var normalizedAction = actionRow.GetProperty("normalized_action_id").GetString()!;
var isEndTurn = normalizedAction.Equals("end_turn", StringComparison.Ordinal) ||
                normalizedAction.StartsWith("end_turn:", StringComparison.Ordinal);
var isPotionAction = !isEndTurn && normalizedAction.StartsWith("use_potion:", StringComparison.Ordinal);

JsonElement teacherDrawPile = default;
var hasTeacherDrawPile = false;
if (isEndTurn)
{
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

// Count attack cards played earlier in the same turn. The public observation
// does not export turn history counters, so end-turn projections rebuild them
// from the realized trace (needed for turn-start effects like Art of War).
var attacksPlayedThisTurn = 0;
if (isEndTurn)
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
        var playedType = FindPlayedHandCardType(earlierPre, earlierAction);
        if (playedType is "Attack" or "攻击")
            attacksPlayedThisTurn++;
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
var relics = playerElement.GetProperty("relics").EnumerateArray().Select(BuildRelic).ToImmutableArray();
// When Pen Nib is charged (counter >= 9), the CLI damage preview for attack
// cards already includes the doubling, so BuildCard must halve it back.
var penNibCharged = relics.Any(static relic =>
    relic.Id.Equals("PEN_NIB", StringComparison.OrdinalIgnoreCase) && (relic.Counter ?? 0) >= 9);
var cards = pre.GetProperty("hand").EnumerateArray()
    .Select(card => BuildCard(card, playerStatuses, penNibCharged))
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
        BuildIntents(enemy, BuildStatuses(enemyPowers), playerStatuses),
        0m);
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

ImmutableArray<CardState> drawPile;
if (isEndTurn && hasTeacherDrawPile)
{
    // The teacher snapshot preserves the real draw-pile order (index 0 = top),
    // so the projected next-turn hand carries the same stable instance IDs as
    // the live engine. Effects are not needed for the turn-boundary checkpoint
    // because no card is played between the projection start and the compare.
    drawPile = teacherDrawPile.EnumerateArray().Select(BuildTeacherPileCard).ToImmutableArray();
}
else
{
    drawPile = Enumerable.Range(0, preDrawCount)
        .Select(i => new CardState($"dummy_draw_{i}", "DUMMY", "Dummy", 0, TargetKind.None, [], CardDestination.Discard))
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
var snapshot = CombatSnapshot.Create(
    "shadow-diff",
    player,
    enemies,
    cards,
    drawPile: drawPile,
    discardPile: Enumerable.Range(0, preDiscardCount).Select(i => new CardState($"dummy_disc_{i}", "DUMMY", "Dummy", 0, TargetKind.None, [], CardDestination.Discard)).ToImmutableArray(),
    exhaustPile: preExhaustPile,
    potions: potions,
    rngStreams: rngStreams,
    round: pre.GetProperty("round").GetInt32(),
    globalRestrictions: [],
    historyCounters: isEndTurn ? new CombatHistoryCounters(AttacksPlayedThisTurn: attacksPlayedThisTurn) : null,
    relics: relics,
    powers: powers,
    provenance: new SnapshotProvenance(
        "v0.111.0", "41cef1ea",
        "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
        "0.2.0", "DeterministicSimulator", 1, ObservationView.Teacher,
        "ShadowDiff", "game-runtime-v0111", "core-default", "1", "none"));

var state = MutableCombatState.FromSnapshot(snapshot);
var simulator = new DeterministicSimulator();
var projected = isEndTurn
    ? simulator.ProjectToNextPlayerTurn(state)
    : isPotionAction
        ? simulator.UsePotion(state, potions.Single(potion => potion.InstanceId == sourceInstanceId), targetId)
        : simulator.PlayCard(
            state,
            selected!,
            targetId,
            null,
            // v0.111 exposes a just-played Power card in a transient state: it
            // is absent from both hand and discard until a later transition.
            finalizeDestination: !string.Equals(selected!.CardType, "Power", StringComparison.OrdinalIgnoreCase));

var fields = new List<object>();
Compare("round", pre.GetProperty("round").GetInt32() + (isEndTurn ? 1 : 0), actual.GetProperty("round").GetInt32());
Compare("player.hp", projected.Player.Hp, actual.GetProperty("player").GetProperty("hp").GetDecimal());
Compare("player.block", projected.Player.Block, actual.GetProperty("player").GetProperty("block").GetDecimal());
Compare("player.energy", projected.Player.Energy, actual.GetProperty("energy").GetInt32());
Compare("draw_pile_count", projected.DrawPile.Count, actual.TryGetProperty("draw_pile_count", out var actDpc) ? actDpc.GetInt32() : 0);
Compare("discard_pile_count", projected.DiscardPile.Count, actual.GetProperty("discard_pile_count").GetInt32());
Compare("exhaust_pile_count",
    projected.ExhaustPile.Count,
    hasTeacherExhaustCount ? teacherExhaustCount : (actual.TryGetProperty("exhaust_pile_count", out var actExp) ? actExp.GetInt32() : 0));
if (isEndTurn && !hasTeacherDrawPile)
{
    // Without the teacher draw-pile order the projected hand contains placeholder
    // cards, so only the count can be compared at this checkpoint.
    Compare("hand_count", projected.Hand.Count,
        actual.GetProperty("hand").EnumerateArray().Count());
}
else
{
    Compare("hand", projected.Hand.Select(static card => card.InstanceId).ToArray(),
        actual.GetProperty("hand").EnumerateArray().Select(static card => card.GetProperty("instance_id").GetString()).ToArray());
}

foreach (var enemy in projected.Enemies)
{
    var actualEnemy = actual.GetProperty("enemies").EnumerateArray().Single(item => item.GetProperty("instance_id").GetString() == enemy.Id);
    Compare($"enemy.{enemy.Id}.hp", enemy.Hp, actualEnemy.GetProperty("hp").GetDecimal());
    Compare($"enemy.{enemy.Id}.block", enemy.Block, actualEnemy.GetProperty("block").GetDecimal());

    // Compare enemy powers / statuses
    var actualEnemyPowers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    if (actualEnemy.TryGetProperty("powers", out var powersElem) && powersElem.ValueKind == JsonValueKind.Array)
    {
        foreach (var p in powersElem.EnumerateArray())
        {
            var pName = p.GetProperty("name").GetString() ?? "";
            var pAmount = p.GetProperty("amount").GetInt32();
            if (pName.Equals("Vulnerable", StringComparison.OrdinalIgnoreCase) || pName.Equals("易伤", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["VULNERABLE"] = pAmount;
            else if (pName.Equals("Weak", StringComparison.OrdinalIgnoreCase) || pName.Equals("虚弱", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["WEAK"] = pAmount;
            else if (pName.Equals("Poison", StringComparison.OrdinalIgnoreCase) || pName.Equals("中毒", StringComparison.OrdinalIgnoreCase))
                actualEnemyPowers["POISON"] = pAmount;
            else
                actualEnemyPowers[pName.ToUpperInvariant()] = pAmount;
        }
    }

    foreach (var status in enemy.Statuses)
    {
        var actAmount = actualEnemyPowers.TryGetValue(status.Key, out var val) ? (decimal)val : 0m;
        Compare($"enemy.{enemy.Id}.status.{status.Key}.amount", status.Value.Amount, actAmount);
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
Compare("power.count", projected.Powers.Count, actualPowers.Length);
foreach (var actualPower in actualPowers)
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
Compare("potion.ids", projected.Potions.Select(static potion => potion.InstanceId).Order().ToArray(),
    actual.GetProperty("player").GetProperty("potions").EnumerateArray().Select(static potion => potion.GetProperty("instance_id").GetString()).Order().ToArray());

var isTerminal = projected.Player.Hp <= 0m || projected.Enemies.All(static e => !e.IsAlive);
Compare("combat.is_terminal", isTerminal, actual.GetProperty("decision").GetString() != "combat_play");

var mismatchCount = fields.Count(field => (bool)field.GetType().GetProperty("match")!.GetValue(field)! == false);
var report = new
{
    schema_version = 1,
    game_version = "v0.111.0",
    game_commit = "41cef1ea",
    assembly_sha256 = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    simulator = "STS2BestChoice.Core.DeterministicSimulator",
    action_ordinal = actionOrdinal,
    action_kind = isEndTurn ? "end_turn" : isPotionAction ? "use_potion" : "play_card",
    normalized_action_id = normalizedAction,
    draw_pile_source = isEndTurn ? (hasTeacherDrawPile ? "teacher_snapshot" : "placeholder_count_only") : "not_applicable",
    attacks_played_this_turn = isEndTurn ? attacksPlayedThisTurn : (int?)null,
    confidence = projected.Confidence.ToString(),
    match = mismatchCount == 0,
    mismatch_count = mismatchCount,
    fields
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
return mismatchCount == 0 ? 0 : 1;

void Compare<T>(string name, T projectedValue, T actualValue)
{
    var match = JsonSerializer.Serialize(projectedValue) == JsonSerializer.Serialize(actualValue);
    fields.Add(new { field = name, projected = projectedValue, actual = actualValue, match });
}

static CardState BuildTeacherPileCard(JsonElement card)
{
    // Identity-only rebuild from the teacher snapshot. The instance ID and the
    // card type are real; effects stay empty because the turn-boundary compare
    // happens before any further card play.
    var instanceId = card.GetProperty("instance_id").GetString()!;
    var modelId = (card.GetProperty("id").GetString() ?? "UNKNOWN").Replace("CARD.", "", StringComparison.Ordinal);
    var cardType = card.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
    return new CardState(
        instanceId,
        modelId,
        modelId,
        0,
        TargetKind.None,
        [],
        CardType: cardType);
}

static ImmutableArray<IntentState> BuildIntents(
    JsonElement enemy,
    ImmutableDictionary<string, StatusState> enemyStatuses,
    ImmutableDictionary<string, StatusState> playerStatuses)
{
    var builder = ImmutableArray.CreateBuilder<IntentState>();
    if (!enemy.TryGetProperty("intents", out var intentsElement) || intentsElement.ValueKind != JsonValueKind.Array)
        return builder.ToImmutable();
    foreach (var intent in intentsElement.EnumerateArray())
    {
        var type = intent.GetProperty("type").GetString() ?? "";
        var previewDamage = intent.TryGetProperty("damage", out var damageElement) ? damageElement.GetDecimal() : 0m;
        var hits = intent.TryGetProperty("hits", out var hitsElement) ? hitsElement.GetInt32() : (previewDamage > 0m ? 1 : 0);
        var weakFactor = StatusAmount(enemyStatuses, "WEAK") > 0 ? 0.75m : 1m;
        var vulnerableFactor = StatusAmount(playerStatuses, "VULNERABLE") > 0 ? 1.5m : 1m;
        var damage = previewDamage <= 0m
            ? 0m
            : Math.Round(previewDamage / (weakFactor * vulnerableFactor), MidpointRounding.AwayFromZero);
        string? restriction = null;
        if (!type.Equals("Attack", StringComparison.OrdinalIgnoreCase))
            restriction = $"enemy intent '{type}' effects are not mirrored from the public observation";
        builder.Add(new IntentState(type, damage, hits, [], restriction));
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

static CardState BuildCard(JsonElement card, ImmutableDictionary<string, StatusState> playerStatuses, bool penNibCharged)
{
    var effects = ImmutableArray.CreateBuilder<EffectSpec>();
    var modelId = card.GetProperty("id").GetString()!.Replace("CARD.", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    if (modelId == "BARRICADE")
    {
        effects.Add(new EffectSpec(
            EffectKind.ApplyStatus,
            1,
            "BARRICADE",
            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("BARRICADE", 1)));
    }
    if (card.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
    {
        // CLI stats.damage/stats.block are live previews: they already include
        // additive STRENGTH/VIGOR (damage) and DEXTERITY (block) modifiers,
        // a charged Pen Nib doubling (attack damage), while multiplicative
        // effects (Vulnerable/Weak/Frail) are only shown in damage_by_target.
        // The simulator re-applies every modifier, so strip the preview part
        // to recover the base value it expects.
        if (stats.TryGetProperty("damage", out var damage) && damage.GetDecimal() > 0)
        {
            var additive = StatusAmount(playerStatuses, "STRENGTH") + StatusAmount(playerStatuses, "VIGOR");
            // Accuracy's Shiv bonus is also already inside the live preview for
            // Shiv cards, and the simulator re-applies it via SHIV_DAMAGE_BONUS.
            if (modelId.Equals("SHIV", StringComparison.OrdinalIgnoreCase))
                additive += StatusAmount(playerStatuses, "SHIV_DAMAGE_BONUS");
            var baseDamage = damage.GetDecimal() - additive;
            var isAttack = card.GetProperty("type").GetString() is "Attack" or "攻击";
            if (penNibCharged && isAttack)
                baseDamage /= 2m;
            if (baseDamage > 0)
                effects.Add(new EffectSpec(EffectKind.Damage, baseDamage));
        }
        if (stats.TryGetProperty("block", out var block) && block.GetDecimal() > 0)
        {
            var baseBlock = block.GetDecimal() - StatusAmount(playerStatuses, "DEXTERITY");
            if (baseBlock > 0)
                effects.Add(new EffectSpec(EffectKind.Block, baseBlock));
        }
        if (stats.TryGetProperty("vulnerablepower", out var vulnerable) && vulnerable.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.ApplyStatus, vulnerable.GetDecimal(), "VULNERABLE", Duration: vulnerable.GetInt32(), IsDebuff: true));
        if (stats.TryGetProperty("strengthpower", out var strength) && strength.GetDecimal() > 0)
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
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue(statusId, strength.GetDecimal())));
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
                Duration: -1,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DEXTERITY", dexterity.GetDecimal())));
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
        if (stats.TryGetProperty("poisonpower", out var poison) && poison.GetDecimal() > 0)
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
        if (stats.TryGetProperty("hploss", out var hpLoss) && hpLoss.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.LoseHp, hpLoss.GetDecimal()));
        if (stats.TryGetProperty("energy", out var energy) && energy.GetDecimal() > 0)
            effects.Add(new EffectSpec(EffectKind.GainEnergy, energy.GetDecimal()));
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
    var cardEffects = effects.ToImmutable();
    return new CardState(
        card.GetProperty("instance_id").GetString()!,
        card.GetProperty("id").GetString()!.Replace("CARD.", "", StringComparison.Ordinal),
        card.GetProperty("name").GetString() ?? "card",
        card.GetProperty("cost").GetInt32(),
        target,
        cardEffects,
        destination,
        RestrictionReason: cardEffects.Length == 0 ? "shadow_unsupported_card_effect" : null,
        CardType: card.GetProperty("type").GetString(),
        Rarity: card.TryGetProperty("rarity", out var rarity) ? rarity.GetString() : null);
}

static PotionState BuildPotion(JsonElement potion)
{
    var id = potion.GetProperty("id").GetString() ?? potion.GetProperty("name").GetString() ?? "UNKNOWN_POTION";
    var cleanId = id.Replace("POTION.", "", StringComparison.OrdinalIgnoreCase);
    var effects = ImmutableArray.CreateBuilder<EffectSpec>();
    
    decimal GetVar(string name)
    {
        if (potion.TryGetProperty("vars", out var vars))
        {
            if (vars.TryGetProperty(name, out var val)) return val.GetDecimal();
            if (vars.TryGetProperty(name.ToLowerInvariant(), out var val2)) return val2.GetDecimal();
        }
        return 0m;
    }

    if (cleanId.Equals("BLOCK_POTION", StringComparison.OrdinalIgnoreCase))
    {
        var blk = GetVar("Block");
        effects.Add(new EffectSpec(EffectKind.Block, blk > 0 ? blk : 12m));
    }
    else if (cleanId.Equals("FIRE_POTION", StringComparison.OrdinalIgnoreCase))
    {
        var dmg = GetVar("Damage");
        effects.Add(new EffectSpec(EffectKind.Damage, dmg > 0 ? dmg : 20m));
    }
    else if (cleanId.Equals("ENERGY_POTION", StringComparison.OrdinalIgnoreCase))
    {
        var nrg = GetVar("Energy");
        effects.Add(new EffectSpec(EffectKind.GainEnergy, nrg > 0 ? nrg : 2m));
    }
    else if (cleanId.Equals("SWIFT_POTION", StringComparison.OrdinalIgnoreCase))
    {
        var crd = GetVar("Cards");
        effects.Add(new EffectSpec(EffectKind.Draw, crd > 0 ? crd : 3m));
    }

    var target = potion.TryGetProperty("target_type", out var targetElement)
        ? targetElement.GetString() switch
        {
            "AnyEnemy" => TargetKind.Enemy,
            "AllEnemies" => TargetKind.AllEnemies,
            "Self" => TargetKind.Self,
            _ => TargetKind.None
        }
        : cleanId.Equals("FIRE_POTION", StringComparison.OrdinalIgnoreCase) ? TargetKind.Enemy : TargetKind.None;

    return new PotionState(
        potion.GetProperty("instance_id").GetString()!,
        cleanId,
        potion.GetProperty("name").GetString() ?? id,
        target,
        effects.ToImmutable(),
        0m);
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
        var amount = power.GetProperty("amount").GetInt32();
        var duration = id is "WEAK" or "VULNERABLE" ? amount : -1;
        builder[id] = new StatusState(
            id,
            amount,
            Duration: duration,
            IsDebuff: id is "WEAK" or "VULNERABLE" or "POISON" or "DOOM");
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

static RelicState BuildRelic(JsonElement relic)
{
    var id = relic.GetProperty("id").GetString()!;
    var support = id switch
    {
        "ANCHOR" or "VAJRA" or "NUNCHAKU" or "PEN_NIB" or "ORICHALCUM" or
        "ART_OF_WAR" or "HAPPY_FLOWER" or "INCENSE_BURNER" or "SUNDIAL" or
        "CENTENNIAL_PUZZLE" or "TOUGH_BANDAGES" or "TUNGSTEN_ROD" or
        "UNCEASING_TOP" => RelicEffectSupportStatus.SimulatorSupported,
        _ => RelicEffectSupportStatus.StateCapturedOnly,
    };
    return new RelicState(
        id,
        ReadNullableInt(relic, "counter"),
        ReadDecimalMap(relic, "dynamic_vars").ToImmutableDictionary(StringComparer.Ordinal),
        SupportStatus: support,
        EvidenceLevel: RelicEvidenceLevel.LiveObserved,
        Name: ReadNullableString(relic, "name"),
        Description: ReadNullableString(relic, "description"));
}

static IEnumerable<JsonElement> ReadActualPowers(JsonElement observation)
{
    if (observation.TryGetProperty("player_powers", out var playerPowers) && playerPowers.ValueKind == JsonValueKind.Array)
        foreach (var power in playerPowers.EnumerateArray()) yield return power;
    foreach (var enemy in observation.GetProperty("enemies").EnumerateArray())
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
