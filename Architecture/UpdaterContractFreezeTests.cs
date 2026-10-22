// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Mirrored by Klacks.Updater.Tests/SharedContractFreezeTests — change both deliberately.
namespace Klacks.UnitTest.Architecture;

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Update;
using Shouldly;

[TestFixture]
public class UpdaterContractFreezeTests
{
    private const string ParakeetModelIdWrittenByKlacksStt = "parakeet-tdt-0.6b-v3-int8";

    [Test]
    public void OperationTypeValues_AreFrozen()
    {
        ((int)UpdateOperationType.Update).ShouldBe(0);
        ((int)UpdateOperationType.Rollback).ShouldBe(1);
        ((int)UpdateOperationType.WhisperInstall).ShouldBe(2);
        ((int)UpdateOperationType.WhisperUninstall).ShouldBe(3);
    }

    [Test]
    public void OperationTypeMembers_AreFrozen()
    {
        Enum.GetNames<UpdateOperationType>().ShouldBe(
            ["Update", "Rollback", "WhisperInstall", "WhisperUninstall"],
            ignoreOrder: true);
    }

    [Test]
    public void WhisperProviderRow_IsFrozen()
    {
        WhisperPluginConstants.ProviderId.ShouldBe(Guid.Parse("7f3d2a10-0000-4000-8000-57705731a001"));
        WhisperPluginConstants.ProviderName.ShouldBe("Whisper (self-hosted)");
        WhisperPluginConstants.ParakeetProviderName.ShouldBe("Parakeet (self-hosted)");
        SttProviderConstants.ConnectionTypeRest.ShouldBe("rest");
        SttProviderConstants.OpenAiModelsPath.ShouldBe("/v1/models");
        WhisperPluginConstants.ModelAliasSmall.ShouldBe("small");
        WhisperPluginConstants.ModelAliasLarge.ShouldBe("large-v3-turbo");
        WhisperPluginConstants.ModelAliasParakeet.ShouldBe("parakeet-v3");
    }

    [Test]
    public void WhisperModelIds_AreFrozen()
    {
        WhisperPluginConstants.SmallModelId.ShouldBe("Systran/faster-whisper-small");
        WhisperPluginConstants.LargeModelId.ShouldBe("deepdml/faster-whisper-large-v3-turbo-ct2");
        WhisperPluginConstants.ParakeetModelId.ShouldBe(ParakeetModelIdWrittenByKlacksStt);
    }
}
