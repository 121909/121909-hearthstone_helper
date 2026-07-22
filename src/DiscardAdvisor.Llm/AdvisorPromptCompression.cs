using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Llm;

public sealed class CompressedAdvisorPrompt
{
    internal CompressedAdvisorPrompt(
        string stateId,
        string candidateSetHash,
        ImmutableArray<string> candidateIds,
        string payloadJson)
    {
        StateId = stateId;
        CandidateSetHash = candidateSetHash;
        CandidateIds = candidateIds;
        PayloadJson = payloadJson;
    }

    public string StateId { get; }

    public string CandidateSetHash { get; }

    public ImmutableArray<string> CandidateIds { get; }

    public string PayloadJson { get; }
}

public sealed class AdvisorPromptCompressor
{
    public const string ProtocolVersion = "1.0.0";
    public const int DefaultMaximumCandidates = 5;
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Culture = CultureInfo.InvariantCulture,
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    };

    public CompressedAdvisorPrompt Compress(
        string stateId,
        RuleGameState state,
        IEnumerable<RiskAwareRouteCandidate> candidates,
        OpponentBelief belief,
        int maximumCandidates = DefaultMaximumCandidates)
    {
        if (string.IsNullOrWhiteSpace(stateId) || stateId.Length > 128)
            throw new ArgumentException("A state id of at most 128 characters is required.", nameof(stateId));
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));
        if (belief is null)
            throw new ArgumentNullException(nameof(belief));
        if (maximumCandidates < 1 || maximumCandidates > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));

        var selected = candidates.Take(maximumCandidates).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        if (selected.Any(candidate =>
                candidate is null || string.IsNullOrWhiteSpace(candidate.CandidateId) || candidate.Actions.IsEmpty))
        {
            throw new ArgumentException("Candidates must have ids and at least one action.", nameof(candidates));
        }
        if (selected.Select(candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Candidate ids must be unique.", nameof(candidates));

        var compressedCandidates = selected.Select(candidate => CompressCandidate(state, candidate)).ToArray();
        var candidateJson = JsonConvert.SerializeObject(compressedCandidates, SerializerSettings);
        var candidateSetHash = Sha256(candidateJson);
        var payload = new AdvisorPromptPayload(
            ProtocolVersion,
            stateId,
            candidateSetHash,
            new OpponentBeliefPayload(belief.Aggro, belief.Control, belief.Combo),
            CompressState(state),
            compressedCandidates);
        return new CompressedAdvisorPrompt(
            stateId,
            candidateSetHash,
            selected.Select(candidate => candidate.CandidateId).ToImmutableArray(),
            JsonConvert.SerializeObject(payload, SerializerSettings));
    }

    public ModelProviderRequest CreateProviderRequest(
        string requestId,
        CompressedAdvisorPrompt prompt,
        TimeSpan timeout,
        int maxOutputTokens = 384)
    {
        if (prompt is null)
            throw new ArgumentNullException(nameof(prompt));
        return new ModelProviderRequest(
            requestId,
            new[]
            {
                new ModelMessage(
                    ModelMessageRole.System,
                    "Select only candidate ids from the supplied set. Return JSON matching the response schema. " +
                    "Do not invent actions, targets, entities, or random outcomes."),
                new ModelMessage(ModelMessageRole.User, prompt.PayloadJson)
            },
            AdvisorSelectionProtocol.ResponseContract,
            timeout,
            maxOutputTokens,
            temperature: 0);
    }

    private static CompressedCandidatePayload CompressCandidate(
        RuleGameState initialState,
        RiskAwareRouteCandidate candidate)
    {
        var steps = candidate.Actions.Select((action, index) => CompressAction(
                initialState,
                candidate.RepresentativeRoute.State,
                action,
                index))
            .ToArray();
        var dimensions = candidate.ExpectedDimensions;
        return new CompressedCandidatePayload(
            candidate.CandidateId,
            steps,
            new RiskPayload(
                candidate.Risk.Expected,
                candidate.Risk.P10,
                candidate.Risk.Variance,
                candidate.Risk.LethalProbability,
                candidate.Risk.CoverageProbability,
                candidate.RiskAdjustedScore,
                candidate.Confidence),
            new DimensionsPayload(
                dimensions.Lethal,
                dimensions.Survival,
                dimensions.Board,
                dimensions.DiscardValue,
                dimensions.Resources,
                dimensions.TemporaryValue,
                dimensions.BoardSpace,
                dimensions.DirectDamage,
                dimensions.SelfDamage,
                dimensions.DukeGrowth,
                dimensions.OpponentPressure),
            Assumptions(candidate));
    }

    private static CompressedActionPayload CompressAction(
        RuleGameState initialState,
        RuleGameState finalState,
        RuleAction action,
        int index)
    {
        var sourceEntityId = action switch
        {
            PlayCardAction play => play.SourceEntityId,
            AttackAction attack => attack.SourceEntityId,
            UseLocationAction location => location.SourceEntityId,
            UseHeroPowerAction heroPower => initialState.Player(heroPower.Side).HeroPower.EntityId,
            _ => (int?)null
        };
        var targetEntityId = action switch
        {
            PlayCardAction play => play.TargetEntityId,
            AttackAction attack => attack.TargetEntityId,
            UseLocationAction location => location.SelectedEntityId,
            SelectChoiceAction choice => choice.SelectedEntityId,
            UseHeroPowerAction heroPower => heroPower.TargetEntityId,
            _ => null
        };
        var boardPosition = action is PlayCardAction positioned ? positioned.BoardPosition : null;
        return new CompressedActionPayload(
            index,
            ActionKind(action),
            sourceEntityId,
            FindCardId(initialState, finalState, sourceEntityId),
            targetEntityId,
            FindCardId(initialState, finalState, targetEntityId),
            boardPosition);
    }

    private static StatePayload CompressState(RuleGameState state)
    {
        var friendly = state.Friendly;
        var opponent = state.Opponent;
        return new StatePayload(
            state.TurnNumber,
            state.ActiveSide.ToString().ToUpperInvariant(),
            new PlayerPayload(
                friendly.Hero.Health,
                friendly.Hero.Armor,
                friendly.Mana.Available,
                friendly.Hand.Length,
                friendly.Deck.Length,
                friendly.Board.Sum(minion => Math.Max(0, minion.Attack)),
                friendly.Board.Sum(minion => Math.Max(0, minion.Health)),
                friendly.Board.Count(minion => minion.Taunt)),
            new PlayerPayload(
                opponent.Hero.Health,
                opponent.Hero.Armor,
                0,
                0,
                opponent.Deck.Length,
                opponent.Board.Sum(minion => Math.Max(0, minion.Attack)),
                opponent.Board.Sum(minion => Math.Max(0, minion.Health)),
                opponent.Board.Count(minion => minion.Taunt)));
    }

    private static string[] Assumptions(RiskAwareRouteCandidate candidate)
    {
        var assumptions = new List<string>();
        if (candidate.Outcomes.Any(outcome => outcome.Route.UsesMonteCarlo))
            assumptions.Add("monte_carlo_random_outcomes");
        if (candidate.Risk.CoverageProbability < 0.999999d)
            assumptions.Add("partial_branch_coverage");
        if (candidate.Outcomes.SelectMany(outcome => outcome.Route.Events).Any(ruleEvent =>
                ruleEvent.Type == "random_summon_effect_unmodeled"))
        {
            assumptions.Add("unmodeled_random_minion_effect");
        }
        if (candidate.Outcomes.SelectMany(outcome => outcome.Route.Events).Any(ruleEvent =>
                ruleEvent.Type.EndsWith("_unresolved", StringComparison.Ordinal)))
        {
            assumptions.Add("unresolved_random_effect");
        }
        return assumptions.ToArray();
    }

    private static string ActionKind(RuleAction action) => action switch
    {
        PlayCardAction => "PLAY_CARD",
        AttackAction => "ATTACK",
        UseHeroPowerAction => "USE_HERO_POWER",
        UseLocationAction => "USE_LOCATION",
        SelectChoiceAction => "SELECT_CHOICE",
        EndTurnAction => "END_TURN",
        _ => "UNSUPPORTED"
    };

    private static string? FindCardId(
        RuleGameState initialState,
        RuleGameState finalState,
        int? entityId)
    {
        if (entityId is not int id)
            return null;
        return FindCardId(initialState, id) ?? FindCardId(finalState, id);
    }

    private static string? FindCardId(RuleGameState state, int entityId)
    {
        foreach (var player in new[] { state.Friendly, state.Opponent })
        {
            if (player.Hero.EntityId == entityId)
                return player.Hero.CardId;
            if (player.HeroPower.EntityId == entityId)
                return player.HeroPower.CardId;
            if (player.Weapon?.EntityId == entityId)
                return player.Weapon.CardId;
            var cardId = player.Hand.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Deck.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Board.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Locations.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Graveyard.FirstOrDefault(card => card.EntityId == entityId)?.CardId;
            if (cardId is not null)
                return cardId;
        }
        return null;
    }

    private static string Sha256(string value)
    {
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(Encoding.UTF8.GetBytes(value))
            .Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private sealed class AdvisorPromptPayload
    {
        public AdvisorPromptPayload(
            string protocolVersion,
            string stateId,
            string candidateSetHash,
            OpponentBeliefPayload opponentBelief,
            StatePayload state,
            CompressedCandidatePayload[] candidates)
        {
            ProtocolVersion = protocolVersion;
            StateId = stateId;
            CandidateSetHash = candidateSetHash;
            OpponentBelief = opponentBelief;
            State = state;
            Candidates = candidates;
        }

        public string ProtocolVersion { get; }
        public string StateId { get; }
        public string CandidateSetHash { get; }
        public OpponentBeliefPayload OpponentBelief { get; }
        public StatePayload State { get; }
        public CompressedCandidatePayload[] Candidates { get; }
    }

    private sealed record OpponentBeliefPayload(double Aggro, double Control, double Combo);
    private sealed record StatePayload(int TurnNumber, string ActiveSide, PlayerPayload Friendly, PlayerPayload Opponent);
    private sealed record PlayerPayload(
        int Health,
        int Armor,
        int AvailableMana,
        int HandCount,
        int DeckCount,
        int BoardAttack,
        int BoardHealth,
        int TauntCount);
    private sealed record CompressedCandidatePayload(
        string CandidateId,
        CompressedActionPayload[] Steps,
        RiskPayload Risk,
        DimensionsPayload Dimensions,
        string[] Assumptions);
    private sealed record CompressedActionPayload(
        int Index,
        string Kind,
        int? SourceEntityId,
        string? SourceCardId,
        int? TargetEntityId,
        string? TargetCardId,
        int? BoardPosition);
    private sealed record RiskPayload(
        double Expected,
        double P10,
        double Variance,
        double LethalProbability,
        double CoverageProbability,
        double RiskAdjusted,
        double Confidence);
    private sealed record DimensionsPayload(
        double Lethal,
        double Survival,
        double Board,
        double DiscardValue,
        double Resources,
        double TemporaryValue,
        double BoardSpace,
        double DirectDamage,
        double SelfDamage,
        double DukeGrowth,
        double OpponentPressure);
}
