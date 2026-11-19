// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The one rule for the external id of a directory account: objectGUID of a company directory, else entryUUID of a
/// directory server, else the distinguished name. Client import and the directory sign-in links both key on it, so a
/// rename or move of the entry must not change it as long as one of the two GUID attributes is delivered.
/// </summary>

using Klacks.Api.Domain.Interfaces.Authentification;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class LdapUserEntryStableIdTests
{
    private const string ObjectGuid = "11111111-1111-1111-1111-111111111111";
    private const string EntryUuid = "22222222-2222-2222-2222-222222222222";
    private const string DistinguishedName = "uid=anna,ou=people,dc=example,dc=com";

    [Test]
    public void StableId_WithObjectGuid_IsTheObjectGuid()
    {
        var entry = new LdapUserEntry { ObjectGuid = ObjectGuid, EntryUuid = EntryUuid, DistinguishedName = DistinguishedName };

        entry.StableId.ShouldBe(ObjectGuid);
    }

    [Test]
    public void StableId_WithoutObjectGuid_IsTheEntryUuid()
    {
        var entry = new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName };

        entry.StableId.ShouldBe(EntryUuid);
    }

    [Test]
    public void StableId_WithoutBothGuids_IsTheDistinguishedName()
    {
        var entry = new LdapUserEntry { DistinguishedName = DistinguishedName };

        entry.StableId.ShouldBe(DistinguishedName);
    }

    [Test]
    public void StableId_AfterTheEntryWasMoved_StaysTheSameWhileAGuidIsDelivered()
    {
        var before = new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName };
        var after = new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = "uid=anna,ou=moved,dc=example,dc=com" };

        after.StableId.ShouldBe(before.StableId);
    }

    [TestCase("{22222222-2222-2222-2222-222222222222}")]
    [TestCase("22222222222222222222222222222222")]
    [TestCase("22222222-2222-2222-2222-222222222222")]
    public void StableId_GuidInAnotherSpelling_IsNormalizedToLowercaseHyphenated(string spelled)
    {
        var entry = new LdapUserEntry { EntryUuid = spelled.ToUpperInvariant(), DistinguishedName = DistinguishedName };

        entry.StableId.ShouldBe(EntryUuid);
        LdapUserEntry.NormalizeExternalId(spelled).ShouldBe(entry.StableId);
    }

    [Test]
    public void NormalizeExternalId_DistinguishedName_IsOnlyTrimmed()
    {
        LdapUserEntry.NormalizeExternalId("  " + DistinguishedName + " ").ShouldBe(DistinguishedName);
        LdapUserEntry.IsGuidId(DistinguishedName).ShouldBeFalse();
        LdapUserEntry.IsGuidId(EntryUuid).ShouldBeTrue();
    }
}
