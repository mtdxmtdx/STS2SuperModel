using Nosl.Contracts;
using Sts2Sim.Core.Models;

namespace Nosl.Worker;

/// <summary>
/// A sufficient public refinement of a card ID, not complete interchangeable-card identity.
/// The source-pinned draw closure preserves upgrade levels. Mutable Retain, costs, other
/// metadata and hidden physical copies are deliberately not collapsed into this key; exact
/// native replay still checks the complete original public evidence.
/// </summary>
internal readonly record struct NativePublicDrawKey(string Id, int Upgrade)
{
    internal string Signature => PublicJson.Serialize(this);
    internal static NativePublicDrawKey From(PublicCard card) => new(card.Id, card.Upgrade);
    internal static NativePublicDrawKey From(CardModel card) => new(card.GetType().Name, card.CurrentUpgradeLevel);
}
