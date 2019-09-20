using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace simdhttp
{
    class Program
    {
        static void Main(string[] args)
        {
            byte[] buffer = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: foo\r\n" +
                "\r\n"
                );

            ParseHttpAVX2(buffer, true, r =>
            {
                string line = Encoding.ASCII.GetString(buffer.AsSpan(r));
                Console.WriteLine($"{r}: {line}");
            });
        }

        static int ParseHttpAVX2(Span<byte> buffer, bool doneReading, Action<Range> onRange)
        {
            var cr = Vector256.Create((byte)'\n');

            ReadOnlySpan<Vector256<byte>> span = MemoryMarshal.Cast<byte, Vector256<byte>>(buffer);
            int lineStart = 0;

            for (int i = 0; i < span.Length; ++i)
            {
                Vector256<byte> x = span[i];
                Vector256<byte> isCr = Avx2.CompareEqual(x, cr);

                if (Avx2.TestZ(isCr, isCr))
                {
                    continue;
                }

                uint mask = (uint)Avx2.MoveMask(isCr);

                do
                {
                    int maskIdx = BitOperations.TrailingZeroCount(mask);
                    int bufferIdx = i * Vector256<byte>.Count + maskIdx;

                    // Clear this bit from mask so it won't be re-read on next loop.
                    mask &= ~(1u << maskIdx);

                    // This code works for both LF and CRLF, for compatibility.
                    int startIdx = bufferIdx;
                    if (bufferIdx != 0 && buffer[bufferIdx - 1] == '\r')
                    {
                        startIdx = bufferIdx - 1;
                    }

                    int tabIdx = bufferIdx + 1;
                    if (tabIdx != buffer.Length)
                    {
                        byte ht = buffer[tabIdx];
                        if (ht == ' ' || ht == '\t')
                        {
                            // Continuation line. replace CRLF with SPSP and keep going.
                            buffer[startIdx] = (byte)' ';
                            buffer[bufferIdx] = (byte)' ';
                            continue;
                        }
                    }
                    else if (!doneReading)
                    {
                        return lineStart;
                    }

                    // Found a line to return.
                    onRange(new Range(lineStart, startIdx));
                    lineStart = tabIdx;
                }
                while (mask != 0);
            }

            return ParseHttpPortable(lineStart, span.Length * Vector256<byte>.Count, buffer, doneReading, onRange);
        }

        static int ParseHttpPortable(int lineStart, int parseFromIdx, Span<byte> buffer, bool doneReading, Action<Range> onRange)
        {
            for (int bufferIdx = parseFromIdx; bufferIdx < buffer.Length; ++bufferIdx)
            {
                if (buffer[bufferIdx] != '\n') continue;

                // This code works for both LF and CRLF, for compatibility.
                int startIdx = bufferIdx;
                if (bufferIdx != 0 && buffer[bufferIdx - 1] == '\r')
                {
                    startIdx = bufferIdx - 1;
                }

                int tabIdx = bufferIdx + 1;
                if (tabIdx != buffer.Length)
                {
                    byte ht = buffer[tabIdx];
                    if (ht == ' ' || ht == '\t')
                    {
                        // Continuation line. replace CRLF with spaces and keep going.
                        buffer[startIdx] = (byte)' ';
                        buffer[bufferIdx] = (byte)' ';
                        continue;
                    }
                }
                else if (!doneReading)
                {
                    return lineStart;
                }

                // Found a line to return.
                onRange(new Range(lineStart, startIdx));
                lineStart = bufferIdx + 1;
            }

            return lineStart;
        }
    }

    sealed class CurrentHttpHeaderReader
    {
        private byte[] _readBuffer;
        private int _readLength, _readOffset;

        public void Reset(byte[] buffer)
        {
            _readBuffer = buffer;
            _readLength = buffer.Length;
            _readOffset = 0;
        }

        private Task FillAsync()
        {
            return Task.CompletedTask;
        }

        private async ValueTask<ArraySegment<byte>> ReadNextResponseHeaderLineAsync(bool foldedHeadersAllowed = false)
        {
            int previouslyScannedBytes = 0;
            while (true)
            {
                int scanOffset = _readOffset + previouslyScannedBytes;
                int lfIndex = Array.IndexOf(_readBuffer, (byte)'\n', scanOffset, _readLength - scanOffset);
                if (lfIndex >= 0)
                {
                    int startIndex = _readOffset;
                    int length = lfIndex - startIndex;
                    if (lfIndex > 0 && _readBuffer[lfIndex - 1] == '\r')
                    {
                        length--;
                    }

                    // If this isn't the ending header, we need to account for the possibility
                    // of folded headers, which per RFC2616 are headers split across multiple
                    // lines, where the continuation line begins with a space or horizontal tab.
                    // The feature was deprecated in RFC 7230 3.2.4, but some servers still use it.
                    if (foldedHeadersAllowed && length > 0)
                    {
                        // If the newline is the last character we've buffered, we need at least
                        // one more character in order to see whether it's space/tab, in which
                        // case it's a folded header.
                        if (lfIndex + 1 == _readLength)
                        {
                            // The LF is at the end of the buffer, so we need to read more
                            // to determine whether there's a continuation.  We'll read
                            // and then loop back around again, but to avoid needing to
                            // rescan the whole header, reposition to one character before
                            // the newline so that we'll find it quickly.
                            int backPos = _readBuffer[lfIndex - 1] == '\r' ? lfIndex - 2 : lfIndex - 1;
                            Debug.Assert(backPos >= 0);
                            previouslyScannedBytes = backPos - _readOffset;
                            await FillAsync().ConfigureAwait(false);
                            continue;
                        }

                        // We have at least one more character we can look at.
                        Debug.Assert(lfIndex + 1 < _readLength);
                        char nextChar = (char)_readBuffer[lfIndex + 1];
                        if (nextChar == ' ' || nextChar == '\t')
                        {
                            // The next header is a continuation.

                            // Folded headers are only allowed within header field values, not within header field names,
                            // so if we haven't seen a colon, this is invalid.
                            if (Array.IndexOf(_readBuffer, (byte)':', _readOffset, lfIndex - _readOffset) == -1)
                            {
                                throw new Exception();
                            }

                            // When we return the line, we need the interim newlines filtered out. According
                            // to RFC 7230 3.2.4, a valid approach to dealing with them is to "replace each
                            // received obs-fold with one or more SP octets prior to interpreting the field
                            // value or forwarding the message downstream", so that's what we do.
                            _readBuffer[lfIndex] = (byte)' ';
                            if (_readBuffer[lfIndex - 1] == '\r')
                            {
                                _readBuffer[lfIndex - 1] = (byte)' ';
                            }

                            // Update how much we've read, and simply go back to search for the next newline.
                            previouslyScannedBytes = (lfIndex + 1 - _readOffset);
                            continue;
                        }

                        // Not at the end of a header with a continuation.
                    }

                    // Advance read position past the LF
                    _readOffset = lfIndex + 1;

                    return new ArraySegment<byte>(_readBuffer, startIndex, length);
                }

                // Couldn't find LF.  Read more. Note this may cause _readOffset to change.
                previouslyScannedBytes = _readLength - _readOffset;
                await FillAsync().ConfigureAwait(false);
            }
        }
    }

    ref struct HttpHeaderReader
    {
        private static readonly Vector256<byte> cr = Vector256.Create((byte)'\n');

        private ReadOnlySpan<Vector256<byte>> _packed;
        private int _packedIter;
        private uint _packedMask;

        private Span<byte> _buffer;
        private int _lineStart;

        private bool TryParseAVX2(out Range range)
        {
            if (_packedMask == 0)
            {
                while (++_packedIter < _packed.Length)
                {
                    Vector256<byte> x = _packed[_packedIter];
                    Vector256<byte> isCr = Avx2.CompareEqual(x, cr);

                    if (!Avx.TestZ(isCr, isCr))
                    {
                        _packedMask = (uint)Avx2.MoveMask(isCr);
                        break;
                    }
                }

                return TryParsePortable(out range);
            }




            return TryParsePortable(out range);
        }

        private bool TryParsePortable(out Range range)
        {
            range = default;
            return false;
        }
    }
}
