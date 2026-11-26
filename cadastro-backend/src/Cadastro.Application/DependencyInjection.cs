using Cadastro.Application.LogSearch;
using Cadastro.Application.UserImports;
using Cadastro.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Cadastro.Application;

/// <summary>Registro dos casos de uso da camada de aplicação.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra os casos de uso do consumidor das linhas (C5a-C5c). Separado de <see cref="AddApplication"/> porque exigem as portas
    /// de identidade e do Legado, que só existem quando o consumidor está ligado.
    /// </summary>
    public static IServiceCollection AddUserImportProcessing(this IServiceCollection services)
    {
        services.AddScoped<UserImportLineCompletion>();
        services.AddScoped<IProcessUserImportLineHandler, ProcessUserImportLineHandler>();
        services.AddScoped<IFailUserImportLineHandler, FailUserImportLineHandler>();
        return services;
    }

    /// <summary>Registra os handlers (CQRS leve, sem MediatR).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IGetMyProfileHandler, GetMyProfileHandler>();
        services.AddScoped<IGetLogSearchQueryHandler, GetLogSearchQueryHandler>();

        // Importação em lote (spec 002): comandos e consultas.
        services.AddSingleton<IUserExtractor, CsvUserExtractor>();
        services.AddScoped<IUploadUserImportHandler, UploadUserImportHandler>();
        services.AddScoped<IConfirmUserImportHandler, ConfirmUserImportHandler>();
        services.AddScoped<ICancelUserImportHandler, CancelUserImportHandler>();
        services.AddScoped<IReprocessUserImportLineHandler, ReprocessUserImportLineHandler>();
        services.AddScoped<IReprocessUserImportErrorsHandler, ReprocessUserImportErrorsHandler>();
        services.AddScoped<IListUserImportsHandler, ListUserImportsHandler>();
        services.AddScoped<IGetUserImportHandler, GetUserImportHandler>();
        services.AddScoped<IListUserImportLinesHandler, ListUserImportLinesHandler>();
        services.AddScoped<IGetUserImportReportHandler, GetUserImportReportHandler>();
        return services;
    }
}
