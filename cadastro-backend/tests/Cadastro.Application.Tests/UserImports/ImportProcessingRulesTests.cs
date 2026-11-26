using System.Text;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;

namespace Cadastro.Application.Tests.UserImports;

/// <summary>Regras puras do processamento: decisão de candidato, política de ciclos e leitura do envelope.</summary>
public sealed class ImportProcessingRulesTests
{
    private static LegacyUserCandidate Candidate(string email) => new(Guid.NewGuid(), email);

    [Fact]
    public void Decide_WithNoCandidates_IsNoCandidate() =>
        LegacyMarkDecision.Decide("a@b.com", []).ShouldBe(new LegacyMarkDecision(LegacyMarkStatus.NoCandidate, null));

    [Fact]
    public void Decide_WithOneExactCandidate_MarksIt()
    {
        var candidate = Candidate(" A@B.com ");

        var decision = LegacyMarkDecision.Decide("a@b.com", [candidate]);

        decision.Status.ShouldBe(LegacyMarkStatus.Marked);
        decision.Target.ShouldBe(candidate);
    }

    [Fact]
    public void Decide_WithOneCandidateOfAnotherEmail_IsNoCandidate() =>
        LegacyMarkDecision.Decide("a@b.com", [Candidate("c@d.com")]).Status.ShouldBe(LegacyMarkStatus.NoCandidate);

    [Fact]
    public void Decide_WithTwoExactCandidates_IsAmbiguous()
    {
        var decision = LegacyMarkDecision.Decide("a@b.com", [Candidate("a@b.com"), Candidate("A@B.COM")]);

        decision.Status.ShouldBe(LegacyMarkStatus.Ambiguous);
        decision.Target.ShouldBeNull();
    }

    [Fact]
    public void Decide_IgnoresInexactCandidatesWhenCountingExactOnes() =>
        LegacyMarkDecision.Decide("a@b.com", [Candidate("a@b.com"), Candidate("x@y.com")]).Status.ShouldBe(LegacyMarkStatus.Marked);

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void RetryPolicy_IsExhaustedAfterFiveCycles(int attempt, bool exhausted)
    {
        ImportRetryPolicy.IsExhausted(attempt).ShouldBe(exhausted);
        ImportRetryPolicy.NextAttempt(attempt).ShouldBe(attempt + 1);
    }

    [Fact]
    public void Envelope_RoundTripsThroughTheParser()
    {
        var (messageId, batchId, lineId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var json = EventEnvelope.CreateLineAccepted(messageId, batchId, lineId, "corr-12345678", null, DateTimeOffset.UtcNow);

        LineAcceptedMessage.TryParse(Encoding.UTF8.GetBytes(json), out var message).ShouldBeTrue();

        message.ShouldBe(new LineAcceptedMessage(messageId, batchId, lineId, "corr-12345678"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"other.v1\",\"messageId\":\"0195f1d2-7c3a-7b11-9a00-000000000001\",\"data\":{}}")]
    [InlineData("{\"type\":\"cadastro.user-import-line.accepted.v1\",\"messageId\":\"x\",\"data\":{}}")]
    [InlineData("{\"type\":\"cadastro.user-import-line.accepted.v1\",\"messageId\":\"0195f1d2-7c3a-7b11-9a00-000000000001\",\"data\":{\"batchId\":\"0195f1d2-7c3a-7b11-9a00-000000000001\"}}")]
    public void Envelope_InvalidBodies_AreRejected(string body)
    {
        LineAcceptedMessage.TryParse(Encoding.UTF8.GetBytes(body), out var message).ShouldBeFalse();
        message.ShouldBeNull();
    }
}
