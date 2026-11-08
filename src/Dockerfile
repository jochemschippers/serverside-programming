FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
# Base image ships no fonts; ImageSharp needs one to draw the weather text.
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
COPY publish/ /home/site/wwwroot
