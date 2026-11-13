#!/usr/bin/env bash
# Análise SonarQube do cadastro (backend .NET + frontend Angular, com cobertura) que falha se o Quality Gate reprovar.
# Fonte única dos parâmetros da análise: usada localmente e pelo job "quality-gate" do CI.
#
# Local (SonarQube do projeto ../sonarqube de pé e "npm ci" já feito no cadastro-frontend):
#   ./scripts/sonar-analysis.sh
# Padrões locais: SONAR_HOST_URL=http://localhost:9001 e token em ../sonarqube/.tokens/cadastro.token.
# No CI, SONAR_HOST_URL e SONAR_TOKEN chegam pelos segredos do repositório.
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT_KEY="cadastro"
SONAR_HOST_URL="${SONAR_HOST_URL:-http://localhost:9001}"

if [[ -z "${SONAR_TOKEN:-}" ]]; then
  token_file="../sonarqube/.tokens/${PROJECT_KEY}.token"
  if [[ ! -s "$token_file" ]]; then
    echo "SONAR_TOKEN is not set and ${token_file} was not found. Start ../sonarqube first." >&2
    exit 2
  fi
  SONAR_TOKEN="$(cat "$token_file")"
fi

# Relatórios de cobertura antigos distorcem a análise: cada execução parte do zero.
rm -rf TestResults cadastro-frontend/coverage

echo "==> Frontend: testes com cobertura lcov (todos os arquivos de src/app)"
(
  cd cadastro-frontend
  npx ng test --watch=false --coverage --coverage-reporters lcov \
    --coverage-include "src/app/**/*.ts" --coverage-exclude "src/app/**/*.spec.ts" --coverage-exclude "src/app/**/*.testing.ts"
)

dotnet tool restore

echo "==> SonarQube begin"
# No Git Bash (Windows) o MSYS converteria "/k:" e "/d:" em caminhos; MSYS_NO_PATHCONV evita isso (sem efeito no Linux).
MSYS_NO_PATHCONV=1 dotnet sonarscanner begin \
  /k:"${PROJECT_KEY}" \
  /d:sonar.host.url="${SONAR_HOST_URL}" \
  /d:sonar.token="${SONAR_TOKEN}" \
  /d:sonar.cs.vscoveragexml.reportsPaths="TestResults/*.coverage.xml" \
  /d:sonar.javascript.lcov.reportPaths="cadastro-frontend/coverage/cadastro-frontend/lcov.info" \
  /d:sonar.exclusions="**/Migrations/**,**/node_modules/**,**/dist/**,**/*.spec.ts,**/*.testing.ts,e2e/**" \
  /d:sonar.coverage.exclusions="**/Program.cs,**/Migrations/**,cadastro-frontend/src/main.ts,cadastro-frontend/src/app/app.config.ts,cadastro-frontend/src/app/app.routes.ts" \
  /d:sonar.qualitygate.wait=true

dotnet build Cadastro.slnx --configuration Release

echo "==> Backend: testes .NET com cobertura (um relatório por projeto; integração exige Docker)"
for project in cadastro-backend/tests/*/; do
  name="$(basename "$project")"
  dotnet test --project "$project" --no-build --configuration Release \
    --coverage --coverage-output-format xml --coverage-output "${name}.coverage.xml" \
    --results-directory "${PWD}/TestResults"
done

echo "==> SonarQube end (falha se o Quality Gate reprovar)"
MSYS_NO_PATHCONV=1 dotnet sonarscanner end /d:sonar.token="${SONAR_TOKEN}"
