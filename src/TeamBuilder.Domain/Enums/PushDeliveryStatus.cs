namespace TeamBuilder.Domain.Enums;

/// <summary>State of one push delivery. Persisted numerically and used in filtered indexes; never renumber.</summary>
public enum PushDeliveryStatus
{
    /// <summary>Waiting to be sent (new, or a retry after a transient failure).</summary>
    Pending = 1,

    /// <summary>Claimed by a dispatcher under a lease.</summary>
    Sending = 2,

    /// <summary>The push service accepted it (2xx). Terminal. Not proof the device showed it.</summary>
    Accepted = 3,

    /// <summary>Permanently failed (expired subscription, rejected, or retries exhausted). Terminal.</summary>
    Failed = 4,

    /// <summary>Not sent: the subscription was gone or inactive, or the alert expired before it could go. Terminal.</summary>
    Abandoned = 5
}
