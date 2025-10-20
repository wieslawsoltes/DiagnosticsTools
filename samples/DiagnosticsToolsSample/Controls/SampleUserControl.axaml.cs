using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiagnosticsToolsSample.Controls;

public partial class SampleUserControl : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<string> DetailsProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(Details), string.Empty);

    public static readonly StyledProperty<string> FooterProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(Footer), string.Empty);

    public static readonly StyledProperty<bool> ShowFooterProperty =
        AvaloniaProperty.Register<SampleUserControl, bool>(nameof(ShowFooter));

    public static readonly StyledProperty<bool> ShowDetailsPanelProperty =
        AvaloniaProperty.Register<SampleUserControl, bool>(nameof(ShowDetailsPanel));

    public static readonly StyledProperty<string> BadgeTextProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(BadgeText), "Preview");

    public static readonly StyledProperty<bool> ShowBadgeProperty =
        AvaloniaProperty.Register<SampleUserControl, bool>(nameof(ShowBadge), true);

    public static readonly StyledProperty<string> ActionLabelProperty =
        AvaloniaProperty.Register<SampleUserControl, string>(nameof(ActionLabel), "Open");

    public static readonly StyledProperty<bool> ShowActionProperty =
        AvaloniaProperty.Register<SampleUserControl, bool>(nameof(ShowAction), true);

    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<SampleUserControl, bool>(nameof(IsHighlighted));

    private Border? _layoutRoot;

    static SampleUserControl()
    {
        IsHighlightedProperty.Changed.AddClassHandler<SampleUserControl>((control, _) => control.UpdateHighlight());
    }

    public SampleUserControl()
    {
        InitializeComponent();
        _layoutRoot = this.FindControl<Border>("LayoutRoot");
        UpdateHighlight();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Details
    {
        get => GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    public string Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public bool ShowFooter
    {
        get => GetValue(ShowFooterProperty);
        set => SetValue(ShowFooterProperty, value);
    }

    public bool ShowDetailsPanel
    {
        get => GetValue(ShowDetailsPanelProperty);
        set => SetValue(ShowDetailsPanelProperty, value);
    }

    public string BadgeText
    {
        get => GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public bool ShowBadge
    {
        get => GetValue(ShowBadgeProperty);
        set => SetValue(ShowBadgeProperty, value);
    }

    public string ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public bool ShowAction
    {
        get => GetValue(ShowActionProperty);
        set => SetValue(ShowActionProperty, value);
    }

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdateHighlight()
    {
        if (_layoutRoot is null)
        {
            return;
        }

        _layoutRoot.Classes.Set("highlight", IsHighlighted);
    }
}
