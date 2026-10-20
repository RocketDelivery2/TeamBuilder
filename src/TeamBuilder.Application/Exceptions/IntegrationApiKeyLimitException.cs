namespace TeamBuilder.Application.Exceptions;

/// <summary>The player already holds the maximum number of active integration API keys. Mapped to 409.</summary>
public sealed class IntegrationApiKeyLimitException : InvalidOperationException
{
    public const string Code = "ApiKeyLimitReached";

    public IntegrationApiKeyLimitException(int limit)
        : base($"You already have {limit} active API keys. Revoke one before creating another.")
    {
    }
}
