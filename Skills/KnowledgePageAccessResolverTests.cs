// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class KnowledgePageAccessResolverTests
{
    private const string SettingsPageKey = "settings";

    private IKlacksyPageKeyCatalog _catalog = null!;
    private KnowledgePageAccessResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _catalog = Substitute.For<IKlacksyPageKeyCatalog>();
        _catalog.GetByPageKey(SettingsPageKey).Returns(
            new KlacksyPageKeyEntry(SettingsPageKey, "/workplace/settings", Permissions.CanViewSettings, false));
        _sut = new KnowledgePageAccessResolver(_catalog);
    }

    [Test]
    public void NoPageKey_IsNoPage()
    {
        Assert.That(_sut.Resolve(null, [Permissions.CanViewSettings]), Is.EqualTo(KnowledgePageAccess.NoPage));
    }

    [Test]
    public void UnknownPageKey_IsNoPage()
    {
        Assert.That(_sut.Resolve("no-such-page", [Permissions.CanViewSettings]), Is.EqualTo(KnowledgePageAccess.NoPage));
    }

    [Test]
    public void UserWithoutThePageRight_IsDenied()
    {
        Assert.That(_sut.Resolve(SettingsPageKey, [Permissions.CanViewClients]), Is.EqualTo(KnowledgePageAccess.Denied));
    }

    [Test]
    public void UserWithThePageRight_IsOpen()
    {
        Assert.That(_sut.Resolve(SettingsPageKey, [Permissions.CanViewSettings]), Is.EqualTo(KnowledgePageAccess.Open));
    }

    [Test]
    public void Admin_IsOpen()
    {
        Assert.That(_sut.Resolve(SettingsPageKey, [Roles.Admin]), Is.EqualTo(KnowledgePageAccess.Open));
    }
}
