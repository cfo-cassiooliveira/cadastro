using Cadastro.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Cadastro.Application;

/// <summary>Registro dos casos de uso da camada de aplicação.</summary>
public static class DependencyInjection
{
    /// <summary>Registra os handlers (CQRS leve, sem MediatR).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IGetMyProfileHandler, GetMyProfileHandler>();
        return services;
    }
}
