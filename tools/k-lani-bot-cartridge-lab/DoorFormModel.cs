using System.Diagnostics;

namespace KLaniBot.CartridgeLab;

// The controlled door fixture offers two variants with distinct dimension and glass capabilities.
public enum DoorVariant
{
    Basic,
    Wide
}

// The fixture accepts only these nominal door widths in millimetres.
public enum DoorWidth
{
    Millimetres900 = 900,
    Millimetres1000 = 1000,
    Millimetres1200 = 1200
}

// The fixture exposes these side-panel configurations when its current width permits them.
public enum DoorSidePanels
{
    None,
    Left,
    Right,
    Both
}

// The fixture permits these glass choices only when a left panel exists.
public enum DoorGlass
{
    Clear,
    Frosted,
    Security
}

// The independent verifier reads the values actually saved by the native door application.
public sealed record DoorFormResult(DoorVariant Model, int WidthMm, DoorSidePanels SidePanels, DoorGlass? LeftGlass);

// The GTK door fixture rejects stale choices and clears invalid dependent values while retaining valid choices.
public sealed class DoorFormModel
{
    public DoorVariant Model { get; private set; } = DoorVariant.Basic;
    public DoorWidth? Width { get; private set; } = DoorWidth.Millimetres900;
    public DoorSidePanels? SidePanels { get; private set; } = DoorSidePanels.None;
    public DoorGlass? LeftGlass
    {
        get; private set;
    }
    public int ContextRevision
    {
        get; private set;
    }
    public int StructureRevision
    {
        get; private set;
    }
    public bool LeftGlassVisible => SidePanels is DoorSidePanels.Left or DoorSidePanels.Both;
    public DoorWidth[] WidthOptions => Model switch
    {
        DoorVariant.Basic => [DoorWidth.Millimetres900, DoorWidth.Millimetres1000],
        DoorVariant.Wide => [DoorWidth.Millimetres1000, DoorWidth.Millimetres1200],
        _ => throw new UnreachableException()
    };
    public DoorSidePanels[] SidePanelOptions => Width switch
    {
        DoorWidth.Millimetres900 => [DoorSidePanels.None],
        DoorWidth.Millimetres1000 => [DoorSidePanels.None, DoorSidePanels.Left, DoorSidePanels.Right],
        DoorWidth.Millimetres1200 => [DoorSidePanels.None, DoorSidePanels.Left, DoorSidePanels.Right, DoorSidePanels.Both],
        null => [],
        _ => throw new UnreachableException()
    };
    public DoorGlass[] GlassOptions => Model switch
    {
        DoorVariant.Basic => [DoorGlass.Clear, DoorGlass.Frosted],
        DoorVariant.Wide => [DoorGlass.Clear, DoorGlass.Frosted, DoorGlass.Security],
        _ => throw new UnreachableException()
    };

    public bool TrySelectModel(DoorVariant model, int observedRevision)
    {
        if (!Enum.IsDefined(model) || observedRevision != ContextRevision)
        {
            return false;
        }

        bool earlierVisibility = LeftGlassVisible;
        Model = model;
        if (Width.HasValue && !WidthOptions.Contains(Width.Value))
        {
            Width = null;
        }
        RevalidateDependents(earlierVisibility);

        return true;
    }

    public bool TrySelectWidth(DoorWidth width, int observedRevision)
    {
        if (observedRevision != ContextRevision || !WidthOptions.Contains(width))
        {
            return false;
        }

        bool earlierVisibility = LeftGlassVisible;
        Width = width;
        RevalidateDependents(earlierVisibility);

        return true;
    }

    public bool TrySelectSidePanels(DoorSidePanels panels, int observedRevision)
    {
        if (observedRevision != ContextRevision || !SidePanelOptions.Contains(panels))
        {
            return false;
        }

        bool earlierVisibility = LeftGlassVisible;
        SidePanels = panels;
        RevalidateDependents(earlierVisibility);

        return true;
    }

    public bool TrySelectGlass(DoorGlass glass, int observedRevision)
    {
        if (observedRevision != ContextRevision || !LeftGlassVisible || !GlassOptions.Contains(glass))
        {
            return false;
        }

        LeftGlass = glass;
        ContextRevision++;

        return true;
    }

    public bool TryValidate(out DoorFormResult? result)
    {
        result = null;
        if (!Width.HasValue || !SidePanels.HasValue || !WidthOptions.Contains(Width.Value)
            || !SidePanelOptions.Contains(SidePanels.Value)
            || LeftGlassVisible && (!LeftGlass.HasValue || !GlassOptions.Contains(LeftGlass.Value)))
        {
            return false;
        }

        result = new DoorFormResult(Model, (int)Width.Value, SidePanels.Value, LeftGlassVisible ? LeftGlass : null);

        return true;
    }

    private void RevalidateDependents(bool earlierVisibility)
    {
        if (SidePanels.HasValue && !SidePanelOptions.Contains(SidePanels.Value))
        {
            SidePanels = null;
        }
        if (!LeftGlassVisible || LeftGlass.HasValue && !GlassOptions.Contains(LeftGlass.Value))
        {
            LeftGlass = null;
        }
        if (LeftGlassVisible != earlierVisibility)
        {
            StructureRevision++;
        }
        ContextRevision++;
    }
}
