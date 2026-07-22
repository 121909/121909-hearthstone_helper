using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public sealed class DiscardWarlockRuleEngine
{
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
            var chronoclawsAttack = action is AttackAction attack &&
                state.Player(attack.Side).Hero.EntityId == attack.SourceEntityId &&
                state.Player(attack.Side).Weapon?.CardId == DiscardWarlockCardIds.Chronoclaws;
            var commonResult = _common.Apply(state, action);
            if (!commonResult.IsLegal)
                return commonResult;
            commonResult = ResolvePlatysaurDeaths(commonResult);
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
            DiscardWarlockCardIds.DisposableAcolytes => Append(
                result,
                new RuleEvent("random_one_cost_summon_pending", card.EntityId, null, 2, card.CardId)),
            DiscardWarlockCardIds.OcularOccultist => Append(
                result,
                new RuleEvent("hand_discard_choice_pending", card.EntityId, null, 0, card.CardId)),
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
            player = player with { Deck = player.Deck.Add(DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, entityId)) };
            state = state.WithPlayer(side, player);
            events = events.Add(new RuleEvent("shuffle_into_deck", sourceEntityId, entityId, 0, DiscardWarlockCardIds.ShredOfTime));
        }
        return result with { State = state, Events = events };
    }

    private static TransitionResult PartyFiend(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var state = result.State;
        var events = result.Events;
        for (var index = 0; index < 2 && state.Player(side).BoardCount < CommonRuleEngine.MaximumBoardSize; index++)
        {
            state = state.AllocateEntity(out var entityId);
            var player = state.Player(side);
            var token = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Felbeast, entityId);
            player = player with
            {
                Board = player.Board.Add(new MinionState(
                    token.EntityId,
                    token.CardId,
                    player.Board.Length + 1,
                    token.Attack,
                    token.Health,
                    token.Health,
                    SummonedThisTurn: true))
            };
            state = state.WithPlayer(side, player);
            events = events.Add(new RuleEvent("summon", sourceEntityId, entityId, 0, token.CardId));
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
        return ResolveRandomDiscard(result, side, result.State.Player(side).Hand, "soulfire");
    }

    private TransitionResult Platysaur(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var eventCount = result.Events.Length;
        result = Draw(result, side, 1, false, sourceEntityId);
        var drawn = result.Events.Skip(eventCount).FirstOrDefault(ruleEvent => ruleEvent.Type == "draw");
        if (drawn?.SourceEntityId is not int drawnEntityId)
            return result;
        return result with
        {
            State = result.State with
            {
                PlatysaurBindings = result.State.Bindings.SetItem(sourceEntityId, drawnEntityId)
            },
            Events = result.Events.Add(new RuleEvent("platysaur_bind", sourceEntityId, drawnEntityId))
        };
    }

    private TransitionResult WickedWhispers(TransitionResult result, PlayerSide side, int sourceEntityId)
    {
        var hand = result.State.Player(side).Hand;
        if (hand.IsEmpty)
            return ApplyBoardBuff(result, side, sourceEntityId);
        var lowestCost = hand.Min(card => card.Cost);
        var candidates = hand.Where(card => card.Cost == lowestCost).ToArray();
        return ApplyBoardBuff(ResolveRandomDiscard(result, side, candidates, "wicked_whispers"), side, sourceEntityId);
    }

    private TransitionResult Draw(TransitionResult result, PlayerSide side, int count, bool temporary, int sourceEntityId)
    {
        var state = result.State;
        var events = result.Events;
        for (var index = 0; index < count; index++)
        {
            var draw = _common.DrawCard(state, side);
            state = draw.State;
            events = events.AddRange(draw.Events);
            var drawn = draw.Events.LastOrDefault(ruleEvent => ruleEvent.Type == "draw");
            if (drawn?.SourceEntityId is not int entityId)
                continue;

            var player = state.Player(side);
            var handCard = player.Hand.First(card => card.EntityId == entityId);
            if (handCard.CardId == DiscardWarlockCardIds.ShredOfTime)
            {
                player = player with
                {
                    Hand = player.Hand.Remove(handCard),
                    Graveyard = player.Graveyard.Add(new ZoneCardState(handCard.EntityId, handCard.CardId)),
                    Hero = DamageHero(player.Hero, 3)
                };
                state = state.WithPlayer(side, player);
                events = events.Add(new RuleEvent("casts_when_drawn", handCard.EntityId, player.Hero.EntityId, 3, handCard.CardId));
            }
            else if (temporary)
            {
                player = player with { Hand = player.Hand.Replace(handCard, handCard with { Temporary = true }) };
                state = state.WithPlayer(side, player);
                events = events.Add(new RuleEvent("mark_temporary", sourceEntityId, handCard.EntityId, 0, handCard.CardId));
            }
        }
        return result with { State = state, Events = events };
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
        var generated = DiscardWarlockCardCatalog.Create(candidate.CardId, candidate.EntityId) with { Temporary = true };
        var events = ImmutableArray.Create(new RuleEvent("choice_selected", choice.SourceEntityId, candidate.EntityId, 0, candidate.CardId));
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
        if (location.Cooldown > 0 || location.Durability <= 0)
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
            Cooldown = location.ActivationCooldown
        };
        var locations = updatedLocation.Durability == 0
            ? player.Locations.Remove(location)
            : player.Locations.Replace(location, updatedLocation);
        player = (player with { Locations = locations }).NormalizePositions();
        var result = TransitionResult.Legal(
            state.WithPlayer(action.Side, player) with { PendingChoice = null },
            new[] { new RuleEvent("use_location", location.EntityId, selected.EntityId, 0, location.CardId) });
        result = DiscardSpecific(result, action.Side, selected.EntityId, "chamber_of_viscidus");
        return Draw(result, action.Side, 2, false, location.EntityId);
    }

    private static TransitionResult ResolveHighestCostDiscard(TransitionResult result, PlayerSide side, string source)
    {
        var hand = result.State.Player(side).Hand;
        if (hand.IsEmpty)
            return result;
        var highestCost = hand.Max(card => card.Cost);
        return ResolveRandomDiscard(result, side, hand.Where(card => card.Cost == highestCost), source);
    }

    private static TransitionResult ResolveRandomDiscard(
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

    private static TransitionResult DiscardSpecific(TransitionResult result, PlayerSide side, int entityId, string source)
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
        return result with
        {
            State = result.State.WithPlayer(side, player),
            Events = result.Events.Add(new RuleEvent("discard", null, card.EntityId, 1, card.CardId))
                .Add(new RuleEvent("discard_source", null, card.EntityId, 0, source))
        };
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

    private static TransitionResult ResolvePlatysaurDeaths(TransitionResult result)
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
