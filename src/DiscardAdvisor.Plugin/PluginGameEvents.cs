using System;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public interface IGameEventSource
{
    void Start(Action gameStarted, Action gameEnded, Action stateChanged);

    void Stop();
}

public interface ISnapshotObservationSource
{
    bool TryCapture(Guid gameId, bool isStable, out GameObservation? observation);
}

