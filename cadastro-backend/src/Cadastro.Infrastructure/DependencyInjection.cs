using Cadastro.Application.LogSearch;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Cadastro.Infrastructure.Antivirus;
using Cadastro.Infrastructure.Keycloak;
using Cadastro.Infrastructure.Logging;
using Cadastro.Infrastructure.Messaging;
using Cadastro.Infrastructure.Persistence;
using Cadastro.Infrastructure.Protection;
using Cadastro.Infrastructure.UserImports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure;

/// <summary>Registro dos adaptadores de infraestrutura (Keycloak e pseudonimização dos logs).</summary>
public static class DependencyInjection
{
    /// <summary>Nome da connection string do runtime (<c>ConnectionStrings:ImportDb</c>, login <c>cadastro_app</c>).</summary>
    public const string ImportDbConnectionName = "ImportDb";

    // Uma tentativa lenta não pode segurar a requisição do usuário: 3 s por tentativa, 10 s no total (Art. VII.1).
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registra o acesso à Admin API do Keycloak com timeout, retry de falhas transitórias e circuit breaker.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(KeycloakServiceTokenProvider.HttpClientName, ConfigureBaseAddress)
            .AddStandardResilienceHandler(ConfigureResilience);
        // O provedor guarda o token em cache: uma instância para a aplicação inteira.
        services.AddSingleton<KeycloakServiceTokenProvider>();
        services.AddHttpClient<IIdentityUserReadService, KeycloakAdminClient>(ConfigureBaseAddress)
            .AddStandardResilienceHandler(ConfigureResilience);
        return services;
    }

    /// <summary>
    /// Registra a persistência do esquema <c>import</c> (login <c>cadastro_app</c>, connection string <c>ImportDb</c>) e a
    /// cifra AES-256-GCM dos dados pessoais. Sem connection string ou sem a chave atual a API não sobe (fail fast, Art. IV.4).
    /// </summary>
    public static IServiceCollection AddImportPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ImportDbConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{ImportDbConnectionName}' is not configured.");
        }

        services.AddDbContext<CadastroDbContext>(options => options.UseSqlServer(connectionString, CadastroDbContext.ConfigureSqlServer));
        services.AddOptions<PersonalDataProtectionOptions>()
            .Bind(configuration.GetSection(PersonalDataProtectionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PersonalDataProtectionOptions>, PersonalDataProtectionOptionsValidator>());
        services.TryAddSingleton<IPersonalDataProtector, AesGcmPersonalDataProtector>();
        services.AddScoped<IUserImportWriter, UserImportWriter>();
        services.AddScoped<IUserImportReadService, UserImportReadService>();
        services.AddScoped<IImportRetentionStore, ImportRetentionStore>();
        services.AddOptions<ImportRetentionOptions>().Bind(configuration.GetSection(ImportRetentionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        if (configuration.GetValue("ImportRetention:Enabled", true))
        {
            services.AddHostedService<ImportRetentionService>();
        }

        services.AddOptions<ImportSummaryOptions>().Bind(configuration.GetSection(ImportSummaryOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ImportSummaryOptions>, ImportSummaryOptionsValidator>());
        services.TryAddSingleton<IImportSummarySigner>(sp =>
        {
            ImportSummaryOptionsValidator.TryDecode(sp.GetRequiredService<IOptions<ImportSummaryOptions>>().Value.SummaryTokenKey, out var key);
            return new HmacImportSummarySigner(key);
        });
        services.AddImportAntivirus(configuration);
        services.AddImportMessaging(configuration);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Registra o cliente do ClamAV (clamd por INSTREAM, fail-closed) atrás da porta <see cref="IVirusScanner"/>.</summary>
    private static IServiceCollection AddImportAntivirus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClamAvOptions>().Bind(configuration.GetSection(ClamAvOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton<IVirusScanner, ClamdVirusScanner>();
        return services;
    }

    /// <summary>
    /// Registra o publicador RabbitMQ (conexão preguiçosa, singleton) e, com <c>RabbitMq:Enabled</c>, o relay da outbox em segundo plano.
    /// </summary>
    private static IServiceCollection AddImportMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<RabbitMqOutboxPublisher>();
        services.AddSingleton<IOutboxPublisher>(sp => sp.GetRequiredService<RabbitMqOutboxPublisher>());
        if (configuration.GetValue("RabbitMq:Enabled", true))
        {
            // Sem senha o relay não consegue autenticar: falha na subida em vez de acumular erros em silêncio.
            services.AddOptions<RabbitMqOptions>().Validate(o => !string.IsNullOrWhiteSpace(o.Password), "RabbitMq:Password is required when the relay is enabled.");
            services.AddScoped<OutboxRelay>();
            services.AddHostedService<OutboxRelayService>();
        }

        services.AddImportConsumer(configuration);

        return services;
    }

    /// <summary>
    /// Registra a pseudonimização dos logs (ADR-0003 do graylog) com as chaves de <c>Logging:Pseudonymization</c>,
    /// validadas na subida: sem chave atual válida a API não sobe (fail fast), em vez de logar sem pseudonimizar.
    /// </summary>
    public static IServiceCollection AddLogPseudonymization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PseudonymizationOptions>()
            .Bind(configuration.GetSection(PseudonymizationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PseudonymizationOptions>, PseudonymizationOptionsValidator>());
        services.TryAddSingleton<ILogPseudonymizer, LogPseudonymizer>();
        return services;
    }

    /// <summary>
    /// Readiness do Keycloak (discovery do realm, timeout de 3 s), com as tags que o composition root define
    /// (a Api não conhece a sonda, só esta extensão).
    /// </summary>
    public static IHealthChecksBuilder AddKeycloakHealthCheck(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpClient(KeycloakHealthCheck.HttpClientName, (services, client) =>
        {
            ConfigureBaseAddress(services, client);
            client.Timeout = AttemptTimeout;
        });
        return builder.AddCheck<KeycloakHealthCheck>("keycloak", HealthStatus.Unhealthy, tags);
    }

    /// <summary>
    /// Sonda do broker (publicador conectado) com as tags do composition root. Não é obrigatória: falha como <c>Degraded</c>.
    /// </summary>
    public static IHealthChecksBuilder AddBrokerHealthCheck(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<RabbitMqHealthCheck>("rabbitmq", HealthStatus.Degraded, tags);
    }

    internal static void ConfigureBaseAddress(IServiceProvider services, HttpClient client) =>
        client.BaseAddress = services.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.BaseUrl;

    internal static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalTimeout;
        options.CircuitBreaker.SamplingDuration = AttemptTimeout * 2 + TimeSpan.FromSeconds(1);
    }
}
