using System.Security.Cryptography;
using System.Text;
using Cadastro.Application;
using Cadastro.Application.UserImports;
using Cadastro.Domain.UserImports;
using Cadastro.Infrastructure.Messaging;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Tests.Messaging;
using Cadastro.Infrastructure.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cadastro.Infrastructure.Tests.UserImports;

/// <summary>Legado falso do teste: devolve candidatos configurados e registra as marcações pedidas.</summary>
internal sealed class FakeLegacyDirectory : ILegacyUserDirectory
{
    private readonly List<(Guid LegacyUserId, string KeycloakUserId)> _marks = [];

    /// <summary>Candidatos devolvidos pela consulta.</summary>
    public Func<string, IReadOnlyList<LegacyUserCandidate>> Candidates { get; set; } = _ => [];

    /// <summary>Quando verdadeiro, a consulta falha como API fora do ar.</summary>
    public bool FailLookup { get; set; }

    /// <summary>Quando verdadeiro, a marcação falha como API fora do ar.</summary>
    public bool FailMark { get; set; }

    /// <summary>Marcações recebidas.</summary>
    public IReadOnlyList<(Guid LegacyUserId, string KeycloakUserId)> Marks
    {
        get
        {
            lock (_marks)
            {
                return [.. _marks];
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LegacyUserCandidate>> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        FailLookup ? throw new LegacyDirectoryUnavailableException("lookup down") : Task.FromResult(Candidates(email));

    /// <inheritdoc />
    public Task MarkMigratedAsync(Guid legacyUserId, string keycloakUserId, CancellationToken cancellationToken)
    {
        if (FailMark)
        {
            throw new LegacyDirectoryUnavailableException("mark down");
        }

        lock (_marks)
        {
            _marks.Add((legacyUserId, keycloakUserId));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Decorator que injeta falhas antes de delegar ao provisionador real do Keycloak.</summary>
internal sealed class FaultInjectingProvisioner(IIdentityUserProvisioner inner, Func<ProvisionUserRequest, Exception?> fault) : IIdentityUserProvisioner
{
    /// <inheritdoc />
    public Task<ProvisionedUser> ProvisionAsync(ProvisionUserRequest request, CancellationToken cancellationToken) =>
        fault(request) is { } exception ? throw exception : inner.ProvisionAsync(request, cancellationToken);
}

/// <summary>Decorator que injeta falhas (ex.: banco fora) antes de delegar ao caso de uso real "Encerrar linha com erro".</summary>
internal sealed class FaultInjectingFailLineHandler(IFailUserImportLineHandler inner, Func<Exception?> fault) : IFailUserImportLineHandler
{
    /// <inheritdoc />
    public Task<LineProcessingResult> HandleAsync(FailUserImportLineCommand command, CancellationToken cancellationToken) =>
        fault() is { } exception ? throw exception : inner.HandleAsync(command, cancellationToken);
}

/// <summary>Provisionador instantâneo, para estressar a concorrência sem o custo do Keycloak.</summary>
internal sealed class InstantProvisioner : IIdentityUserProvisioner
{
    /// <inheritdoc />
    public Task<ProvisionedUser> ProvisionAsync(ProvisionUserRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProvisionedUser(Guid.NewGuid().ToString("D"), ProvisionOutcome.Created));
}

/// <summary>
/// Sobe o consumidor de verdade (DI de produção, usuário RabbitMQ sem configure) contra SQL Server, RabbitMQ e Keycloak reais;
/// só o Legado (e, por teste, falhas do provisionador) é substituído.
/// </summary>
internal sealed class ConsumerHarness : IAsyncDisposable
{
    private static readonly string DataKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly ServiceProvider _provider;
    private readonly IHostedService _consumer;
    private readonly ImportDatabaseFixture _database;
    private readonly RabbitMqFixture _broker;

    private ConsumerHarness(ServiceProvider provider, IHostedService consumer, ImportDatabaseFixture database, RabbitMqFixture broker, FakeLegacyDirectory legacy)
    {
        _provider = provider;
        _consumer = consumer;
        _database = database;
        _broker = broker;
        Legacy = legacy;
    }

    /// <summary>Legado falso usado pelo consumidor.</summary>
    public FakeLegacyDirectory Legacy { get; }

    /// <summary>Monta e inicia o consumidor.</summary>
    public static async Task<ConsumerHarness> StartAsync(
        ImportDatabaseFixture database,
        RabbitMqFixture broker,
        KeycloakMailFixture keycloak,
        Func<ProvisionUserRequest, Exception?>? fault = null,
        bool instantIdentity = false,
        Func<Exception?>? failLineFault = null)
    {
        await database.MigrateAsync();
        await broker.PurgeAsync();
        var caFile = typeof(ConsumerHarness).Assembly.Location;
        // Com pool, como em produção. O fixture desliga o pool, e abrir conexão e login a cada comando derrubava a vazão do consumidor
        // a ~2 linhas/s: as 60 linhas do teste de paralelismo passavam dos 90 s de espera quando a máquina estava carregada.
        var connectionString = new SqlConnectionStringBuilder(database.AppConnectionString) { Pooling = true }.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ImportDb"] = connectionString,
            ["PersonalData:Protection:CurrentKeyVersion"] = "k1",
            ["PersonalData:Protection:Keys:k1"] = DataKey,
            ["UserImports:SummaryTokenKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["RabbitMq:Enabled"] = "false",
            ["KeycloakAdmin:BaseUrl"] = keycloak.KeycloakUrl + "/",
            ["KeycloakAdmin:Realm"] = "workspace",
            ["KeycloakAdmin:ClientId"] = "cadastro",
            ["KeycloakAdmin:ClientSecret"] = "unused-in-this-test",
            ["KeycloakImporter:ClientSecret"] = KeycloakMailFixture.ImporterSecret,
            ["ImportConsumer:Enabled"] = "true",
            ["ImportConsumer:Prefetch"] = "10",
            ["ImportConsumer:ReconnectDelaySeconds"] = "1",
            ["Legado:BaseUrl"] = "https://legado.test/",
            ["Legado:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Legado:CaCertificatePath"] = caFile,
        }).Build();

        var legacy = new FakeLegacyDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, ConsoleProvider>();
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddImportPersistence(configuration);
        // O usuário do broker é o cadastro-app do fixture (configure vazio): se o consumidor tentasse declarar algo, falharia.
        var brokerOptions = broker.Options();
        services.PostConfigure<RabbitMqOptions>(o =>
        {
            o.Host = brokerOptions.Host;
            o.Port = brokerOptions.Port;
            o.VirtualHost = brokerOptions.VirtualHost;
            o.UserName = brokerOptions.UserName;
            o.Password = brokerOptions.Password;
        });
        services.AddSingleton<ILegacyUserDirectory>(legacy);
        if (instantIdentity)
        {
            services.AddTransient<IIdentityUserProvisioner, InstantProvisioner>();
        }
        else if (fault is not null)
        {
            var original = services.Last(d => d.ServiceType == typeof(IIdentityUserProvisioner));
            services.Remove(original);
            services.AddTransient<IIdentityUserProvisioner>(sp =>
                new FaultInjectingProvisioner((IIdentityUserProvisioner)original.ImplementationFactory!(sp), fault));
        }

        if (failLineFault is not null)
        {
            var original = services.Last(d => d.ServiceType == typeof(IFailUserImportLineHandler));
            services.Remove(original);
            services.AddScoped<IFailUserImportLineHandler>(sp => new FaultInjectingFailLineHandler(
                (IFailUserImportLineHandler)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), failLineFault));
        }

        var provider = services.BuildServiceProvider();
        var consumer = provider.GetServices<IHostedService>().Single(s => s is UserImportLineConsumerService);
        await consumer.StartAsync(TestContext.Current.CancellationToken);
        return new ConsumerHarness(provider, consumer, database, broker, legacy);
    }

    /// <summary>Cria um lote confirmado com as pessoas e publica uma mensagem por linha; devolve ids do lote, das linhas e das mensagens.</summary>
    public async Task<SeededBatch> SeedAndPublishAsync(IReadOnlyList<(string Name, string Email, string? Phone)> people)
    {
        var protector = _provider.GetRequiredService<IPersonalDataProtector>();
        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.CreateVersion7();
        var lines = new List<ImportLine>();
        for (var i = 0; i < people.Count; i++)
        {
            var lineId = Guid.CreateVersion7();
            var (name, email, phone) = people[i];
            var data = new ProtectedPersonalData(
                protector.Protect(name, ImportLineProtection.Context(lineId, ImportLineProtection.NameField)),
                protector.Protect(email, ImportLineProtection.Context(lineId, ImportLineProtection.EmailField)),
                protector.Protect("52998224725", ImportLineProtection.Context(lineId, ImportLineProtection.CpfField)),
                phone is null ? null : protector.Protect(phone, ImportLineProtection.Context(lineId, ImportLineProtection.PhoneField)),
                protector.ComputeEmailIndex(email),
                protector.CurrentKeyVersion);
            lines.Add(ImportLine.Create(lineId, batchId, i + 1, data, now));
        }

        var fileHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(batchId.ToString())));
        var batch = ImportBatch.Create(batchId, fileHash, "admin-test", lines.Count, 0, 0, now);
        batch.Confirm(now);
        await using (var db = ImportDatabaseFixture.CreateContext(_database.AppConnectionString))
        {
            db.ImportBatches.Add(batch);
            db.ImportLines.AddRange(lines);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var messageIds = new List<Guid>();
        foreach (var line in lines)
        {
            var messageId = Guid.CreateVersion7();
            messageIds.Add(messageId);
            await _broker.PublishLineAcceptedAsync(
                EventEnvelope.CreateLineAccepted(messageId, batchId, line.Id, "corr-test-0001", null, now), messageId);
        }

        return new SeededBatch(batchId, [.. lines.Select(l => l.Id)], messageIds);
    }

    /// <summary>Publica de novo a mesma mensagem (entrega duplicada, mesmo <c>messageId</c> e mesmo corpo).</summary>
    public Task RepublishAsync(SeededBatch batch, int index) =>
        _broker.PublishLineAcceptedAsync(
            EventEnvelope.CreateLineAccepted(batch.MessageIds[index], batch.BatchId, batch.LineIds[index], "corr-test-0001", null, DateTimeOffset.UtcNow),
            batch.MessageIds[index]);

    /// <summary>Espera o lote sair de <c>Processing</c> (ou a condição ser verdadeira) e devolve o estado lido do banco.</summary>
    public async Task<BatchState> WaitForBatchAsync(Guid batchId, Func<BatchState, bool>? until = null, int timeoutSeconds = 90)
    {
        until ??= state => state.Batch.Status != ImportBatchStatus.Processing;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var state = await ReadAsync(batchId);
            if (until(state))
            {
                return state;
            }

            (DateTime.UtcNow < deadline).ShouldBeTrue($"batch did not reach the expected state; now {state.Batch.Status}, {state.Batch.FinishedLines} finished");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Lê lote, linhas e a contagem da inbox direto do banco.</summary>
    public async Task<BatchState> ReadAsync(Guid batchId)
    {
        await using var db = ImportDatabaseFixture.CreateContext(_database.AppConnectionString);
        var batch = await db.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId, TestContext.Current.CancellationToken);
        var lines = await db.ImportLines.AsNoTracking().Where(l => l.BatchId == batchId).OrderBy(l => l.LineNumber).ToListAsync(TestContext.Current.CancellationToken);
        return new BatchState(batch, lines);
    }

    /// <summary>Quantas mensagens da inbox têm estes ids.</summary>
    public async Task<int> InboxCountAsync(IEnumerable<Guid> messageIds)
    {
        var ids = messageIds.ToList();
        await using var db = ImportDatabaseFixture.CreateContext(_database.AppConnectionString);
        return await db.InboxMessages.CountAsync(m => ids.Contains(m.MessageId), TestContext.Current.CancellationToken);
    }

    /// <summary>Espera a DLQ ter ao menos a quantidade de mensagens.</summary>
    public async Task WaitForDeadLettersAsync(uint count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await _broker.MessageCountAsync(RabbitMqFixture.DlqQueue) < count)
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("dead-letter queue did not receive the message");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _consumer.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}

/// <summary>Lote semeado e publicado pelo teste.</summary>
/// <param name="BatchId">Lote.</param>
/// <param name="LineIds">Linhas, na ordem das pessoas.</param>
/// <param name="MessageIds">Mensagens publicadas, na mesma ordem.</param>
internal sealed record SeededBatch(Guid BatchId, IReadOnlyList<Guid> LineIds, IReadOnlyList<Guid> MessageIds);

/// <summary>Estado do lote e das linhas lido do banco.</summary>
/// <param name="Batch">Lote.</param>
/// <param name="Lines">Linhas por número.</param>
internal sealed record BatchState(ImportBatch Batch, IReadOnlyList<ImportLine> Lines);

/// <summary>Logger mínimo que escreve no console do teste (diagnóstico do consumidor).</summary>
internal sealed class ConsoleProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    /// <inheritdoc />
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class ConsoleLogger(string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Console.WriteLine($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
