# Build context is the web/ folder (see deploy/docker-compose.yml).

FROM node:24-alpine AS build
WORKDIR /app

# Install first, from the lock file only, so this slow layer is reused until a dependency changes.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# The unprivileged image runs nginx as a non-root user and listens on 8080.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf

# The app is served under /app/ (its base href), so the files live in an "app" folder.
COPY --from=build /app/dist/web/browser /usr/share/nginx/html/app
EXPOSE 8080
