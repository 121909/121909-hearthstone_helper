using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DiscardAdvisor.Plugin;

internal sealed class DiscardAdvisorOverlayView : UserControl
{
    private static readonly Brush Surface = Brush(22, 26, 30);
    private static readonly Brush RaisedSurface = Brush(31, 36, 41);
    private static readonly Brush Border = Brush(76, 84, 92);
    private static readonly Brush PrimaryText = Brush(246, 247, 248);
    private static readonly Brush SecondaryText = Brush(183, 190, 196);
    private static readonly Brush Neutral = Brush(123, 134, 145);
    private static readonly Brush Positive = Brush(74, 188, 119);
    private static readonly Brush Caution = Brush(240, 176, 67);
    private static readonly Brush Critical = Brush(239, 100, 97);
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailText;
    private readonly StackPanel _content;

    public DiscardAdvisorOverlayView()
    {
        Width = AdvisorOverlayLayout.PanelWidth;
        Height = AdvisorOverlayLayout.PanelHeight;
        Focusable = true;
        AutomationProperties.SetName(this, "Discard Advisor recommendations");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        _statusDot = new Ellipse { Width = 8, Height = 8, Fill = Neutral, Margin = new Thickness(0, 1, 8, 0) };
        _statusText = Text(string.Empty, 12, FontWeights.SemiBold, PrimaryText);
        _detailText = Text(string.Empty, 11, FontWeights.Normal, SecondaryText);
        _detailText.TextTrimming = TextTrimming.CharacterEllipsis;
        _content = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };

        var root = new Border
        {
            Width = AdvisorOverlayLayout.PanelWidth,
            Height = AdvisorOverlayLayout.PanelHeight,
            Background = Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = BuildLayout()
        };
        Content = root;
    }

    public event EventHandler? HideRequested;

    public void Update(AdvisorOverlayState state)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        _statusText.Text = state.StatusText;
        _detailText.Text = state.DetailText;
        _statusDot.Fill = Tone(state.StatusTone);
        _content.Children.Clear();
        if (state.PrimaryRoute is null)
            _content.Children.Add(BuildEmptyState(state));
        else
            BuildRoutes(state);
        AutomationProperties.SetHelpText(this, state.StatusText + ". " + state.DetailText);

        if (SystemParameters.MenuAnimation)
        {
            _content.BeginAnimation(OpacityProperty, new DoubleAnimation(
                0.72,
                1,
                TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private UIElement BuildLayout()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(AdvisorOverlayLayout.HeaderHeight) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(BuildHeader());
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            Content = _content
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        return layout;
    }

    private UIElement BuildHeader()
    {
        var header = new Grid
        {
            Background = RaisedSurface,
            Margin = new Thickness(0, 0, 0, 1)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        };
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal };
        statusLine.Children.Add(_statusDot);
        statusLine.Children.Add(_statusText);
        textStack.Children.Add(statusLine);
        textStack.Children.Add(_detailText);
        header.Children.Add(textStack);

        var hide = new Button
        {
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 10, 8, 10),
            Background = Brushes.Transparent,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Foreground = PrimaryText,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Content = "\uE711",
            ToolTip = "Hide Discard Advisor",
            FocusVisualStyle = FocusStyle()
        };
        AutomationProperties.SetName(hide, "Hide Discard Advisor");
        hide.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(hide, 1);
        header.Children.Add(hide);
        return header;
    }

    private UIElement BuildEmptyState(AdvisorOverlayState state)
    {
        var container = new StackPanel
        {
            Margin = new Thickness(4, 108, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 284
        };
        var status = Text(state.StatusText, 15, FontWeights.SemiBold, PrimaryText);
        status.TextAlignment = TextAlignment.Center;
        var detail = Text(state.DetailText, 12, FontWeights.Normal, SecondaryText);
        detail.Margin = new Thickness(0, 8, 0, 0);
        detail.TextAlignment = TextAlignment.Center;
        detail.TextWrapping = TextWrapping.Wrap;
        container.Children.Add(status);
        container.Children.Add(detail);
        if (state.Status == PluginAdvisorStatus.Analyzing)
        {
            container.Children.Add(new Border
            {
                Height = 3,
                Width = 96,
                Margin = new Thickness(0, 18, 0, 0),
                Background = Positive,
                CornerRadius = new CornerRadius(1)
            });
        }
        return container;
    }

    private void BuildRoutes(AdvisorOverlayState state)
    {
        _content.Children.Add(BuildRouteSummary(state.PrimaryRoute!, true));
        _content.Children.Add(BuildSteps(state.PrimaryRoute!.Steps));
        if (state.Alternatives.Count == 0)
            return;
        var alternativesLabel = Text("Alternatives", 11, FontWeights.SemiBold, SecondaryText);
        alternativesLabel.Margin = new Thickness(0, 12, 0, 4);
        _content.Children.Add(alternativesLabel);
        foreach (var route in state.Alternatives)
        {
            var expander = new Expander
            {
                Header = BuildRouteSummary(route, false),
                Content = BuildSteps(route.Steps),
                Foreground = PrimaryText,
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 5, 0, 5),
                IsExpanded = false,
                FocusVisualStyle = FocusStyle()
            };
            AutomationProperties.SetName(expander, route.Title + ", " + route.Risk);
            _content.Children.Add(expander);
        }
    }

    private static UIElement BuildRouteSummary(OverlayRoute route, bool primary)
    {
        var grid = new Grid { Margin = new Thickness(0, primary ? 0 : 2, 0, primary ? 6 : 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel();
        labels.Children.Add(Text(route.Title, primary ? 14 : 12, FontWeights.SemiBold, PrimaryText));
        var risk = Text(route.Risk, 11, FontWeights.Medium, Tone(route.RiskTone));
        risk.Margin = new Thickness(0, 2, 0, 0);
        labels.Children.Add(risk);
        grid.Children.Add(labels);
        var confidence = Text(route.Confidence, 11, FontWeights.Normal, SecondaryText);
        confidence.VerticalAlignment = VerticalAlignment.Top;
        confidence.Margin = new Thickness(8, 2, 0, 0);
        Grid.SetColumn(confidence, 1);
        grid.Children.Add(confidence);
        return grid;
    }

    private static UIElement BuildSteps(IReadOnlyList<OverlayStep> steps)
    {
        var stack = new StackPanel();
        foreach (var step in steps.Take(AdvisorOverlayLayout.DefaultVisibleSteps))
            stack.Children.Add(BuildStep(step));
        if (steps.Count > AdvisorOverlayLayout.DefaultVisibleSteps)
        {
            var remainder = new StackPanel();
            foreach (var step in steps.Skip(AdvisorOverlayLayout.DefaultVisibleSteps))
                remainder.Children.Add(BuildStep(step));
            var hiddenStepCount = steps.Count - AdvisorOverlayLayout.DefaultVisibleSteps;
            var more = new Expander
            {
                Header = hiddenStepCount + " more steps",
                Content = remainder,
                Foreground = SecondaryText,
                FontSize = 11,
                Margin = new Thickness(32, 2, 0, 2),
                FocusVisualStyle = FocusStyle()
            };
            AutomationProperties.SetName(more, hiddenStepCount + " more route steps");
            stack.Children.Add(more);
        }
        return stack;
    }

    private static UIElement BuildStep(OverlayStep step)
    {
        var row = new Grid { Height = AdvisorOverlayLayout.StepHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var number = new Border
        {
            Width = 22,
            Height = 22,
            Background = RaisedSurface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(step.Index.ToString(), 11, FontWeights.SemiBold, PrimaryText)
        };
        ((TextBlock)number.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)number.Child).VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(number);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(step.Title, 12, FontWeights.Medium, PrimaryText);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        labels.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(step.Detail))
        {
            var detail = Text(step.Detail, 11, FontWeights.Normal, SecondaryText);
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(detail);
        }
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        AutomationProperties.SetName(row, "Step " + step.Index + ": " + step.Title + ". " + step.Detail);
        return row;
    }

    private static TextBlock Text(string value, double size, FontWeight weight, Brush color) => new()
    {
        Text = value,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        LineHeight = size * 1.35
    };

    private static Brush Tone(OverlayRiskTone tone) => tone switch
    {
        OverlayRiskTone.Positive => Positive,
        OverlayRiskTone.Caution => Caution,
        OverlayRiskTone.Critical => Critical,
        _ => Neutral
    };

    private static Style FocusStyle()
    {
        var style = new Style(typeof(Control));
        var template = new ControlTemplate(typeof(Control));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, Positive);
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(2));
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Brush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
