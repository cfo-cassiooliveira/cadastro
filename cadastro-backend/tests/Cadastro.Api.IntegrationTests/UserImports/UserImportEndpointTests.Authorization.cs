using System.Net;
using System.Net.Http.Json;
using Cadastro.Api.Auth;
using Cadastro.Api.IntegrationTests.Infrastructure;
using Cadastro.Domain.UserImports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Cadastro.Api.IntegrationTests.UserImports;

/// <summary>
/// Matriz de autorização leitura x escrita: leituras pedem role + sessão com <c>acr=otp</c> (sem limite de idade) e
/// escritas pedem role + OTP digitado há no máximo 5 min. Todo 403 é auditado uma vez.
/// </summary>
public sealed partial class UserImportEndpointTests
{
    private const string BasePath = "/api/v1/user-imports";
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    private static readonly string[] ReadPaths = ["", $"/{AnyId}", $"/{AnyId}/lines", $"/{AnyId}/report"];

    private static readonly string[] WritePaths =
        ["", $"/{AnyId}/confirm", $"/{AnyId}/cancel", $"/{AnyId}/lines/{AnyId}/reprocess", $"/{AnyId}/reprocess-errors"];

    private static IEnumerable<(string Method, string Path)> Routes =>
        ReadPaths.Select(path => ("GET", BasePath + path)).Concat(WritePaths.Select(path => ("POST", BasePath + path)));

    public static TheoryData<string, string> AllRoutes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in Routes)
        {
            data.Add(method, path);
        }

        return data;
    }

    // Sem a role (mesmo com OTP recente) e com a role mas sem OTP na sessão: nem leitura nem escrita.
    public static TheoryData<string, string, string, string?> DeniedEverywhere()
    {
        var data = new TheoryData<string, string, string, string?>();
        foreach (var (method, path) in Routes)
        {
            data.Add(method, path, "user", "otp");
            data.Add(method, path, "admin", "otp");
            data.Add(method, path, "user-importer", "password");
            data.Add(method, path, "user-importer", "0");
            data.Add(method, path, "user-importer", null);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Authorization_WithoutSession_Returns401(string method, string path)
    {
        await using var host = Host(out _, out _);
        using var client = Client(host, null);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(DeniedEverywhere))]
    public async Task Authorization_WithoutRoleOrOtpSession_Returns403AndWarnsTheDenialOnce(string method, string path, string role, string? acr)
    {
        await using var host = Host(out var store, out _);

        using var response = await SendAsync(
            host, new HttpMethod(method), new Uri(path, UriKind.Relative), identity: new TestIdentity(role, acr));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        store.Batches.ShouldBeEmpty();
        ShouldHaveWarnedOneDenial(host);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(36_000)]
    public async Task Authorization_ReadsWithAnOldOtpSession_Return200WithoutAudit(int ageSeconds)
    {
        await using var host = Host(out _, out _);
        var batch = await CreateBatchAsync(host);
        var identity = new TestIdentity(AuthAgeSeconds: ageSeconds);

        foreach (var suffix in new[] { "", $"/{batch.BatchId}", $"/{batch.BatchId}/lines", $"/{batch.BatchId}/report" })
        {
            using var response = await SendAsync(host, HttpMethod.Get, new Uri(BasePath + suffix, UriKind.Relative), identity: identity);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: suffix);
        }

        AuditEvents(host).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/confirm")]
    [InlineData("/cancel")]
    [InlineData("/lines/{lineId}/reprocess")]
    [InlineData("/reprocess-errors")]
    public async Task Authorization_WritesWithAnOldOtpSession_Return403ChangeNothingAndWarnTheDenialOnce(string action)
    {
        await using var host = Host(out var store, out _);
        var batch = await CreateBatchAsync(host);
        var stale = new TestIdentity(AuthAgeSeconds: 360);
        var suffix = action.Length == 0
            ? ""
            : $"/{batch.BatchId}" + action.Replace("{lineId}", store.Lines[0].Id.ToString(), StringComparison.Ordinal);
        using var content = action.Length == 0
            ? Multipart(Header + "Outra Pessoa,outra@example.com,52998224725,\n")
            : (HttpContent)JsonContent.Create(new { summaryToken = batch.SummaryToken });

        using var response = await SendAsync(host, HttpMethod.Post, new Uri(BasePath + suffix, UriKind.Relative), content, stale);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        store.Batches.Single().Status.ShouldBe(ImportBatchStatus.AwaitingConfirmation);
        store.Events.ShouldBeEmpty();
        ShouldHaveWarnedOneDenial(host);
    }

    // Os códigos de sucesso das escritas com OTP recente são cobertos pelos testes de cada ação; aqui, a regra de montagem.
    [Fact]
    public async Task Authorization_EveryEndpoint_ReadsUseOnlyTheReadPolicyAndWritesRequireRecentOtp()
    {
        await using var host = Host(out _, out _);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith(BasePath, StringComparison.Ordinal))
            .ToList();

        endpoints.Count.ShouldBe(ReadPaths.Length + WritePaths.Length);
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();
            string[] expected = methods.Single() == "GET"
                ? [BffAuthenticationSetup.UserImportsReadPolicy]
                : [BffAuthenticationSetup.UserImportsReadPolicy, BffAuthenticationSetup.UserImportsPolicy];

            policies.ShouldBe(expected.Cast<string?>(), customMessage: endpoint.DisplayName);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(endpoint.DisplayName);
        }
    }

    private void ShouldHaveWarnedOneDenial(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var denied = AuditEvents(host).ShouldHaveSingleItem();
        denied.Level.ShouldBe(LogEventLevel.Warning);
        CollectingLogSink.Scalar(denied.Properties["audit_event"]).ShouldBe("user_import_access_denied");
        denied.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldNotContain("example.com");
    }
}
