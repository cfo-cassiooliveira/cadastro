using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Opções do broker (<c>RabbitMq:*</c>). O usuário <c>cadastro-app</c> não tem permissão de configure: o código nunca
/// declara exchange, fila ou binding (a topologia vem do definitions do ../rabbitmq). A senha só vem de ambiente/user-secrets.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>Liga o relay da outbox; desligado, as mensagens ficam pendentes na outbox.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Host do broker na rede <c>workspace-messaging</c>.</summary>
    [Required]
    public string Host { get; set; } = "rabbitmq";

    /// <summary>Porta AMQP.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    /// <summary>Virtual host do ambiente.</summary>
    [Required]
    public string VirtualHost { get; set; } = "dev";

    /// <summary>Usuário da aplicação.</summary>
    [Required]
    public string UserName { get; set; } = "cadastro-app";

    /// <summary>Senha do usuário (<c>RABBITMQ_CADASTRO_APP_PASSWORD</c>); nunca versionada.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Exchange de eventos.</summary>
    [Required]
    public string Exchange { get; set; } = "cadastro.events";

    /// <summary>Nome da conexão na UI de management.</summary>
    [Required]
    public string ClientProvidedName { get; set; } = "cadastro-api";

    /// <summary>Tempo máximo para o broker confirmar uma mensagem, em segundos.</summary>
    [Range(1, 300)]
    public int ConfirmTimeoutSeconds { get; set; } = 10;

    /// <summary>Intervalo da recuperação automática de rede da conexão, em segundos.</summary>
    [Range(1, 3600)]
    public int NetworkRecoveryIntervalSeconds { get; set; } = 5;

    /// <summary>Depois de quantos segundos de conexão fechada o publicador descarta a conexão e cria outra.</summary>
    [Range(1, 3600)]
    public int ConnectionRecreateAfterSeconds { get; set; } = 60;

    /// <summary>Mensagens lidas da outbox por ciclo do relay.</summary>
    [Range(1, 1000)]
    public int RelayBatchSize { get; set; } = 100;

    /// <summary>Espera entre ciclos quando não há mensagens pendentes, em milissegundos.</summary>
    [Range(10, 600000)]
    public int RelayIdleDelayMs { get; set; } = 1000;

    /// <summary>Base do backoff exponencial por mensagem, em milissegundos.</summary>
    [Range(1, 600000)]
    public int BackoffBaseMs { get; set; } = 2000;

    /// <summary>Teto do backoff por mensagem, em milissegundos.</summary>
    [Range(1, 3600000)]
    public int BackoffMaxMs { get; set; } = 300000;
}
