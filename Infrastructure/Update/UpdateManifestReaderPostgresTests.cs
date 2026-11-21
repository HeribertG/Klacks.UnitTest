// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Infrastructure.Update;

using System.Net;
using System.Text;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Services.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class UpdateManifestReaderPostgresTests
{
    private const string Release1079Manifest = """
    {
      "channel": "Stable",
      "latestVersion": "1.0.79",
      "minUpgradableFrom": "1.0.0",
      "releasedAt": "2026-11-20T10:21:27Z",
      "containsMigrations": true,
      "artifacts": {
        "docker": {
          "apiImage": "ghcr.io/heribertg/klacks-api:1.0.79",
          "uiImage": "ghcr.io/heribertg/klacks-ui:1.0.79",
          "sha256": "sha256:a977bca8a2283233ff19400d864ad76965fb04cca45b3fc46b191ceedb649686"
        },
        "postgres": {
          "major": 19,
          "version": "19.0",
          "pgvector": "0.8.7",
          "docker": {
            "image": "ghcr.io/heribertg/klacks-postgres:19",
            "sha256": "sha256:981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2",
            "signature": "I0XsF5o002DnxYmjw3RGDZx+faGrVp3EcnGTvAlAqThIwEgo/JMyRDpPh1fCqyUi0PHLHylrbyjd2wMuTqHxqNyc4iRHgHkxAcvQowUbPe3aybrdK/QNt+hKyqdw5Fqh4bLfON++u1whVrghac/SRdPH0Qv/zXl9T9qaHOQaCtcs9bUISiVaTwcUDXIkPPFJy85Bs+SAlP6c+3NnAUE/5ag37HP5NV+2yy6HxkyvU1HvPxJDHfb1EuavwanH+uq+f43FnP9HBNrwFWEQ47Tg0OzTyHcV+D7bxDJH24/RVvrStdoZHx34T7p1OPdzMNKl1sJ05hVHPOyEIP6OcxjTkQ=="
          },
          "native": [
            {
              "rid": "win-x64",
              "bundleUrl": "https://github.com/HeribertG/Klacks.Api/releases/download/postgres-engine-19.0-pgvector0.8.7/klacks-postgres-19.0-win-x64.zip",
              "sha256": "8c6038cf697f804008c81614aed4e3fcd375ec418888466bcd74c7e01bb11f6c",
              "signature": "JiRnJEQYtf73Eul4P3CoDMKnnsw4ZCtDxwAJrMham1kDxZ2mUEZMwHx42TOos5WNQ/mNpS7G49FzuGxBe73MJW/CFdG0ll+oeOFm2GO+hdbw/Dx2vLOCCeHj3gmbTjYIucOmZVkb7gfYk1r9PLrT2VSKb6NJWgp0FxPr+Xr9Mn+PVjk12FjB4E753L/C7BYNfeARVIDnYi9Rtgtt0FwQGeDrQk4lZqMz+CGM4vBVn8HTxJreVYXOIcG+41GQQ0lb066ETePWOWv6sxErccISqjq509QM4alBGnorcNLBM76RHRTk9w/FriOWJrkZyGDfeS3VGHjuek7HvnxXdNH/vA=="
            }
          ]
        },
        "native": [
          {
            "rid": "win-x64",
            "bundleUrl": "https://github.com/HeribertG/Klacks.Api/releases/download/v1.0.79/klacks-1.0.79-win-x64.zip",
            "sha256": "0b7bb33a9febd88edf3720fefcda2092685a511062f78bfc5039649db0ec25e6",
            "signature": "jh9Liawy0mQBOv4dfcXLRcX4hbDAQSbr4rQjo0fMjqDdjG/PvQu8dYzvF3jOBpqbyqaXjN0gLL8FMGqRQMb7ztciGASCw7PyJ7d4HeVgCG1dckEukY0/iCN2QnxRXNW9BDvQ7ClNBA41Zdfe0FJWkoMNmUZZR1+XtHkA3TjDrGFsc2cz3OZwUSaXcaRTkl61ubUG41H8Glux//0ADPrXDT21/5cglLADXYpGYg0QxyAHm31oRwuKD0cm8p3jOFSQxJby952CsiXQaxlfkMiQoGojN96AJy/UDDhETtYCCRj9/ZgM0GhkhdZc+H8y8mRwo/BNUW+pyaLZH02dqO8Ccw=="
          }
        ]
      },
      "signature": "ZtyQ/wll0bFZw2airp9BfTB5QJqIhbQLBhdW5OUeFqZ9kpog+YvreVAB1I2h64SRTG7MEjVk3eQ8OeP2Gq7t5+Nf6PHiAzOU6aHo+aQeK3zLcPuUoprBY58F5bwXArKElk/OZMb+tCP7C1Y1Yb3Ua/Z2vIjsFGuPJw2n9sbiktDlaQjGmJGPrZCtCk2/+v67/65SJjTVncQrgHz6emsn7zxl8NmwnNFu2bWzTV6Q1UJ5xkWInqgoXe6TMldejkLiPwP4d4gLEsH2DJtOw2Yt0gAw3qAgLZ9ED5yxmCthgTEMdBSO+RZ40DTcmbDH0mN2dgx7v7coIBh3PbbvM/Nv+A=="
    }
    """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<UpdateManifest?> ReadAsync(string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(UpdateManifestReader.HttpClientName).Returns(new HttpClient(new StubHandler(body)));
        var options = Options.Create(new UpdateTrustOptions { ManifestBaseUrl = "https://updates.example.com" });
        var reader = new UpdateManifestReader(factory, options, NullLogger<UpdateManifestReader>.Instance);
        return await reader.GetManifestAsync(UpdateChannel.Stable);
    }

    [Test]
    public async Task Release1079_ParsesPostgresEngineBlock()
    {
        var manifest = await ReadAsync(Release1079Manifest);

        manifest.ShouldNotBeNull();
        manifest!.LatestVersion.ShouldBe("1.0.79");
        var engine = manifest.Artifacts.Postgres;
        engine.ShouldNotBeNull();
        engine!.Major.ShouldBe(19);
        engine.Version.ShouldBe("19.0");
        engine.Pgvector.ShouldBe("0.8.7");
        engine.Docker.ShouldNotBeNull();
        engine.Docker!.Image.ShouldBe("ghcr.io/heribertg/klacks-postgres:19");
        engine.Docker.Sha256.ShouldBe("sha256:981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2");
        engine.Docker.Signature.ShouldStartWith("I0XsF5o002DnxYmjw3RGDZx+");
        engine.Native.Count.ShouldBe(1);
        engine.Native[0].Rid.ShouldBe("win-x64");
        engine.Native[0].BundleUrl.ShouldBe(
            "https://github.com/HeribertG/Klacks.Api/releases/download/postgres-engine-19.0-pgvector0.8.7/klacks-postgres-19.0-win-x64.zip");
        engine.Native[0].Sha256.ShouldBe("8c6038cf697f804008c81614aed4e3fcd375ec418888466bcd74c7e01bb11f6c");
        engine.Native[0].Signature.ShouldStartWith("JiRnJEQYtf73Eul4P3CoDMKnnsw4ZCtD");
    }

    [Test]
    public async Task Release1079_KeepsApplicationArtifactsUnchanged()
    {
        var manifest = await ReadAsync(Release1079Manifest);

        manifest!.Artifacts.Docker!.ApiImage.ShouldBe("ghcr.io/heribertg/klacks-api:1.0.79");
        manifest.Artifacts.Native.Single().BundleUrl.ShouldBe(
            "https://github.com/HeribertG/Klacks.Api/releases/download/v1.0.79/klacks-1.0.79-win-x64.zip");
    }

    [Test]
    public async Task ManifestWithoutPostgresBlock_ParsesWithNullEngine()
    {
        const string json = """
        {
            "channel": "Stable",
            "latestVersion": "1.0.70",
            "minUpgradableFrom": "1.0.0",
            "artifacts": { "docker": { "apiImage": "ghcr.io/x/api:1.0.70", "uiImage": "ghcr.io/x/ui:1.0.70" } }
        }
        """;

        var manifest = await ReadAsync(json);

        manifest.ShouldNotBeNull();
        manifest!.Artifacts.Postgres.ShouldBeNull();
    }
}
