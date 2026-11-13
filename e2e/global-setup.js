// Dentro do container do Playwright, "localhost" é o próprio container. Quando E2E_FORWARD_HOST está definido
// (scripts/e2e.sh), este setup encaminha localhost:<porta> para o host, preservando o hostname "localhost": é o único
// tratado como contexto seguro em HTTP (cookies __Host-) e é o hostname do Keycloak (issuer e cookies do login).
const net = require("node:net");

// 4300: Cadastro (nginx). 8180: Keycloak (tela de login).
const FORWARDED_PORTS = [4300, 8180];

module.exports = async () => {
  const targetHost = process.env.E2E_FORWARD_HOST;
  if (!targetHost) {
    return undefined;
  }

  const servers = await Promise.all(
    FORWARDED_PORTS.map(
      (port) =>
        new Promise((resolve, reject) => {
          const server = net.createServer((client) => {
            const upstream = net.connect(port, targetHost);
            client.pipe(upstream).pipe(client);
            // Conexão encerrada de um lado fecha a outra; erros de rede só derrubam o par afetado.
            client.on("error", () => upstream.destroy());
            upstream.on("error", () => client.destroy());
          });
          server.once("error", reject);
          server.listen(port, "127.0.0.1", () => resolve(server));
        }),
    ),
  );

  return async () => Promise.all(servers.map((server) => new Promise((resolve) => server.close(resolve))));
};
