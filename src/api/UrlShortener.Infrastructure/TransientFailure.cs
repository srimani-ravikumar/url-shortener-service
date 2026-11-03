using Cassandra;
using StackExchange.Redis;

namespace UrlShortener.Infrastructure;

/// <summary>Tells apart "storage is temporarily unavailable" from genuine bugs, so APIs can answer 503 instead of 500.</summary>
public static class TransientFailure
{
    /// <summary>
    /// True for failures that are likely to go away on retry: Redis or Cassandra unreachable, timing out,
    /// or not enough replicas. Query errors such as invalid CQL are not transient.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception is
        RedisException or
        TimeoutException or
        NoHostAvailableException or
        OperationTimedOutException or
        QueryExecutionException;
}
