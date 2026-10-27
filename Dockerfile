FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS builder
WORKDIR /app

COPY Titan.slnx ./
COPY Titan.Domain/Titan.Domain.csproj Titan.Domain/
COPY Titan.Application/Titan.Application.csproj Titan.Application/
COPY Titan.Infrastructure/Titan.Infrastructure.csproj Titan.Infrastructure/
COPY Titan.Host/Titan.Host.csproj Titan.Host/
COPY nuget.config ./

# The runtime identifier of the image being built: buildx names the platform amd64, dotnet names it x64.
ARG TARGETARCH
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 ;; \
      arm64) echo linux-arm64 ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac > /rid

RUN dotnet restore Titan.Host/Titan.Host.csproj -r "$(cat /rid)"

COPY . .

RUN dotnet publish Titan.Host/Titan.Host.csproj \
    --configuration Release \
    --no-restore \
    -r "$(cat /rid)" \
    --output /publish

RUN dotnet tool install --global dotnet-ef
ENV PATH="$PATH:/root/.dotnet/tools"

RUN dotnet ef migrations bundle \
    --project Titan.Infrastructure \
    --startup-project Titan.Host \
    --self-contained \
    -r "$(cat /rid)" \
    -o /efbundle

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=builder /publish .
COPY --from=builder /efbundle ./efbundle
EXPOSE 8080
ENTRYPOINT ["dotnet", "Titan.Host.dll"]
