using System.Text;

namespace KLaniBot.Transport;

// The supervisor, the stdio worker and the MCP gateway read one JSON frame per line here, with a size limit per frame.
public sealed class JsonLineReader(Stream stream, int maximum = 4 * 1024 * 1024)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _start, _end;
    // Throws InvalidDataException for an incomplete or oversized frame.
    public async Task<string?> ReadAsync(CancellationToken cancellation = default)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _end = await stream.ReadAsync(_buffer, cancellation);
                _start = 0;
                if (_end == 0)
                    return line.Length == 0 ? null : throw new InvalidDataException("Incomplete JSON frame.");
            }
            int index = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int count = (index >= 0 ? index : _end) - _start;
            if (line.Length + count > maximum)
                throw new InvalidDataException("JSON frame exceeds the protocol limit.");
            line.Write(_buffer, _start, count);
            _start += count;
            if (index >= 0)
            {
                _start++;
                return new UTF8Encoding(false, true).GetString(line.ToArray());
            }
        }
    }
}
