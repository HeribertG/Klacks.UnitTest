// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Read-only stream that hides its length, so a StreamContent built on it carries no Content-Length -
/// the shape of a chunked HTTP response whose size is only known while it is read.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Marketplace;

internal sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableStream(byte[] content)
    {
        _inner = new MemoryStream(content, writable: false);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
