using Nosl.Objectives;
using Sts2Sim.Core.Entities.Players;

namespace Nosl.Worker;

// Private execution diagnostics, never policy history. Every entry describes an
// already committed public value, without consulting RNG or hidden model state.
internal sealed record HpMutation(string Kind, int Before, int After, int MaxBefore, int MaxAfter);

internal sealed class OutcomeEventLedger : IPlayerOutcomeObserver
{
    private readonly List<HpMutation> _hp = [];
    private readonly List<ResourceEvent> _resources = [];
    private readonly Dictionary<string, int> _inventory = new(StringComparer.Ordinal);
    private Player? _boundPlayer;
    private bool _started, _sealed, _hpContinuous = true, _resourcesContinuous = true;
    private int _startHp, _hpNow, _maxNow;

    internal IReadOnlyList<HpMutation> HpEvents => _hp;
    internal IReadOnlyList<ResourceEvent> ResourceEvents => _resources;
    internal double Damage => _hp.Where(e => e.Kind == "loss").Sum(e => (double)e.Before - e.After);
    internal double Healing => _hp.Where(e => e.Kind == "heal").Sum(e => (double)Math.Max(0, e.After - e.Before));
    internal double OtherHpAdjustment => _hp.Where(e => e.Kind is "set" or "maxcap" || e.Kind == "heal" && e.After < e.Before)
        .Sum(e => (double)e.After - e.Before);
    internal bool HpComplete => _started && _sealed && _hpContinuous
        && _startHp - Damage + Healing + OtherHpAdjustment == _hpNow;
    internal bool ResourcesComplete => _started && _sealed && _resourcesContinuous;

    internal void Begin(Player player)
    {
        if (_started) throw new InvalidOperationException("Outcome ledger already started");
        _started = true;
        _startHp = _hpNow = player.Creature.CurrentHp;
        _maxNow = player.Creature.MaxHp;
        foreach (var potion in player.PotionSlots.Where(p => p is not null))
        {
            string id = potion!.GetType().Name;
            _inventory[id] = _inventory.GetValueOrDefault(id) + 1;
        }
        Bind(player);
    }

    // Bind only after native graph reconstruction. Clone setup deliberately calls
    // the same mutators, and those calls are not new effects in the continuation.
    internal void Bind(Player player)
    {
        if (!_started || _sealed) throw new InvalidOperationException("Only a live outcome ledger can bind");
        if (_boundPlayer is not null || player.OutcomeObserver is not null)
            throw new InvalidOperationException("Outcome ledger binding already owned");
        CheckCurrent(player);
        _boundPlayer = player;
        player.OutcomeObserver = this;
    }

    internal OutcomeEventLedger Copy()
    {
        var result = new OutcomeEventLedger
        {
            _started = _started, _sealed = _sealed, _hpContinuous = _hpContinuous,
            _resourcesContinuous = _resourcesContinuous, _startHp = _startHp, _hpNow = _hpNow, _maxNow = _maxNow,
        };
        result._hp.AddRange(_hp);
        result._resources.AddRange(_resources);
        foreach (var (id, quantity) in _inventory) result._inventory.Add(id, quantity);
        return result;
    }

    internal void Seal(Player player)
    {
        if (_sealed) return;
        if (!ReferenceEquals(_boundPlayer, player) || !ReferenceEquals(player.OutcomeObserver, this))
            _hpContinuous = _resourcesContinuous = false;
        CheckCurrent(player);
        _sealed = true;
        Detach();
    }

    internal void Detach()
    {
        if (!_sealed) _hpContinuous = _resourcesContinuous = false;
        if (_boundPlayer is { } player && ReferenceEquals(player.OutcomeObserver, this)) player.OutcomeObserver = null;
        _boundPlayer = null;
    }

    private void CheckCurrent(Player player)
    {
        _hpContinuous &= _hpNow == player.Creature.CurrentHp && _maxNow == player.Creature.MaxHp;
        var actual = player.PotionSlots.Where(p => p is not null).GroupBy(p => p!.GetType().Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        _resourcesContinuous &= _inventory.Keys.Union(actual.Keys)
            .All(id => _inventory.GetValueOrDefault(id) == actual.GetValueOrDefault(id));
    }

    void IPlayerOutcomeObserver.HpChanged(Player player, HpMutationKind kind, int before, int after, int maxBefore, int maxAfter)
    {
        _hpContinuous &= !_sealed && ReferenceEquals(_boundPlayer, player) && before == _hpNow && maxBefore == _maxNow;
        _hpNow = after; _maxNow = maxAfter;
        if (before == after && maxBefore == maxAfter) return;
        string operation = kind switch
        { HpMutationKind.Loss => "loss", HpMutationKind.Heal => "heal", HpMutationKind.Set => "set", HpMutationKind.MaxCap => "maxcap", _ => "unknown" };
        _hpContinuous &= operation != "unknown" && (kind != HpMutationKind.Loss || after <= before);
        _hp.Add(new(operation, before, after, maxBefore, maxAfter));
    }

    void IPlayerOutcomeObserver.PotionChanged(Player player, string potionId, PotionMutationKind kind)
    {
        _resourcesContinuous &= !_sealed && ReferenceEquals(_boundPlayer, player);
        string operation = kind switch
        { PotionMutationKind.Acquired => "generated", PotionMutationKind.Consumed => "consumed", PotionMutationKind.Discarded => "discarded", _ => "removed" };
        _inventory[potionId] = _inventory.GetValueOrDefault(potionId) + (kind == PotionMutationKind.Acquired ? 1 : -1);
        _resourcesContinuous &= operation != "removed" && _inventory[potionId] >= 0;
        _resources.Add(new(operation, potionId, 1, "native_potion_slot_" + operation));
    }
}
