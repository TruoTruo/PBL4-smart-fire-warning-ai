# ============================================================
# Dockerfile (mqtt-subscriber)
# Build bằng SDK 8.0, chạy bằng runtime 8.0 (image nhỏ, không có SDK).
# Dùng qua infra/docker-compose.yml, hoặc tự build:
#   docker build -t smartfire-subscriber services/database-network/mqtt-subscriber
# Cấu hình broker / MySQL truyền bằng biến môi trường (MqttBroker__Host, Database__ConnectionString...)
# ============================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore riêng để Docker cache gói NuGet khi chỉ sửa code
COPY MqttSubscriber.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "MqttSubscriber.dll"]
