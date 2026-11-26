using System.ComponentModel.DataAnnotations;

namespace Cadastro.Infrastructure.Messaging;

/// <summary>
/// Opções do consumidor das linhas da importação (<c>ImportConsumer:*</c>). Host, vhost e credenciais vêm de
/// <see cref="RabbitMqOptions"/>. O consumidor só lê a fila e publica em <c>cadastro.retry</c>: nunca declara topologia.
/// </summary>
public sealed class ImportConsumerOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "ImportConsumer";

    /// <summary>Liga o consumidor em segundo plano (desligado por padrão; o compose de importação liga).</summary>
    public bool Enabled { get; set; }

    /// <summary>Fila de linhas aceitas (quorum, criada pelo definitions do ../rabbitmq).</summary>
    [Required]
    public string Queue { get; set; } = "cadastro.user-import-lines";

    /// <summary>Exchange de retentativa (a fila <c>.retry</c> tem TTL de 30 s e devolve à fila principal).</summary>
    [Required]
    public string RetryExchange { get; set; } = "cadastro.retry";

    /// <summary>Mensagens sem ack por vez (prefetch) e também o paralelismo de processamento.</summary>
    [Range(1, 1000)]
    public int Prefetch { get; set; } = 10;

    /// <summary>Nome da conexão na UI de management.</summary>
    [Required]
    public string ClientProvidedName { get; set; } = "cadastro-consumer";

    /// <summary>Espera antes de reconectar depois de uma queda, em segundos.</summary>
    [Range(1, 3600)]
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Quantas vezes seguidas a publicação na retry (ou o ack) pode falhar antes de encerrar a linha como erro e ir à DLQ.</summary>
    [Range(1, 4)]
    public int MaxRepublishFailures { get; set; } = 3;

    /// <summary>Base do atraso (com jitter) entre as reentregas quando a retry não pôde ser publicada, em milissegundos.</summary>
    [Range(1, 60000)]
    public int RepublishBackoffMs { get; set; } = 1000;

    /// <summary>Tempo máximo para o broker confirmar a publicação na fila de retry, em segundos.</summary>
    [Range(1, 300)]
    public int RetryConfirmTimeoutSeconds { get; set; } = 10;
}
