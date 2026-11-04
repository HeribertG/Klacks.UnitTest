// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// HttpMessageHandler stub for marketplace package downloads: answers every request with the given status,
/// body bytes and optional X-Klacks-Signature header, either with a declared Content-Length or as a body
/// of unknown length. Records the number of calls and the last requested URI.
/// </summary>
using System.Net;
using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Infrastructure.Marketplace;

internal sealed class MarketplaceStubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly byte[] _body;
    private readonly string? _signature;
    private readonly bool _declareLength;

    public MarketplaceStubHandler(HttpStatusCode status, byte[] body, string? signature, bool declareLength = true)
    {
        _status = status;
        _body = body;
        _signature = signature;
        _declareLength = declareLength;
    }

    public int Calls { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequestUri = request.RequestUri;

        HttpContent content = _declareLength
            ? new ByteArrayContent(_body)
            : new StreamContent(new NonSeekableStream(_body));

        var response = new HttpResponseMessage(_status) { Content = content };
        if (_signature != null)
        {
            response.Headers.Add(HttpHeaderNames.KlacksSignature, _signature);
        }

        return Task.FromResult(response);
    }
}
