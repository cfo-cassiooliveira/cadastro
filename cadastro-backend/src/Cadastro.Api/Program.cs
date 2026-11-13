using Cadastro.Api.Auth;
using Cadastro.Api.ClientLogs;
using Cadastro.Api.Common;
using Cadastro.Api.Health;
using Cadastro.Api.Users;
using Cadastro.Application;
using Cadastro.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Não revela o servidor/versão no header "Server" (OWASP A02).
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCadastroHealthChecks(builder.Configuration);
builder.Services.AddBffAuthentication(builder.Configuration);
builder.Services.AddCadastroRateLimiting(builder.Configuration);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // A API só é alcançável pela rede interna do Docker, atrás do nginx (porta não publicada).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
if (app.Configuration.GetValue<bool>("Bff:LocalSecureContext"))
{
    // Só no ambiente local (compose): o navegador trata http://localhost como contexto seguro e aceita os cookies
    // __Host-/Secure; o BFF passa a ver a requisição como segura, senão o antiforgery recusa gravar o cookie Secure.
    // Fora do local o BFF fica atrás de TLS e esta opção não existe.
    app.Use((context, next) =>
    {
        context.Request.Scheme = Uri.UriSchemeHttps;
        return next(context);
    });
}
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ApiResponseHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    // OpenAPI só em desenvolvimento (OWASP A02); anônimo porque a política padrão nega tudo.
    app.MapOpenApi().AllowAnonymous();
}

app.MapCadastroHealthChecks();
app.MapBffEndpoints();
app.MapUserEndpoints();
app.MapClientLogEndpoints();

await app.RunAsync();
