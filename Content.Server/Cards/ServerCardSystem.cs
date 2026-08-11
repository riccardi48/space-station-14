using Content.Shared.Cards;
using Content.Shared.Throwing;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Cards;

/// <inheritdoc />
[UsedImplicitly]
public sealed partial class CardSystem : SharedCardSystem
{
    // Server-side index counter. Needed so that all cards have unique ids. Basically a EntityUid but for cards.
    private int _indexCounter = 1;

    [SubscribeLocalEvent]
    private void OnPrepareThrow(Entity<CardsComponent> ent, ref PrepareThrowEvent args)
    {
        if (args.Handled || ent.Comp.Cards.Count <= 1)
            return;

        if (SplitDeck(ent, args.SpawnCoordinates, MovedCards(ent.Comp, 1)) is { Valid: true } split)
            args.ItemUid = split;

        args.Handled = true;
    }

    [SubscribeNetworkEvent]
    private void HandleDropMerge(CardDropMergeEvent args)
    {
        var donor = GetEntity(args.Donor);
        var recipient = GetEntity(args.Recipient);

        if (
            TryComp<CardsComponent>(donor, out var donorComp)
            && TryComp<CardsComponent>(recipient, out var recipientComp)
        )
            TryMergeDecks((donor, donorComp), (recipient, recipientComp), out _);
    }

    protected override void OnCardsInit(Entity<CardsComponent> ent, ref ComponentInit args)
    {
        base.OnCardsInit(ent, ref args);
        for (var i = 0; i < ent.Comp.Cards.Count; i++)
        {
            var card = ent.Comp.Cards[i];
            if (card.CardIndex != 0)
                continue;
            card.CardIndex = _indexCounter;
            _indexCounter++;
            ent.Comp.Cards[i] = card;
        }
        Dirty(ent.Owner, ent.Comp);
    }

    ///  <inheritdoc />
    public override EntityUid? SplitDeck(Entity<CardsComponent> ent, EntityCoordinates spawnPosition, List<int> cardIndexes = default!)
    {
        if (cardIndexes.Count == 0 || cardIndexes.Count != GetCardFromIndex(ent.Comp.Cards, cardIndexes).Count)
            return null;

        if (!ProtoMan.Resolve(ent.Comp.CardStackType, out var cardStack))
            return null;

        var split = SpawnAtPosition(cardStack.Spawn, spawnPosition);

        if (!TryComp<CardsComponent>(split, out var splitComp)
            || !TryMoveCards((split, splitComp), ent, cardIndexes))
        {
            QueueDel(split);
            return null;
        }
        splitComp.Flipped = ent.Comp.Flipped;
        splitComp.Fanned = ent.Comp.Fanned;

        UpdateVisualState(ent);
        UpdateVisualState((split, splitComp));

        Dirty(ent.Owner, ent.Comp);
        Dirty(split, splitComp);

        return split;
    }

    protected override void PlayCardAnimation(
        EntityCoordinates donorCoords,
        Angle donorRotation,
        bool donorFlipped,
        EntityCoordinates recipientCoords,
        ProtoId<CardStackPrototype> cardStackId,
        List<CardData> selected,
        bool playOnUser = false
    )
    {
        var ev = new CardAnimationEvent(
            GetNetCoordinates(donorCoords),
            donorRotation,
            donorFlipped,
            GetNetCoordinates(recipientCoords),
            cardStackId,
            selected
        );
        // Don't for the client which is running it as they can have their own predicted animation
        // Do play it for systems which are not predicted i.e. splitting stacks
        var filter = Filter
            .Pvs(recipientCoords)
            .RemoveWhereAttachedEntity(e => !playOnUser && (e == recipientCoords.EntityId || e == donorCoords.EntityId));
        RaiseNetworkEvent(ev, filter);
    }
}
