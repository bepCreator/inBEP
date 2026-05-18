// =============================================================================
// BitIO — MSB-first bit-level read/write for inBEP streams.
// Independent of Rich's existing BitIo.cs to keep the project self-contained.
// =============================================================================

namespace InBep.Core;

public sealed class BitWriter
{
    private readonly List<byte> _bytes = new();
    private byte _current;
    private int _bitPos; // 0..7, where next bit goes (0 = MSB of next byte to emit)

    public long BitsWritten { get; private set; }

    public void WriteBits(ulong value, int nBits)
    {
        if (nBits < 0 || nBits > 64) throw new ArgumentOutOfRangeException(nameof(nBits));
        for (int i = nBits - 1; i >= 0; i--)
        {
            ulong bit = (value >> i) & 1UL;
            if (bit != 0) _current |= (byte)(1 << (7 - _bitPos));
            _bitPos++;
            BitsWritten++;
            if (_bitPos == 8)
            {
                _bytes.Add(_current);
                _current = 0;
                _bitPos = 0;
            }
        }
    }

    public void WriteBytes(byte[] data)
    {
        // Optimized path when byte-aligned, but fall back to per-bit if not.
        if (_bitPos == 0)
        {
            _bytes.AddRange(data);
            BitsWritten += data.Length * 8L;
        }
        else
        {
            foreach (byte b in data) WriteBits(b, 8);
        }
    }

    public byte[] ToArray()
    {
        if (_bitPos > 0)
        {
            _bytes.Add(_current);
            _current = 0;
            _bitPos = 0;
        }
        return _bytes.ToArray();
    }
}

public sealed class BitReader
{
    private readonly byte[] _bytes;
    private long _bitPos;

    public BitReader(byte[] bytes) { _bytes = bytes; _bitPos = 0; }

    public long Position => _bitPos;
    public long Remaining => (long)_bytes.Length * 8 - _bitPos;

    public ulong ReadBits(int nBits)
    {
        if (nBits < 0 || nBits > 64) throw new ArgumentOutOfRangeException(nameof(nBits));
        ulong v = 0;
        for (int i = 0; i < nBits; i++)
        {
            long byteIdx = _bitPos >> 3;
            int bitIdx = 7 - (int)(_bitPos & 7);
            if (byteIdx >= _bytes.Length)
                throw new EndOfStreamException("BitReader past end of buffer");
            ulong bit = (ulong)((_bytes[byteIdx] >> bitIdx) & 1);
            v = (v << 1) | bit;
            _bitPos++;
        }
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)ReadBits(8);
        return b;
    }
}
