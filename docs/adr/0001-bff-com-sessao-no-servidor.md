# ADR-0001: BFF com sessão no servidor (em memória)

- **Status**: Proposto
- **Data**: 2026-11-13
- **Decisores**: Cassio Oliveira (responsável técnico)
- **Artigos da Constituição relacionados**: Art. II.1, Art. IV.2

## Contexto

O Cadastro guarda dados pessoais e o `security.md` define BFF como padrão para SPA: client confidencial, tokens no
servidor e cookie `__Host-` para o navegador. O projeto não tem banco próprio (ADR-0002).

## Decisão

1. `Cadastro.Api` é o BFF: OIDC Authorization Code + PKCE S256 com o client confidencial `cadastro`, cookie
   `__Host-cadastro` (`HttpOnly; Secure; SameSite=Strict`), sessão ociosa de 30 min, antiforgery por header.
2. Tokens guardados num `ITicketStore` **em memória** (`IMemoryCache`); o cookie leva só a chave da sessão.
3. Access token renovado pelo refresh token; falha na renovação encerra a sessão local. Logout pelo `end_session_endpoint`.

## Alternativas consideradas

| Alternativa | Prós | Contras | Motivo da rejeição |
|---|---|---|---|
| Tokens no cookie cifrado (`SaveTokens`) | Sem estado no servidor | Cookie grande (chunking); tokens saem do servidor | Diverge do "tokens no servidor" do `security.md` |
| Redis/SQL como store de sessão | Sobrevive a restart; escala | Componente novo sem necessidade em dev | YAGNI (Art. II.1) nesta fase |
| SPA como client público | Sem BFF | Tokens no navegador | Exige exceção; dados pessoais |

## Consequências

- **Positivas**: o navegador nunca vê token; sem componente novo.
- **Negativas / riscos**: restart da API encerra as sessões; uma instância só; `SameSite=Strict` pode exigir ajuste
  quando Keycloak e Cadastro estiverem em sites diferentes (produção).
- **Ações decorrentes**: rever este ADR (store distribuído e política de cookie) antes de qualquer ambiente além do local.
