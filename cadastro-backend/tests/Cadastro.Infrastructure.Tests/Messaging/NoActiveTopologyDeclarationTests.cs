using System.Runtime.CompilerServices;

namespace Cadastro.Infrastructure.Tests.Messaging;

/// <summary>
/// O usuário <c>cadastro-app</c> do broker não tem permissão de configure (a topologia vem do definitions do ../rabbitmq):
/// o código de produção nunca pode declarar exchange, fila nem binding. Varre o código-fonte da mensageria.
/// </summary>
public sealed class NoActiveTopologyDeclarationTests
{
    private static readonly string[] Forbidden =
    [
        "QueueDeclareAsync", "ExchangeDeclareAsync", "QueueBindAsync", "ExchangeBindAsync", "QueueDeleteAsync", "ExchangeDeleteAsync",
        "QueueDeclare(", "ExchangeDeclare(", "QueueBind(",
    ];

    [Fact]
    public void MessagingSources_NeverDeclareTopology()
    {
        var root = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "..", "src", "Cadastro.Infrastructure"));
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var call in Forbidden)
            {
                text.ShouldNotContain(call, customMessage: $"{Path.GetFileName(file)} must not call {call}");
            }
        }
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
