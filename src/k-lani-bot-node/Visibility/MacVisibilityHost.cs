using System.Diagnostics;

#if !WINDOWS
using System.Runtime.InteropServices;
using KLaniBot.Startup;
using KLaniBot.Guard;
using KLaniBot.Localization;
namespace KLaniBot.Visibility;

// The macOS indicator with AppKit: a status bar item with the tray menu and a red border on every screen.
// The border windows ignore the mouse and float above normal windows, so they neither take clicks meant for the
// target nor hide behind it.
public sealed partial class MacVisibilityHost : NativeVisibilityHost
{
    private const string Obj = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private static readonly Dictionary<IntPtr, MacVisibilityHost> Targets = new();
    // The Objective-C action that AppKit calls for menu items and buttons.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActionCallback(IntPtr self, IntPtr selector, IntPtr sender);
    private static readonly ActionCallback Callback = Choose;
    private readonly IntPtr _app, _target, _status, _button, _bar, _rootPool;
    private readonly List<IntPtr> _borders = new(); private IntPtr _question; private string? _questionId; private string _menuState = "";
    private IntPtr _assignmentWindow;
    private IntPtr _assignmentLabel;
    private string _monitorGeometry = "";

    // What a menu item or button does; AppKit hands the value back to Choose as the sender's tag.
    private enum MenuCommand
    {
        // A greyed-out line that only informs.
        Information = 0,
        AllowSession = 1,
        DenySession = 2,
        AskLater = 3,
        ShowPending = 4,
        AllowAutomatically = 5,
        ShowBorder = 6,
        LockAccess = 7,
        Quit = 8,
        OperateInBackground = 9,
        AllowModalDialogs = 10,
        RecordScreenshots = 11,
        OpenTrace = 12,
        PauseAgent = 13,
        SlowMotion = 14,
        NormalSpeed = 15
    }

    // An answer button of the consent window and the command it sends.
    private sealed record ConsentButton(UiText Title, MenuCommand Command);

    private static readonly ConsentButton[] ConsentButtons =
    [
        new(UiText.AllowThisSession, MenuCommand.AllowSession),
        new(UiText.NotNow, MenuCommand.DenySession),
        new(UiText.AskAgainInFiveMinutes, MenuCommand.AskLater)
    ];
    // Throws PlatformNotSupportedException outside macOS, InvalidOperationException without a menu-bar item and InvalidDataException when
    // the logo cannot be decoded.
    public MacVisibilityHost()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        NativeLibrary.Load(AppKit);
        _rootPool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        _app = Send(Class("NSApplication"), "sharedApplication");
        Long(_app, "setActivationPolicy:", 1);
        Send(_app, "finishLaunching");
        var cls = objc_allocateClassPair(Class("NSObject"), "KLBotVisibilityTarget", 0);
        if (cls != IntPtr.Zero)
        {
            class_addMethod(cls, Sel("choose:"), Marshal.GetFunctionPointerForDelegate(Callback), "v@:@");
            objc_registerClassPair(cls);
        }
        else
            cls = Class("KLBotVisibilityTarget");
        _target = Send(Send(cls, "alloc"), "init");
        Targets[_target] = this;
        _bar = Send(Class("NSStatusBar"), "systemStatusBar");
        _status = SendDouble(_bar, Sel("statusItemWithLength:"), -1);
        _button = Send(_status, "button");
        if (_status == IntPtr.Zero || _button == IntPtr.Zero)
            throw new InvalidOperationException("Visible macOS menu-bar item unavailable.");
        Long(_status, "setVisible:", 1);
        Pointer(_button, "setTitle:", Str(ProductIdentity.MacMenuTitle));
        byte[] logoBytes = ProductBranding.Read(ProductImage.MacMenuBar);
        IntPtr logoData = SendBytes(Class("NSData"), Sel("dataWithBytes:length:"), logoBytes, (nuint)logoBytes.Length);
        IntPtr logoImage = Send1(Send(Class("NSImage"), "alloc"), Sel("initWithData:"), logoData);
        if (logoImage == IntPtr.Zero)
            throw new InvalidDataException("The menu-bar logo could not be decoded.");
        SendSize(logoImage, Sel("setSize:"), new NativeImageSize { Width = 72, Height = 24 });
        Pointer(_button, "setImage:", logoImage);
        Send(logoImage, "release");
    }
    private static void Choose(IntPtr self, IntPtr selector, IntPtr sender)
    {
        if (!Targets.TryGetValue(self, out var host))
            return;
        try
        {
            ActionFor(host, (MenuCommand)ReadLong(sender, Sel("tag")))?.Invoke();
        }
        catch (Exception ex) { Console.Error.WriteLine("Local visibility decision failed: " + ex.GetType().Name); }
    }

    // Null for an information line and for a tag this host never set.
    private static Action? ActionFor(MacVisibilityHost host, MenuCommand command)
    {
        return command switch
        {
            MenuCommand.Information => null,
            MenuCommand.AllowSession => () => host.Decide(SessionConsent.Allowed),
            MenuCommand.DenySession => () => host.Decide(SessionConsent.Denied),
            MenuCommand.AskLater => () => host.Decide(SessionConsent.Deferred),
            MenuCommand.ShowPending => host.ShowPending,
            MenuCommand.AllowAutomatically => host.SetUnattended,
            MenuCommand.ShowBorder => host.SetBorder,
            MenuCommand.LockAccess => host.ToggleAccessLock,
            MenuCommand.Quit => host.Quit,
            MenuCommand.OperateInBackground => host.SetBackgroundInteraction,
            MenuCommand.AllowModalDialogs => host.SetModalInteraction,
            MenuCommand.RecordScreenshots => host.SetScreenshotRecording,
            MenuCommand.OpenTrace => host.OpenTraceViewer,
            MenuCommand.PauseAgent => host.PauseAgent,
            MenuCommand.SlowMotion => host.SlowMotion,
            MenuCommand.NormalSpeed => host.NormalSpeed,
            _ => null
        };
    }
    protected override void Perform(Action action)
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            action();
        }
        finally { Send(pool, "drain"); }
    }
    private static string StatusColorName(VisibilityStatus status)
    {
        return status switch
        {
            VisibilityStatus.Active => "systemRedColor",
            VisibilityStatus.Connected => "systemBlueColor",
            VisibilityStatus.Idle => "systemGrayColor",
            _ => throw new UnreachableException()
        };
    }
    // Throws InvalidOperationException when the indicator, a monitor, the border or the banner is unavailable.
    protected override void Apply(VisibilityState state)
    {
        if (ReadLong(_status, Sel("isVisible")) == 0)
            throw new InvalidOperationException("Permanent macOS indicator hidden.");
        UpdateStatusItem(state);
        string stamp = state.Status.ToString() + state.Unattended + state.ScreenBorder + state.AccessLocked + state.Prompt?.Challenge
            + state.Presentation + state.Pace;
        if (stamp != _menuState)
        {
            _menuState = stamp;
            RebuildMenu(state);
        }
        ApplyBorder(state);
        RaiseIndicators(state);
        Send(_app, "updateWindows");
    }

    private void UpdateStatusItem(VisibilityState state)
    {
        string color = StatusColorName(state.Status);
        var attributes = Send(Class("NSMutableDictionary"), "dictionary");
        Send2(attributes, Sel("setObject:forKey:"), Send(Class("NSColor"), color), Str("NSColor"));
        var title = Send2(
            Send(Class("NSAttributedString"), "alloc"),
            Sel("initWithString:attributes:"),
            Str(ProductIdentity.MacMenuTitle),
            attributes);
        Pointer(_button, "setAttributedTitle:", title);
        Send(title, "release");
        Pointer(
            _button,
            "setToolTip:",
            Str(ProductIdentity.ProductName + " " + ProductIdentity.DisplayVersion + " · " + StatusText(state)));
    }

    private void RebuildMenu(VisibilityState state)
    {
        var menu = Send(Send(Class("NSMenu"), "alloc"), "init");
        Add(menu, UiTexts.Format(UiText.MenuVersionAndNode, ProductIdentity.DisplayVersion, ShownNodeId), MenuCommand.Information);
        Add(menu, StatusText(state), MenuCommand.Information);
        Add(menu, UiTexts.Format(UiText.MenuMaster, ShownMasterOf(state)), MenuCommand.Information);
        if (state.Presentation != null)
        {
            Add(menu, UiTexts.Format(UiText.MenuClient, state.Presentation.ShownClient), MenuCommand.Information);
            Add(menu, UiTexts.Format(UiText.MenuPurpose, state.Presentation.ShownPurpose), MenuCommand.Information);
        }
        if (state.Prompt != null)
        {
            foreach (ConsentButton consentButton in ConsentButtons)
                Add(menu, UiTexts.Get(consentButton.Title), consentButton.Command);
        }
        Add(menu, UiTexts.Get(UiText.MenuPause), MenuCommand.PauseAgent, state.Pace == Pace.Paused);
        Add(menu, UiTexts.Get(UiText.MenuSlowMotion), MenuCommand.SlowMotion, state.Pace == Pace.SlowMotion);
        Add(menu, UiTexts.Get(UiText.MenuNormalSpeed), MenuCommand.NormalSpeed, state.Pace == Pace.Normal);
        Add(menu, UiTexts.Get(UiText.ShowPendingRequest), MenuCommand.ShowPending);
        Add(menu, UiTexts.Get(UiText.AllowAutomatically), MenuCommand.AllowAutomatically, state.Unattended);
        Add(
            menu,
            UiTexts.Get(UiText.OperateInBackground),
            MenuCommand.OperateInBackground,
            state.WindowInteraction == WindowInteractionMode.BackgroundSemantic);
        bool modalDialogsAllowed = state.ModalDialogs == ModalDialogMode.AllowInteraction;
        Add(menu, UiTexts.Get(UiText.AllowModalDialogs), MenuCommand.AllowModalDialogs, modalDialogsAllowed);
        Add(menu, UiTexts.Get(UiText.RecordScreenshots), MenuCommand.RecordScreenshots, state.RecordActionScreenshots);
        Add(menu, UiTexts.Get(UiText.OpenTrace), MenuCommand.OpenTrace);
        Add(menu, UiTexts.Get(UiText.ShowScreenBorder), MenuCommand.ShowBorder, state.ScreenBorder);
        Add(menu, AccessLockLabel, MenuCommand.LockAccess, state.AccessLocked);
        Add(menu, UiTexts.Get(UiText.QuitProgram), MenuCommand.Quit);
        Pointer(_status, "setMenu:", menu);
        Send(menu, "release");
    }

    // Every screen gets the border, so the person sees that an AI is working whichever screen they look at.
    // The first screen of NSScreen.screens is the one with the menu bar; the assignment banner goes there.
    // Throws InvalidOperationException when there is no screen or one has no size.
    private void ApplyBorder(VisibilityState state)
    {
        List<ScreenArea> areas = ReadScreens();
        // The banner grows with the step list, so a different number of lines recreates it.
        string geometry = string.Join(";", areas.Select(screen =>
            $"{screen.Area.X}:{screen.Area.Y}:{screen.Area.W}:{screen.Area.H}:{screen.Visible.Y}:{screen.Visible.H}:{screen.Scale}"))
            + "|" + AssignmentLines(state).Count;
        if (_borders.Count > 0 && (!state.Border || geometry != _monitorGeometry))
        {
            CloseActivityWindows();
        }
        _monitorGeometry = geometry;
        if (!state.Border || _borders.Count != 0)
            return;

        if (areas.Count == 0 || areas.Any(screen => screen.Area.W <= 0 || screen.Area.H <= 0))
            throw new InvalidOperationException("Monitor unavailable.");
        foreach (var screen in areas)
        {
            AddBorder(screen.Area, 4 / Math.Max(1, screen.Scale));
        }
        ScreenArea menuBarScreen = areas[0];
        double width = Math.Min(1000, menuBarScreen.Area.W - 32);
        double left = menuBarScreen.Area.X + (menuBarScreen.Area.W - width) / 2;
        double height = AssignmentLines(state).Count * AssignmentLineHeight + AssignmentPadding;
        double top = menuBarScreen.Visible.Y + menuBarScreen.Visible.H - height - AssignmentGap;
        AssignmentWindow assignment = CreateAssignmentWindow(new Rect(left, top, width, height), DescribeAssignment(state));
        _assignmentWindow = assignment.Window;
        _assignmentLabel = assignment.Label;
        Send(_assignmentWindow, "orderFrontRegardless");
    }

    private static List<ScreenArea> ReadScreens()
    {
        IntPtr screens = Send(Class("NSScreen"), "screens");
        nint screenCount = ReadLong(screens, Sel("count"));
        var areas = new List<ScreenArea>();
        for (nint index = 0; index < screenCount; index++)
        {
            IntPtr screen = SendLong(screens, Sel("objectAtIndex:"), index);
            areas.Add(new ScreenArea(Frame(screen, "frame"), Frame(screen, "visibleFrame"), ReadDouble(screen, Sel("backingScaleFactor"))));
        }
        return areas;
    }

    // A border or banner that did not appear stops the activity instead of letting it run unseen.
    // Throws InvalidOperationException when border or banner is not visible.
    private void RaiseIndicators(VisibilityState state)
    {
        foreach (var window in _borders)
        {
            if (ReadLong(window, Sel("isVisible")) == 0)
                throw new InvalidOperationException("Activity border unavailable.");
            Send(window, "orderFrontRegardless");
            Send(window, "displayIfNeeded");
        }
        if (_assignmentWindow != IntPtr.Zero)
        {
            Pointer(_assignmentLabel, "setStringValue:", Str(DescribeAssignment(state)));
            Send(_assignmentWindow, "orderFrontRegardless");
            Send(_assignmentWindow, "displayIfNeeded");
            if (ReadLong(_assignmentWindow, Sel("isVisible")) == 0)
            {
                throw new InvalidOperationException("Visible assignment indicator unavailable.");
            }
        }
    }

    private void AddBorder(Rect area, double p)
    {
        foreach (var rect in new[]
        {
            new Rect(area.X, area.Y, area.W, p),
            new Rect(area.X, area.Y + area.H - p, area.W, p),
            new Rect(area.X, area.Y, p, area.H),
            new Rect(area.X + area.W - p, area.Y, p, area.H)
        })
        {
            var window = Window(rect, 0);
            Pointer(window, "setBackgroundColor:", Send(Class("NSColor"), "systemRedColor"));
            Long(window, "setOpaque:", 1);
            Long(window, "setIgnoresMouseEvents:", 1);
            Long(window, "setHasShadow:", 0);
            Long(window, "setLevel:", 1000);
            Long(window, "setCollectionBehavior:", 1 | 8 | 16 | 256);
            Send(window, "orderFrontRegardless");
            _borders.Add(window);
        }
    }

    // Area is the whole screen, Visible the part without menu bar and dock, Scale the backing pixels per point.
    private sealed record ScreenArea(Rect Area, Rect Visible, double Scale);

    // The banner window and its label, kept to update the text.
    private sealed record AssignmentWindow(IntPtr Window, IntPtr Label);

    private const double AssignmentLineHeight = 20;
    private const double AssignmentPadding = 12;
    private const double AssignmentGap = 4;

    private static AssignmentWindow CreateAssignmentWindow(Rect bounds, string text)
    {
        var window = Window(bounds, 0);
        Long(window, "setIgnoresMouseEvents:", 1);
        Long(window, "setHasShadow:", 0);
        Long(window, "setLevel:", 1000);
        Long(window, "setCollectionBehavior:", 1 | 8 | 16 | 256);
        var box = SendRect(Send(Class("NSBox"), "alloc"), Sel("initWithFrame:"), new Rect(0, 0, bounds.W, bounds.H));
        Long(box, "setBoxType:", 4);
        Long(box, "setBorderType:", 1);
        Long(box, "setTitlePosition:", 0);
        SendSize(box, Sel("setContentViewMargins:"), new NativeImageSize { Width = 0, Height = 0 });
        var color = SendColor(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), .55, .04, .04, 1);
        Pointer(box, "setFillColor:", color);
        Pointer(box, "setBorderColor:", color);
        SendDouble(box, Sel("setBorderWidth:"), 0);
        Pointer(window, "setContentView:", box);
        Send(box, "release");
        var label = SendRect(Send(Class("NSTextField"), "alloc"), Sel("initWithFrame:"), new Rect(10, 4, bounds.W - 20, bounds.H - 8));
        Long(label, "setEditable:", 0);
        Long(label, "setSelectable:", 0);
        Long(label, "setBezeled:", 0);
        Long(label, "setDrawsBackground:", 0);
        Pointer(label, "setTextColor:", Send(Class("NSColor"), "whiteColor"));
        Long(Send(label, "cell"), "setWraps:", 1);
        Long(Send(label, "cell"), "setTruncatesLastVisibleLine:", 1);
        Pointer(label, "setStringValue:", Str(text));
        Pointer(Send(box, "contentView"), "addSubview:", label);
        Send(label, "release");
        return new AssignmentWindow(window, label);
    }

    private void CloseActivityWindows()
    {
        if (_assignmentWindow != IntPtr.Zero)
        {
            Send(_assignmentWindow, "close");
            Send(_assignmentWindow, "release");
            _assignmentWindow = IntPtr.Zero;
            _assignmentLabel = IntPtr.Zero;
        }
        foreach (IntPtr window in _borders)
        {
            Send(window, "close");
            Send(window, "release");
        }
        _borders.Clear();
    }
    private void Add(IntPtr menu, string title, MenuCommand command, bool check = false)
    {
        bool information = command == MenuCommand.Information;
        var item = Send3(
            Send(Class("NSMenuItem"), "alloc"),
            Sel("initWithTitle:action:keyEquivalent:"),
            Str(title),
            information ? IntPtr.Zero : Sel("choose:"),
            Str(""));
        Long(item, "setTag:", (int)command);
        Pointer(item, "setTarget:", _target);
        Long(item, "setState:", check ? 1 : 0);
        if (information)
            Long(item, "setEnabled:", 0);
        Pointer(menu, "addItem:", item);
        Send(item, "release");
    }
    protected override void ShowQuestion(ConsentPrompt? prompt)
    {
        if (prompt == null)
        {
            if (_question != IntPtr.Zero)
            {
                Send(_question, "close");
                Send(_question, "release");
            }
            _question = IntPtr.Zero;
            _questionId = null;
            return;
        }
        if (_questionId == prompt.Challenge && _question != IntPtr.Zero)
        {
            Pointer(_question, "makeKeyAndOrderFront:", IntPtr.Zero);
            return;
        }
        if (_question != IntPtr.Zero)
        {
            Send(_question, "close");
            Send(_question, "release");
        }
        _questionId = prompt.Challenge;
        _question = CreateQuestionWindow(prompt);
        Pointer(_question, "makeKeyAndOrderFront:", IntPtr.Zero);
    }

    private const double QuestionSize = 600;

    // A centered window above other apps with the question text and one button per answer.
    private IntPtr CreateQuestionWindow(ConsentPrompt prompt)
    {
        var area = Frame(Send(Class("NSScreen"), "mainScreen"), "frame");
        IntPtr question = Window(
            new(area.X + (area.W - QuestionSize) / 2, area.Y + (area.H - QuestionSize) / 2, QuestionSize, QuestionSize), 1 | 2);
        Pointer(question, "setTitle:", Str(UiTexts.Get(UiText.ConsentWindowTitle)));
        Long(question, "setLevel:", 25);
        Pointer(question, "setAppearance:", Send1(Class("NSAppearance"), Sel("appearanceNamed:"), Str("NSAppearanceNameAqua")));
        var content = Send(question, "contentView");
        var label = SendRect(Send(Class("NSTextField"), "alloc"), Sel("initWithFrame:"), new(20, 165, 560, 420));
        Long(label, "setEditable:", 0);
        Long(label, "setBezeled:", 0);
        Long(label, "setDrawsBackground:", 0);
        Pointer(label, "setStringValue:", Str(DescribeConsent(prompt)));
        Pointer(content, "addSubview:", label);
        Send(label, "release");
        AddConsentButtons(content);
        return question;
    }

    private void AddConsentButtons(IntPtr content)
    {
        int y = 115;
        foreach (ConsentButton consentButton in ConsentButtons)
        {
            var button = SendRect(Send(Class("NSButton"), "alloc"), Sel("initWithFrame:"), new(20, y, 450, 32));
            Pointer(button, "setTitle:", Str(UiTexts.Get(consentButton.Title)));
            Long(button, "setBezelStyle:", 1);
            Long(button, "setTag:", (int)consentButton.Command);
            Pointer(button, "setTarget:", _target);
            Pointer(button, "setAction:", Sel("choose:"));
            Pointer(content, "addSubview:", button);
            Send(button, "release");
            y -= 40;
        }
    }
    private static IntPtr Window(Rect rect, nuint style)
    {
        var window = InitWindow(Send(Class("NSWindow"), "alloc"), Sel("initWithContentRect:styleMask:backing:defer:"), rect, style, 2, 0);
        Long(window, "setReleasedWhenClosed:", 0);
        return window;
    }
    protected override IEscapeKeyWatch? StartEscapeKeyWatch()
    {
        if (!MacEscapeKeyWatch.TryStart(out MacEscapeKeyWatch? watch))
        {
            Console.Error.WriteLine("ESC cannot end sessions: the key watch needs the accessibility permission.");
            return null;
        }
        return watch;
    }
    protected override void Pump()
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var ev = NextEvent(
                    _app,
                    Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                    nuint.MaxValue,
                    Send(Class("NSDate"), "distantPast"),
                    Str("kCFRunLoopDefaultMode"),
                    1);
                if (ev == IntPtr.Zero)
                    break;
                Pointer(_app, "sendEvent:", ev);
            }
            Send(_app, "updateWindows");
        }
        finally { Send(pool, "drain"); }
    }
    protected override void CloseNative()
    {
        ShowQuestion(null);
        CloseActivityWindows();
        Pointer(_bar, "removeStatusItem:", _status);
        Targets.Remove(_target);
        Send(_target, "release");
        Send(_rootPool, "drain");
    }
    // A rectangle as AppKit passes it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public double X, Y, W, H; public Rect(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }
    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr Str(string value) => SendString(Class("NSString"), Sel("stringWithUTF8String:"), value);
    private static IntPtr Send(IntPtr receiver, string selector) => Send0(receiver, Sel(selector));
    private static void Pointer(IntPtr receiver, string selector, IntPtr value) => Send1(receiver, Sel(selector), value);
    private static void Long(IntPtr receiver, string selector, nint value) => SendLong(receiver, Sel(selector), value);
    private static Rect Frame(IntPtr receiver, string selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return ReadRect(receiver, Sel(selector));
        ReadRectStret(out var value, receiver, Sel(selector));
        return value;
    }
    [DllImport(Obj)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Obj)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Obj)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extra);
    [DllImport(Obj)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Obj)] private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr method, string types);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr Send0(IntPtr receiver, IntPtr selector);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr Send2(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send3(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr d);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr SendLong(IntPtr receiver, IntPtr selector, nint value);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern nint ReadLong(IntPtr receiver, IntPtr selector);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern double ReadDouble(IntPtr receiver, IntPtr selector);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDouble(IntPtr receiver, IntPtr selector, double value);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendColor(IntPtr receiver, IntPtr selector, double red, double green, double blue, double alpha);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRect(IntPtr receiver, IntPtr selector, Rect rect);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] private static extern Rect ReadRect(IntPtr receiver, IntPtr selector);
    [DllImport(Obj, EntryPoint = "objc_msgSend_stret")]
    private static extern void ReadRectStret(out Rect rect, IntPtr receiver, IntPtr selector);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr InitWindow(IntPtr receiver, IntPtr selector, Rect rect, nuint style, nuint backing, byte defer);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr NextEvent(IntPtr receiver, IntPtr selector, nuint mask, IntPtr until, IntPtr mode, byte dequeue);
    // An image size as AppKit passes it.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImageSize
    {
        public double Width; public double Height;
    }
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendBytes(IntPtr receiver, IntPtr selector, byte[] bytes, nuint length);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    private static extern void SendSize(IntPtr receiver, IntPtr selector, NativeImageSize size);

}
#endif
