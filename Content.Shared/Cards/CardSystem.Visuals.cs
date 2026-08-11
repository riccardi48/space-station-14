using System.Linq;
using Content.Shared.Examine;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Cards;

// Event handlers for appearance updates.
public abstract partial class SharedCardSystem
{
    [SubscribeLocalEvent]
    private void OnCardsStarted(Entity<CardsComponent> ent, ref ComponentStartup args)
    {
        UpdateVisualState(ent);
    }

    [SubscribeLocalEvent]
    private void OnCardsExamined(Entity<CardsComponent> ent, ref ExaminedEvent args)
    {
        // Can only see top card if the deck is flipped
        if (!args.IsInDetailsRange || !ent.Comp.Flipped)
            return;

        var cards = GetCardListVisualState(ent.Comp);
        var cardName = (string)cards.CardList.Last().CardId;
        args.PushMarkup(
            Loc.GetString("comp-cards-examine-detail", ("card", Loc.GetString(cardName.Replace('_', '-'))))
        );
    }


    /// <summary>
    /// Builds the <see cref="CardListVisualState"/> describing which cards in the stack are currently
    /// visible to the player and should be rendered, based on whether the stack is fanned or flipped.
    /// </summary>
    /// <remarks>
    /// This determines what the client renders for the card sprite:
    /// if not fanned, only the top card is shown; if fanned, up to <see cref="CardsComponent.MaxFanned"/>
    /// cards are shown. If the stack is flipped, the visible window is taken from the end of the list
    /// instead of the start.
    /// </remarks>
    /// <param name="cards">The card stack component to compute the visual state for.</param>
    /// <returns>A <see cref="CardListVisualState"/> describing the visible slice of cards.</returns>
    public CardListVisualState GetCardListVisualState(CardsComponent cards)
    {
        var count = Math.Min(cards.Fanned ? cards.MaxFanned : 1, cards.Cards.Count) * (cards.Flipped ? 1 : -1);
        var start = cards.Flipped ? cards.Cards.Count - count : -count - 1;
        return new CardListVisualState
        {
            CardList = cards.Cards,
            Start = start,
            Count = count,
            MaxFanned = cards.MaxFanned,
        };
    }

    protected void UpdateVisualState(Entity<CardsComponent> ent)
    {
        if (TryComp<AppearanceComponent>(ent, out var appearance))
        {
            Appearance.SetData(ent, CardVisuals.CardList, GetCardListVisualState(ent.Comp), appearance);
            Appearance.SetData(ent, CardVisuals.IsFlipped, ent.Comp.Flipped, appearance);
        }
    }

    protected void PlayCardDrawAnimation(
        Entity<CardsComponent> donor,
        Entity<CardsComponent> recipient,
        int delta,
        bool playOnUser = false
    )
    {
        // Plays animation for a split or merge where the cards taken are from the top or bottom
        var selected = GetCardFromIndex(donor.Comp.Cards, MovedCards(donor.Comp, delta));
        PlayCardAnimation(donor, recipient, selected, playOnUser: playOnUser);
    }

    protected void PlayCardTakeAnimation(
        Entity<CardsComponent> donor,
        Entity<CardsComponent> recipient,
        List<int> cardInx,
        bool playOnUser = false
    )
    {
        // Plays animation for a split or merge where the cards taken are from somewhere in the deck
        var selected = GetCardFromIndex(recipient.Comp.Cards, cardInx);
        PlayCardAnimation(donor, recipient, selected, playOnUser: playOnUser);
    }

    private void PlayCardAnimation(
        Entity<CardsComponent> donor,
        Entity<CardsComponent> recipient,
        List<CardData> selected,
        bool playOnUser = false
    )
    {
        var xform = Transform(donor.Owner);
        PlayCardAnimation(
            xform.Coordinates,
            xform.LocalRotation,
            donor.Comp.Flipped,
            Transform(recipient).Coordinates,
            donor.Comp.CardStackType,
            selected,
            playOnUser: playOnUser
        );
    }

    protected abstract void PlayCardAnimation(
        EntityCoordinates donorCoords,
        Angle donorRotation,
        bool donorFlipped,
        EntityCoordinates recipientCoords,
        ProtoId<CardStackPrototype> cardStackId,
        List<CardData> selected,
        bool playOnUser = false
    );

}

/// <summary>
/// AppearanceData keys for playing card data.
/// </summary>
[Serializable, NetSerializable]
public enum CardVisuals : byte
{
    IsFlipped,
    CardList,
}

/// <summary>
/// Visual layers for playing card data.
/// </summary>
[Serializable, NetSerializable]
public enum CardVisualLayers : byte
{
    Base
}

/// <summary>
/// Appearance data structure stored at <see cref="CardVisuals.CardList"/>.
/// </summary>
[Serializable, NetSerializable]
public sealed class CardListVisualState : ICloneable
{
    public List<CardData> CardList = new();
    public int Start;
    public int Count;
    public int MaxFanned;

    public object Clone() => new CardListVisualState
    {
        CardList = [.. CardList],
        Start = Start,
        Count = Count,
        MaxFanned = MaxFanned,
    };
}
