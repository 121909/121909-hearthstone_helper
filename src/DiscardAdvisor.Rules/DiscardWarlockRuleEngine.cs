using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public sealed class DiscardWarlockRuleEngine
{
    public const string ContinueWickedWhispersPending = "continue_wicked_whispers_pending";
    public const string ContinueChamberDrawPending = "continue_chamber_draw_pending";
    public const string ContinueEndTurnPending = "continue_end_turn_pending";
    public const string ContinueLifeTapDamagePending = "continue_life_tap_damage_pending";
    public const string RandomDrawPending = "random_draw_pending";
    public const string RandomTemporaryDrawPending = "random_temporary_draw_pending";
    public const string RandomBoundDrawPending = "random_bound_draw_pending";

    private readonly CommonRuleEngine _common = new();

    public TransitionResult Apply(RuleGameState state, RuleAction action)
    {
        if (action is SelectChoiceAction choice)
            return SelectChoice(state, choice);
        if (action is UseLocationAction useLocation &&
            state.Player(useLocation.Side).Locations.Any(location =>
                location.EntityId == useLocation.SourceEntityId && location.CardId == DiscardWarlockCardIds.ChamberOfViscidus))
            return ActivateChamber(state, useLocation);
        if (action is not PlayCardAction play)
        {
            if (action is EndTurnAction endTurn)
                return ResolveTemporaryCardsAndEndTurn(
                    TransitionResult.Legal(state, Array.Empty<RuleEvent>()),
                    endTurn.Side);
            var lifeTap = action is UseHeroPowerAction heroPower &&
                          state.Player(heroPower.Side).HeroPower.CardId == DiscardWarlockCardIds.LifeTap;
            var chronoclawsAttack = action is AttackAction attack &&
                state.Player(attack.Side).Hero.EntityId == attack.SourceEntityId &&
                state.Player(attack.Side).Weapon?.CardId == DiscardWarlockCardIds.Chronoclaws;
            var commonResult = _common.Apply(state, action);
            if (!commonResult.IsLegal)
                return commonResult;
            commonResult = ResolvePlatysaurDeaths(commonResult);
            if (lifeTap)
                return ResolveLifeTap(commonResult, action.Side);
            return chronoclawsAttack ? ResolveHighestCostDiscard(commonResult, action.Side, "chronoclaws") : commonResult;
        }

        var card = state.Player(play.Side).Hand.FirstOrDefault(candidate => candidate.EntityId == play.SourceEntityId);
        var result = _common.Apply(state, action);
        if (!result.IsLegal || card is null)
            return result;

        return ApplyOnPlay(result, play.Side, card, play.TargetEntityId);
    }

    private TransitionResult ApplyOnPlay(TransitionResult result, PlayerSide side, HandCardState card, int? targetEntityId)
    {
        return card.CardId switch
        {
            DiscardWarlockCardIds.CursedCatacombs => Append(result, new RuleEvent("choice_pending", card.EntityId, null, 0, card.CardId)),
            DiscardWarlockCardIds.EntropicContinuity => EntropicContinuity(result, side, card.EntityId),
            DiscardWarlockCardIds.PartyFiend => PartyFiend(result, side, card.EntityId),
            DiscardWarlockCardIds.Platysaur => Platysaur(result, side, card.EntityId),
            DiscardWarlockCardIds.Soulfire => Soulfire(result, side, card.EntityId, targetEntityId!.Value),
            DiscardWarlockCardIds.Soularium => Draw(result, side, 3, true, card.EntityId),
            DiscardWarlockCardIds.WickedWhispers => WickedWhispers(result, side, card.EntityId),
            DiscardWarlockCardIds.DisposableAcolytes => RequestRandomOneCostSummons(result, side, card.EntityId),
            DiscardWarlockCardIds.OcularOccultist => OcularOccultist(result, side, card.EntityId),
            DiscardWarlockCardIds.SoulBarrage => Append(
                result,
                new RuleEvent("random_damage_pending", card.EntityId, null, 5, card.CardId)),
            DiscardWarlockCardIds.HandOfGuldan => Draw(result, side, 3, false, card.EntityId),
            _ => result
        };
    }

    private static TransitionResult EntropicContinuity(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var player = result.State.Player(side);
        player = player with
        {
            Board = player.Board.Select(minion => minion with
            {
                Attack = minion.Attack + 1,
                Health = minion.Health + 1,
                MaxHealth = minion.MaxHealth + 1
            }).ToImmutableArray()
        };
        var state = result.State.WithPlayer(side, player);
        var events = result.Events.Add(new RuleEvent("board_buff", sourceEntityId, null, 1, DiscardWarlockCardIds.EntropicContinuity));
        for (var index = 0; index < 2; index++)
        {
            state = state.AllocateEntity(out var entityId);
            player = state.Player(side);
            player = player with
            {
                Deck = player.Deck.Add(DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, entityId)),
                DeckOrderKnown = false
            };
            state = state.WithPlayer(side, player);
            events = events.Add(new RuleEvent("shuffle_into_deck", sourceEntityId, entityId, 0, DiscardWarlockCardIds.ShredOfTime));
        }
        return result with { State = state, Events = events };
    }

    private static TransitionResult PartyFiend(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var state = result.State;
        var events = result.Events;
        var sourcePosition = state.Player(side).Board
            .First(minion => minion.EntityId == sourceEntityId).BoardPosition;
        var insertionPosition = sourcePosition + 1;
        for (var index = 0; index < 2; index++)
        {
            var player = state.Player(side);
            if (player.BoardCount >= CommonRuleEngine.MaximumBoardSize)
            {
                events = events.Add(new RuleEvent(
                    "summon_failed_board_full",
                    sourceEntityId,
                    null,
                    0,
                    DiscardWarlockCardIds.Felbeast));
                continue;
            }
            state = state.AllocateEntity(out var entityId);
            player = state.Player(side);
            var token = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Felbeast, entityId);
            player = player with
            {
                Board = player.Board.Select(minion => minion.BoardPosition >= insertionPosition
                        ? minion with { BoardPosition = minion.BoardPosition + 1 }
                        : minion)
                    .Append(new MinionState(
                        token.EntityId,
                        token.CardId,
                        insertionPosition,
                        token.Attack,
                        token.Health,
                        token.Health,
                        SummonedThisTurn: true))
                    .OrderBy(minion => minion.BoardPosition)
                    .ToImmutableArray(),
                Locations = player.Locations.Select(location => location.BoardPosition >= insertionPosition
                    ? location with { BoardPosition = location.BoardPosition + 1 }
                    : location).ToImmutableArray()
            };
            state = state.WithPlayer(side, player);
            events = events.Add(new RuleEvent("summon", sourceEntityId, entityId, 0, token.CardId));
            insertionPosition++;
        }

        var damagedPlayer = state.Player(side);
        damagedPlayer = damagedPlayer with { Hero = DamageHero(damagedPlayer.Hero, 3) };
        state = state.WithPlayer(side, damagedPlayer);
        events = events.Add(new RuleEvent("damage", sourceEntityId, damagedPlayer.Hero.EntityId, 3));
        return result with { State = state, Events = events };
    }

    private TransitionResult Soulfire(TransitionResult result, PlayerSide side, int sourceEntityId, int targetEntityId)
    {
        result = DealDamage(result, targetEntityId, 4, sourceEntityId);
        result = ResolvePlatysaurDeaths(result);
        if (IsTerminal(result.State))
            return result;
        return ResolveRandomDiscard(result, side, result.State.Player(side).Hand, "soulfire");
    }

    private TransitionResult ResolveLifeTap(TransitionResult result, PlayerSide side)
    {
        var sourceEntityId = result.State.Player(side).HeroPower.EntityId;
        result = Draw(result, side, 1, false, sourceEntityId);
        return ApplyAfterRandomEffects(
            result,
            new RuleEvent(ContinueLifeTapDamagePending, sourceEntityId, (int)side),
            value => ApplyLifeTapDamage(value, side, sourceEntityId));
    }

    private static TransitionResult ApplyLifeTapDamage(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        if (IsTerminal(result.State))
            return result;
        var player = result.State.Player(side);
        player = player with { Hero = DamageHero(player.Hero, 2) };
        return result with
        {
            State = result.State.WithPlayer(side, player),
            Events = result.Events.Add(new RuleEvent("damage", sourceEntityId, player.Hero.EntityId, 2))
        };
    }

    private TransitionResult Platysaur(TransitionResult result, PlayerSide side, int sourceEntityId)
        => Draw(result, side, 1, false, sourceEntityId, sourceEntityId);

    private static TransitionResult OcularOccultist(TransitionResult result, PlayerSide side, int sourceEntityId) =>
        result.State.Player(side).Hand.IsEmpty
            ? result
            : Append(result, new RuleEvent(
                "hand_discard_choice_pending",
                sourceEntityId,
                null,
                0,
                DiscardWarlockCardIds.OcularOccultist));

    private TransitionResult WickedWhispers(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var hand = result.State.Player(side).Hand;
        if (hand.IsEmpty)
            return ApplyBoardBuff(result, side, sourceEntityId);
        var lowestCost = hand.Min(card => card.Cost);
        var candidates = hand.Where(card => card.Cost == lowestCost).ToArray();
        var discarded = ResolveRandomDiscard(result, side, candidates, "wicked_whispers");
        return ApplyAfterRandomEffects(
            discarded,
            new RuleEvent(ContinueWickedWhispersPending, sourceEntityId, (int)side),
            value => ApplyBoardBuff(value, side, sourceEntityId));
    }

    private TransitionResult Draw(
        TransitionResult result,
        PlayerSide side,
        int count,
        bool temporary,
        int sourceEntityId,
        int? bindToEntityId = null)
    {
        while (count > 0 && !IsTerminal(result.State))
        {
            var player = result.State.Player(side);
            if (player.Deck.IsEmpty)
            {
                var fatigue = _common.DrawCard(result.State, side);
                result = result with
                {
                    State = fatigue.State,
                    Events = result.Events.AddRange(fatigue.Events)
                };
                count--;
                continue;
            }
            if (!player.DeckOrderKnown && player.Deck.Length > 1)
                return Append(result, CreateRandomDrawPending(side, count, temporary, sourceEntityId, bindToEntityId));

            var resolved = ResolveDrawnEntity(
                result,
                side,
                player.Deck[0].EntityId,
                temporary,
                sourceEntityId,
                bindToEntityId);
            result = resolved.Result;
            if (resolved.RequestConsumed)
                count--;
        }
        return result;
    }

    public TransitionResult ResolveRandomDraw(RuleGameState state, RuleEvent pending, int selectedEntityId)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (pending is null)
            throw new ArgumentNullException(nameof(pending));
        if (pending.TargetEntityId is not int sideValue || !Enum.IsDefined(typeof(PlayerSide), sideValue) || pending.Amount < 1)
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);
        if (pending.Type is not (RandomDrawPending or RandomTemporaryDrawPending or RandomBoundDrawPending))
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);
        var side = (PlayerSide)sideValue;
        if (state.Player(side).Deck.All(card => card.EntityId != selectedEntityId))
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);

        var temporary = pending.Type == RandomTemporaryDrawPending;
        var bindToEntityId = pending.Type == RandomBoundDrawPending ? pending.SourceEntityId : null;
        var result = TransitionResult.Legal(state, Array.Empty<RuleEvent>());
        var resolved = ResolveDrawnEntity(
            result,
            side,
            selectedEntityId,
            temporary,
            pending.SourceEntityId ?? 0,
            bindToEntityId);
        var remaining = pending.Amount - (resolved.RequestConsumed ? 1 : 0);
        return Draw(
            resolved.Result,
            side,
            remaining,
            temporary,
            pending.SourceEntityId ?? 0,
            bindToEntityId);
    }

    private static (TransitionResult Result, bool RequestConsumed) ResolveDrawnEntity(
        TransitionResult result,
        PlayerSide side,
        int selectedEntityId,
        bool temporary,
        int sourceEntityId,
        int? bindToEntityId)
    {
        var player = result.State.Player(side);
        var card = player.Deck.First(candidate => candidate.EntityId == selectedEntityId);
        player = player with { Deck = player.Deck.Remove(card) };
        if (card.CardId == DiscardWarlockCardIds.ShredOfTime)
        {
            player = player with
            {
                Graveyard = player.Graveyard.Add(new ZoneCardState(card.EntityId, card.CardId)),
                Hero = DamageHero(player.Hero, 3)
            };
            return (result with
            {
                State = result.State.WithPlayer(side, player),
                Events = result.Events
                    .Add(new RuleEvent("draw", card.EntityId, null, 0, card.CardId))
                    .Add(new RuleEvent("casts_when_drawn", card.EntityId, player.Hero.EntityId, 3, card.CardId))
            }, false);
        }

        if (player.Hand.Length >= CommonRuleEngine.MaximumHandSize)
        {
            player = player with { Graveyard = player.Graveyard.Add(new ZoneCardState(card.EntityId, card.CardId)) };
            return (result with
            {
                State = result.State.WithPlayer(side, player),
                Events = result.Events.Add(new RuleEvent("burn", card.EntityId, null, 0, card.CardId))
            }, true);
        }

        var handCard = temporary ? card with { Temporary = true } : card;
        player = player with { Hand = player.Hand.Add(handCard) };
        var next = result.State.WithPlayer(side, player);
        var events = result.Events.Add(new RuleEvent("draw", card.EntityId, null, 0, card.CardId));
        if (temporary)
            events = events.Add(new RuleEvent("mark_temporary", sourceEntityId, card.EntityId, 0, card.CardId));
        if (bindToEntityId is int bindingSource)
        {
            next = next with { PlatysaurBindings = next.Bindings.SetItem(bindingSource, card.EntityId) };
            events = events.Add(new RuleEvent("platysaur_bind", bindingSource, card.EntityId));
        }
        return (result with { State = next, Events = events }, true);
    }

    private static RuleEvent CreateRandomDrawPending(
        PlayerSide side,
        int count,
        bool temporary,
        int sourceEntityId,
        int? bindToEntityId)
    {
        var type = bindToEntityId.HasValue
            ? RandomBoundDrawPending
            : temporary ? RandomTemporaryDrawPending : RandomDrawPending;
        return new RuleEvent(type, sourceEntityId, (int)side, count);
    }

    private TransitionResult SelectChoice(RuleGameState state, SelectChoiceAction action)
    {
        if (action.Side != state.ActiveSide)
            return TransitionResult.Illegal(state, RuleError.NotActivePlayer);
        var choice = state.PendingChoice;
        if (choice is null || choice.ChoiceId != action.ChoiceId)
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);
        var candidate = choice.Candidates.FirstOrDefault(value => value.EntityId == action.SelectedEntityId);
        if (candidate is null)
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);
        if (choice.SourceCardId == DiscardWarlockCardIds.OcularOccultist)
        {
            var selectedCard = state.Player(action.Side).Hand.FirstOrDefault(card => card.EntityId == candidate.EntityId);
            if (selectedCard is null)
                return TransitionResult.Illegal(state, RuleError.InvalidTarget);
            return DiscardSpecific(
                TransitionResult.Legal(state with { PendingChoice = null }, new[]
                {
                    new RuleEvent("choice_selected", choice.SourceEntityId, candidate.EntityId, 0, candidate.CardId)
                }),
                action.Side,
                selectedCard.EntityId,
                "ocular_occultist");
        }
        if (choice.SourceCardId == DiscardWarlockCardIds.ChamberOfViscidus && choice.SourceEntityId is int locationEntityId)
            return ActivateChamber(state, new UseLocationAction(action.Side, locationEntityId, candidate.EntityId));
        if (choice.SourceCardId != DiscardWarlockCardIds.CursedCatacombs)
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);

        var player = state.Player(action.Side);
        if (!DiscardWarlockCardCatalog.TryCreate(candidate.CardId, candidate.EntityId, out var generatedCard) || generatedCard is null)
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);
        var deckCard = player.Deck.FirstOrDefault(card => card.CardId == candidate.CardId);
        if (deckCard is null)
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);
        var generated = generatedCard with { Temporary = true };
        var events = ImmutableArray.Create(new RuleEvent("choice_selected", choice.SourceEntityId, candidate.EntityId, 0, candidate.CardId));
        player = player with { Deck = player.Deck.Remove(deckCard) };
        if (player.Hand.Length >= CommonRuleEngine.MaximumHandSize)
        {
            player = player with { Graveyard = player.Graveyard.Add(new ZoneCardState(generated.EntityId, generated.CardId)) };
            events = events.Add(new RuleEvent("burn", generated.EntityId, null, 0, generated.CardId));
        }
        else
        {
            player = player with { Hand = player.Hand.Add(generated) };
            events = events.Add(new RuleEvent("mark_temporary", choice.SourceEntityId, generated.EntityId, 0, generated.CardId));
        }
        return TransitionResult.Legal(state.WithPlayer(action.Side, player) with { PendingChoice = null }, events);
    }

    private TransitionResult ActivateChamber(RuleGameState state, UseLocationAction action)
    {
        if (action.Side != state.ActiveSide)
            return TransitionResult.Illegal(state, RuleError.NotActivePlayer);
        var player = state.Player(action.Side);
        var location = player.Locations.FirstOrDefault(candidate => candidate.EntityId == action.SourceEntityId);
        if (location is null)
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);
        if (!location.Available || location.Cooldown > 0 || location.Durability <= 0)
            return TransitionResult.Illegal(state, RuleError.LocationUnavailable);
        var choice = state.PendingChoice;
        if (choice is null || choice.SourceCardId != DiscardWarlockCardIds.ChamberOfViscidus ||
            choice.SourceEntityId != location.EntityId || choice.Candidates.All(candidate => candidate.EntityId != action.SelectedEntityId))
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);
        var selected = player.Hand.FirstOrDefault(card => card.EntityId == action.SelectedEntityId);
        if (selected is null)
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);

        var updatedLocation = location with
        {
            Durability = location.Durability - 1,
            Cooldown = location.ActivationCooldown,
            Available = false
        };
        var locations = updatedLocation.Durability == 0
            ? player.Locations.Remove(location)
            : player.Locations.Replace(location, updatedLocation);
        player = (player with { Locations = locations }).NormalizePositions();
        var result = TransitionResult.Legal(
            state.WithPlayer(action.Side, player) with { PendingChoice = null },
            new[] { new RuleEvent("use_location", location.EntityId, selected.EntityId, 0, location.CardId) });
        result = DiscardSpecific(result, action.Side, selected.EntityId, "chamber_of_viscidus");
        return ApplyAfterRandomEffects(
            result,
            new RuleEvent(ContinueChamberDrawPending, location.EntityId, (int)action.Side, 2),
            value => Draw(value, action.Side, 2, false, location.EntityId));
    }

    private TransitionResult ResolveHighestCostDiscard(TransitionResult result, PlayerSide side, string source)
    {
        var hand = result.State.Player(side).Hand;
        if (hand.IsEmpty)
            return result;
        var highestCost = hand.Max(card => card.Cost);
        return ResolveRandomDiscard(result, side, hand.Where(card => card.Cost == highestCost), source);
    }

    private TransitionResult ResolveRandomDiscard(
        TransitionResult result,
        PlayerSide side,
        IEnumerable<HandCardState> candidates,
        string source)
    {
        var outcomes = candidates.OrderBy(card => card.EntityId).ToArray();
        if (outcomes.Length == 0)
            return result;
        if (outcomes.Length == 1)
            return DiscardSpecific(result, side, outcomes[0].EntityId, source);

        var probability = 1d / outcomes.Length;
        var branches = outcomes.Select(card =>
        {
            var branch = DiscardSpecific(result, side, card.EntityId, source);
            return new RuleBranch($"discard:{card.EntityId}", probability, branch.State, branch.Events);
        }).ToImmutableArray();
        return result with
        {
            Events = result.Events.Add(new RuleEvent("random_discard", null, null, outcomes.Length, source)),
            Branches = branches
        };
    }

    private TransitionResult DiscardSpecific(TransitionResult result, PlayerSide side, int entityId, string source)
    {
        var player = result.State.Player(side);
        var card = player.Hand.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (card is null)
            return result;
        player = player with
        {
            Hand = player.Hand.Remove(card),
            Graveyard = player.Graveyard.Add(new ZoneCardState(card.EntityId, card.CardId)),
            DiscardCount = player.DiscardCount + 1
        };
        result = result with
        {
            State = result.State.WithPlayer(side, player),
            Events = result.Events.Add(new RuleEvent("discard", null, card.EntityId, 1, card.CardId))
                .Add(new RuleEvent("discard_source", null, card.EntityId, 0, source))
        };
        result = ApplyDukeGrowth(result, side);
        return ResolveDiscardBenefit(result, side, card);
    }

    private static TransitionResult ApplyBoardBuff(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        RuleGameState Buff(RuleGameState state)
        {
            var player = state.Player(side);
            player = player with
            {
                Board = player.Board.Select(minion => minion with
                {
                    Attack = minion.Attack + 1,
                    Health = minion.Health + 1,
                    MaxHealth = minion.MaxHealth + 1
                }).ToImmutableArray()
            };
            return state.WithPlayer(side, player);
        }

        if (!result.Branches.IsEmpty)
        {
            return result with
            {
                Branches = result.Branches.Select(branch => branch with
                {
                    State = Buff(branch.State),
                    Events = branch.Events.Add(new RuleEvent("board_buff", sourceEntityId, null, 1, DiscardWarlockCardIds.WickedWhispers))
                }).ToImmutableArray()
            };
        }
        return result with
        {
            State = Buff(result.State),
            Events = result.Events.Add(new RuleEvent("board_buff", sourceEntityId, null, 1, DiscardWarlockCardIds.WickedWhispers))
        };
    }

    private TransitionResult ResolvePlatysaurDeaths(TransitionResult result)
    {
        var state = result.State;
        var events = result.Events;
        foreach (var death in result.Events.Where(ruleEvent =>
                     ruleEvent.Type == "death" && ruleEvent.CardId == DiscardWarlockCardIds.Platysaur).ToArray())
        {
            if (death.SourceEntityId is not int platysaurId || !state.Bindings.TryGetValue(platysaurId, out var drawnEntityId))
                continue;
            state = state with { PlatysaurBindings = state.Bindings.Remove(platysaurId) };
            var side = state.Friendly.Hand.Any(card => card.EntityId == drawnEntityId)
                ? PlayerSide.Friendly
                : state.Opponent.Hand.Any(card => card.EntityId == drawnEntityId) ? PlayerSide.Opponent : (PlayerSide?)null;
            if (side is null)
                continue;
            var discarded = DiscardSpecific(
                result with { State = state, Events = events },
                side.Value,
                drawnEntityId,
                "platysaur_deathrattle");
            state = discarded.State;
            events = discarded.Events;
        }
        return result with { State = state, Events = events };
    }

    public TransitionResult ResolvePendingContinuation(RuleGameState state, RuleEvent pending)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (pending is null)
            throw new ArgumentNullException(nameof(pending));
        if (pending.TargetEntityId is not int sideValue || !Enum.IsDefined(typeof(PlayerSide), sideValue))
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);
        var side = (PlayerSide)sideValue;
        var result = TransitionResult.Legal(state, Array.Empty<RuleEvent>());
        if (IsTerminal(state))
            return result;
        return pending.Type switch
        {
            ContinueWickedWhispersPending => ApplyBoardBuff(result, side, pending.SourceEntityId ?? 0),
            ContinueChamberDrawPending => Draw(result, side, pending.Amount, false, pending.SourceEntityId ?? 0),
            ContinueEndTurnPending => ResolveTemporaryCardsAndEndTurn(result, side),
            ContinueLifeTapDamagePending => ApplyLifeTapDamage(result, side, pending.SourceEntityId ?? 0),
            _ => TransitionResult.Illegal(state, RuleError.UnsupportedAction)
        };
    }

    private TransitionResult ResolveTemporaryCardsAndEndTurn(TransitionResult result, PlayerSide side)
    {
        while (!IsTerminal(result.State))
        {
            var temporary = result.State.Player(side).Hand.FirstOrDefault(card => card.Temporary);
            if (temporary is null)
                break;
            result = DiscardSpecific(result, side, temporary.EntityId, "temporary_expired");
            if (HasPendingRandomEffect(result.Events))
            {
                return Append(result, new RuleEvent(
                    ContinueEndTurnPending,
                    TargetEntityId: (int)side));
            }
        }
        if (IsTerminal(result.State))
            return result;
        var ended = _common.Apply(result.State, new EndTurnAction(side));
        return ended.IsLegal
            ? ended with { Events = result.Events.AddRange(ended.Events) }
            : ended;
    }

    private static TransitionResult ApplyAfterRandomEffects(
        TransitionResult result,
        RuleEvent continuation,
        Func<TransitionResult, TransitionResult> apply)
    {
        if (!result.Branches.IsEmpty)
        {
            return result with
            {
                Branches = result.Branches.Select(branch =>
                {
                    var branchResult = TransitionResult.Legal(branch.State, branch.Events);
                    var continued = HasPendingRandomEffect(branch.Events)
                        ? Append(branchResult, continuation)
                        : apply(branchResult);
                    return branch with { State = continued.State, Events = continued.Events };
                }).ToImmutableArray()
            };
        }
        return HasPendingRandomEffect(result.Events) ? Append(result, continuation) : apply(result);
    }

    private static bool HasPendingRandomEffect(IEnumerable<RuleEvent> events) => events.Any(ruleEvent =>
        ruleEvent.Type is "random_damage_pending" or "random_one_cost_summon_pending" or
            RandomDrawPending or RandomTemporaryDrawPending or RandomBoundDrawPending);

    private static bool IsTerminal(RuleGameState state) =>
        state.Friendly.Hero.Health <= 0 || state.Opponent.Hero.Health <= 0;

    private TransitionResult ResolveDiscardBenefit(TransitionResult result, PlayerSide side, HandCardState card)
    {
        return card.CardId switch
        {
            DiscardWarlockCardIds.HandOfGuldan => Draw(result, side, 3, false, card.EntityId),
            DiscardWarlockCardIds.BonewebEgg => SummonTokens(
                result,
                side,
                card.EntityId,
                DiscardWarlockCardIds.BonewebSpider,
                2),
            DiscardWarlockCardIds.SilverwareGolem or DiscardWarlockCardIds.WalkingDead =>
                SummonDiscardedMinion(result, side, card),
            DiscardWarlockCardIds.DisposableAcolytes => RequestRandomOneCostSummons(result, side, card.EntityId),
            DiscardWarlockCardIds.SoulBarrage => Append(
                result,
                new RuleEvent("random_damage_pending", card.EntityId, null, 5, card.CardId)),
            _ => result
        };
    }

    private static TransitionResult ApplyDukeGrowth(TransitionResult result, PlayerSide side)
    {
        var player = result.State.Player(side);
        player = player with
        {
            Hand = player.Hand.Select(GrowDuke).ToImmutableArray(),
            Deck = player.Deck.Select(GrowDuke).ToImmutableArray(),
            Board = player.Board.Select(minion => minion.CardId == DiscardWarlockCardIds.DukeOfBelow
                ? minion with
                {
                    Attack = minion.Attack + 2,
                    Health = minion.Health + 2,
                    MaxHealth = minion.MaxHealth + 2
                }
                : minion).ToImmutableArray()
        };
        return result with
        {
            State = result.State.WithPlayer(side, player),
            Events = result.Events.Add(new RuleEvent("duke_growth", null, null, 2, DiscardWarlockCardIds.DukeOfBelow))
        };

        HandCardState GrowDuke(HandCardState candidate) => candidate.CardId == DiscardWarlockCardIds.DukeOfBelow
            ? candidate with { Attack = candidate.Attack + 2, Health = candidate.Health + 2 }
            : candidate;
    }

    private static TransitionResult SummonTokens(
        TransitionResult result,
        PlayerSide side,
        int sourceEntityId,
        string tokenCardId,
        int count)
    {
        var state = result.State;
        var events = result.Events;
        for (var index = 0; index < count; index++)
        {
            var player = state.Player(side);
            if (player.BoardCount >= CommonRuleEngine.MaximumBoardSize)
            {
                events = events.Add(new RuleEvent("summon_failed_board_full", sourceEntityId, null, 0, tokenCardId));
                continue;
            }
            state = state.AllocateEntity(out var entityId);
            player = state.Player(side);
            var token = DiscardWarlockCardCatalog.Create(tokenCardId, entityId);
            player = player with
            {
                Board = player.Board.Add(new MinionState(
                    token.EntityId,
                    token.CardId,
                    player.BoardCount + 1,
                    token.Attack,
                    token.Health,
                    token.Health,
                    SummonedThisTurn: true))
            };
            state = state.WithPlayer(side, player);
            events = events.Add(new RuleEvent("summon", sourceEntityId, entityId, 0, tokenCardId));
        }
        return result with { State = state, Events = events };
    }

    private static TransitionResult SummonDiscardedMinion(
        TransitionResult result,
        PlayerSide side,
        HandCardState card)
    {
        var player = result.State.Player(side);
        if (player.BoardCount >= CommonRuleEngine.MaximumBoardSize)
            return Append(result, new RuleEvent("summon_failed_board_full", card.EntityId, null, 0, card.CardId));

        var graveyardCard = player.Graveyard.FirstOrDefault(candidate => candidate.EntityId == card.EntityId);
        player = player with
        {
            Graveyard = graveyardCard is null ? player.Graveyard : player.Graveyard.Remove(graveyardCard),
            Board = player.Board.Add(new MinionState(
                card.EntityId,
                card.CardId,
                player.BoardCount + 1,
                card.Attack,
                Math.Max(1, card.Health),
                Math.Max(1, card.Health),
                Taunt: card.Taunt,
                Rush: card.Rush,
                Charge: card.Charge,
                SummonedThisTurn: true))
        };
        return result with
        {
            State = result.State.WithPlayer(side, player),
            Events = result.Events.Add(new RuleEvent("summon", card.EntityId, card.EntityId, 0, card.CardId))
        };
    }

    private static TransitionResult RequestRandomOneCostSummons(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var availableSlots = Math.Max(0, CommonRuleEngine.MaximumBoardSize - result.State.Player(side).BoardCount);
        var summonCount = Math.Min(2, availableSlots);
        return Append(
            result,
            new RuleEvent("random_one_cost_summon_pending", sourceEntityId, null, summonCount, DiscardWarlockCardIds.DisposableAcolytes));
    }

    private static TransitionResult DealDamage(TransitionResult result, int targetEntityId, int amount, int sourceEntityId)
    {
        var state = result.State;
        var events = result.Events.Add(new RuleEvent("damage", sourceEntityId, targetEntityId, amount));
        foreach (var side in new[] { PlayerSide.Friendly, PlayerSide.Opponent })
        {
            var player = state.Player(side);
            if (player.Hero.EntityId == targetEntityId)
            {
                state = state.WithPlayer(side, player with { Hero = DamageHero(player.Hero, amount) });
                return result with { State = state, Events = events };
            }

            var minion = player.Board.FirstOrDefault(candidate => candidate.EntityId == targetEntityId);
            if (minion is null)
                continue;
            var damaged = minion with { Health = minion.Health - (minion.Immune ? 0 : amount) };
            if (damaged.Health > 0)
                player = player with { Board = player.Board.Replace(minion, damaged) };
            else
            {
                player = (player with
                {
                    Board = player.Board.Remove(minion),
                    Graveyard = player.Graveyard.Add(new ZoneCardState(minion.EntityId, minion.CardId))
                }).NormalizePositions();
                events = events.Add(new RuleEvent("death", minion.EntityId, null, 0, minion.CardId));
            }
            state = state.WithPlayer(side, player);
            return result with { State = state, Events = events };
        }
        return result;
    }

    private static HeroState DamageHero(HeroState hero, int amount)
    {
        if (hero.Immune || amount <= 0)
            return hero;
        var armorDamage = Math.Min(hero.Armor, amount);
        return hero with
        {
            Armor = hero.Armor - armorDamage,
            Health = Math.Max(0, hero.Health - amount + armorDamage)
        };
    }

    private static TransitionResult Append(TransitionResult result, params RuleEvent[] events) =>
        result with { Events = result.Events.AddRange(events) };
}
