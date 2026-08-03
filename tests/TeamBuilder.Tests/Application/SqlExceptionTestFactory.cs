using System.Reflection;
using Microsoft.Data.SqlClient;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Microsoft.Data.SqlClient's SqlException/SqlError/SqlErrorCollection constructors are
/// internal, so real duplicate-key errors can't be constructed directly in tests. This
/// helper uses reflection (test-only, never shipped in production code) to build a
/// representative SqlException carrying a specific error number and message, so
/// TeamMembershipConflictClassifier can be exercised against a realistic exception shape.
/// </summary>
internal static class SqlExceptionTestFactory
{
    public static SqlException Create(int number, string message)
    {
        var errorCtor = typeof(SqlError).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types:
            [
                typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string),
                typeof(int), typeof(int), typeof(Exception)
            ],
            modifiers: null)
            ?? throw new InvalidOperationException("Unable to locate the SqlError constructor via reflection.");

        var sqlError = errorCtor.Invoke([number, (byte)0, (byte)0, "server", message, "procedure", 0, 0, null]);

        var errorCollection = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)
            ?? throw new InvalidOperationException("Unable to construct a SqlErrorCollection via reflection.");

        var addMethod = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to locate the SqlErrorCollection.Add method via reflection.");
        addMethod.Invoke(errorCollection, [sqlError]);

        var exceptionCtor = typeof(SqlException).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(string), typeof(SqlErrorCollection), typeof(Exception), typeof(Guid)],
            modifiers: null)
            ?? throw new InvalidOperationException("Unable to locate the SqlException constructor via reflection.");

        return (SqlException)exceptionCtor.Invoke([message, errorCollection, null, Guid.NewGuid()]);
    }
}
