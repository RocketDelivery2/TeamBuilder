using Microsoft.Data.SqlClient;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Raw-SQL access to the Teams table as it existed BEFORE the
/// SeparateTeamLifecycleFromRecruitment migration (persisted int Status, no LifecycleStatus /
/// IsAcceptingMembers). Historical migration tests use this instead of the current EF model,
/// which no longer matches that schema.
/// </summary>
internal static class LegacyTeamsTable
{
    internal sealed record LegacyTeamRow(Guid Id, string Name, int Status, int MaxMembers, int CurrentMemberCount, Guid? OwnerId);

    public static async Task<Guid> InsertAsync(
        string connectionString,
        int maxMembers,
        int storedCount = 0,
        TeamStatus status = TeamStatus.Recruiting,
        Guid? ownerId = null,
        string? name = null) =>
        await InsertRawStatusAsync(connectionString, maxMembers, storedCount, (int)status, ownerId, name);

    public static async Task<Guid> InsertRawStatusAsync(
        string connectionString,
        int maxMembers,
        int storedCount,
        int status,
        Guid? ownerId = null,
        string? name = null)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [Teams] ([Id], [Name], [Status], [MaxMembers], [CurrentMemberCount], [OwnerId], [CreatedAtUtc])
            VALUES (@id, @name, @status, @max, @count, @owner, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name ?? $"team_{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@max", maxMembers);
        command.Parameters.AddWithValue("@count", storedCount);
        command.Parameters.AddWithValue("@owner", (object?)ownerId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<LegacyTeamRow> GetAsync(string connectionString, Guid id)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [Id], [Name], [Status], [MaxMembers], [CurrentMemberCount], [OwnerId]
            FROM [Teams] WHERE [Id] = @id;
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Team {id} not found.");

        return new LegacyTeamRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5));
    }
}
