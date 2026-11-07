using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace erp_inteligente.ViewModels;

/// <summary>Button of the menu column: icon on the left, text on the right.</summary>
public sealed partial class NavigationItem : ObservableObject
{
    private readonly Func<PageViewModel> _factory;
    private PageViewModel? _page;

    /// <param name="iconKey">Name of an "Icon.*" geometry in Styles/Icons.axaml.</param>
    /// <param name="requiredAnyOf">The item is shown when the user has at least one of these permissions; empty means always.</param>
    public NavigationItem(string label, string iconKey, Func<PageViewModel> factory, params string[] requiredAnyOf)
    {
        Label = label;
        IconKey = iconKey;
        _factory = factory;
        RequiredAnyOf = requiredAnyOf;
    }

    public string Label { get; }
    public string IconKey { get; }
    public IReadOnlyList<string> RequiredAnyOf { get; }

    /// <summary>The page is created once and kept, so the Nexus transcript survives navigation.</summary>
    public PageViewModel Page => _page ??= _factory();

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;
}
