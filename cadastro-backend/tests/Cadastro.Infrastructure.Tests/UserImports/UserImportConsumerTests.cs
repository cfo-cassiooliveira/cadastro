using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Tests.Messaging;
using Cadastro.Infrastructure.Tests.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>
/// Consumidor das linhas (C5a-C5c) de ponta a ponta: RabbitMQ real (usuário sem configure), Keycloak real com Mailpit e SQL Server
/// real. Só o Legado é falso (a API HTTP do Legado tem os próprios testes).
/// </summary>
[Collection(ImportDatabaseSharing.Name)]
public sealed class UserImportConsumerTests(ImportDatabaseFixture database, RabbitMqFixture broker, KeycloakMailFixture keycloak)
{
    private const int ActivationLifespanSeconds = 72 * 3600;

    private static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@example.test";

    private Task<ConsumerHarness> StartAsync(
        Func<ProvisionUserRequest, Exception?>? fault = null, bool instantIdentity = false, Func<Exception?>? failLineFault = null) =>
        ConsumerHarness.StartAsync(database, broker, keycloak, fault, instantIdentity, failLineFault);

    [Fact]
    public async Task Consumer_CreatesUserWithoutPasswordWithUpdatePasswordAndSendsActivationEmailValidFor72Hours()
    {
        var email = NewEmail("ana");
        await using var harness = await StartAsync();

        var seeded = await harness.SeedAndPublishAsync([("Ana Souza", email, "(11) 99999-0000")]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        state.Lines.Select(l => l.FailureReason).ShouldAllBe(reason => reason == null);
        state.Batch.Status.ShouldBe(ImportBatchStatus.Completed);
        var line = state.Lines.Single();
        line.Status.ShouldBe(ImportLineStatus.Created);
        line.HasPersonalData.ShouldBeFalse();
        var users = await keycloak.FindUsersByEmailAsync(email);
        users.GetArrayLength().ShouldBe(1);
        var user = users[0];
        user.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        user.GetProperty("firstName").GetString().ShouldBe("Ana");
        user.GetProperty("lastName").GetString().ShouldBe("Souza");
        user.GetProperty("requiredActions")[0].GetString().ShouldBe("UPDATE_PASSWORD");
        user.GetProperty("attributes").GetProperty("importLineId")[0].GetString().ShouldBe(line.Id.ToString("D"));
        user.GetProperty("attributes").GetProperty("mobilePhone")[0].GetString().ShouldBe("+5511999990000");
        (await keycloak.CredentialCountAsync(user.GetProperty("id").GetString()!)).ShouldBe(0);
        (await keycloak.ActivationLinkLifespanSecondsAsync(email)).ShouldBe(ActivationLifespanSeconds);
        (await harness.InboxCountAsync(seeded.MessageIds)).ShouldBe(1);
    }

    [Fact]
    public async Task Consumer_WhenPersonAlreadyExistsByEmail_MarksAlreadyExistsWithoutDuplicatingOrEmailing()
    {
        var email = NewEmail("beto");
        await keycloak.CreateUserAsync($"pre.{Guid.NewGuid():N}"[..20], email);
        await using var harness = await StartAsync();

        var seeded = await harness.SeedAndPublishAsync([("Beto Lima", email, null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        state.Lines.Single().Status.ShouldBe(ImportLineStatus.AlreadyExists);
        state.Batch.AlreadyExistsCount.ShouldBe(1);
        (await keycloak.FindUsersByEmailAsync(email)).GetArrayLength().ShouldBe(1);
        (await keycloak.MailCountAsync(email)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_WhenSameMessageIsDeliveredTwice_AppliesTheEffectOnce()
    {
        var email = NewEmail("carla");
        await using var harness = await StartAsync();

        var seeded = await harness.SeedAndPublishAsync([("Carla Dias", email, null), ("Outra Pessoa", NewEmail("outra"), null)]);
        await harness.WaitForBatchAsync(seeded.BatchId);
        await harness.RepublishAsync(seeded, 0);
        await harness.RepublishAsync(seeded, 0);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var state = await harness.ReadAsync(seeded.BatchId);
        state.Batch.CreatedCount.ShouldBe(2);
        (await keycloak.FindUsersByEmailAsync(email)).GetArrayLength().ShouldBe(1);
        (await keycloak.MailCountAsync(email)).ShouldBe(1);
        (await harness.InboxCountAsync(seeded.MessageIds)).ShouldBe(2);
        (await broker.MessageCountAsync(RabbitMqFixture.DlqQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Consumer_AfterTransientFailures_RetriesThroughTheRetryQueueAndSucceeds()
    {
        var email = NewEmail("dora");
        var failures = 0;
        await using var harness = await StartAsync(_ => Interlocked.Increment(ref failures) <= 2 ? new IdentityProviderUnavailableException("503") : null);

        var seeded = await harness.SeedAndPublishAsync([("Dora Reis", email, null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        state.Lines.Single().Status.ShouldBe(ImportLineStatus.Created);
        failures.ShouldBe(3);
        (await keycloak.FindUsersByEmailAsync(email)).GetArrayLength().ShouldBe(1);
        (await broker.MessageCountAsync(RabbitMqFixture.DlqQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Consumer_WhenTransientFailureNeverEnds_ExhaustsFiveCyclesThenDeadLettersAndFailsTheLine()
    {
        var attempts = 0;
        await using var harness = await StartAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return new IdentityProviderUnavailableException("503");
        });

        var seeded = await harness.SeedAndPublishAsync([("Eva Costa", NewEmail("eva"), null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId, timeoutSeconds: 120);
        await harness.WaitForDeadLettersAsync(1);

        attempts.ShouldBe(ImportRetryPolicy.MaxCycles + 1);
        state.Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        var line = state.Lines.Single();
        line.Status.ShouldBe(ImportLineStatus.Error);
        line.FailureReason.ShouldBe(ImportFailureCodes.RetriesExhausted);
        line.HasPersonalData.ShouldBeTrue();
    }

    // Regressão: esgotados os ciclos com o banco fora, a mensagem ia à DLQ sem marcar a linha (Pending para sempre, lote em Processing).
    [Fact]
    public async Task Consumer_WhenTheLineCannotBeMarkedAfterTheLastCycle_KeepsTheMessageAliveUntilTheDatabaseIsBack()
    {
        const int Outages = 3;
        var processAttempts = 0;
        var markAttempts = 0;
        await using var harness = await StartAsync(
            _ =>
            {
                Interlocked.Increment(ref processAttempts);
                return new InvalidOperationException("unexpected failure");
            },
            failLineFault: () => Interlocked.Increment(ref markAttempts) <= Outages ? new InvalidOperationException("database down") : null);

        var seeded = await harness.SeedAndPublishAsync([("Ivo Nunes", NewEmail("ivo"), null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId, timeoutSeconds: 120);
        await harness.WaitForDeadLettersAsync(1);

        // 1 entrega + 5 ciclos processam; as voltas seguintes só tentam encerrar a linha, sem reprocessar.
        processAttempts.ShouldBe(ImportRetryPolicy.MaxCycles + 1);
        markAttempts.ShouldBe(Outages + 1);
        state.Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        var line = state.Lines.Single();
        line.Status.ShouldBe(ImportLineStatus.Error);
        line.FailureReason.ShouldBe(ImportFailureCodes.RetriesExhausted);
        (await broker.MessageCountAsync(RabbitMqFixture.DlqQueue)).ShouldBe(1u);
        (await broker.MessageCountAsync(RabbitMqFixture.RetryQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Consumer_WhenKeycloakRejectsTheUser_DeadLettersAtOnceAndTheBatchStillCompletes()
    {
        var goodEmail = NewEmail("fabio");
        await using var harness = await StartAsync();

        // Primeiro nome de 101 caracteres passa pelo upload (nome até 200) mas excede o limite de 100 do User Profile: o Keycloak devolve 400.
        var seeded = await harness.SeedAndPublishAsync([(new string('A', 101) + " Silva", NewEmail("ruim"), null), ("Fabio Alves", goodEmail, null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);
        await harness.WaitForDeadLettersAsync(1);

        state.Batch.Status.ShouldBe(ImportBatchStatus.CompletedWithErrors);
        state.Lines[0].Status.ShouldBe(ImportLineStatus.Error);
        state.Lines[0].FailureReason.ShouldBe(ImportFailureCodes.IdentityRejected);
        state.Lines[1].Status.ShouldBe(ImportLineStatus.Created);
        (await broker.MessageCountAsync(RabbitMqFixture.RetryQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Consumer_WhenLegacyApiIsDown_KeepsTheKeycloakUserAndRecordsFailedMark()
    {
        var email = NewEmail("gil");
        await using var harness = await StartAsync();
        harness.Legacy.FailLookup = true;

        var seeded = await harness.SeedAndPublishAsync([("Gil Prado", email, null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        var line = state.Lines.Single();
        line.Status.ShouldBe(ImportLineStatus.Created);
        line.LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Failed);
        (await keycloak.FindUsersByEmailAsync(email)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Consumer_WithOneTwoOrNoLegacyCandidates_MarksOnlyTheSingleExactOne()
    {
        var single = NewEmail("um");
        var several = NewEmail("varios");
        var none = NewEmail("nenhum");
        var legacyId = Guid.NewGuid();
        await using var harness = await StartAsync();
        harness.Legacy.Candidates = email =>
            email == single ? [new LegacyUserCandidate(legacyId, single)]
            : email == several ? [new LegacyUserCandidate(Guid.NewGuid(), several), new LegacyUserCandidate(Guid.NewGuid(), several)]
            : [];

        var seeded = await harness.SeedAndPublishAsync([("Um Candidato", single, null), ("Varios Candidatos", several, null), ("Nenhum Candidato", none, null)]);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        state.Lines[0].LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Marked);
        state.Lines[1].LegacyMarkStatus.ShouldBe(LegacyMarkStatus.Ambiguous);
        state.Lines[2].LegacyMarkStatus.ShouldBe(LegacyMarkStatus.NoCandidate);
        state.Lines.ShouldAllBe(l => l.Status == ImportLineStatus.Created);
        var mark = harness.Legacy.Marks.Single();
        mark.LegacyUserId.ShouldBe(legacyId);
        var keycloakId = (await keycloak.FindUsersByEmailAsync(single))[0].GetProperty("id").GetString();
        mark.KeycloakUserId.ShouldBe(keycloakId);
    }

    [Fact]
    public async Task Consumer_WithManyLinesInParallel_FinishesEachOnceDespiteBatchConcurrency()
    {
        await using var harness = await StartAsync(instantIdentity: true);
        var people = Enumerable.Range(0, 60).Select(i => ($"Pessoa {i}", NewEmail($"p{i}"), (string?)null)).ToList();

        var seeded = await harness.SeedAndPublishAsync(people);
        var state = await harness.WaitForBatchAsync(seeded.BatchId);

        state.Batch.Status.ShouldBe(ImportBatchStatus.Completed);
        state.Batch.CreatedCount.ShouldBe(60);
        state.Lines.ShouldAllBe(l => l.Status == ImportLineStatus.Created);
        (await harness.InboxCountAsync(seeded.MessageIds)).ShouldBe(60);
    }

    [Fact]
    public async Task RestrictedBrokerUser_CannotDeclareTopologyOrPublishToTheDeadLetterExchange()
    {
        await using var connection = await broker.ConnectAsRestrictedAppAsync();
        await using var declareChannel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Should.ThrowAsync<OperationInterruptedException>(() => declareChannel.QueueDeclareAsync(
            "cadastro.rogue", durable: true, exclusive: false, autoDelete: false, cancellationToken: TestContext.Current.CancellationToken));

        await using var publishChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<Exception>(() => publishChannel.BasicPublishAsync(
            RabbitMqFixture.DlxExchange, "#", mandatory: true, new BasicProperties(), new byte[] { 1 }, TestContext.Current.CancellationToken).AsTask());
    }
}
