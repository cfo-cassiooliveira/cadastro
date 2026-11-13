namespace Cadastro.Application.Users;

/// <summary>Perfil do usuário da sessão, como guardado no User Profile do Keycloak (ADR-0002).</summary>
/// <param name="UserName">Login.</param>
/// <param name="FirstName">Primeiro nome.</param>
/// <param name="LastName">Sobrenome.</param>
/// <param name="Email">E-mail.</param>
/// <param name="MobilePhone">Celular em E.164, se informado.</param>
/// <param name="Address">Endereço, ou <c>null</c> quando não foi informado (ex.: usuário criado no Admin Console).</param>
public sealed record MyProfile(
    string UserName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? MobilePhone,
    ProfileAddress? Address);

/// <summary>Endereço residencial do perfil.</summary>
/// <param name="PostalCode">CEP com 8 dígitos.</param>
/// <param name="Street">Logradouro.</param>
/// <param name="Number">Número.</param>
/// <param name="Complement">Complemento opcional.</param>
/// <param name="District">Bairro.</param>
/// <param name="City">Município.</param>
/// <param name="State">Sigla da UF.</param>
public sealed record ProfileAddress(
    string PostalCode,
    string Street,
    string Number,
    string? Complement,
    string District,
    string City,
    string State);
