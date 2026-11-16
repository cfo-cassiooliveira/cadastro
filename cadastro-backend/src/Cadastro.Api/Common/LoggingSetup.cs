using Cadastro.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.Graylog;
using Serilog.Sinks.Graylog.Core.Transport;

namespace Cadastro.Api.Common;

/// <summary>Serilog com logs estruturados em JSON no console e envio GELF opcional para o Graylog (mesmo padrão do Legado).</summary>
/// <remarks>
/// <para><b>Dados pessoais (LGPD)</b>: nenhum log contém dado pessoal (Constituição, Art. IV.6). Só um dado
/// <b>aprovado em ADR</b> vai ao log, e apenas como campo estruturado do message template com o nome do tipo:
/// <c>{Cpf}</c>, <c>{Cnpj}</c>, <c>{Phone}</c>/<c>{MobilePhone}</c>, <c>{UserName}</c> ou <c>{Email}</c>. Nunca
/// concatenado ou interpolado; destruturado (<c>{@Obj}</c>) também é tratado, mas não entra na pesquisa padrão.</para>
/// <para><b>Pseudonimização na origem</b> (ADR-0003 do projeto graylog): o <see cref="PseudonymizationEnricher"/>
/// roda depois dos demais enrichers e antes dos sinks. Cada campo cujo nome está em
/// <see cref="Cadastro.Application.LogSearch.PersonalDataFields"/> (as mesmas listas <c>LGPD_*</c> do graylog) vira a
/// máscara do ADR-0002 (<c>***.456.789-**</c>, <c>12.3**.***/***1-90</c>, <c>11*****4321</c>, <c>mar***</c>,
/// <c>ana***@gmail</c>) e ganha <c>&lt;Campo&gt;_token</c> (<c>k1-</c> + HMAC-SHA256 truncado, o mesmo do legado; e-mail
/// e login geram o mesmo token), também dentro de objetos destruturados, dicionários e listas (token no topo, fora do
/// texto: <c>Customer_Email_token</c>). Valor fora do formato, ou objeto/lista no lugar do valor, vira <c>***</c> sem
/// token. O texto (<c>message</c>/<c>full_message</c>) é renderizado depois, a partir das propriedades já trocadas, e o
/// texto já renderizado dos escopos (<c>Scope</c>) recebe a troca do valor pela máscara. <b>Não é tratado</b>: a
/// exceção (por isso nunca deve conter dado pessoal) e nomes fora das listas; o pipeline do Graylog continua como rede
/// de segurança.</para>
/// </remarks>
internal static class LoggingSetup
{
    /// <summary>Nome do serviço em todos os logs (campo <c>service_name</c> usado pelos streams <c>Cadastro - *</c> do Graylog).</summary>
    public const string ServiceName = "cadastro-api";

    private const int DefaultGelfPort = 12201;
    private const int StackTraceDepth = 30;
    private const string MessageTemplateField = "message_template";

    /// <summary>Registra o Serilog com enriquecimento padrão (serviço, versão, ambiente, trace) e sinks configurados.</summary>
    public static IServiceCollection AddCadastroLogging(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Chaves validadas na subida: sem chave atual válida a API não sobe (fail fast), em vez de logar sem pseudonimizar.
        services.AddLogPseudonymization(configuration);
        // Tipo concreto, não ILogEventEnricher: o ReadFrom.Services o rodaria antes do LogContext e dos demais enrichers.
        services.AddSingleton<PseudonymizationEnricher>();

        return services.AddSerilog((sp, logger) =>
        {
            logger.ReadFrom.Configuration(configuration)
                .ReadFrom.Services(sp)
                .Enrich.FromLogContext()
                .Enrich.With<TraceContextEnricher>()
                .Enrich.WithProperty("service_name", ServiceName)
                .Enrich.WithProperty("service_version", typeof(LoggingSetup).Assembly.GetName().Version?.ToString() ?? "unknown")
                .Enrich.WithProperty("environment", environment.EnvironmentName)
                // Por último: também trata as propriedades vindas do LogContext e dos enrichers acima.
                .Enrich.With(sp.GetRequiredService<PseudonymizationEnricher>())
                .WriteTo.Console(new CompactJsonFormatter());

            // Graylog é opcional: habilitado quando "Graylog:Host" está configurado (docker-compose.graylog.yml).
            if (BuildGraylogOptions(configuration) is { } graylog)
            {
                logger.WriteTo.Graylog(graylog);
            }
        },
        // Não usa nem substitui o Log.Logger estático: cada host usa só a própria instância (sem estado global,
        // Art. III.3); um host descartado não silencia os demais (ex.: as fábricas dos testes de integração).
        preserveStaticLogger: true);
    }

    /// <summary>
    /// Monta as opções do sink GELF a partir de <c>Graylog:*</c>; <c>null</c> quando o host não está configurado.
    /// TCP por padrão (não perde mensagens como o UDP), com o template da mensagem para agrupar eventos iguais.
    /// </summary>
    public static GraylogSinkOptions? BuildGraylogOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var host = configuration["Graylog:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return new GraylogSinkOptions
        {
            HostnameOrAddress = host,
            Port = configuration.GetValue("Graylog:Port", DefaultGelfPort),
            TransportType = configuration.GetValue("Graylog:Transport", TransportType.Tcp),
            Facility = ServiceName,
            IncludeMessageTemplate = true,
            MessageTemplateFieldName = MessageTemplateField,
            StackTraceDepth = StackTraceDepth,
            ParseArrayValues = true,
        };
    }
}
