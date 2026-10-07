# Private-QA web client: static build served by nginx, which also proxies /api to the API so
# the browser uses one origin. VITE_* values are public build-time settings, never secrets.
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
ARG VITE_API_BASE_URL=
ARG VITE_OIDC_AUTHORITY=
ARG VITE_OIDC_CLIENT_ID=
ARG VITE_OIDC_SCOPE="openid profile"
# Off unless explicitly requested; a QA image built with "true" shows a warning banner.
ARG VITE_ALLOW_DEV_TOKEN=false
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY \
    VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID \
    VITE_OIDC_SCOPE=$VITE_OIDC_SCOPE \
    VITE_ALLOW_DEV_TOKEN=$VITE_ALLOW_DEV_TOKEN
RUN npm run build

FROM nginx:1.27-alpine AS final
ENV API_UPSTREAM=http://api:8080
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
