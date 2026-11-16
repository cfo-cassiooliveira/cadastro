using Cadastro.Api.ClientLogs;
using Cadastro.Application.LogSearch;
using Cadastro.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace Cadastro.Api.IntegrationTests.Logging;

/// <summary>
/// Reconhecimento pelo formato em texto livre (client-logs): mesmos padrões do estágio 4 do Graylog e máscara do
/// ADR-0002 vinda da porta <see cref="ILogPseudonymizer"/>, sem token.
/// </summary>
public sealed class PersonalDataTextMaskerTests
{
    // Chave dos vetores (bytes 00 01 02 … 1f): pública por definição, só para os testes.
    private const string VectorKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static readonly LogPseudonymizer Pseudonymizer = new(Options.Create(new PseudonymizationOptions
    {
        CurrentKeyId = "k1",
        Keys = { ["k1"] = VectorKey },
    }));

    [Theory]
    [InlineData("para Ana.Souza@Gmail.com agora", "para ana***@gmail agora")]
    [InlineData("/users/ana.souza%40gmail.com", "/users/ana***@gmail")]
    [InlineData("bia@x.com", "b***@x")]
    [InlineData("cnpj 12.345.678/0001-90.", "cnpj 12.3**.***/***1-90.")]
    [InlineData("cpf 123.456.789-09", "cpf ***.456.789-**")]
    [InlineData("cpf 12345678909", "cpf ***.456.789-**")]
    [InlineData("tel +55 (11) 98765-4321", "tel 11*****4321")]
    [InlineData("tel 11 98765-4321", "tel 11*****4321")]
    public void Mask_WithPersonalDataInText_ReplacesItWithAdr0002Mask(string text, string expected) =>
        PersonalDataTextMasker.Mask(Pseudonymizer, text).ShouldBe(expected);

    [Theory]
    [InlineData("HTTP 500 Internal Server Error")]
    [InlineData("at Cadastro.Api.Users.RegisterUser (main.js:120:15)")]
    [InlineData("pedido 98765-4321")] // Celular sem DDD fica de fora (ADR-0003 do graylog, item 9).
    [InlineData("ana***@gmail e ***.456.789-**")] // Máscara já aplicada não é mascarada de novo.
    public void Mask_WithoutPersonalData_KeepsText(string text) =>
        PersonalDataTextMasker.Mask(Pseudonymizer, text).ShouldBe(text);
}
