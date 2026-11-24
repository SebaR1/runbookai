# Build: Node compiles the SPA. Runtime: nginx serves the static files and proxies /api.
# The NSwag client is versioned (src/app/api), so this image needs no .NET.

FROM node:26.13.1-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx ng build

FROM nginx:1.31.10-alpine
COPY nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/runbookai-frontend/browser /usr/share/nginx/html
EXPOSE 80
