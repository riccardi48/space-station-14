using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Cards;

[Serializable, NetSerializable]
public sealed class CardAnimationEvent(
    NetCoordinates donorCoords,
    Angle donorRotation,
    bool donorFlipped,
    NetCoordinates recipientCoords,
    ProtoId<CardStackPrototype> cardStackId,
    List<CardData> selected
) : EntityEventArgs
{
    public readonly NetCoordinates DonorCoords = donorCoords;
    public readonly Angle DonorRotation = donorRotation;
    public readonly bool DonorFlipped = donorFlipped;
    public readonly NetCoordinates RecipientCoords = recipientCoords;
    public readonly ProtoId<CardStackPrototype> CardStackId = cardStackId;
    public readonly List<CardData> Selected = selected;
}

[Serializable, NetSerializable]
public sealed class CardDropMergeEvent(NetEntity donor, NetEntity recipient) : EntityEventArgs
{
    public readonly NetEntity Donor = donor;
    public readonly NetEntity Recipient = recipient;
}
