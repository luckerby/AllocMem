FROM mcr.microsoft.com/dotnet/core/runtime:3.1-buster-slim
COPY bin/Release/netcoreapp3.1/ App/
WORKDIR /App
ENTRYPOINT ["dotnet", "AllocMem.dll"]