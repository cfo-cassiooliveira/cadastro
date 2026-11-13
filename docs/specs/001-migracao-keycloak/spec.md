# Spec 001 — Cadastro com Keycloak e migração sob demanda do Legado

- **Status**: Em revisão
- **Data**: 2026-11-13 · **Autor**: Cassio Oliveira
- **Projeto/Bounded context**: `cadastro` (novo) · `keycloak` (Identity) · `legado` (Users legado)

## 1. Problema e objetivo

O `legado` autentica com login próprio (ASP.NET Core Identity + JWT HS256), uma exceção temporária à Constituição
(Art. IV.2, ADR-0001 do legado). Precisamos de um sistema novo, **Cadastro**, com as mesmas funcionalidades
(autocadastro, perfil e listagem administrativa), mas autenticado **exclusivamente pelo Keycloak**, e de um caminho
para levar os usuários do Legado para o Keycloak **sem obrigar ninguém a redefinir a senha** e sem migração em lote.

A migração é **sob demanda**: na primeira vez em que o usuário do Legado entra (ou pede reset de senha) pelo
Keycloak, o Keycloak pergunta ao Legado se as credenciais são válidas e, se forem, cria o usuário com a senha e os
dados. A partir daí ele é gerido só pelo Keycloak.

**Métricas de sucesso**

- 100% dos usuários do Legado que entram pelo Keycloak com a senha correta são migrados no mesmo login, sem reset.
- 0 contas duplicadas (mesma pessoa no Keycloak e ativa no Legado depois da migração).
- 0 e-mails ou logins completos nos logs (verificado por teste e por busca no Graylog).
- 100% das tentativas inválidas de login/reset de usuário desconhecido rastreáveis por identificador mascarado.
- Login de usuário já migrado com p95 igual ao do Keycloak sem extensão (a SPI não chama o Legado nesse caso).

## 2. Usuários e permissões

| Perfil | O que pode fazer nesta feature |
|---|---|
| Visitante | Criar conta no Cadastro; pedir reset de senha |
| Usuário do Legado (não migrado) | Entrar no Cadastro com login/e-mail e senha do Legado (é migrado nesse momento); pedir reset de senha |
| Usuário (`user`) | Entrar e sair; ver o próprio perfil |
| Administrador (`admin`) | Tudo do usuário + listagem paginada de usuários; OTP obrigatório |
| Segurança/Operação | Consultar no Graylog as migrações e as tentativas inválidas (identificador mascarado) |

## 3. Histórias de usuário e critérios de aceite

### US-01 — Login de quem já está no Keycloak
**Como** usuário do Keycloak, **quero** entrar no Cadastro com meu login/e-mail e senha, **para** usar o sistema.

```gherkin
Cenário: login com senha correta
  Dado um usuário existente no Keycloak com senha definida
  Quando ele informa login (ou e-mail) e senha corretos na tela de login
  Então entra no Cadastro e vê "Meu perfil"
  E o Legado não é consultado

Cenário: senha errada de usuário do Keycloak
  Dado um usuário existente no Keycloak com senha definida
  Quando ele informa a senha errada
  Então vê "Usuário ou senha inválidos."
  E o Legado não é consultado
  E a proteção contra força bruta do Keycloak contabiliza a falha

Cenário: administrador sem OTP configurado
  Dado um usuário com a role "admin" sem OTP
  Quando ele informa login e senha corretos
  Então o Keycloak exige configurar o OTP antes de concluir o login
```

### US-02 — Primeiro login de um usuário do Legado (migração transparente)
**Como** usuário do Legado, **quero** entrar no Cadastro com a senha que já uso, **para** não precisar criar outra conta.

```gherkin
Cenário: credenciais válidas e senha dentro da política nova
  Dado "ana.souza" existe só no Legado com a senha "Senha-Forte-2026!"
  Quando ela entra no Cadastro com "ana.souza@gmail.com" e essa senha
  Então o Keycloak pergunta ao Legado, que responde "válido"
  E o usuário é criado no Keycloak com login, e-mail, nome, celular, endereço e roles do Legado
  E a senha passa a ser guardada pelo Keycloak (hash do algoritmo do realm)
  E ela entra no Cadastro no mesmo login
  E o Legado registra a conta como migrada
  E o log registra LEGACY_USER_MIGRATED com o id técnico do usuário

Cenário: credenciais válidas e senha fora da política nova
  Dado "bruno.lima" existe só no Legado com a senha de 9 caracteres "Abc-1234x"
  Quando ele entra com essa senha
  Então o usuário é migrado
  E antes de concluir o login vê "Sua senha precisa ser atualizada para a nova política de segurança."
  E só entra depois de definir uma senha que atenda à política do realm

Cenário: usuário do Legado com senha errada
  Dado "carla.mendes" existe só no Legado
  Quando ela informa a senha errada
  Então vê "Usuário ou senha inválidos."
  E nada é criado no Keycloak
  E o log registra LEGACY_PASSWORD_MISMATCH com o identificador "car***@gmail"

Cenário: usuário inexistente nos dois sistemas
  Dado que "ana.silva@gmail.com" não existe no Keycloak nem no Legado
  Quando alguém tenta entrar com esse e-mail
  Então vê "Usuário ou senha inválidos."
  E o log registra LEGACY_USER_NOT_FOUND com o identificador "ana***@gmail"

Cenário: Legado fora do ar, lento ou com erro
  Dado que o Legado não responde em 2 segundos ou responde com erro
  Quando um usuário que não está no Keycloak tenta entrar
  Então vê "Usuário ou senha inválidos."
  E o log registra LEGACY_UNAVAILABLE em nível Error com o identificador mascarado
  E usuários que já estão no Keycloak continuam entrando normalmente

Cenário: conta bloqueada no Legado
  Dado que a conta está bloqueada no Legado por excesso de tentativas
  Quando o usuário tenta entrar pelo Keycloak
  Então vê "Usuário ou senha inválidos."
  E o log registra LEGACY_ACCOUNT_LOCKED com o identificador mascarado

Cenário: administrador do Legado
  Dado "admin" existe só no Legado com o perfil Admin
  Quando entra com a senha correta
  Então é migrado com as roles "admin" e "user"
  E precisa configurar o OTP antes de concluir o login

Cenário: dois primeiros logins simultâneos do mesmo usuário
  Dado que o mesmo usuário do Legado envia o login em duas abas ao mesmo tempo
  Então existe um único usuário no Keycloak
  E as duas tentativas terminam como um login normal (sucesso ou "Usuário ou senha inválidos.")
```

### US-03 — Esqueci minha senha
**Como** usuário, **quero** redefinir minha senha pelo e-mail, **para** recuperar o acesso.

```gherkin
Cenário: usuário do Keycloak
  Dado um usuário existente no Keycloak
  Quando ele pede o reset informando login ou e-mail
  Então vê "Se houver uma conta para esse login ou e-mail, você receberá as instruções em instantes."
  E recebe o e-mail padrão de redefinição

Cenário: usuário que só existe no Legado
  Dado "ana.souza" existe só no Legado
  Quando ela pede o reset
  Então o usuário é criado no Keycloak com os dados do Legado e sem senha
  E vê a mesma mensagem de instruções enviadas
  E recebe o e-mail padrão de redefinição
  E ao abrir o link define a senha nova e entra no Cadastro
  E o Legado registra a conta como migrada só depois que a senha nova é definida

Cenário: usuário que pediu o reset mas entra com a senha antiga
  Dado "ana.souza" foi criada no Keycloak por um pedido de reset e ainda não definiu a senha
  Quando ela entra com a senha do Legado
  Então o Keycloak pergunta ao Legado e, sendo válida, grava a senha e conclui o login

Cenário: usuário inexistente
  Dado que "ana.silva@gmail.com" não existe no Keycloak nem no Legado
  Quando alguém pede o reset com esse e-mail
  Então vê a mesma mensagem de instruções enviadas
  E nenhum e-mail é enviado
  E o log registra RESET_UNKNOWN_USER com o identificador "ana***@gmail"

Cenário: Legado fora do ar no reset
  Dado que o Legado não responde
  Quando um usuário que não está no Keycloak pede o reset
  Então vê a mesma mensagem de instruções enviadas
  E o log registra LEGACY_UNAVAILABLE em nível Error
```

### US-04 — Legado depois da migração
**Como** responsável pelo produto, **quero** que a conta migrada deixe de funcionar no Legado, **para** haver uma
única conta e uma única senha por pessoa.

```gherkin
Cenário: usuário migrado tenta entrar no Legado
  Dado "ana.souza" já foi migrada
  Quando ela entra no Legado com a senha correta
  Então vê "Sua conta foi transferida para o novo sistema." com o link "Ir para o novo sistema"
  E em 5 segundos é levada ao Cadastro, a menos que escolha "Ficar nesta página"
  E as sessões abertas dela no Legado deixam de valer

Cenário: senha errada de conta migrada no Legado
  Dado "ana.souza" já foi migrada
  Quando alguém informa a senha errada no Legado
  Então vê "Usuário ou senha inválidos." (o aviso de migração só aparece com a senha correta)

Cenário: Legado funcionando sem o Keycloak
  Dado que o Keycloak e o Cadastro estão desligados
  Quando o Legado é iniciado e um usuário não migrado entra nele
  Então o Legado sobe, fica saudável e o login funciona como antes
```

### US-05 — Autocadastro no Cadastro
**Como** visitante, **quero** criar minha conta em 4 passos, **para** usar o sistema.

```gherkin
Cenário: cadastro completo
  Dado que preenchi dados pessoais, acesso, endereço e revisei
  Quando clico em "Criar conta"
  Então a conta é criada no Keycloak com a role "user"
  E vejo "Conta criada! Agora é só entrar com seu login e senha." e o botão "Entrar"

Cenário: e-mail ou login já existente no Legado
  Dado que o e-mail informado pertence a um usuário do Legado ainda não migrado
  Quando envio o cadastro
  Então vejo "Você já tem conta. Entre com seu e-mail e senha atuais." e o botão "Entrar"
  E nada é criado no Keycloak

Cenário: e-mail ou login já existente no Keycloak
  Quando envio o cadastro com um login já usado
  Então o foco vai para "Login" com a mensagem "Este login já está em uso."

Cenário: senha fora da política
  Quando informo uma senha com menos de 12 caracteres
  Então o checklist mostra o item "Pelo menos 12 caracteres" como pendente e o botão "Continuar" fica bloqueado

Cenário: Legado indisponível durante o cadastro
  Dado que o Legado não responde
  Quando envio o cadastro
  Então vejo "Não foi possível concluir o cadastro agora. Tente novamente em instantes."
  E nada é criado no Keycloak

Cenário: CEP inexistente
  Quando informo um CEP que não existe
  Então o foco vai para "CEP" com a mensagem "CEP não encontrado. Confira os números."
```

### US-06 — Meu perfil
```gherkin
Cenário: usuário autenticado vê o próprio perfil
  Dado que entrei no Cadastro
  Quando abro "Meu perfil"
  Então vejo nome, login, e-mail, celular e endereço que estão no Keycloak

Cenário: visitante tenta abrir o perfil
  Dado que não entrei
  Quando abro /profile
  Então sou levado à tela de login e, depois de entrar, volto para "Meu perfil"
```

### US-07 — Listagem de usuários (admin)
```gherkin
Cenário: administrador lista usuários
  Dado que entrei como "admin"
  Quando abro "Usuários"
  Então vejo a lista paginada (20 por página) com nome, login, e-mail e perfis

Cenário: usuário sem a role admin
  Dado que entrei como usuário comum
  Quando abro /users
  Então vejo "Você não tem permissão para acessar esta página." e o link "Voltar para meu perfil"
  E a API responde 403 se chamada diretamente
```

### US-08 — Sair
```gherkin
Cenário: sair encerra a sessão no Cadastro e no Keycloak
  Dado que entrei
  Quando clico em "Sair"
  Então a sessão do Cadastro e a do Keycloak são encerradas
  E volto para a tela inicial com "Você saiu da sua conta."
  E voltar no navegador não reabre páginas autenticadas
```

### US-09 — Rastreabilidade de tentativas inválidas (Segurança/Operação)
```gherkin
Cenário: rastrear tentativas por identificador mascarado
  Dado várias tentativas de login com "ana.silva@gmail.com", que não existe
  Quando busco no Graylog por masked_login:"ana***@gmail"
  Então encontro os eventos LEGACY_USER_NOT_FOUND com data, client e trace_id
  E nenhum evento contém o e-mail completo, a senha ou o IP

Cenário: ataque em massa
  Dado mais de 30 eventos LEGACY_USER_NOT_FOUND ou LEGACY_PASSWORD_MISMATCH em 5 minutos
  Então o alerta "Identidade - Tentativas de migração inválidas" dispara
```

## 3b. Experiência do usuário

**Jornadas principais**

1. *Usuário do Legado*: Cadastro (Início) → "Entrar" → tela de login do Keycloak (tema `workspace`) → informa
   e-mail/login e a senha de sempre → (se preciso: atualizar senha / configurar OTP) → "Meu perfil". É um login
   comum: ele não fica sabendo que houve migração.
2. *Visitante*: Início → "Criar conta" → 4 passos (dados pessoais → acesso → endereço → revisão) → "Conta criada!"
   → "Entrar".
3. *Esqueceu a senha*: login do Keycloak → "Esqueci minha senha" → informa login/e-mail → mensagem neutra →
   e-mail → link → define a senha → entra.

**Telas e estados**

| Tela / rota | Carregando | Vazio | Erro (+ recuperação) | Sucesso |
|---|---|---|---|---|
| Cadastro · Início `/` | Skeleton do cabeçalho enquanto consulta `/bff/user` | — (visitante vê "Entrar" e "Criar conta") | Falha ao consultar a sessão: trata como visitante e registra no `AppLogger` | Autenticado: redireciona para `/profile` |
| Cadastro · Criar conta `/register` | Botão "Criar conta" com spinner e desabilitado; busca de CEP com indicador no campo | Cidades da UF sem resultado: "Nenhuma cidade encontrada para esta UF." | Validação: foco no 1º campo inválido + mensagem; falha geral: alerta "Não foi possível concluir o cadastro agora. Tente novamente em instantes." + "Tentar de novo" (dados preservados) | "Conta criada! Agora é só entrar com seu login e senha." + "Entrar" |
| Cadastro · Meu perfil `/profile` | Skeleton dos cartões de dados | Endereço ausente (usuário criado no Admin Console): "Endereço não informado." | "Não foi possível carregar seu perfil." + "Tentar de novo" | Dados exibidos |
| Cadastro · Usuários `/users` | Skeleton de 5 linhas | "Nenhum usuário encontrado." | "Não foi possível carregar a lista de usuários." + "Tentar de novo"; 403: "Você não tem permissão para acessar esta página." + "Voltar para meu perfil" | Tabela paginada |
| Cadastro · Sessão expirada (qualquer rota autenticada) | — | — | 401 da API: redireciona ao login; ao voltar mostra "Sua sessão expirou. Entre novamente." | — |
| Keycloak · Login | Botão "Entrar" desabilitado durante o envio (já existe no tema) | — | "Usuário ou senha inválidos." (sempre o mesmo texto) | Redireciona ao Cadastro |
| Keycloak · Esqueci minha senha | Botão desabilitado durante o envio | — | Login vazio: "Informe seu login ou e-mail." | "Se houver uma conta para esse login ou e-mail, você receberá as instruções em instantes." |
| Keycloak · Atualizar senha (migrado fora da política) | Botão desabilitado durante o envio | — | Mensagem da política que falhou (ex.: "A senha precisa ter pelo menos 12 caracteres.") | Conclui o login |
| Keycloak · Configurar OTP (admin) | Padrão do tema | — | "Código inválido. Confira o aplicativo autenticador." | Conclui o login |
| Legado · Login (conta migrada) | Já existente | — | "Sua conta foi transferida para o novo sistema. Levando você para lá em instantes…" + "Ir para o novo sistema" + "Ficar nesta página" (redireciona em 5 s) | — |

**Microtextos** (PT-BR; no Cadastro via `$localize`, no Keycloak via `messages_pt_BR.properties`)

| Elemento | Texto |
|---|---|
| Botões do Início | "Entrar" · "Criar conta" |
| Botões do cadastro | "Continuar" · "Voltar" · "Criar conta" |
| Login inválido (qualquer motivo) | "Usuário ou senha inválidos." |
| Senha migrada fora da política | "Sua senha precisa ser atualizada para a nova política de segurança." |
| Reset (sempre) | "Se houver uma conta para esse login ou e-mail, você receberá as instruções em instantes." |
| Cadastro com conta no Legado | "Você já tem conta. Entre com seu e-mail e senha atuais." |
| Login em uso | "Este login já está em uso." |
| E-mail em uso | "Este e-mail já está em uso." |
| Cadastro concluído | "Conta criada! Agora é só entrar com seu login e senha." |
| Falha temporária no cadastro | "Não foi possível concluir o cadastro agora. Tente novamente em instantes." |
| Checklist de senha | "Pelo menos 12 caracteres" · "Uma letra maiúscula" · "Uma letra minúscula" · "Um número" · "Um símbolo" · "Diferente do login e do e-mail" |
| Sair | Botão "Sair"; depois: "Você saiu da sua conta." |
| Sem permissão | "Você não tem permissão para acessar esta página." · "Voltar para meu perfil" |
| Legado: conta migrada | "Sua conta foi transferida para o novo sistema. Levando você para lá em instantes…" · "Ir para o novo sistema" · "Ficar nesta página" |

**Usabilidade e acessibilidade**

- Dispositivos: mobile, tablet e desktop obrigatórios (`frontend.md` §3). Mobile: menu do cabeçalho vira menu
  recolhível; listagem de usuários vira lista de cartões; passos do cadastro mostram "Passo 2 de 4" em vez da
  barra completa.
- Teclado: todo o fluxo sem mouse; foco inicial no primeiro campo de cada passo; ao trocar de passo o foco vai
  para o título do passo; mensagens de erro ligadas ao campo por `aria-describedby` e anunciadas (`aria-live`).
- Prevenção de erro: máscara de celular e CEP; CEP preenche logradouro, bairro, cidade e UF; checklist de senha
  ao vivo com a política do realm; login normalizado em minúsculas; revisão antes de criar a conta.
- Ações destrutivas: não há (sair não perde dados).
- Contraste AA e paleta/tokens do Workspace (mesma identidade do tema `workspace` do Keycloak).

**SEO**: sistema interno. `title` por rota, `meta description` no Início e `robots: noindex`.

**Métricas de UX**: taxa de conclusão do cadastro (iniciado → criado), erros de validação por envio, % de
migrados que precisaram atualizar a senha, tempo do primeiro login migrado (p95).

```gherkin
Cenário: feedback durante o envio do cadastro
  Dado que preenchi o formulário corretamente
  Quando clico em "Criar conta"
  Então o botão indica progresso e não aceita um segundo clique
  E ao concluir vejo "Conta criada! Agora é só entrar com seu login e senha."

Cenário: erro de validação no cadastro
  Dado que deixei "Celular" inválido
  Quando clico em "Continuar"
  Então o foco vai para "Celular" com a mensagem "Informe um celular com DDD, ex.: (11) 91234-5678."
  E os demais dados continuam preenchidos
```

## 4. Regras de negócio

| ID | Regra |
|---|---|
| RN-01 | O Keycloak é consultado primeiro. Usuário encontrado no Keycloak (por login ou e-mail) **com senha definida** segue o fluxo padrão do Keycloak; o Legado não é consultado. |
| RN-02 | Usuário não encontrado no Keycloak, ou encontrado sem senha e com vínculo ao Legado (`legacyUserId`), tem as credenciais enviadas ao Legado, que é o **único** a decidir se são válidas. O Keycloak nunca recebe nem compara o hash do Legado. |
| RN-03 | Legado responde "válido": o usuário é criado (ou completado) no Keycloak com login, e-mail, e-mail verificado (= `EmailConfirmed`), nome, celular, endereço, `legacyUserId` e roles (`Admin` → `admin` + `user`; `User` → `user`), e a senha digitada vira a credencial do Keycloak no algoritmo do realm. |
| RN-04 | Senha migrada que não atende à política do realm gera a ação obrigatória "atualizar senha" no mesmo login. |
| RN-05 | Qualquer resposta diferente de "válido" (senha errada, inexistente, bloqueado, já migrado, timeout de 2 s, erro HTTP, resposta inválida) resulta em "Usuário ou senha inválidos." e em um log com o identificador mascarado. Nada é criado no Keycloak. |
| RN-06 | Reset de senha: usuário no Keycloak → fluxo padrão. Só no Legado → criado no Keycloak **sem senha** (RN-03 sem credencial) e recebe o link padrão de redefinição. Inexistente ou Legado indisponível → mesma tela de sucesso, nenhum e-mail e log. |
| RN-07 | O Legado marca a conta como migrada (`MigratedAt`) **somente quando o usuário passa a ter senha no Keycloak** (login migrado ou reset concluído), e só depois que essa alteração é confirmada no Keycloak. A marcação é idempotente. |
| RN-08 | Conta migrada no Legado: com a senha correta, o login é recusado com o aviso de migração; com a senha errada, a resposta é a genérica. A marcação invalida as sessões abertas no Legado. Com o endereço do Cadastro configurado, a tela redireciona para ele em 5 s (com "Ficar nesta página" para cancelar). |
| RN-09 | O autocadastro do Legado continua aberto (o Legado só muda no RN-08). O autocadastro do Cadastro recusa login ou e-mail que exista no Keycloak ou no Legado; se o Legado não responder, o cadastro não é criado (falha fechada). A migração é de mão única (Legado → Keycloak): nada criado no Cadastro/Keycloak é gravado ou reservado no Legado. O Keycloak é o mandatório: se alguém criar no Legado um login que já existe no Keycloak, vale o do Keycloak, e essa conta do Legado não migra (risco aceito). |
| RN-10 | Identificador mascarado: até 3 primeiros caracteres (1 se o trecho tiver até 3) + `***`; para e-mail, + `@` + primeiro rótulo do domínio. `ana.souza@gmail.com` → `ana***@gmail`; `bruno.lima` → `bru***`; `al@x.com.br` → `a***@x`. O valor é normalizado (trim, minúsculas) antes da máscara. |
| RN-11 | Nenhum log contém login, e-mail ou senha completos, token, IP ou sessão. Usuário existente aparece só pelo id técnico (Keycloak e/ou Legado). |
| RN-12 | Usuário com a role `admin` precisa de OTP para concluir o login. |
| RN-13 | Política de senha do Cadastro = política do realm `workspace` (12+ caracteres, maiúscula, minúscula, número, símbolo, diferente do login/e-mail, histórico de 5). |
| RN-14 | O Legado não depende do Keycloak nem do Cadastro: não chama nenhum dos dois, não valida tokens deles e sobe sozinho. A dependência é só no sentido Keycloak/Cadastro → Legado, com credenciais (certificado TLS e chaves HMAC) emitidas pelo Legado. |

## 5. Requisitos não funcionais

- **Performance**: login de usuário já no Keycloak sem custo extra; primeiro login migrado com p95 < 1,5 s (Legado
  com PBKDF2 100k + criação no Keycloak); chamadas ao Legado com timeout de 2 s.
- **Disponibilidade**: o Legado fora do ar afeta só quem ainda não migrou (falha fechada com resposta genérica).
  O Cadastro depende do Keycloak (login, perfil, listagem e cadastro).
- **Segurança**: sem enumeração de contas por mensagem nem por tempo (o Legado verifica um hash fictício quando o
  usuário não existe); login e senha enviados ao Legado só por **TLS** e em requisição **assinada (HMAC)** com chave
  emitida pelo Legado para cada sistema, válida por 60 s e sem reenvio; endpoints internos só na rede interna; BFF
  sem token no navegador.
- **Independência do Legado**: o Legado continua funcionando sozinho, sem Keycloak e sem Cadastro (RN-14).
- **Dados pessoais (LGPD)**: nome, e-mail, celular e endereço passam do Legado para o Keycloak, para a mesma
  finalidade (execução do serviço ao titular). O identificador mascarado nos logs é dado pseudonimizado, mantido
  pelos 30 dias de retenção do Graylog, com finalidade de segurança (legítimo interesse/prevenção a fraude).
- **Auditoria**: toda migração (LEGACY_USER_MIGRATED, LEGACY_MIGRATION_MARKED) e toda tentativa inválida ficam
  rastreáveis no Graylog com `trace_id`.

## 6. Fora de escopo

- Migração em lote dos usuários que nunca entrarem, e o desligamento do Legado (spec futura).
- Edição de perfil e troca de e-mail no Cadastro.
- Login social e MFA para usuários comuns.
- Produção (HTTPS próprio, cluster, armazenamento de sessão distribuído).

## 7. Premissas e dúvidas em aberto

- [Premissa] Os dados do usuário ficam no **User Profile do Keycloak** (atributos); o Cadastro não tem banco próprio.
- [Premissa] Nome completo do Legado → `firstName` (primeira palavra) + `lastName` (restante); o Cadastro exibe os dois juntos.
- [Premissa] A role `user` é role padrão do realm (todo usuário novo a recebe).
- [Premissa] SMTP de desenvolvimento com **Mailpit** (UI em `127.0.0.1:8025`).
- [Premissa] Cadastro em `http://localhost:4300`; Keycloak continua em `http://localhost:8180`.
- [Premissa] Alerta de ataque em massa: > 30 eventos de tentativa inválida em 5 min (ajustável no Graylog).
- [Dúvida] Nenhuma em aberto.
