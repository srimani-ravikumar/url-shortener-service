using Cassandra;
using StackExchange.Redis;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Tests which failures are treated as "storage temporarily unavailable" (answered with 503).</summary>
public class TransientFailureTests
{
    private static IEnumerable<TestCaseData> TransientFailures()
    {
        yield return new TestCaseData(new TimeoutException()).SetName("TimeoutException");
        yield return new TestCaseData(new RedisException("Redis is unavailable")).SetName("RedisException");
        yield return new TestCaseData(new NoHostAvailableException(new Dictionary<System.Net.IPEndPoint, Exception>())).SetName("NoHostAvailableException");
        yield return new TestCaseData(new OperationTimedOutException(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9042), 1000)).SetName("OperationTimedOutException");
    }

    [TestCaseSource(nameof(TransientFailures))]
    public void IsTransient_IsTrue_ForUnavailableStorage(Exception exception)
    {
        Assert.That(TransientFailure.IsTransient(exception), Is.True);
    }

    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(ArgumentException))]
    [TestCase(typeof(NullReferenceException))]
    public void IsTransient_IsFalse_ForBugs(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.That(TransientFailure.IsTransient(exception), Is.False);
    }

    [Test]
    public void IsTransient_IsFalse_ForAnInvalidQuery()
    {
        Assert.That(TransientFailure.IsTransient(new InvalidQueryException("bad cql")), Is.False);
    }
}
