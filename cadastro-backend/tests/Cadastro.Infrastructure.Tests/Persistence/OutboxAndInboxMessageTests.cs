using Cadastro.Infrastructure.Persistence;

namespace Cadastro.Infrastructure.Tests.Persistence;

/// <summary>Regras locais da outbox e da inbox, sem banco.</summary>
public sealed class OutboxAndInboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-3));

    private static OutboxMessage NewMessage() =>
        OutboxMessage.Create(Guid.CreateVersion7(), "cadastro.user-import-line.accepted.v1", "{}", null, Now);

    [Fact]
    public void Create_StartsPendingWithUtcDate()
    {
        var message = NewMessage();

        message.PublishedAtUtc.ShouldBeNull();
        message.Attempts.ShouldBe(0);
        message.OccurredAtUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("type", " ")]
    public void Create_WithoutTypeOrPayload_Throws(string type, string payload) =>
        Should.Throw<ArgumentException>(() => OutboxMessage.Create(Guid.CreateVersion7(), type, payload, null, Now));

    [Fact]
    public void MarkPublished_SetsDateCountsAttemptAndClearsError()
    {
        var message = NewMessage();
        message.MarkAttemptFailed("broker.unavailable", Now);

        message.MarkPublished(Now.AddSeconds(1));

        message.PublishedAtUtc.ShouldBe(Now.AddSeconds(1));
        message.Attempts.ShouldBe(2);
        message.LastErrorCode.ShouldBeNull();
    }

    [Fact]
    public void MarkPublished_Twice_ThrowsAndKeepsFirstDate()
    {
        var message = NewMessage();
        message.MarkPublished(Now);

        Should.Throw<InvalidOperationException>(() => message.MarkPublished(Now.AddMinutes(1)));
        message.PublishedAtUtc.ShouldBe(Now);
        message.Attempts.ShouldBe(1);
    }

    [Fact]
    public void MarkAttemptFailed_WithoutCode_Throws() =>
        Should.Throw<ArgumentException>(() => NewMessage().MarkAttemptFailed(" ", Now));

    [Fact]
    public void Inbox_Create_KeepsIdAndUtcDate()
    {
        var id = Guid.CreateVersion7();

        var inbox = InboxMessage.Create(id, Now);

        inbox.MessageId.ShouldBe(id);
        inbox.ProcessedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
    }
}
