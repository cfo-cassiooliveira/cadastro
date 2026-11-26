# ADR-0010: OTP recente só nas ações da importação em lote

- **Status**: Aceito (decisão aprovada pelo responsável técnico em 2026-11-26)
- **Data**: 2026-11-26
- **Decisores**: Cassio Oliveira (responsável técnico)
- **Artigos da Constituição relacionados**: Art. IV.2, Art. IV.3, Art. IV.6
- **Relacionados**: ADR-0001 (BFF), contrato de step-up do `keycloak`

## Contexto

A policy `user-imports:manage` (role `user-importer` + `acr=otp` com `auth_time` de no máximo 5 minutos) valia para todo o
`/api/v1/user-imports`. Acompanhar um lote de vários minutos pedia o código do autenticador a cada 5 minutos, embora as consultas não
devolvam dado pessoal.

## Decisão

1. Consultas (GET) exigem `user-imports:read`: role `user-importer` e sessão com `acr=otp`, sem limite de idade do `auth_time`.
2. Toda escrita (POST e qualquer outra que surja) exige também `user-imports:manage`: OTP digitado há no máximo 5 minutos.
3. Um GET que passe a devolver dado pessoal volta a exigir `user-imports:manage`.
4. Como a leitura passa a depender de uma sessão longa, a renovação de tokens do BFF atualiza as roles da sessão a partir do access
   token novo (assinatura, emissor, validade, client e `sub` conferidos; falha encerra a sessão). O refresh não concede `acr=otp` nem
   adianta o `auth_time`. Vale para todas as roles e áreas do BFF.
   **Rotação de chave no realm**: chave desconhecida faz o BFF buscar discovery e JWKS aguardando o resultado (o
   `ConfigurationManager` só atualiza em segundo plano e derrubaria quem acabou de gastar o refresh token), no máximo uma busca a
   cada 30 s. O switch global `Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking` não é usado.
5. O 403 das duas policies continua auditado (`user_import_access_denied`).

## Alternativas consideradas

| Alternativa | Prós | Contras | Motivo da rejeição |
|---|---|---|---|
| Manter OTP recente em tudo | Mais restritivo | Código a cada 5 minutos só para acompanhar | Atrito sem dado pessoal a proteger |
| Leituras só com a role, sem `acr=otp` | Menos pedidos de código | Sessão sem segundo fator veria os lotes | Perde o segundo fator sem necessidade |
| Aumentar o limite de 5 minutos | Mudança pequena | Afrouxa também as ações | Enfraquece o que cria contas |

## Consequências

- **Positivas**: acompanhamento sem interrupção; ações continuam pedindo o código recente; role retirada no Keycloak deixa de valer na
  renovação seguinte.
- **Negativas / riscos**: quem tomar uma sessão aberta lê os lotes (sem dado pessoal) até ela acabar (30 minutos de inatividade, no
  máximo a sessão do Keycloak); a retirada de uma role leva até a validade do access token (10 minutos no realm `workspace`).
- **Rotação de chave**: uma segunda rotação menos de 30 s depois de uma busca forçada encerra as sessões renovadas nesse intervalo
  (pedem login de novo); falha na busca também encerra (falha fechada).
- **Ações decorrentes**: nenhuma migration. A primeira consulta de uma sessão ainda pede o código uma vez (o login comum emite
  `acr=password`).
