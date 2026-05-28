// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SearchAndNavigateSkill — verifies that a single search hit produces the
/// correct /workplace/... route, since the frontend only navigates to /workplace/ routes.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SearchAndNavigateSkillTests
{
    private IClientSearchRepository _clientSearch = null!;
    private IGroupSearchRepository _groupSearch = null!;
    private IShiftSearchRepository _shiftSearch = null!;
    private SearchAndNavigateSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _clientSearch = Substitute.For<IClientSearchRepository>();
        _groupSearch = Substitute.For<IGroupSearchRepository>();
        _shiftSearch = Substitute.For<IShiftSearchRepository>();
        _skill = new SearchAndNavigateSkill(_clientSearch, _groupSearch, _shiftSearch);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "Admin" }
        };
    }

    private static string? RouteOf(SkillResult result)
    {
        var json = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("Route", out var route) ? route.GetString() : null;
    }

    [Test]
    public async Task ExecuteAsync_SingleClient_NavigatesToWorkplaceEditAddress()
    {
        var id = Guid.NewGuid();
        _clientSearch.SearchAsync(searchTerm: "Heribert", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 1,
                Items = new[]
                {
                    new ClientSearchItem { Id = id, FirstName = "Heribert", LastName = "Gasparoli" }
                }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "Heribert" }
        });

        result.Type.ShouldBe(SkillResultType.Navigation);
        RouteOf(result).ShouldBe($"/workplace/edit-address/{id}");
    }

    [Test]
    public async Task ExecuteAsync_SingleGroup_NavigatesToWorkplaceEditGroup()
    {
        var id = Guid.NewGuid();
        _groupSearch.SearchAsync(searchTerm: "Bern", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                TotalCount = 1,
                Items = new[] { new GroupSearchItem { Id = id, Name = "Bern" } }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "group" },
            { "searchQuery", "Bern" }
        });

        RouteOf(result).ShouldBe($"/workplace/edit-group/{id}");
    }

    [Test]
    public async Task ExecuteAsync_RouteAlwaysStartsWithWorkplace()
    {
        var id = Guid.NewGuid();
        _clientSearch.SearchAsync(searchTerm: "x", limit: 10, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                TotalCount = 1,
                Items = new[] { new ClientSearchItem { Id = id, FirstName = "X", LastName = "Y" } }
            });

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            { "entityType", "client" },
            { "searchQuery", "x" }
        });

        RouteOf(result).ShouldNotBeNull();
        RouteOf(result)!.ShouldStartWith("/workplace/");
    }
}
