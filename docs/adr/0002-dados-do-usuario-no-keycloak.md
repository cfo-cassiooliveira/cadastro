# ADR-0002: Dados do usuário no User Profile do Keycloak, sem banco próprio

- **Status**: Proposto
- **Data**: 2026-11-13
- **Decisores**: Cassio Oliveira (responsável técnico)
- **Artigos da Constituição relacionados**: Art. II.1, Art. III.4, Art. IV.1

## Contexto

O Cadastro tem as mesmas funcionalidades do Legado (cadastro, perfil, listagem), e a migração leva os dados do
usuário (nome, celular, endereço) para dentro do Keycloak junto com a senha.

## Decisão

1. Nome, e-mail, celular, endereço e `legacyUserId` são atributos do **User Profile declarativo** do realm `workspace`,
   com validação e permissões por atributo (edição só por admin/conta de serviço; `legacyUserId` oculto ao usuário).
2. O Cadastro lê e grava pela **Admin API** com a conta de serviço do client `cadastro` (`view-users`, `query-users`,
   `manage-users`). O Cadastro **não tem banco**.

## Alternativas consideradas

| Alternativa | Prós | Contras | Motivo da rejeição |
|---|---|---|---|
| Banco próprio do Cadastro com perfil por `sub` | Consultas livres; Keycloak só com identidade | Banco, migrator e migração em dois lugares | Mais componentes sem ganho nesta fase |
| Dados no token (claims) | Sem chamada extra | Dado pessoal no token | Viola a minimização (LGPD) |

## Consequências

- **Positivas**: uma única fonte do usuário; migração num passo só; nenhuma infraestrutura de banco no Cadastro.
- **Negativas / riscos**: listagem e busca limitadas à Admin API; `manage-users` é amplo (mitigado: só a API do
  Cadastro tem o segredo, e não há rota que repasse operações arbitrárias).
- **Ações decorrentes**: User Profile no `realm-workspace.json` e no `provision.sh`; reavaliar se surgirem dados de
  negócio que não sejam de identidade (aí, banco próprio).
