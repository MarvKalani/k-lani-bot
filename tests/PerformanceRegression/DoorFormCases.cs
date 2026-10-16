using KLaniBot.CartridgeLab;

// Deterministic tests cover the door fixture's published dimension, dependency and revision rules without a model or GUI.
internal static class DoorFormCases
{
    private static int passed;

    public static int Run()
    {
        CheckBasic();
        CheckWide();
        CheckInvalidation();

        return passed;
    }

    private static void CheckBasic()
    {
        var model = new DoorFormModel();
        Check(model.TryValidate(out var result) && result?.WidthMm == 900 && result.LeftGlass == null, "Basic/900 is valid without glass.");
        Check(!model.TrySelectGlass(DoorGlass.Clear, model.ContextRevision), "Hidden glass rejects input.");
        Check(!model.TrySelectWidth(DoorWidth.Millimetres1200, model.ContextRevision), "Basic cannot select 1200 mm.");
        Check(!model.TrySelectSidePanels(DoorSidePanels.Left, model.ContextRevision), "900 mm cannot select side panels.");
        Check(model.TrySelectWidth(DoorWidth.Millimetres1000, model.ContextRevision), "Basic supports 1000 mm.");
        Check(!model.TrySelectSidePanels(DoorSidePanels.Both, model.ContextRevision), "1000 mm cannot select both panels.");
        Check(model.TrySelectSidePanels(DoorSidePanels.Left, model.ContextRevision), "1000 mm supports a left panel.");
        Check(model.LeftGlassVisible && !model.TryValidate(out _), "Visible left glass is required.");
        Check(!model.TrySelectGlass(DoorGlass.Security, model.ContextRevision), "Basic cannot select security glass.");
        Check(model.TrySelectGlass(DoorGlass.Frosted, model.ContextRevision) && model.TryValidate(out _), "Basic accepts its valid glass.");
    }

    private static void CheckWide()
    {
        var model = new DoorFormModel();
        int oldRevision = model.ContextRevision;
        Check(model.TrySelectModel(DoorVariant.Wide, oldRevision), "Wide changes the model.");
        Check(model.Width == null && model.SidePanels == null && !model.TryValidate(out _), "Invalid dependent choices are cleared.");
        Check(!model.TrySelectWidth(DoorWidth.Millimetres1200, oldRevision), "Stale context refuses input.");
        Check(model.TrySelectWidth(DoorWidth.Millimetres1200, model.ContextRevision), "Wide accepts 1200 mm.");
        Check(model.TrySelectSidePanels(DoorSidePanels.Both, model.ContextRevision), "1200 mm accepts both panels.");
        Check(model.TrySelectGlass(DoorGlass.Security, model.ContextRevision), "Wide accepts security glass.");
        Check(model.TryValidate(out var result) && result?.SidePanels == DoorSidePanels.Both, "Wide result includes both panels.");
        Check(model.GlassOptions.Contains(DoorGlass.Security), "The same glass field has Wide's expanded options.");
    }

    private static void CheckInvalidation()
    {
        var model = new DoorFormModel();
        model.TrySelectModel(DoorVariant.Wide, model.ContextRevision);
        model.TrySelectWidth(DoorWidth.Millimetres1000, model.ContextRevision);
        model.TrySelectSidePanels(DoorSidePanels.Left, model.ContextRevision);
        model.TrySelectGlass(DoorGlass.Security, model.ContextRevision);
        int structure = model.StructureRevision;
        Check(model.TrySelectModel(DoorVariant.Basic, model.ContextRevision), "Switching to Basic is permitted.");
        Check(model.Width == DoorWidth.Millimetres1000 && model.LeftGlass == null, "Valid width remains; invalid glass is removed.");
        Check(model.StructureRevision == structure && model.LeftGlassVisible, "An option change does not replace the glass field.");
        Check(!model.TryValidate(out _), "Removed required glass prevents saving.");
        Check(model.TrySelectSidePanels(DoorSidePanels.Right, model.ContextRevision), "Right panel removes the left-glass branch.");
        Check(!model.LeftGlassVisible && model.StructureRevision > structure && model.TryValidate(out _), "Hidden glass is not required.");
        int revision = model.ContextRevision;
        Check(!model.TrySelectModel((DoorVariant)999, revision), "Undefined model rejects input.");
        Check(!model.TrySelectWidth((DoorWidth)999, revision), "Undefined width rejects input.");
        Check(!model.TrySelectSidePanels((DoorSidePanels)999, revision), "Undefined panels reject input.");
        Check(!model.TrySelectGlass((DoorGlass)999, revision), "Undefined glass rejects input.");
        Check(model.ContextRevision == revision, "Refused input leaves context unchanged.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        passed++;
    }
}
