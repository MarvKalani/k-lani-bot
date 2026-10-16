using System.Reflection.PortableExecutable;
using System.Text;
using KLaniBot.Cartridges;
using KLaniBot.Startup;
using KLaniBot.Updates;

namespace KLaniBot.Platforms.Windows;

// Publishing and installing an update read the product version embedded in the executable, so a version number cannot be put on other
// bytes.
public static class WindowsExecutableMetadata
{
    // Throws InvalidDataException when the file is no Windows-x64 node executable with valid version metadata.
    public static string ReadProductVersion(string path)
    {
        CartridgeManifest.RejectLinks(path);
        try
        {
            using var stream = File.OpenRead(path);
            using var executable = new PEReader(stream);
            if (executable.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            {
                throw new InvalidDataException("Windows-x64 executable required.");
            }
            string text = ReadVersionResource(executable);
            bool productIdentity = VersionField(text, "ProductName") == ProductIdentity.ProductName
                && VersionField(text, "FileDescription") == ProductIdentity.NodeName;
            // Only the exact product identity is accepted; signature, developer, architecture and version
            // checks still apply.
            if (!productIdentity ||
                VersionField(text, "CompanyName") != ProductIdentity.DeveloperName
                || !NodeUpdateManifest.IsProductVersion(VersionField(text, "ProductVersion")))
            {
                throw new InvalidDataException("Executable product identity does not match k-lani-bot-node.");
            }
            return VersionField(text, "ProductVersion");
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            throw new InvalidDataException("Malformed executable version metadata.", exception);
        }
    }

    private const int MinimumResourceBytes = 24;
    private const int MaximumResourceTableBytes = 8 * 1024 * 1024;
    private const int MaximumVersionResourceBytes = 1024 * 1024;
    private const uint VersionResourceType = 16;

    // The VS_VERSIONINFO block as UTF-16 text, found through the resource directory: type, then name, then language.
    // Throws InvalidDataException when the resource table or the version resource is missing or out of bounds.
    private static string ReadVersionResource(PEReader executable)
    {
        var table = executable.PEHeaders.PEHeader!.ResourceTableDirectory;
        if (table.Size is < MinimumResourceBytes or > MaximumResourceTableBytes)
        {
            throw new InvalidDataException("Native version metadata missing or oversized.");
        }
        byte[] resources = executable.GetSectionData(table.RelativeVirtualAddress).GetContent(0, table.Size).ToArray();
        int directory = VersionDirectory(resources);
        int address = BitConverter.ToInt32(resources, directory);
        int length = BitConverter.ToInt32(resources, directory + 4);
        if (length is < MinimumResourceBytes or > MaximumVersionResourceBytes)
        {
            throw new InvalidDataException("Invalid version resource size.");
        }
        return Encoding.Unicode.GetString(executable.GetSectionData(address).GetContent(0, length).ToArray());
    }

    // Throws InvalidDataException when the resource table has no version entry.
    private static int VersionDirectory(byte[] resources)
    {
        int entries = BitConverter.ToUInt16(resources, 12) + BitConverter.ToUInt16(resources, 14);
        int directory = -1;
        for (int index = 0; index < entries; index++)
        {
            int entry = 16 + index * 8;
            if (BitConverter.ToUInt32(resources, entry) == VersionResourceType)
            {
                directory = (int)(BitConverter.ToUInt32(resources, entry + 4) & 0x7fffffff);
                break;
            }
        }
        if (directory < 0)
        {
            throw new InvalidDataException("Native version resource missing.");
        }
        for (int level = 0; level < 2; level++)
        {
            directory = (int)(BitConverter.ToUInt32(resources, directory + 20) & 0x7fffffff);
        }
        return directory;
    }

    // The value after a key in the version block, or empty when the key is missing.
    private static string VersionField(string text, string name)
    {
        int index = text.IndexOf(name + '\0', StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }
        index += name.Length + 1;
        while (index < text.Length && text[index] == '\0')
        {
            index++;
        }
        int end = text.IndexOf('\0', index);
        return end < 0 ? "" : text[index..end];
    }
}
