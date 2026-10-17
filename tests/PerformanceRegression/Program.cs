using KLaniBot.CartridgeLab;

if (args.Length == 2 && args[0] == "--native")
{
    return NativeFixtureCases.Run(args[1]);
}
if (args.Length == 2 && args[0] == "--native-discovery")
{
    return NativeFixtureCases.RunDiscovery(args[1]);
}
if (args.Length == 2 && args[0] == "--native-drift")
{
    return await BuildDriftNativeCases.RunAsync(args[1]);
}
if (args.Length == 2 && args[0] == "--native-door")
{
    return NativeFixtureCases.RunDoor(args[1]);
}

var model = new DynamicFormModel();
int passed = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
    passed++;
}

Check(!model.AddressVisible && model.Options.SequenceEqual([DeliveryOption.Collect]), "Local hides the address.");
Check(!model.TrySetAddress("hidden"), "A hidden address rejects writes.");
Check(!model.TrySelectOption(DeliveryOption.Courier, model.Revision), "Absent options reject selection.");
Check(!model.TryConfirm(out _), "Confirmation requires an open modal.");
int oldRevision = model.Revision;
Check(model.TryChangeMode(DeliveryMode.Delivery), "Delivery changes the context.");
Check(model.AddressVisible, "The address reappears.");
Check(model.Options.SequenceEqual([DeliveryOption.Courier, DeliveryOption.Express]), "The same control has new options.");
Check(!model.TrySelectOption(DeliveryOption.Courier, oldRevision), "Stale observations reject writes.");
Check(!model.TrySelectOption(DeliveryOption.Collect, model.Revision), "Removed options reject writes.");
Check(!model.TryOpenConfirmation(), "An empty required address rejects submission.");
Check(model.TrySetAddress(" ") && !model.TryOpenConfirmation(), "A cleared address rejects submission.");
Check(model.TrySetAddress("Example Road 11"), "A visible address accepts data.");
Check(model.TrySelectOption(DeliveryOption.Express, model.Revision), "A fresh available option accepts selection.");
Check(model.TryOpenConfirmation(), "A valid form opens a modal.");
Check(!model.TryChangeMode(DeliveryMode.Local), "The modal blocks background mode changes.");
Check(!model.TrySelectOption(DeliveryOption.Courier, model.Revision), "The modal blocks background choice changes.");
Check(!model.TrySetAddress("other"), "The modal blocks background text changes.");
Check(!model.TryOpenConfirmation(), "A modal cannot be opened twice.");
model.CancelConfirmation();
Check(!model.TryConfirm(out _), "A cancelled modal cannot be confirmed.");
Check(model.TryOpenConfirmation() && model.TryConfirm(out var result) && result?.Address == "Example Road 11",
    "Confirmation returns actual entered values.");
Check(model.TryChangeMode(DeliveryMode.Local) && model.Address == "", "A hidden field loses the previous context value.");
Check(model.TryChangeMode(DeliveryMode.Delivery) && model.Address == "", "A reappearing field requires fresh data.");
Check(!model.TryChangeMode((DeliveryMode)999), "Undefined modes reject input.");
Check(!model.TrySelectOption((DeliveryOption)999, model.Revision), "Undefined choices reject input.");
Console.WriteLine($"{passed} performance fixture regression checks passed.");
Console.WriteLine($"{DoorFormCases.Run()} door model regression checks passed.");
return 0;
