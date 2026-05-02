using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRipApp.Core
{

    public class StitchedRarStream : Stream
    {
        private readonly List<(long Offset, long Length, byte[]? Buffer)> _segments = new();
        private long _position = 0;
        private readonly string _url;
        private readonly Func<string, long, long, Task<byte[]>> _readRange;

        private byte[]? _cache;
        private long _cacheOffset = -1;
        private const int CacheSize = 64 * 1024;

        public StitchedRarStream(string url, Func<string, long, long, Task<byte[]>> readRange)
        {
            _url = url;
            _readRange = readRange;
        }

        public void AddBuffer(byte[] buffer) => _segments.Add((0, buffer.Length, buffer));
        public void AddRemote(long offset, long length) => _segments.Add((offset, length, null));

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _segments.Sum(s => s.Length);
        public override long Position { get => _position; set => _position = value; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {

            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_position >= Length) return 0;

            int totalRead = 0;

            while (count > 0 && _position < Length)
            {
                var seg = FindSegmentAt(_position, out long segStart);
                if (seg.Length == 0) break;

                long relPos = _position - segStart;
                int toRead = (int)Math.Min(count, seg.Length - relPos);

                if (seg.Buffer != null)
                {
                    Array.Copy(seg.Buffer, relPos, buffer, offset, toRead);
                }
                else
                {
                    long absoluteRemotePos = seg.Offset + relPos;

                    if (_cache != null && absoluteRemotePos >= _cacheOffset && absoluteRemotePos < _cacheOffset + _cache.Length)
                    {
                        int cacheRelPos = (int)(absoluteRemotePos - _cacheOffset);
                        int canReadFromCache = Math.Min(toRead, _cache.Length - cacheRelPos);
                        Array.Copy(_cache, cacheRelPos, buffer, offset, canReadFromCache);
                        toRead = canReadFromCache;
                    }
                    else
                    {

                        int toFetch = (int)Math.Max(CacheSize, toRead);
                        _cache = await _readRange(_url, absoluteRemotePos, toFetch);
                        _cacheOffset = absoluteRemotePos;

                        int actualRead = Math.Min(_cache.Length, toRead);
                        Array.Copy(_cache, 0, buffer, offset, actualRead);
                        toRead = actualRead;
                    }
                }

                _position += toRead;
                offset += toRead;
                count -= toRead;
                totalRead += toRead;

                if (seg.Buffer == null) break;
            }

            return totalRead;
        }

        private (long Offset, long Length, byte[]? Buffer) FindSegmentAt(long pos, out long start)
        {
            long current = 0;
            foreach (var seg in _segments)
            {
                if (pos >= current && pos < current + seg.Length)
                {
                    start = current;
                    return seg;
                }
                current += seg.Length;
            }
            start = 0;
            return (0, 0, null);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin) _position = offset;
            else if (origin == SeekOrigin.Current) _position += offset;
            else _position = Length + offset;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}