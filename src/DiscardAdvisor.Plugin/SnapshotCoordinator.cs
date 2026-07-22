using System;
using System.Threading;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public sealed class SnapshotWorkItem
{
    internal SnapshotWorkItem(GameSnapshot snapshot, CancellationToken cancellationToken)
    {
        Snapshot = snapshot;
        CancellationToken = cancellationToken;
    }

    public GameSnapshot Snapshot { get; }
    public string StateId => Snapshot.StateId;
    public CancellationToken CancellationToken { get; }
}

public sealed class SnapshotCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _activeWork;
    private DateTimeOffset _lastDirtyAt;
    private long _dirtyVersion;
    private bool _dirty;
    private string? _lastDispatchedStateId;
    private string? _currentStateId;

    public SnapshotCoordinator()
        : this(() => DateTimeOffset.UtcNow, DefaultDebounce)
    {
    }

    internal SnapshotCoordinator(Func<DateTimeOffset> utcNow, TimeSpan debounce)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        _debounce = debounce;
    }

    public string? CurrentStateId
    {
        get
        {
            lock (_gate)
                return _currentStateId;
        }
    }

    public void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            _lastDirtyAt = _utcNow();
            _dirtyVersion++;
            _currentStateId = null;
            CancelActiveWork();
        }
    }

    public bool TryCreateWork(Func<GameSnapshot?> capture, out SnapshotWorkItem? workItem)
    {
        if (capture is null)
            throw new ArgumentNullException(nameof(capture));

        long captureVersion;
        lock (_gate)
        {
            if (!_dirty || _utcNow() - _lastDirtyAt < _debounce)
            {
                workItem = null;
                return false;
            }
            captureVersion = _dirtyVersion;
        }

        var snapshot = capture();
        lock (_gate)
        {
            if (captureVersion != _dirtyVersion)
            {
                workItem = null;
                return false;
            }

            if (snapshot is null)
            {
                _lastDirtyAt = _utcNow();
                workItem = null;
                return false;
            }

            _dirty = false;
            _currentStateId = snapshot.StateId;
            if (string.Equals(_lastDispatchedStateId, snapshot.StateId, StringComparison.Ordinal))
            {
                workItem = null;
                return false;
            }

            _lastDispatchedStateId = snapshot.StateId;
            _activeWork = new CancellationTokenSource();
            workItem = new SnapshotWorkItem(snapshot, _activeWork.Token);
            return true;
        }
    }

    public bool CanAcceptResult(string stateId)
    {
        lock (_gate)
            return !_dirty && string.Equals(_currentStateId, stateId, StringComparison.Ordinal);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _dirty = false;
            _dirtyVersion++;
            _currentStateId = null;
            _lastDispatchedStateId = null;
            CancelActiveWork();
        }
    }

    public void Dispose() => Reset();

    private void CancelActiveWork()
    {
        if (_activeWork is null)
            return;
        _activeWork.Cancel();
        _activeWork.Dispose();
        _activeWork = null;
    }
}

