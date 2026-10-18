namespace KLaniBot.Platforms.Mirroring;

// Use case: the mirrored-phone backend reaches the host only through this interface: it finds the one mirroring window, reads
// the text visible in it, captures it on request, taps a point and types into it. The macOS adapter answers with the window
// server and Apple Vision; regression tests answer with a fake, so the backend is checked without a phone.
public interface IMirroredScreen
{
    // Null when no visible mirroring window exists.
    // Throws InvalidOperationException when the window server or the helper does not answer.
    MirroredWindow? FindWindow();

    // Throws InvalidOperationException when the window cannot be captured or its text cannot be recognized.
    IReadOnlyList<RecognizedLine> Read(MirroredWindow window);

    // Throws InvalidOperationException when the window cannot be captured.
    byte[] Capture(MirroredWindow window);

    UiOutcome Tap(MirroredWindow window, ScreenPoint point);

    UiOutcome Type(MirroredWindow window, string text);

    UiOutcome Keys(MirroredWindow window, string[] keys);
}

// The mirroring window in the host's input coordinates (points on macOS), the ones a pointer event uses.
public sealed record MirroredWindow(long WindowId, int ProcessId, double X, double Y, double Width, double Height)
{
    // Turns a position relative to the window image (0 to 1, from the top left) into a host input point.
    public ScreenPoint PointAt(double relativeX, double relativeY)
    {
        return new ScreenPoint(X + relativeX * Width, Y + relativeY * Height);
    }
}

// One line of recognized text with its box relative to the window image: 0 to 1 from the top left, so neither the window's
// position nor the display scale changes it.
public sealed record RecognizedLine(string Text, double Confidence, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;
}

// A host input position.
public sealed record ScreenPoint(double X, double Y);
