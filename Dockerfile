# Use the official .NET ASP.NET Core runtime as base image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

# Use the SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["src/Backend/ClaudeMobileTerminal.Backend.csproj", "Backend/"]
RUN dotnet restore "Backend/ClaudeMobileTerminal.Backend.csproj"

# Copy all source files
COPY src/ .
WORKDIR "/src/Backend"

# Build the application
RUN dotnet build "ClaudeMobileTerminal.Backend.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "ClaudeMobileTerminal.Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Create the final runtime image
FROM base AS final
WORKDIR /app

# Install Node.js and Claude Code CLI
RUN apt-get update && apt-get install -y \
    curl \
    gnupg \
    ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && npm install -g @anthropic-ai/claude-code \
    && rm -rf /var/lib/apt/lists/*

# Copy the published application
COPY --from=publish /app/publish .

# Expose the WebSocket ports
EXPOSE 8765 8766

# Set the entry point
ENTRYPOINT ["dotnet", "ClaudeMobileTerminal.Backend.dll"]