using System;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public interface IGameEventSource
{
    void Start(Action gameStarted, Action gameEnded, Action stateChanged);

    void Poll();

    void Stop();
}

public interface ISnapshotObservationSource
{
    bool TryCapture(
        Guid gameId,
        bool isStable,
        out GameObservation? observation,
        out SnapshotCaptureFailure failure);
}

public enum SnapshotCaptureFailure
{
    None,
    MissingFriendlyHero,
    MissingOpponentHero,
    MissingFriendlyHeroPower,
    MissingOpponentHeroPower,
    MissingPlayerEntity,
    MissingGameEntity,
    InvalidTurn,
    EmptyObservation
}
