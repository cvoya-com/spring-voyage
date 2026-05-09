// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Models;

using System.Text.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

public class PackageInstallModelsTests
{
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void PackageInstallRequest_ConnectorBindingsWithoutUnits_DeserializesWithNullUnitBindings()
    {
        var json = """
            {
              "targets": [
                {
                  "packageName": "software-agents",
                  "inputs": {},
                  "connectorBindings": {
                    "package": {
                      "github": {
                        "config": {
                          "bindingId": "binding-123"
                        }
                      }
                    }
                  }
                }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<PackageInstallRequest>(
            json,
            WebJsonOptions);

        request.ShouldNotBeNull();
        var target = request.Targets.ShouldHaveSingleItem();
        target.PackageName.ShouldBe("software-agents");
        target.ConnectorBindings.ShouldNotBeNull();
        target.ConnectorBindings.Units.ShouldBeNull();
        target.ConnectorBindings.Package.ShouldNotBeNull();
        var binding = target.ConnectorBindings.Package["github"];
        binding.Config.GetProperty("bindingId").GetString().ShouldBe("binding-123");
    }
}