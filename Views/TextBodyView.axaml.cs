using System;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

public partial class TextBodyView : UserControl
{
    public TextBodyView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var inlines = Body.Inlines ??= new InlineCollection();
        inlines.Clear();
        if (DataContext is not TextBodyViewModel body)
            return;

        foreach (var segment in body.Segments)
            inlines.Add(CreateRun(segment));
    }

    /// <summary>
    /// One run per segment. Entities use the data palette (never the brand red nor the state colors, UI.md 9.3/9.4);
    /// codes use the tabular cut so they line up with the plan board.
    /// </summary>
    private Run CreateRun(NexusTextSegment segment)
    {
        var run = new Run(segment.Text);
        switch (segment.Kind)
        {
            case NexusTextKind.Sku:
                Style(run, "Data2Brush", "TabularStrongFont");
                break;
            case NexusTextKind.Location:
                Style(run, "Data1Brush", "TabularStrongFont");
                break;
            case NexusTextKind.Zone:
                Style(run, "Data3Brush", "TabularStrongFont");
                break;
            case NexusTextKind.Order:
                Style(run, "Data4Brush", "TabularStrongFont");
                break;
            case NexusTextKind.Product:
                Style(run, "TextPrimaryBrush", null);
                run.FontWeight = FontWeight.SemiBold;
                break;
            case NexusTextKind.Quantity:
            case NexusTextKind.Plan:
                Style(run, "TextPrimaryBrush", "TabularStrongFont");
                break;
        }
        return run;
    }

    private void Style(Run run, string brushKey, string? fontKey)
    {
        if (this.TryFindResource(brushKey, ActualThemeVariant, out var brush) && brush is IBrush b)
            run.Foreground = b;
        if (fontKey is not null && this.TryFindResource(fontKey, out var font) && font is FontFamily f)
            run.FontFamily = f;
    }
}
