using System.Diagnostics;
using System.Text.Json;
using KLaniBot;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Linux;

// Deterministic regressions observe and operate the actual GTK fixture only in a private display.
internal sealed class NativeFixtureCases(Process fixture, string output)
{
    private readonly LinuxUiService ui = new();
    private int passed;
    private const int ObservationAttempts = 50;
    private const int ObservationDelayMilliseconds = 100;
    private const int DiscoveryControlCount = 250;

    // The private GUI regression selects the controlled native application variant to inspect.
    private enum FixtureMode
    {
        Dynamic,
        Discovery,
        Door
    }

    public static int Run(string fixturePath)
    {
        return RunFixture(fixturePath, FixtureMode.Dynamic);
    }

    public static int RunDiscovery(string fixturePath)
    {
        return RunFixture(fixturePath, FixtureMode.Discovery);
    }

    public static int RunDoor(string fixturePath)
    {
        return RunFixture(fixturePath, FixtureMode.Door);
    }

    private static int RunFixture(string fixturePath, FixtureMode mode)
    {
        PrivateGuiSession.Require();
        string directory = Path.Combine(Path.GetTempPath(), "k-lani-form-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "result.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(fixturePath));
        start.ArgumentList.Add(FixtureCommand(mode));
        start.ArgumentList.Add(output);
        using var fixture = Process.Start(start)!;
        try
        {
            var cases = new NativeFixtureCases(fixture, output);
            if (mode == FixtureMode.Discovery)
            {
                cases.CheckDiscovery();
            }
            else if (mode == FixtureMode.Door)
            {
                cases.CheckDoor();
            }
            else
            {
                cases.CheckNativeChanges();
            }
            Console.WriteLine($"{cases.passed} native {mode} checks passed.");
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(true);
                fixture.WaitForExit();
            }
            Directory.Delete(directory, true);
        }

        return 0;
    }

    private static string FixtureCommand(FixtureMode mode)
    {
        return mode switch
        {
            FixtureMode.Dynamic => "dynamic-form",
            FixtureMode.Discovery => "discovery",
            FixtureMode.Door => "door-form",
            _ => throw new UnreachableException()
        };
    }

    private void CheckDoor()
    {
        var initial = ui.ReadFieldMetadata(WaitForChoice("Door model").Id);
        Check(initial.Source == UiFieldMetadataSource.ApplicationDeclaration, "Field constraints have public provider provenance.");
        Check(initial.Required == true && initial.ReadOnly == false && initial.Visible == true, "Declared and native state are explicit.");
        Check(initial.Choices.Options.Select(option => option.Label).SequenceEqual(["Basic", "Wide"]), "Actual model options are read.");
        Check(initial.Choices.SelectedId != null, "The actual initial selection is observed.");
        var model = WaitForChoice("Door model");
        Check(ui.OpenChoices(model.Id).Success, "Native dropdown opens without a coordinate.");
        var opened = ui.ReadFieldMetadata(model.Id);
        Check(opened.Context == initial.Context && opened.Choices.SelectedId == initial.Choices.SelectedId, "Opening selects no option.");
        SetDoorChoice("Door model", "Wide");
        SetDoorChoice("Door width", "1200 mm");
        SetDoorChoice("Side panels", "Both");
        var glass = ui.ReadFieldMetadata(WaitForChoice("Left glass").Id);
        Check(glass.Visible == true && glass.Required == true, "Left glass appears and is required.");
        Check(glass.Choices.Options.Any(option => option.Label == "Security"), "Wide has the additional actual glass option.");
        SetDoorChoice("Left glass", "Security");
        Click("Save door");
        WaitForOutput();
        using var saved = JsonDocument.Parse(File.ReadAllText(output));
        Check(saved.RootElement.GetProperty("WidthMm").GetInt32() == 1200, "The app saves the actual configured width.");
        Check(saved.RootElement.GetProperty("LeftGlass").GetString() == "Security", "The app saves the actual glass selection.");
        CheckStaleDoorOptions(glass);
    }

    private void CheckStaleDoorOptions(UiFieldMetadata previousGlass)
    {
        SetDoorChoice("Door model", "Basic");
        var target = WaitForChoice("Left glass");
        var current = ui.ReadFieldMetadata(target.Id);
        Check(current.Visible == false && current.Required == false, "Invalid dependent panels remove the required glass branch.");
        Check(current.Choices.Options.Length == 2 && current.Choices.Revision != previousGlass.Choices.Revision,
            "The same field identifier has a fresh option revision.");
        var previous = previousGlass.Choices.Options.Single(option => option.Label == "Security");
        var stale = ui.SelectChoice(new(target.Id, previous.Id, previousGlass.Choices.Revision, previousGlass.Context));
        Check(!stale.Success, "Hidden/stale glass cannot receive a native selection.");
        Check(ui.ReadFieldMetadata(target.Id).Context == current.Context, "Refused stale input leaves business context unchanged.");
    }

    private void SetDoorChoice(string name, string label)
    {
        var target = WaitForChoice(name);
        var metadata = ui.ReadFieldMetadata(target.Id);
        var option = metadata.Choices.Options.Single(choice => choice.Label == label);
        Check(ui.SelectChoice(new(target.Id, option.Id, metadata.Choices.Revision, metadata.Context)).Success,
            "Observed native choice is dispatched: " + label);

        for (int attempt = 0; attempt < ObservationAttempts; attempt++)
        {
            var actual = ui.ReadFieldMetadata(target.Id);
            if (actual.Choices.Options.Any(choice => choice.Id == actual.Choices.SelectedId && choice.Label == label))
            {
                passed++;
                return;
            }
            Thread.Sleep(ObservationDelayMilliseconds);
        }
        throw new Exception("Native door selection was not confirmed: " + label);
    }

    private void CheckDiscovery()
    {
        WaitFor("Field 250");
        var controls = Flatten(Observe()).Where(node => node.Role == "Edit" && node.Name.StartsWith("Field ")).ToArray();
        Check(controls.Length == DiscoveryControlCount, "Every native field is observed.");
        Check(controls.Select(node => node.Name).Distinct().Count() == DiscoveryControlCount, "Field identities are unique.");
        var target = WaitFor("Field 119");
        Check(ui.SetText(target.Id, "Requested Größe").Success, "A fresh native target accepts semantic input.");
        Check(WaitFor("Field 119").Value == "Requested Größe", "The actual field value is read back fully.");
        Click("Save observed fields");
        WaitForOutput();
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var fields = document.RootElement.EnumerateObject().ToArray();
        Check(fields.Length == DiscoveryControlCount, "The application persists its complete field inventory.");
        Check(document.RootElement.GetProperty("Field 119").GetString() == "Requested Größe", "The saved target matches actual input.");
        Check(fields.Where(field => field.Name != "Field 119").All(field => field.Value.GetString() == "Initial " + field.Name),
            "Unrelated fields remain unchanged.");
    }

    private void WaitForOutput()
    {
        for (int attempt = 0; attempt < ObservationAttempts && !File.Exists(output); attempt++)
        {
            Thread.Sleep(ObservationDelayMilliseconds);
        }
    }

    private void CheckNativeChanges()
    {
        WaitFor("Delivery");
        var initial = Observe();
        Check(Flatten(initial).Count(node => node.Name == "Apply") == 2, "Duplicate labels exist.");
        Check(Flatten(initial).Any(node => node.Name == "Order actions"), "Duplicate actions have named contexts.");
        Check(Flatten(initial).Any(node => node.Name == "Collect"), "The initial option is visible.");
        Click("Delivery");
        Check(Flatten(Observe()).Any(node => node.Name == "Courier"), "The option control changes its available values.");
        var address = WaitFor("Delivery address");
        Check(ui.ProbeNativeTextSupport(address.Id) == UiNativeTextSupport.Available,
            "The actual AT-SPI editable interface is probed without input.");
        Check(ui.ProbeNativeTextSupport(WaitFor("Apply").Id) == UiNativeTextSupport.Unavailable,
            "A real GTK button has no native editable interface.");
        Check(ui.ProbeNativeTextSupport(int.MaxValue) == UiNativeTextSupport.Unknown,
            "An unknown native element cannot prove the absence of a setter.");
        Check(ui.SetText(WaitFor("Delivery address").Id, "Example Road 22").Success, "The visible field accepts semantic text.");
        Click("Local");
        Click("Delivery");
        Check(WaitFor("Delivery address").Value == "", "The reappearing address is fresh.");
        Check(ui.SetText(WaitFor("Delivery address").Id, "Example Road 33").Success, "A fresh field accepts a new value.");
        ClickOrderApply();
        WaitFor("Confirm");
        Check(!string.IsNullOrWhiteSpace(ui.ShowingModalWindowTitle(fixture.Id)), "The confirmation is a real modal window.");
        CheckBackgroundRefusal();
        Click("Confirm", true);
        CheckPersistedOutput();
    }

    private void CheckBackgroundRefusal()
    {
        var local = WaitFor("Local");
        bool refused = false;
        try
        {
            ui.PrepareElementInteraction(local.Id, WindowInteractionMode.BackgroundSemantic, ModalDialogMode.AllowInteraction, true);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Check(refused, "Background actions behind the modal are refused.");
    }

    private void CheckPersistedOutput()
    {
        for (int attempt = 0; attempt < ObservationAttempts && !File.Exists(output); attempt++)
        {
            Thread.Sleep(ObservationDelayMilliseconds);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(output));
        Check(document.RootElement.GetProperty("Address").GetString() == "Example Road 33", "The actual saved address matches.");
        Check(document.RootElement.GetProperty("Mode").GetString() == "Delivery", "The actual saved mode matches.");
        Check(document.RootElement.GetProperty("Option").GetString() == "Courier", "The actual saved choice matches.");
    }

    private void ClickOrderApply()
    {
        var context = Flatten(Observe()).Single(node => node.Name == "Order actions" && node.Children.Count > 0);
        var button = Flatten(context).Single(node => node.Name == "Apply");
        ui.PrepareElementInteraction(button.Id, WindowInteractionMode.BackgroundSemantic, ModalDialogMode.Pause, false);

        Check(ui.InvokeBackgroundElement(button.Id).Success, "The observed order action opens confirmation.");
    }

    private void Click(string name, bool modal = false)
    {
        var element = WaitFor(name);
        ui.PrepareElementInteraction(element.Id, WindowInteractionMode.BackgroundSemantic,
            modal ? ModalDialogMode.AllowInteraction : ModalDialogMode.Pause, modal);

        Check(ui.InvokeBackgroundElement(element.Id).Success, "Semantic action succeeded: " + name);
    }

    private VisualTreeNode WaitForChoice(string name)
    {
        for (int attempt = 0; attempt < ObservationAttempts; attempt++)
        {
            var nodes = Flatten(Observe()).Where(element => element.Name == name && element.Role == "combo box").ToArray();
            if (nodes.Length == 1)
            {
                return nodes[0];
            }
            Thread.Sleep(ObservationDelayMilliseconds);
        }

        throw new Exception("Unique native choice did not appear: " + name);
    }

    private VisualTreeNode Observe()
    {
        return (VisualTreeNode)ui.GetVisualTree(fixture.Id, null, 12)!;
    }

    private VisualTreeNode WaitFor(string name)
    {
        for (int attempt = 0; attempt < ObservationAttempts; attempt++)
        {
            var node = Flatten(Observe()).FirstOrDefault(element => element.Name == name);
            if (node != null)
            {
                return node;
            }
            Thread.Sleep(ObservationDelayMilliseconds);
        }

        throw new Exception("Native fixture control did not appear: " + name);
    }

    private static IEnumerable<VisualTreeNode> Flatten(VisualTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        passed++;
    }
}
