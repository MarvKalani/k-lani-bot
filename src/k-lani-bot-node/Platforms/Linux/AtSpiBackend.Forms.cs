#if !WINDOWS
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KLaniBot.Platforms.Linux;

// Linux form operations query AT-SPI 2.0 state, public provider declarations and actual semantic option children.
internal sealed partial class AtSpiBackend
{
    private const int EditableState = 7;
    private const int RequiredState = 33;
    private const int SelectedState = 23;
    private const int MaximumChoices = 1000;
    private const int MaximumChoiceDepth = 3;
    private const int MaximumDeclarationLength = 4096;

    // The caller owns each native child reference until its option enumeration or selection completes.
    private sealed record NativeChoice(UiChoiceOption Option, IntPtr Pointer);

    // Throws ArgumentException for old IDs and InvalidOperationException for unavailable native state or provider errors.
    public UiFieldMetadata ReadFieldMetadata(int id)
    {
        var element = Current(id);
        var pointer = Get(id);
        var states = atspi_accessible_get_state_set(pointer);
        if (states == IntPtr.Zero)
        {
            throw new InvalidOperationException("Field state unavailable.");
        }

        try
        {
            var declaration = ReadFieldDeclaration(pointer);
            bool? readOnly = atspi_state_set_contains(states, EditableState) != 0 ? false : null;
            bool? required = atspi_state_set_contains(states, RequiredState) != 0 ? true : null;
            return new(UiFormSupport.Available,
                declaration == null ? UiFieldMetadataSource.AtSpiState : UiFieldMetadataSource.ApplicationDeclaration,
                element.IsEnabled, atspi_state_set_contains(states, Visible) != 0,
                declaration?.ReadOnly ?? readOnly, declaration?.Required ?? required,
                declaration?.Context, declaration?.Field, declaration?.Dependencies,
                IsChoice(element.Role) ? ChoiceMetadata(id) : UiChoiceMetadata.Unsupported);
        }
        finally
        {
            g_object_unref(states);
        }
    }

    private UiFieldDeclaration? ReadFieldDeclaration(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        string text = Text(atspi_accessible_get_description(pointer, ref error));
        Check(error);
        if (text.Length > MaximumDeclarationLength || !text.StartsWith('{'))
        {
            return null;
        }

        try
        {
            var declaration = JsonSerializer.Deserialize<UiFieldDeclaration>(text);
            if (declaration?.Format != "k-lani-bot-form-field/1" || string.IsNullOrWhiteSpace(declaration.Field)
                || declaration.Field.Length > 80 || declaration.Context?.Length > 80
                || declaration.Dependencies?.Length > 20)
            {
                return null;
            }
            return declaration;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsChoice(string role)
    {
        return role is "combo box" or "list box";
    }

    private UiChoiceMetadata ChoiceMetadata(int id)
    {
        var choices = ReadNativeChoices(id);

        try
        {
            string? selected = ReadSelectedChoice(Get(id), choices);
            var options = choices.Select(choice => choice.Option).ToArray();
            return new(UiFormSupport.Available, ChoiceRevision(options), options, true, choices.Count == 0, selected);
        }
        finally
        {
            ReleaseChoices(choices);
        }
    }

    private static string? ReadSelectedChoice(IntPtr pointer, List<NativeChoice> choices)
    {
        var selection = atspi_accessible_get_selection_iface(pointer);
        if (selection == IntPtr.Zero)
        {
            return choices.FirstOrDefault(choice => HasNativeState(choice.Pointer, SelectedState))?.Option.Id;
        }

        try
        {
            IntPtr error = IntPtr.Zero;
            int count = atspi_selection_get_n_selected_children(selection, ref error);
            Check(error);
            if (count != 1)
            {
                return null;
            }
            var child = atspi_selection_get_selected_child(selection, 0, ref error);
            Check(error);
            try
            {
                return choices.SingleOrDefault(choice => choice.Pointer == child)?.Option.Id;
            }
            finally
            {
                if (child != IntPtr.Zero)
                    g_object_unref(child);
            }
        }
        finally
        {
            g_object_unref(selection);
        }
    }

    // Throws Exception when native enumeration fails, after releasing any collected child references.
    private List<NativeChoice> ReadNativeChoices(int id)
    {
        var choices = new List<NativeChoice>();

        try
        {
            CollectChoices(Get(id), Get(id), 0, choices);
            return choices;
        }
        catch
        {
            ReleaseChoices(choices);
            throw;
        }
    }

    // Throws NotSupportedException when a complete bounded option hierarchy cannot be inspected.
    private static void CollectChoices(IntPtr parent, IntPtr field, int depth, List<NativeChoice> choices)
    {
        if (depth >= MaximumChoiceDepth)
        {
            throw new NotSupportedException("Native option hierarchy exceeds its declared complete scope.");
        }

        foreach (var child in Children(parent))
        {
            try
            {
                IntPtr error = IntPtr.Zero;
                string role = Text(atspi_accessible_get_role_name(child, ref error));
                Check(error);
                if (role is "menu item" or "list item")
                {
                    AddChoice(field, child, choices);
                }
                else if (role is "menu" or "list" or "list box")
                {
                    CollectChoices(child, field, depth + 1, choices);
                }
            }
            finally
            {
                g_object_unref(child);
            }
        }
    }

    // Throws NotSupportedException for oversized option sets or labels.
    private static void AddChoice(IntPtr field, IntPtr child, List<NativeChoice> choices)
    {
        string label = Name(child);
        if (choices.Count >= MaximumChoices || label.Length > MaximumDeclarationLength)
        {
            throw new NotSupportedException("Native choice inventory exceeds its bounded scope.");
        }

        string identity = field.ToInt64().ToString(CultureInfo.InvariantCulture) + "/"
            + choices.Count.ToString(CultureInfo.InvariantCulture) + "/" + label;
        string identifier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        bool enabled = HasNativeState(child, Sensitive);
        choices.Add(new(new(identifier, label, enabled), g_object_ref(child)));
    }

    private static string ChoiceRevision(UiChoiceOption[] options)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options))).ToLowerInvariant();
    }

    private static bool HasNativeState(IntPtr pointer, int state)
    {
        var states = atspi_accessible_get_state_set(pointer);
        if (states == IntPtr.Zero)
        {
            return false;
        }

        bool result = atspi_state_set_contains(states, state) != 0;
        g_object_unref(states);

        return result;
    }

    private static void ReleaseChoices(List<NativeChoice> choices)
    {
        foreach (var choice in choices)
        {
            g_object_unref(choice.Pointer);
        }
    }

    // Throws ArgumentException for old IDs, InvalidOperationException for stale state and NotSupportedException for absent actions.
    public void SelectChoice(UiChoiceCommand command)
    {
        var metadata = ReadFieldMetadata(command.ElementId);
        var element = Current(command.ElementId);
        if (!IsChoice(element.Role) || !element.IsEnabled || metadata.Visible != true || metadata.ReadOnly == true
            || metadata.Context != command.Context || metadata.Choices.Revision != command.OptionsRevision)
        {
            throw new InvalidOperationException("stale_or_unwritable_choice");
        }

        var choices = ReadNativeChoices(command.ElementId);
        try
        {
            if (ChoiceRevision(choices.Select(choice => choice.Option).ToArray()) != command.OptionsRevision)
            {
                throw new InvalidOperationException("stale_options");
            }
            var choice = choices.SingleOrDefault(choice => choice.Option.Id == command.OptionId && choice.Option.Enabled);
            if (choice == null || HasNativeState(choice.Pointer, Defunct))
            {
                throw new InvalidOperationException("Unknown or defunct observed option.");
            }
            InvokeChoice(choice.Pointer);
        }
        finally
        {
            ReleaseChoices(choices);
        }
    }

    // Throws NotSupportedException without a unique native action and InvalidOperationException when the provider refuses.
    private void InvokeChoice(IntPtr pointer)
    {
        var action = atspi_accessible_get_action_iface(pointer);
        if (action == IntPtr.Zero)
        {
            throw new NotSupportedException("Option has no native semantic selection action.");
        }

        try
        {
            IntPtr error = IntPtr.Zero;
            int count = atspi_action_get_n_actions(action, ref error);
            Check(error);
            int selected = SelectPrimaryAction(action, count);
            if (DoAction(pointer, action, selected) == NativeActionResult.Refused)
            {
                throw new InvalidOperationException("Native provider refused the selected option.");
            }
        }
        finally
        {
            g_object_unref(action);
        }
    }

    // Throws ArgumentException for old IDs, InvalidOperationException for refusal and NotSupportedException for missing actions.
    public void OpenChoices(int id)
    {
        if (!IsChoice(Current(id).Role))
        {
            throw new NotSupportedException("Target is not a native choice field.");
        }

        Invoke(id);
    }

    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_description(IntPtr accessible, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_selection_iface(IntPtr accessible);
    [DllImport(Lib)] private static extern int atspi_selection_get_n_selected_children(IntPtr selection, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_selection_get_selected_child(IntPtr selection, int selectedIndex, ref IntPtr error);
}
#endif
