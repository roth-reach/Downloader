FROM mwader/static-ffmpeg:7.1 AS ffmpeg

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TelegramDownloader.csproj ./
RUN dotnet restore TelegramDownloader.csproj

COPY . .
RUN dotnet publish TelegramDownloader.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# FFmpeg + FFprobe
COPY --from=ffmpeg /ffmpeg /usr/local/bin/ffmpeg
COPY --from=ffmpeg /ffprobe /usr/local/bin/ffprobe

# yt-dlp standalone Linux executable
ADD https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux /usr/local/bin/yt-dlp

RUN chmod +x \
    /usr/local/bin/ffmpeg \
    /usr/local/bin/ffprobe \
    /usr/local/bin/yt-dlp

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TelegramDownloader.dll"]
