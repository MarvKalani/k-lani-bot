using System.Globalization;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Actions.ApplicationMcp;

// Use case: a stored app MCP function checks the file a program wrote, for example the GIF FilmCraft exported, before and after
// its steps. app_mcp_read {program, file} answers with these facts so the file itself never crosses the wire; only files inside
// the node's workspace are read, and the program is not started for it.
internal static class ApplicationMcpFiles
{
    // Throws UnauthorizedAccessException for a path outside the workspace or through a link.
    public static JsonObject Facts(NodePolicy policy, string programId, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new UnauthorizedAccessException("The file must be an absolute path inside the workspace.");
        string full = policy.ResolvePath(path);

        var facts = new JsonObject { ["path"] = full, ["exists"] = File.Exists(full) };
        if (File.Exists(full))
            AddFileFacts(facts, full);
        return new JsonObject { ["success"] = true, ["program"] = programId, ["file"] = facts };
    }

    private static void AddFileFacts(JsonObject facts, string full)
    {
        var info = new FileInfo(full);
        facts["bytes"] = info.Length;
        facts["modified"] = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (GifFrameCounter.TryCount(stream, out int frames))
            facts["gif_frames"] = frames;
    }
}

// Use case: ApplicationMcpFiles counts the frames of a GIF the program exported, so a stored function can check that an export
// holds a plausible number of frames. It walks the block structure of GIF89a (W3C/CompuServe 1990) and skips all pixel data.
internal static class GifFrameCounter
{
    private const int HeaderBytes = 6;
    private const int ScreenDescriptorBytes = 7;
    private const int ImageDescriptorBytes = 9;
    private const int ColorTableFlag = 0x80;
    private const int ColorTableSizeMask = 0x07;
    private const int ExtensionIntroducer = 0x21;
    private const int ImageSeparator = 0x2C;
    private const int Trailer = 0x3B;
    private const int MaximumFrames = 1_000_000;

    // False for a stream that is not a complete GIF; a stream that cannot be read counts as not a GIF.
    public static bool TryCount(Stream stream, out int frames)
    {
        frames = 0;
        try
        {
            return TryWalk(stream, out frames);
        }
        catch (IOException)
        {
            frames = 0;
            return false;
        }
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TryWalk(Stream stream, out int frames)
    {
        frames = 0;
        if (!TryHeader(stream))
            return false;

        while (frames <= MaximumFrames)
        {
            int block = stream.ReadByte();
            if (block == Trailer)
                return true;
            if (block == ExtensionIntroducer && stream.ReadByte() >= 0 && TrySkipSubBlocks(stream))
                continue;
            if (block != ImageSeparator || !TrySkipImage(stream))
                return false;
            frames++;
        }
        return false;
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TryHeader(Stream stream)
    {
        var header = new byte[HeaderBytes + ScreenDescriptorBytes];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            return false;
        string signature = System.Text.Encoding.ASCII.GetString(header, 0, HeaderBytes);
        if (signature is not "GIF87a" and not "GIF89a")
            return false;

        return TrySkipColorTable(stream, header[HeaderBytes + 4]);
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TrySkipImage(Stream stream)
    {
        var descriptor = new byte[ImageDescriptorBytes];
        if (stream.ReadAtLeast(descriptor, descriptor.Length, throwOnEndOfStream: false) != descriptor.Length)
            return false;
        if (!TrySkipColorTable(stream, descriptor[ImageDescriptorBytes - 1]))
            return false;
        int minimumCodeSize = stream.ReadByte();

        return minimumCodeSize >= 0 && TrySkipSubBlocks(stream);
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TrySkipColorTable(Stream stream, byte packed)
    {
        if ((packed & ColorTableFlag) == 0)
            return true;
        int entries = 1 << ((packed & ColorTableSizeMask) + 1);

        return TrySkip(stream, entries * 3);
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TrySkipSubBlocks(Stream stream)
    {
        while (true)
        {
            int size = stream.ReadByte();
            if (size < 0)
                return false;
            if (size == 0)
                return true;
            if (!TrySkip(stream, size))
                return false;
        }
    }

    // Throws IOException when the stream fails while it is read.
    private static bool TrySkip(Stream stream, int count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length)
                return false;
            stream.Seek(count, SeekOrigin.Current);
            return true;
        }
        var buffer = new byte[count];
        return stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false) == count;
    }
}
