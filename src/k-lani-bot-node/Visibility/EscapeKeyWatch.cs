namespace KLaniBot.Visibility;

// Use case: while the agent works and the person sees the red border, the person ends the agent's sessions with ESC. Each
// desktop's indicator reports a physical ESC through this; keys the node itself sends are never reported, so the agent's
// own hotkey cannot end its session. A watch only listens: the focused program still receives the key.
public interface IEscapeKeyWatch : IDisposable
{
    // True once for every physical ESC since the last call.
    bool TakePhysicalEscape();
}
