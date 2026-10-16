#if !WINDOWS
namespace KLaniBot.Platforms.Linux;

internal sealed partial class AtSpiBackend
{
    public UiNativeTextSupport ProbeNativeTextSupport(int id)
    {
        try
        {
            if (!Current(id).IsEnabled)
            {
                return UiNativeTextSupport.Unknown;
            }
            var editable = atspi_accessible_get_editable_text_iface(Get(id));
            if (editable == IntPtr.Zero)
            {
                return UiNativeTextSupport.Unavailable;
            }
            g_object_unref(editable);

            return UiNativeTextSupport.Available;
        }
        catch (Exception)
        {
            return UiNativeTextSupport.Unknown;
        }
    }
}
#endif
