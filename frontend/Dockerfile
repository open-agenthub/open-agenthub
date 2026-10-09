# ---- build ----
FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# Same value the backend image gets; the UI only falls back to it when the backend
# answers /api/config without a version (an older backend behind a newer frontend).
ARG APP_VERSION=dev
RUN VITE_APP_VERSION=$APP_VERSION npm run build

# ---- runtime (non-root, port 8080) ----
FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 8080
