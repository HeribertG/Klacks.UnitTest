// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Presentation.Mcp;
using Klacks.Docs;
using ModelContextProtocol.Protocol;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpResourceCatalogTests
{
    private McpResourceCatalog _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new McpResourceCatalog();
    }

    [Test]
    public void ListResources_ReturnsAllAvailableDocs()
    {
        var availableDocs = DocsReader.GetAvailableDocs();

        var resources = _sut.ListResources();

        Assert.That(resources, Has.Count.EqualTo(availableDocs.Count));
        Assert.That(resources.Select(resource => resource.Name), Is.EquivalentTo(availableDocs.Keys));
    }

    [Test]
    public void ListResources_BuildsDocsUriFormatWithMarkdownMimeType()
    {
        var availableDocs = DocsReader.GetAvailableDocs();

        var resources = _sut.ListResources();

        foreach (var resource in resources)
        {
            Assert.That(resource.Uri, Is.EqualTo($"{McpServerConstants.DocsResourceUriPrefix}{resource.Name}"));
            Assert.That(resource.MimeType, Is.EqualTo(McpServerConstants.MarkdownMimeType));
            Assert.That(resource.Description, Is.EqualTo(availableDocs[resource.Name]));
        }
    }

    [Test]
    public async Task ReadResourceAsync_KnownDoc_ReturnsMarkdownContent()
    {
        var docName = DocsReader.GetAvailableDocs().Keys.First();
        var uri = $"{McpServerConstants.DocsResourceUriPrefix}{docName}";

        var result = await _sut.ReadResourceAsync(uri);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Contents, Has.Count.EqualTo(1));
        var contents = result.Contents[0] as TextResourceContents;
        Assert.That(contents, Is.Not.Null);
        Assert.That(contents!.Uri, Is.EqualTo(uri));
        Assert.That(contents.MimeType, Is.EqualTo(McpServerConstants.MarkdownMimeType));
        Assert.That(contents.Text, Is.Not.Empty);
    }

    [Test]
    public async Task ReadResourceAsync_UnknownDocName_ReturnsNull()
    {
        var uri = $"{McpServerConstants.DocsResourceUriPrefix}does-not-exist";

        var result = await _sut.ReadResourceAsync(uri);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReadResourceAsync_WrongUriPrefix_ReturnsNull()
    {
        var docName = DocsReader.GetAvailableDocs().Keys.First();

        var result = await _sut.ReadResourceAsync($"other://docs/{docName}");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReadResourceAsync_PrefixWithoutDocName_ReturnsNull()
    {
        var result = await _sut.ReadResourceAsync(McpServerConstants.DocsResourceUriPrefix);

        Assert.That(result, Is.Null);
    }
}
