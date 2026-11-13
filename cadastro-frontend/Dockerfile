# Frontend Angular: build de produção (AOT) servido por nginx sem privilégios de root.
# Versões fixadas por digest (OWASP A03): Node 24.22.2 / nginx 1.28.2. Atualize tag e digest juntos.
FROM node:24.22.2-alpine@sha256:a34ab6d7f6f4813f4d0c5469d74685604b962fbdbf5793a7bc7b4998ecb867c8 AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:1.30.6-alpine@sha256:f5116ed4606f3eb015dd4a8e28cb9b351476dc0ad4bc6e9f93b19cec130cdf03
# A base fixada demora a receber as correções do Alpine (o Trivy no CI acusava brotli, curl e openssl):
# aplica as atualizações de segurança do SO no build e volta ao usuário sem privilégio.
USER root
RUN apk upgrade --no-cache
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY security-headers.conf /etc/nginx/security-headers.conf
COPY --from=build /app/dist/cadastro-frontend/browser /usr/share/nginx/html
EXPOSE 8080
# A imagem nginx-unprivileged roda como 101 (nginx); declarado aqui para não depender do herdado (Trivy DS002).
USER 101
