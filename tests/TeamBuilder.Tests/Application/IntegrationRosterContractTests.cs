using System.Text.Json;
using FluentAssertions;
using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Keeps the published JSON Schema (docs/schemas/teambuilder-roster-v1.schema.json) and the
/// teambuilder.roster.v1 DTOs in step: every object the API serializes has exactly the
/// properties the schema requires, so a renamed or added field fails here first.
/// </summary>
public class IntegrationRosterContractTests
{
    [Fact]
    public void SerializedRoster_HasExactlyTheSchemasRequiredProperties()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath()));
        var defs = schema.RootElement.GetProperty("$defs");
        var roster = JsonSerializer.SerializeToElement(new IntegrationRosterDto
        {
            Venue = new IntegrationVenueDto(),
            Requirements = [new IntegrationRequirementDto()],
            Participants = [new IntegrationParticipantDto()]
        }, JsonSerializerOptions.Web);

        Names(roster).Should().BeEquivalentTo(Required(schema.RootElement));
        Names(roster.GetProperty("occurrence")).Should().BeEquivalentTo(Required(defs.GetProperty("occurrence")));
        Names(roster.GetProperty("venue")).Should().BeEquivalentTo(Required(defs.GetProperty("venue")));
        Names(roster.GetProperty("requirements")[0]).Should().BeEquivalentTo(Required(defs.GetProperty("requirement")));
        Names(roster.GetProperty("participants")[0]).Should().BeEquivalentTo(Required(defs.GetProperty("participant")));
        roster.GetProperty("schema").GetString().Should().Be(schema.RootElement.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString());
    }

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name);

    private static IEnumerable<string?> Required(JsonElement schema) => schema.GetProperty("required").EnumerateArray().Select(e => e.GetString());

    private static string SchemaPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "schemas", "teambuilder-roster-v1.schema.json");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("docs/schemas/teambuilder-roster-v1.schema.json not found above the test output directory.");
    }
}
