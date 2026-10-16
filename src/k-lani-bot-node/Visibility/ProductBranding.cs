using System.Diagnostics;
using System.Reflection;

namespace KLaniBot.Visibility;

// Each place that shows the product gets an image drawn for its exact pixel size, because a scaled
// logo turns blurry at 24 pixels and the owner prepares every size by hand.
public enum ProductImage
{
    Icon24,
    Icon32,
    LinuxPanel,
    MacMenuBar,
    WindowsDialog
}

// The indicators and dialogs read the logo and icons embedded in the executable here.
public static class ProductBranding
{
    // Throws InvalidDataException when the image is missing from this build.
    public static byte[] Read(ProductImage image)
    {
        string resource = ResourceName(image);
        using Stream content = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("The product image " + resource + " is missing.");
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Must match the LogicalName of the EmbeddedResource items in the project files.
    public static string ResourceName(ProductImage image)
    {
        return image switch
        {
            ProductImage.Icon24 => "KLaniBot.icon-24.png",
            ProductImage.Icon32 => "KLaniBot.icon-32.png",
            ProductImage.LinuxPanel => "KLaniBot.logo-linux-panel.png",
            ProductImage.MacMenuBar => "KLaniBot.logo-macos-menubar.png",
            ProductImage.WindowsDialog => "KLaniBot.logo-windows-dialog.png",
            _ => throw new UnreachableException()
        };
    }
}
