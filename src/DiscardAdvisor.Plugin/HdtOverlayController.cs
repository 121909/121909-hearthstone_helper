using System;
using System.Windows;
using System.Windows.Controls;
using HearthDb;
using HdtApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace DiscardAdvisor.Plugin;

internal sealed class HdtOverlayController : IDisposable
{
    private readonly IOverlayStateSource? _source;
    private readonly AdvisorOverlayPresenter _presenter = new(new HdtCardNameResolver());
    private readonly DiscardAdvisorOverlayView _view = new();
    private bool _attached;

    public HdtOverlayController(IOverlayStateSource? source)
    {
        _source = source;
        _view.HideRequested += HandleHideRequested;
        _view.Update(_presenter.Present(
            source?.CurrentAdvisorUpdate ?? PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline)));
    }

    public void Attach()
    {
        var canvas = HdtApiCore.OverlayCanvas;
        if (!canvas.Dispatcher.CheckAccess())
        {
            canvas.Dispatcher.Invoke(Attach);
            return;
        }
        if (_attached)
            return;
        Canvas.SetRight(_view, 18);
        Canvas.SetTop(_view, 112);
        Panel.SetZIndex(_view, 120);
        canvas.Children.Add(_view);
        if (_source is not null)
            _source.AdvisorUpdated += HandleAdvisorUpdated;
        _attached = true;
    }

    public void ToggleVisibility()
    {
        var canvas = HdtApiCore.OverlayCanvas;
        if (!canvas.Dispatcher.CheckAccess())
        {
            canvas.Dispatcher.Invoke(ToggleVisibility);
            return;
        }
        _view.Visibility = _view.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void Dispose()
    {
        var canvas = HdtApiCore.OverlayCanvas;
        if (!canvas.Dispatcher.CheckAccess())
        {
            canvas.Dispatcher.Invoke(Dispose);
            return;
        }
        if (!_attached)
            return;
        if (_source is not null)
            _source.AdvisorUpdated -= HandleAdvisorUpdated;
        canvas.Children.Remove(_view);
        _view.HideRequested -= HandleHideRequested;
        _attached = false;
    }

    private void HandleAdvisorUpdated(PluginAdvisorUpdate update)
    {
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.BeginInvoke(new Action(() => HandleAdvisorUpdated(update)));
            return;
        }
        _view.Update(_presenter.Present(update));
    }

    private void HandleHideRequested(object? sender, EventArgs eventArgs) => ToggleVisibility();

    private sealed class HdtCardNameResolver : ICardNameResolver
    {
        public string Resolve(string cardId) => Cards.All.TryGetValue(cardId, out var card) &&
                                                !string.IsNullOrWhiteSpace(card.Name)
            ? card.Name
            : cardId;
    }
}
