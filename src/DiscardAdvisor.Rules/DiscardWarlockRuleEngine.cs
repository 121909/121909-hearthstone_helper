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
        if (action is not PlayCardAction play)
            return _common.Apply(state, action);

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
            DiscardWarlockCardIds.Platysaur => Draw(result, side, 1, false, card.EntityId),
            DiscardWarlockCardIds.Soulfire => Soulfire(result, side, card.EntityId, targetEntityId!.Value),
            DiscardWarlockCardIds.Soularium => Draw(result, side, 3, true, card.EntityId),
            DiscardWarlockCardIds.WickedWhispers => Append(
                result,
                new RuleEvent("discard_lowest_pending", card.EntityId, null, 0, card.CardId),
                new RuleEvent("buff_after_discard_pending", card.EntityId, null, 1, card.CardId)),
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
        return Append(result, new RuleEvent("random_discard_pending", sourceEntityId, null, 1, DiscardWarlockCardIds.Soulfire));
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

    private static TransitionResult SelectChoice(RuleGameState state, SelectChoiceAction action)
    {
        if (action.Side != state.ActiveSide)
            return TransitionResult.Illegal(state, RuleError.NotActivePlayer);
        var choice = state.PendingChoice;
        if (choice is null || choice.ChoiceId != action.ChoiceId)
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);
        var candidate = choice.Candidates.FirstOrDefault(value => value.EntityId == action.SelectedEntityId);
        if (candidate is null)
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);
        if (choice.SourceCardId != DiscardWarlockCardIds.CursedCatacombs)
            return TransitionResult.Illegal(state, RuleError.UnsupportedAction);

        var player = state.Player(action.Side);
        var generated = DiscardWarlockCardCatalog.Create(candidate.CardId, candidate.EntityId) with { Temporary = true };
        var events = ImmutableArray.Create(new RuleEvent("choice_selected", null, candidate.EntityId, 0, candidate.CardId));
        if (player.Hand.Length >= CommonRuleEngine.MaximumHandSize)
        {
            player = player with { Graveyard = player.Graveyard.Add(new ZoneCardState(generated.EntityId, generated.CardId)) };
            events = events.Add(new RuleEvent("burn", generated.EntityId, null, 0, generated.CardId));
        }
        else
        {
            player = player with { Hand = player.Hand.Add(generated) };
            events = events.Add(new RuleEvent("mark_temporary", null, generated.EntityId, 0, generated.CardId));
        }
        return TransitionResult.Legal(state.WithPlayer(action.Side, player) with { PendingChoice = null }, events);
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

