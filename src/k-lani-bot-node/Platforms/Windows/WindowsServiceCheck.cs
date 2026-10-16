#if WINDOWS
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using KLaniBot.Cartridges;
using KLaniBot.Visibility;
using KLaniBot.Localization;

namespace KLaniBot.Platforms.Windows;

// The operated node shows the master's service message in WPF. Approval still requires the master's authorization
// and the person's explicit confirmation; presentation never changes the review's decision or fingerprint.
public static class WindowsServiceCheck
{
    private const int MaximumMessageLength = 12000;

    // The master's request and the permitted decision shape stay bound to the report that the person reviewed.
    private sealed record ServiceMessage(JsonObject Request, string Text, string Type, bool ApprovalAllowed);

    // Throws InvalidOperationException outside an interactive session and InvalidDataException for an invalid request.
    public static int Run(string requestPath)
    {
        WindowsWpfRuntime.Initialize();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!Environment.UserInteractive || process.SessionId == 0)
        {
            throw new InvalidOperationException("A local service review requires an interactive user session.");
        }
        ServiceMessage message = ReadMessage(requestPath);
        var window = new Window
        {
            Title = UiTexts.Get(UiText.ServiceCheckTitle), Width = 690, Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ResizeMode = ResizeMode.NoResize
        };
        WindowsWpfTheme.Apply(window);
        var notes = new TextBox { Height = 85, AcceptsReturn = true, MaxLength = 4000, TextWrapping = TextWrapping.Wrap };
        var confirmation = new CheckBox { Content = UiTexts.Get(UiText.ServiceCheckConfirmation) };
        window.Content = ReviewLayout(window, message, notes, confirmation);
        bool accepted = window.ShowDialog() == true;
        var result = new JsonObject
        {
            ["success"] = true,
            ["approved"] = accepted && message.Type == "review" && message.ApprovalAllowed && confirmation.IsChecked == true,
            ["acknowledged"] = accepted,
            ["report_fingerprint"] = message.Request["report_fingerprint"]!.ToString(),
            ["reviewer"] = WindowsIdentity.GetCurrent().Name,
            ["notes"] = notes.Text,
            ["reviewed_at"] = DateTimeOffset.UtcNow.ToString("O")
        };
        Console.WriteLine(result.ToJsonString());

        return 0;
    }

    // Throws InvalidDataException for a message that is too long or of an unknown type.
    private static ServiceMessage ReadMessage(string requestPath)
    {
        CartridgeManifest.RejectLinks(requestPath);
        JsonObject request = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
        string text = request["text"]!.ToString();
        string type = request["type"]!.ToString();
        if (text.Length > MaximumMessageLength || type is not ("review" or "message"))
        {
            throw new InvalidDataException("Invalid service message.");
        }

        return new ServiceMessage(request, text, type, request["allow_approval"]?.GetValue<bool>() == true);
    }

    private static Border ReviewLayout(Window window, ServiceMessage message, TextBox notes, CheckBox confirmation)
    {
        var layout = new StackPanel();
        layout.Children.Add(WindowsWpfTheme.Logo());
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.ServiceCheckActionsPaused), 18));
        layout.Children.Add(new TextBox
        {
            Text = message.Text, Height = 260, AcceptsReturn = true, IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.ServiceCheckNotes)));
        layout.Children.Add(notes);
        layout.Children.Add(confirmation);
        layout.Children.Add(ReviewButtons(window, message, confirmation));

        return WindowsWpfTheme.Surface(window, layout, 24);
    }

    private static WrapPanel ReviewButtons(Window window, ServiceMessage message, CheckBox confirmation)
    {
        var buttons = new WrapPanel();
        UiText approveText = message.Type == "message" ? UiText.ServiceCheckSendAnswer : UiText.ServiceCheckApprove;
        var approve = new Button { Content = UiTexts.Get(approveText), IsEnabled = message.Type == "message" };
        var reject = new Button { Content = UiTexts.Get(UiText.ServiceCheckReject) };
        void UpdateApproval()
        {
            approve.IsEnabled = message.Type == "message" || message.ApprovalAllowed && confirmation.IsChecked == true;
        }
        confirmation.Checked += (_, _) => UpdateApproval();
        confirmation.Unchecked += (_, _) => UpdateApproval();
        approve.Click += (_, _) => window.DialogResult = true;
        reject.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(approve);
        buttons.Children.Add(reject);

        return buttons;
    }
}
#endif
