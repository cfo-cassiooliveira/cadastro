using Cadastro.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure.Tests.Messaging;

/// <summary>Backoff com jitter, validação de opções e sonda de saúde do broker, sem I/O.</summary>
public sealed class OutboxBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 16000)]
    public void Next_WithoutJitterOffset_DoublesEachAttempt(int attempts, double expectedMs) =>
        OutboxBackoff.Next(attempts, Base, Max, 0.5).TotalMilliseconds.ShouldBe(expectedMs);

    [Fact]
    public void Next_IsCappedByTheMaximumBeforeJitter()
    {
        OutboxBackoff.Next(50, Base, Max, 0.5).ShouldBe(Max);
        OutboxBackoff.Next(int.MaxValue, Base, Max, 0.5).ShouldBe(Max);
    }

    [Fact]
    public void Next_JitterSpreadsBetweenHalfAndOneAndAHalfTimes()
    {
        OutboxBackoff.Next(3, Base, Max, 0).TotalMilliseconds.ShouldBe(4000);
        OutboxBackoff.Next(3, Base, Max, 0.999).TotalMilliseconds.ShouldBeInRange(11990, 12000);
    }

    [Fact]
    public void Next_NonPositiveAttempts_UsesTheBaseDelay() =>
        OutboxBackoff.Next(0, Base, Max, 0.5).ShouldBe(Base);

    [Fact]
    public async Task HealthCheck_IsDegradedOnlyAfterAConnectionProblem()
    {
        await using var publisher = new RabbitMqOutboxPublisher(
            Options.Create(new RabbitMqOptions { Host = "127.0.0.1", Port = 1, ConfirmTimeoutSeconds = 1 }), NullLogger<RabbitMqOutboxPublisher>.Instance);
        var check = new RabbitMqHealthCheck(publisher);

        (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);

        await publisher.PublishAsync(
            [new Cadastro.Application.UserImports.OutboundMessage(Guid.CreateVersion7(), "x", "{}", null, DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken);

        (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Degraded);
    }
}
