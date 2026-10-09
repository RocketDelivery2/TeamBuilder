# TeamBuilder web client: the static build served by nginx. One image serves every
# environment: public settings (OIDC authority, client ID, environment) are written to
# /config.js at container start from TEAMBUILDER_* variables (nginx/15-teambuilder-config.envsh).
# In the local compose stack nginx also proxies /api to the API so the browser uses one origin.
# VITE_* build arguments are public build-time defaults, never secrets.
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
ARG VITE_API_BASE_URL=
ARG VITE_OIDC_AUTHORITY=
ARG VITE_OIDC_CLIENT_ID=
ARG VITE_OIDC_SCOPE="openid profile"
# Off unless explicitly requested (local compose stack only); such a build shows a warning
# banner, and TEAMBUILDER_ENVIRONMENT=qa|production switches it off at run time regardless.
ARG VITE_ALLOW_DEV_TOKEN=false
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY \
    VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID \
    VITE_OIDC_SCOPE=$VITE_OIDC_SCOPE \
    VITE_ALLOW_DEV_TOKEN=$VITE_ALLOW_DEV_TOKEN
RUN npm run build

FROM nginx:1.27-alpine AS final
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="teambuilder-web" \
      org.opencontainers.image.source="https://github.com/RocketDelivery2/TeamBuilder" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}"
ENV API_UPSTREAM=http://api:8080 \
    TEAMBUILDER_CSP_CONNECT_SRC=""
COPY nginx/default.conf.template nginx/security-headers.inc.template /etc/nginx/templates/
COPY --chmod=0755 nginx/15-teambuilder-config.envsh /docker-entrypoint.d/15-teambuilder-config.envsh
COPY --from=build /app/dist /usr/share/nginx/html
RUN printf '{"version":"%s","commit":"%s"}\n' "$VERSION" "$SOURCE_REVISION" > /usr/share/nginx/html/version.json
EXPOSE 80
