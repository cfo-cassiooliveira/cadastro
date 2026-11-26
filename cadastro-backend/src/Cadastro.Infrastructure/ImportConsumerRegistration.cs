using System.Security.Cryptography.X509Certificates;
using Cadastro.Application;
using Cadastro.Application.UserImports;
using Cadastro.Infrastructure.Keycloak;
using Cadastro.Infrastructure.Legado;
using Cadastro.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Cadastro.Infrastructure;

/// <summary>Registro do consumidor das linhas da importação e dos clientes que ele usa (Keycloak e Legado).</summary>
internal static class ImportConsumerRegistration
{
    private const int MinSigningKeyBytes = 32;

    /// <summary>
    /// Registra o consumidor quando <c>ImportConsumer:Enabled</c>: cliente do Keycloak com o client <c>cadastro-importer</c>,
    /// cliente HTTPS/HMAC do Legado e o serviço em segundo plano. Desligado, nenhuma dessas configurações é exigida.
    /// </summary>
    public static IServiceCollection AddImportConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ImportConsumerOptions>().Bind(configuration.GetSection(ImportConsumerOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        if (!configuration.GetValue<bool>($"{ImportConsumerOptions.SectionName}:Enabled"))
        {
            return services;
        }

        services.AddOptions<RabbitMqOptions>().Validate(o => !string.IsNullOrWhiteSpace(o.Password), "RabbitMq:Password is required when the import consumer is enabled.");
        services.AddOptions<KeycloakImporterOptions>().Bind(configuration.GetSection(KeycloakImporterOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<LegadoOptions>().Bind(configuration.GetSection(LegadoOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.BaseUrl?.Scheme == Uri.UriSchemeHttps, "Legado:BaseUrl must use https.")
            .Validate(o => TryDecodeKey(o.SigningKey, out _), "Legado:SigningKey must be Base64 with at least 32 bytes.")
            .Validate(o => File.Exists(o.CaCertificatePath), "Legado:CaCertificatePath must point to the Legado CA file.")
            .ValidateOnStart();

        services.AddSingleton(sp => new CreationRateLimiter(
            sp.GetRequiredService<IOptions<KeycloakImporterOptions>>().Value.MaxCreationsPerSecond, sp.GetRequiredService<TimeProvider>()));
        services.AddKeyedSingleton(KeycloakUserProvisioner.TokenProviderKey, (sp, _) =>
        {
            var admin = sp.GetRequiredService<IOptions<KeycloakAdminOptions>>();
            var importer = sp.GetRequiredService<IOptions<KeycloakImporterOptions>>();
            return new KeycloakServiceTokenProvider(
                sp.GetRequiredService<IHttpClientFactory>(),
                () => new KeycloakServiceTokenProvider.ServiceCredentials(admin.Value.Realm, importer.Value.ClientId, importer.Value.ClientSecret),
                sp.GetRequiredService<TimeProvider>());
        });

        // Criar usuário e enviar e-mail são mais lentos que uma leitura: 10 s por tentativa e 30 s no total.
        services.AddHttpClient("keycloak-importer", DependencyInjection.ConfigureBaseAddress)
            .AddTypedClient<IIdentityUserProvisioner>((http, sp) => new KeycloakUserProvisioner(
                http,
                sp.GetRequiredKeyedService<KeycloakServiceTokenProvider>(KeycloakUserProvisioner.TokenProviderKey),
                sp.GetRequiredService<IOptions<KeycloakAdminOptions>>(),
                sp.GetRequiredService<CreationRateLimiter>()))
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        // A resiliência vem ANTES da assinatura: cada retry passa de novo pelo handler e ganha horário e nonce novos.
        var legadoClient = services.AddHttpClient<ILegacyUserDirectory, LegadoUserDirectory>((sp, client) =>
                client.BaseAddress = sp.GetRequiredService<IOptions<LegadoOptions>>().Value.BaseUrl)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var authority = X509CertificateLoader.LoadCertificateFromFile(sp.GetRequiredService<IOptions<LegadoOptions>>().Value.CaCertificatePath);
                return new SocketsHttpHandler
                {
                    SslOptions =
                    {
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                            LegadoTlsTrust.IsTrusted(certificate as X509Certificate2, errors, authority),
                    },
                };
            });
        legadoClient.AddStandardResilienceHandler(DependencyInjection.ConfigureResilience);
        legadoClient.AddHttpMessageHandler(sp =>
        {
            var legado = sp.GetRequiredService<IOptions<LegadoOptions>>().Value;
            TryDecodeKey(legado.SigningKey, out var key);
            return new LegadoSigningHandler(key, legado.KeyId, sp.GetRequiredService<TimeProvider>());
        });

        services.AddUserImportProcessing();
        services.AddSingleton<LineDeliveryProcessor>();
        services.AddHostedService<UserImportLineConsumerService>();
        return services;
    }

    private static bool TryDecodeKey(string value, out byte[] key)
    {
        key = [];
        Span<byte> buffer = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written < MinSigningKeyBytes)
        {
            return false;
        }

        key = buffer[..written].ToArray();
        return true;
    }
}
