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
/// concatenado, interpolado ou destruturado (<c>{@Obj}</c> vira <c>Obj_Campo</c>, que não é mascarado).</para>
/// <para>O sink GELF envia cada propriedade como campo próprio, além do texto renderizado (<c>message</c> e
/// <c>full_message</c>). O pipeline do Graylog reconhece o tipo pelo nome do campo, mascara o campo e troca o mesmo
/// valor dentro do texto, sem adivinhar. Nome fora dessa lista não tem máscara garantida (ADR-0002 do projeto graylog).</para>
/// </remarks>
internal static class LoggingSetup
{
    /// <summary>Nome do serviço em todos os logs (campo <c>service_name</c> usado pelos streams <c>Cadastro - *</c> do Graylog).</summary>
    public const string ServiceName = "cadastro-api";

    private const int DefaultGelfPort = 12201;
    private const int StackTraceDepth = 30;
    private const string MessageTemplateField = "message_template";

    /// <summary>Registra o Serilog com enriquecimento padrão (serviço, versão, ambiente, trace) e sinks configurados.</summary>
    public static IServiceCollection AddCadastroLogging(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment) =>
        services.AddSerilog((sp, logger) =>
        {
            logger.ReadFrom.Configuration(configuration)
                .ReadFrom.Services(sp)
                .Enrich.FromLogContext()
                .Enrich.With<TraceContextEnricher>()
                .Enrich.WithProperty("service_name", ServiceName)
                .Enrich.WithProperty("service_version", typeof(LoggingSetup).Assembly.GetName().Version?.ToString() ?? "unknown")
                .Enrich.WithProperty("environment", environment.EnvironmentName)
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
