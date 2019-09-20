using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
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
    [RankColumn]
    public class Program
    {
        static byte[] s_buffer = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: foo\r\n" +
                "\r\n"
                );

        static Action<Range> nopAction = delegate { };

        //[Benchmark]
        //public void SSE2()
        //{
        //    ParseHttpSSE2(s_buffer, true, nopAction);
        //}

        //[Benchmark]
        //public void SSE2_Try2()
        //{
        //    ParseHttpSSE2_Try2(s_buffer, true, nopAction);
        //}

        //[Benchmark]
        //public void Portable()
        //{
        //    ParseHttpPortable(0, 0, s_buffer, true, nopAction);
        //}

        [Benchmark]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void New()
        {
            var reader = new HttpHeaderReader(s_buffer, true);

            while (reader.TryParse())
            {
                // do nothing.
            }
        }

        [Benchmark(Baseline = true)]
        public void Original()
        {
            ParseOriginal(s_buffer, true, nopAction);
        }

        static void Main(string[] args)
        {
            new Program().New();

            if (false)
            {
                ParseHttpSSE2_Try2(s_buffer, true, r =>
                {
                    string s = Encoding.ASCII.GetString(s_buffer.AsSpan(r));
                    Console.WriteLine($"{r} -> {s}");
                });
            }

            if (false)
            {
                var reader = new HttpHeaderReader(s_buffer, true);

                while (reader.TryParse())
                {
                    Range r = reader.Range;

                    string s = Encoding.ASCII.GetString(s_buffer.AsSpan(r));
                    Console.WriteLine($"{r} -> {s}");
                }
            }

            if(false)
            {
                BenchmarkRunner.Run<Program>();
            }
        }

        static int ParseHttpSSE2_Try2(Span<byte> buffer, bool doneReading, Action<Range> onRange)
        {
            var cr = Vector128.Create((byte)'\n');
            int lineStart = 0;

            ref byte firstByte = ref buffer[0];
            int i = 0;
            while ((buffer.Length - i) >= Vector128<byte>.Count)
            {
                Vector128<byte> x = Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref firstByte, i));

                // Each bit set indicates a LF.
                Vector128<byte> isCr = Sse2.CompareEqual(x, cr);
                uint mask = (uint)Sse2.MoveMask(isCr);

                if (mask != 0)
                {
                    i += BitOperations.TrailingZeroCount(mask);

                    // This code works for both LF and CRLF, for compatibility.
                    int startIdx = i != 0 && buffer[i - 1] == '\r' ? i - 1 : i;

                    int tabIdx = i + 1;
                    if (tabIdx != buffer.Length)
                    {
                        byte ht = buffer[tabIdx];
                        if (ht == ' ' || ht == '\t')
                        {
                            // Continuation line. Check that we're in a header value, not name.
                            if (!buffer.Slice(lineStart, startIdx - lineStart).Contains((byte)':'))
                            {
                                throw new Exception();
                            }

                            // Replace CRLF with spaces and keep going.
                            buffer[startIdx] = (byte)' ';
                            buffer[i] = (byte)' ';
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
                    i = tabIdx;
                }
                else
                {
                    i += Vector128<byte>.Count;
                }
            }

            return ParseHttpPortable(lineStart, i, buffer, doneReading, onRange);
        }

        static int ParseHttpSSE2(Span<byte> buffer, bool doneReading, Action<Range> onRange)
        {
            var cr = Vector128.Create((byte)'\n');

            ReadOnlySpan<Vector128<byte>> span = MemoryMarshal.Cast<byte, Vector128<byte>>(buffer);
            int lineStart = 0;

            for (int i = 0; i < span.Length; ++i)
            {
                Vector128<byte> x = span[i];

                // Each bit set indicates a LF.
                Vector128<byte> isCr = Sse2.CompareEqual(x, cr);
                uint mask = (uint)Sse2.MoveMask(isCr);

                while (mask != 0)
                {
                    int maskIdx = BitOperations.TrailingZeroCount(mask);
                    int bufferIdx = i * Vector128<byte>.Count + maskIdx;

                    // Clear this bit from mask so it won't be re-read on next loop.
                    mask &= ~(1u << maskIdx);

                    // This code works for both LF and CRLF, for compatibility.
                    int startIdx = bufferIdx != 0 && buffer[bufferIdx - 1] == '\r' ? bufferIdx - 1 : bufferIdx;

                    int tabIdx = bufferIdx + 1;
                    if (tabIdx != buffer.Length)
                    {
                        byte ht = buffer[tabIdx];
                        if (ht == ' ' || ht == '\t')
                        {
                            // Continuation line. Check that we're in a header value, not name.
                            if (!buffer.Slice(lineStart, startIdx - lineStart).Contains((byte)':'))
                            {
                                throw new Exception();
                            }

                            // Replace CRLF with spaces and keep going.
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
            }

            return ParseHttpPortable(lineStart, span.Length * Vector128<byte>.Count, buffer, doneReading, onRange);
        }

        static int ParseHttpPortable(int lineStart, int bufferIdx, Span<byte> buffer, bool doneReading, Action<Range> onRange)
        {
            for (; bufferIdx < buffer.Length; ++bufferIdx)
            {
                int nextIdx = buffer.Slice(bufferIdx).IndexOf((byte)'\n');

                if (nextIdx < 0)
                {
                    break;
                }

                bufferIdx += nextIdx;

                // This code works for both LF and CRLF, for compatibility.
                int startIdx = bufferIdx != 0 && buffer[bufferIdx - 1] == '\r' ? bufferIdx - 1 : bufferIdx;

                int tabIdx = bufferIdx + 1;
                if (tabIdx != buffer.Length)
                {
                    byte ht = buffer[tabIdx];
                    if (ht == ' ' || ht == '\t')
                    {
                        // Continuation line. Check that we're in a header value, not name.
                        if (!buffer.Slice(lineStart, startIdx - lineStart).Contains((byte)':'))
                        {
                            throw new Exception();
                        }

                        // Replace CRLF with spaces and keep going.
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

        static readonly Vector128<byte> linefeed = Vector128.Create((byte)'\n');

        static void ParseOriginal(byte[] _readBuffer, bool foldedHeadersAllowed, Action<Range> onRange)
        {
            int _readOffset = 0, _readLength = _readBuffer.Length;

            while (_readOffset != _readLength)
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

                        onRange(new Range(startIndex, startIndex + length));
                        break;
                    }

                    // Couldn't find LF.  Read more. Note this may cause _readOffset to change.
                    previouslyScannedBytes = _readLength - _readOffset;
                }
            }
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
        private static readonly Vector128<byte> s_lineFeed128 = Vector128.Create((byte)'\n');

        private readonly Span<byte> _buffer;
        private int _lineStart, _iter;
        private readonly bool _doneReading;

        public Range Range { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HttpHeaderReader(Span<byte> buffer, bool doneReading)
        {
            _buffer = buffer;
            _lineStart = 0;
            _iter = 0;
            _doneReading = doneReading;
            Range = default;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TryParse()
        {
            if (Sse2.IsSupported)
            {
                return TryParseSSE2();
            }

            return TryParsePortable();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool TryParseSSE2()
        {
            while (_buffer.Length - _iter >= Vector128<byte>.Count)
            {
                Vector128<byte> x = Unsafe.As<byte, Vector128<byte>>(ref _buffer[_iter]);

                Vector128<byte> isLf = Sse2.CompareEqual(x, s_lineFeed128);
                int mask = Sse2.MoveMask(isLf);

                if (mask != 0)
                {
                    _iter += BitOperations.TrailingZeroCount(mask);

                    bool? res = OnNewLine();
                    if (res != null)
                    {
                        ++_iter;
                        return res.GetValueOrDefault();
                    }
                }
                else
                {
                    _iter += Vector128<byte>.Count;
                    continue;
                }
            }

            return TryParsePortable();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool TryParsePortable()
        {
            for (; _iter != _buffer.Length; ++_iter)
            {
                int nextIdx = _buffer.Slice(_iter).IndexOf((byte)'\n');

                if (nextIdx < 0)
                {
                    return false;
                }

                _iter += nextIdx;

                bool? res = OnNewLine();
                if (res != null)
                {
                    ++_iter;
                    return res.GetValueOrDefault();
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool? OnNewLine()
        {
            // This code works for both LF and CRLF, for compatibility.
            int startIdx = _iter != 0 && _buffer[_iter - 1] == '\r' ? _iter - 1 : _iter;

            int tabIdx = _iter + 1;
            if (tabIdx != _buffer.Length)
            {
                byte ht = _buffer[tabIdx];
                if (ht == ' ' || ht == '\t')
                {
                    // Continuation line. Check that we're in a header value, not name.
                    if (!_buffer.Slice(_lineStart, startIdx - _lineStart).Contains((byte)':'))
                    {
                        throw new Exception();
                    }

                    // Replace CRLF with spaces and keep going.
                    _buffer[startIdx] = (byte)' ';
                    _buffer[_iter] = (byte)' ';
                    return null;
                }
            }
            else if (!_doneReading)
            {
                return false;
            }

            // Found a line to return.
            Range = new Range(_lineStart, startIdx);
            _lineStart = tabIdx;
            return true;
        }
    }
}
