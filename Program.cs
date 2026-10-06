using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    // ============================================================
    // CONFIGURATION
    // ============================================================

    static TelegramBotClient? Bot;

    static readonly ConcurrentDictionary<long, UserSession> Sessions = new();

    static readonly HttpClient Http = CreateHttpClient();

    static readonly string TempRoot =
        Path.Combine(Path.GetTempPath(), "TelegramMediaDownloader");

    // Telegram Bot API upload limit is kept conservative here.
    const long MaxUploadBytes = 49L * 1024 * 1024;

    const int ProgressUpdateMilliseconds = 2000;

    static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    // Put yt-dlp.exe and ffmpeg.exe in PATH, or change these names
    // to absolute paths if you prefer.
    const string YtDlpExecutable = "yt-dlp";
    const string FfmpegExecutable = "ffmpeg";

    // ============================================================
    // MAIN
    // ============================================================

    static async Task Main()
    {
        string? botToken =
            Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(botToken))
        {
            Console.WriteLine(
                "TELEGRAM_BOT_TOKEN environment variable is not configured.");

            Console.WriteLine();
            Console.WriteLine("PowerShell:");
            Console.WriteLine(
                "$env:TELEGRAM_BOT_TOKEN=\"8819842879:AAHBFngEYXuN3mzoirGpVfReF3XL98uS9lU\"");

            return;
        }

        Directory.CreateDirectory(TempRoot);

        if (!IsExecutableAvailable(YtDlpExecutable))
        {
            Console.WriteLine(
                "WARNING: yt-dlp was not found on PATH.");
            Console.WriteLine(
                "TikTok MP4 downloads will not work until yt-dlp is installed.");
        }

        if (!IsExecutableAvailable(FfmpegExecutable))
        {
            Console.WriteLine(
                "WARNING: FFmpeg was not found on PATH.");
            Console.WriteLine(
                "MP3 and MP4 normalization require FFmpeg.");
        }

        Bot = new TelegramBotClient(botToken);

        using CancellationTokenSource appCts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appCts.Cancel();
        };

        ReceiverOptions receiverOptions = new()
        {
            AllowedUpdates = new[]
            {
                UpdateType.Message,
                UpdateType.CallbackQuery
            },
            DropPendingUpdates = false
        };

        Bot.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions,
            appCts.Token);

        User me = await Bot.GetMe(appCts.Token);

        Console.WriteLine($"Bot started: @{me.Username}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(
                Timeout.Infinite,
                appCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        foreach (UserSession session in Sessions.Values)
        {
            try
            {
                session.DownloadCancellation?.Cancel();
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    // ============================================================
    // HTTP CLIENT
    // ============================================================

    static HttpClient CreateHttpClient()
    {
        HttpClient client = new();

        client.Timeout = TimeSpan.FromMinutes(2);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/131.0 Safari/537.36");

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/json," +
            "application/xml;q=0.9,*/*;q=0.8");

        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            "en-US,en;q=0.9");

        return client;
    }

    // ============================================================
    // UPDATE HANDLER
    // ============================================================

    static async Task HandleUpdateAsync(
        ITelegramBotClient bot,
        Update update,
        CancellationToken cancellationToken)
    {
        try
        {
            if (update.Message is not null)
            {
                await HandleMessageAsync(
                    update.Message,
                    cancellationToken);
            }
            else if (update.CallbackQuery is not null)
            {
                await HandleCallbackAsync(
                    update.CallbackQuery,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update error: {ex}");
        }
    }

    static Task HandlePollingErrorAsync(
        ITelegramBotClient bot,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Polling error: {exception}");
        return Task.CompletedTask;
    }

    // ============================================================
    // MESSAGE HANDLER
    // ============================================================

    static async Task HandleMessageAsync(
        Message message,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        if (message.Chat.Type != ChatType.Private)
        {
            await Bot.SendMessage(
                message.Chat.Id,
                "Please use this bot in a private chat.",
                cancellationToken: cancellationToken);

            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        string text = message.Text.Trim();

        long userId =
            message.From?.Id ?? message.Chat.Id;

        long chatId =
            message.Chat.Id;

        if (text.Equals(
            "/start",
            StringComparison.OrdinalIgnoreCase))
        {
            await StartAsync(
                chatId,
                userId,
                cancellationToken);

            return;
        }

        if (text.Equals(
            "/cancel",
            StringComparison.OrdinalIgnoreCase))
        {
            await CancelAsync(
                chatId,
                userId,
                cancellationToken);

            return;
        }

        if (!Sessions.TryGetValue(
            userId,
            out UserSession? session) ||
            !session.WaitingForUrl)
        {
            await Bot.SendMessage(
                chatId,
                "Use /start to begin.",
                cancellationToken: cancellationToken);

            return;
        }

        await ProcessUrlAsync(
            chatId,
            userId,
            text,
            cancellationToken);
    }

    // ============================================================
    // CALLBACK HANDLER
    // ============================================================

    static async Task HandleCallbackAsync(
        CallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (Bot is null ||
            callback.Message is null ||
            string.IsNullOrWhiteSpace(callback.Data))
        {
            return;
        }

        long chatId =
            callback.Message.Chat.Id;

        long userId =
            callback.From.Id;

        try
        {
            await Bot.AnswerCallbackQuery(
                callback.Id,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Callback answer error: {ex.Message}");
        }

        switch (callback.Data)
        {
            case "platform_tiktok":
                await SelectPlatformAsync(
                    chatId,
                    userId,
                    "TikTok",
                    cancellationToken);
                break;

            case "platform_pinterest":
                await SelectPlatformAsync(
                    chatId,
                    userId,
                    "Pinterest",
                    cancellationToken);
                break;

            case "format_mp3":
                await SelectFormatAsync(
                    chatId,
                    userId,
                    "MP3",
                    cancellationToken);
                break;

            case "format_mp4":
                await SelectFormatAsync(
                    chatId,
                    userId,
                    "MP4",
                    cancellationToken);
                break;

            case "back_platform":
                await ShowPlatformMenuAsync(
                    chatId,
                    userId,
                    cancellationToken);
                break;

            case "cancel":
                await CancelAsync(
                    chatId,
                    userId,
                    cancellationToken);
                break;
        }
    }

    // ============================================================
    // START
    // ============================================================

    static async Task StartAsync(
        long chatId,
        long userId,
        CancellationToken cancellationToken)
    {
        CancelUserDownload(userId);

        Sessions[userId] = new UserSession();

        await ShowPlatformMenuAsync(
            chatId,
            userId,
            cancellationToken);
    }

    // ============================================================
    // PLATFORM MENU
    // ============================================================

    static async Task ShowPlatformMenuAsync(
        long chatId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        UserSession session =
            Sessions.GetOrAdd(
                userId,
                _ => new UserSession());

        session.Platform = null;
        session.Format = null;
        session.WaitingForUrl = false;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🎵 TikTok",
                        "platform_tiktok")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "📌 Pinterest",
                        "platform_pinterest")
                }
            });

        await Bot.SendMessage(
            chatId,
            "🤖 Welcome to Media Downloader Bot!\n\n" +
            "Choose a platform:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    // ============================================================
    // PLATFORM SELECTION
    // ============================================================

    static async Task SelectPlatformAsync(
        long chatId,
        long userId,
        string platform,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        UserSession session =
            Sessions.GetOrAdd(
                userId,
                _ => new UserSession());

        session.Platform = platform;
        session.Format = null;
        session.WaitingForUrl = false;

        string emoji =
            platform == "TikTok"
                ? "🎵"
                : "📌";

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🎧 MP3",
                        "format_mp3"),

                    InlineKeyboardButton.WithCallbackData(
                        "🎬 MP4",
                        "format_mp4")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "◀️ Back",
                        "back_platform")
                }
            });

        await Bot.SendMessage(
            chatId,
            $"{emoji} {platform} selected!\n\n" +
            "Choose download format:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    // ============================================================
    // FORMAT SELECTION
    // ============================================================

    static async Task SelectFormatAsync(
        long chatId,
        long userId,
        string format,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        if (!Sessions.TryGetValue(
            userId,
            out UserSession? session) ||
            string.IsNullOrWhiteSpace(session.Platform))
        {
            await ShowPlatformMenuAsync(
                chatId,
                userId,
                cancellationToken);

            return;
        }

        session.Format = format;
        session.WaitingForUrl = true;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "❌ Cancel",
                        "cancel")
                }
            });

        string text;

        if (session.Platform == "TikTok")
        {
            text =
                format == "MP3"
                    ? "🎧 TikTok MP3\n\nPlease paste your TikTok link:"
                    : "🎬 TikTok MP4\n\nPlease paste your TikTok link:";
        }
        else
        {
            text =
                format == "MP3"
                    ? "🎧 Pinterest MP3\n\nPlease paste your Pinterest video link:"
                    : "🎬 Pinterest MP4\n\nPlease paste your Pinterest video link:";
        }

        await Bot.SendMessage(
            chatId,
            text,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    // ============================================================
    // URL PROCESSING
    // ============================================================

    static async Task ProcessUrlAsync(
        long chatId,
        long userId,
        string url,
        CancellationToken appCancellationToken)
    {
        if (Bot is null)
            return;

        if (!Sessions.TryGetValue(
            userId,
            out UserSession? session) ||
            string.IsNullOrWhiteSpace(session.Platform) ||
            string.IsNullOrWhiteSpace(session.Format))
        {
            await Bot.SendMessage(
                chatId,
                "Use /start to begin.",
                cancellationToken: appCancellationToken);

            return;
        }

        bool valid =
            session.Platform == "TikTok"
                ? IsTikTokUrl(url)
                : IsPinterestUrl(url);

        if (!valid)
        {
            await Bot.SendMessage(
                chatId,
                $"❌ Invalid {session.Platform} URL.\n\n" +
                "Please check the link and try again.",
                cancellationToken: appCancellationToken);

            return;
        }

        if (!IsExecutableAvailable(YtDlpExecutable))
        {
            await Bot.SendMessage(
                chatId,
                "❌ yt-dlp is missing on the server.\n\n" +
                "Please install yt-dlp and make sure it is in PATH.",
                cancellationToken: appCancellationToken);

            return;
        }

        if (!IsExecutableAvailable(FfmpegExecutable))
        {
            await Bot.SendMessage(
                chatId,
                "❌ FFmpeg is missing on the server.\n\n" +
                "MP3 and MP4 processing require FFmpeg.",
                cancellationToken: appCancellationToken);

            return;
        }

        session.WaitingForUrl = false;

        CancelUserDownload(userId);

        using CancellationTokenSource timeoutCts =
            new(DownloadTimeout);

        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                appCancellationToken,
                timeoutCts.Token);

        session.DownloadCancellation = linkedCts;

        string workDir =
            Path.Combine(
                TempRoot,
                $"{userId}_{Guid.NewGuid():N}");

        Directory.CreateDirectory(workDir);

        Message progressMessage =
            await Bot.SendMessage(
                chatId,
                "⏳ Starting...",
                replyMarkup: CreateCancelKeyboard(),
                cancellationToken: appCancellationToken);

        try
        {
            string finalFile;

            if (session.Platform == "TikTok")
            {
                finalFile =
                    await DownloadTikTokAsync(
                        url,
                        workDir,
                        chatId,
                        progressMessage.MessageId,
                        session.Format,
                        linkedCts.Token);
            }
            else
            {
                finalFile =
                    await DownloadPinterestAsync(
                        url,
                        workDir,
                        chatId,
                        progressMessage.MessageId,
                        session.Format,
                        linkedCts.Token);
            }

            linkedCts.Token.ThrowIfCancellationRequested();

            FileInfo fileInfo = new(finalFile);

            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new Exception("Output file was not created.");

            if (fileInfo.Length > MaxUploadBytes)
            {
                await SafeEditMessageAsync(
                    chatId,
                    progressMessage.MessageId,
                    "❌ File is too large to upload.\n\n" +
                    $"Size: {FormatBytes(fileInfo.Length)}\n" +
                    $"Limit: {FormatBytes(MaxUploadBytes)}",
                    null,
                    appCancellationToken);

                return;
            }

            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                "✅ Download completed!\n\n" +
                $"📁 {fileInfo.Name}\n" +
                $"📦 {FormatBytes(fileInfo.Length)}",
                null,
                appCancellationToken);

            await SendMediaAsync(
                chatId,
                finalFile,
                session.Format,
                appCancellationToken);
        }
        catch (OperationCanceledException)
        {
            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                "❌ Download cancelled.",
                null,
                appCancellationToken);
        }
        catch (MediaNotFoundException ex)
        {
            Console.WriteLine($"Media not found: {ex.Message}");

            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                "❌ Download failed.\n\n" +
                ex.Message,
                null,
                appCancellationToken);
        }
        catch (ProcessFailedException ex)
        {
            Console.WriteLine(
                $"Process failed ({ex.ExitCode}):\n{ex.Output}");

            string message =
                session.Platform == "TikTok"
                    ? "❌ TikTok download failed.\n\n" +
                      "yt-dlp could not download this video. " +
                      "The video may be private, deleted, region-restricted, " +
                      "or TikTok may have changed its page/API."
                    : "❌ Pinterest download failed.\n\n" +
                      "Please check that the pin contains a public video.";

            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                message,
                null,
                appCancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Network error: {ex}");

            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                "❌ Download failed.\n\n" +
                "Network error. Please try again.",
                null,
                appCancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download error: {ex}");

            await SafeEditMessageAsync(
                chatId,
                progressMessage.MessageId,
                "❌ Download failed.\n\n" +
                "Please check your URL and try again.",
                null,
                appCancellationToken);
        }
        finally
        {
            session.DownloadCancellation = null;
            session.WaitingForUrl = false;

            TryDeleteDirectory(workDir);
        }
    }

    // ============================================================
    // TIKTOK - YT-DLP
    // ============================================================

    static async Task<string> DownloadTikTokAsync(
        string url,
        string workDir,
        long chatId,
        int messageId,
        string format,
        CancellationToken cancellationToken)
    {
        string outputTemplate =
            Path.Combine(workDir, "%(id)s.%(ext)s");

        List<string> args = new()
        {
            "--no-playlist",
            "--no-part",
            "--newline",
            "--progress",
            "--retries", "3",
            "--fragment-retries", "3",
            "--concurrent-fragments", "4",
            "--merge-output-format", "mp4",
            "-f", "bv*+ba/b",
            "-o", outputTemplate,
            url
        };

        await SafeEditMessageAsync(
            chatId,
            messageId,
            format == "MP4"
                ? "🎬 TikTok MP4\n\n⏳ Downloading with yt-dlp..."
                : "🎧 TikTok MP3\n\n⏳ Downloading with yt-dlp...",
            CreateCancelKeyboard(),
            cancellationToken);

        string output =
            await RunProcessWithProgressAsync(
                YtDlpExecutable,
                args,
                chatId,
                messageId,
                cancellationToken);

        string? downloaded =
            FindLargestMediaFile(
                workDir,
                includeMp4Only: false);

        if (downloaded is null)
        {
            throw new MediaNotFoundException(
                "yt-dlp completed but no media file was created.");
        }

        if (format == "MP3")
        {
            await SafeEditMessageAsync(
                chatId,
                messageId,
                "🎧 Converting TikTok to MP3...",
                CreateCancelKeyboard(),
                cancellationToken);

            string mp3 =
                Path.Combine(
                    workDir,
                    "media.mp3");

            await ConvertToMp3Async(
                downloaded,
                mp3,
                cancellationToken);

            return mp3;
        }

        // Normalize the downloaded video to a Telegram-friendly MP4.
        await SafeEditMessageAsync(
            chatId,
            messageId,
            "🎬 Preparing MP4 for Telegram...",
            CreateCancelKeyboard(),
            cancellationToken);

        string finalMp4 =
            Path.Combine(
                workDir,
                "media.mp4");

        await NormalizeVideoToMp4Async(
            downloaded,
            finalMp4,
            cancellationToken);

        return finalMp4;
    }

    // ============================================================
    // PINTEREST
    // ============================================================

    static async Task<string> DownloadPinterestAsync(
        string url,
        string workDir,
        long chatId,
        int messageId,
        string format,
        CancellationToken cancellationToken)
    {
        await SafeEditMessageAsync(
            chatId,
            messageId,
            "📌 Finding Pinterest media...",
            CreateCancelKeyboard(),
            cancellationToken);

        string? mediaUrl =
            await ResolvePinterestMediaUrlAsync(
                url,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            throw new MediaNotFoundException(
                "No public downloadable Pinterest video was found.");
        }

        string sourceFile =
            Path.Combine(
                workDir,
                "pinterest_source.mp4");

        await SafeEditMessageAsync(
            chatId,
            messageId,
            "📌 Downloading Pinterest video...\n\n" +
            "░░░░░░░░░░░░░░░░░░░░ 0%",
            CreateCancelKeyboard(),
            cancellationToken);

        await DownloadFileAsync(
            mediaUrl,
            sourceFile,
            chatId,
            messageId,
            cancellationToken);

        if (format == "MP3")
        {
            await SafeEditMessageAsync(
                chatId,
                messageId,
                "🎧 Converting Pinterest video to MP3...",
                CreateCancelKeyboard(),
                cancellationToken);

            string mp3 =
                Path.Combine(
                    workDir,
                    "media.mp3");

            await ConvertToMp3Async(
                sourceFile,
                mp3,
                cancellationToken);

            return mp3;
        }

        await SafeEditMessageAsync(
            chatId,
            messageId,
            "🎬 Preparing Pinterest MP4...",
            CreateCancelKeyboard(),
            cancellationToken);

        string finalMp4 =
            Path.Combine(
                workDir,
                "media.mp4");

        await NormalizeVideoToMp4Async(
            sourceFile,
            finalMp4,
            cancellationToken);

        return finalMp4;
    }

    // ============================================================
    // PINTEREST PUBLIC MEDIA RESOLVER
    // ============================================================

    static async Task<string?> ResolvePinterestMediaUrlAsync(
        string originalUrl,
        CancellationToken cancellationToken)
    {
        Uri? finalUri =
            await ResolveRedirectAsync(
                originalUrl,
                cancellationToken);

        string pageUrl =
            finalUri?.ToString() ?? originalUrl;

        string html =
            await GetHtmlAsync(
                pageUrl,
                cancellationToken);

        // Strategy 1: common JSON fields.
        string? url =
            FindJsonUrlByKeys(
                html,
                "video_list",
                "videoUrl",
                "video_url",
                "contentUrl");

        if (!string.IsNullOrWhiteSpace(url) &&
            LooksLikeVideoUrl(url))
        {
            return NormalizeJsonUrl(url);
        }

        // Strategy 2: common video/CDN URLs.
        url =
            FindVideoUrlRegex(html);

        if (!string.IsNullOrWhiteSpace(url))
        {
            return NormalizeJsonUrl(url);
        }

        // Strategy 3: JSON-LD contentUrl.
        Match contentUrlMatch =
            Regex.Match(
                html,
                "\"contentUrl\"\\s*:\\s*\"(?<url>(?:\\\\.|[^\"])*)\"",
                RegexOptions.IgnoreCase);

        if (contentUrlMatch.Success)
        {
            string value =
                contentUrlMatch.Groups["url"].Value;

            string normalized =
                NormalizeJsonUrl(value);

            if (LooksLikeVideoUrl(normalized))
                return normalized;
        }

        return null;
    }

    // ============================================================
    // HTML FETCHING
    // ============================================================

    static async Task<string> GetHtmlAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Get, url);

        using HttpResponseMessage response =
            await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(
            cancellationToken);
    }

    static async Task<Uri?> ResolveRedirectAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Get, url);

        using HttpResponseMessage response =
            await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        return response.RequestMessage?.RequestUri;
    }

    // ============================================================
    // PUBLIC JSON / HTML URL EXTRACTION
    // ============================================================

    static string? FindJsonUrlByKeys(
        string html,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            string escapedKey =
                Regex.Escape(key);

            Match match =
                Regex.Match(
                    html,
                    $"[\"']{escapedKey}[\"']\\s*:\\s*[\"'](?<url>(?:\\\\.|[^\"'])+)[\"']",
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string value =
                    match.Groups["url"].Value;

                string normalized =
                    NormalizeJsonUrl(value);

                if (LooksLikeVideoUrl(normalized))
                    return normalized;
            }
        }

        return null;
    }

    static string? FindVideoUrlRegex(
        string html)
    {
        string[] patterns = new[]
        {
            @"https?:\\?/\\?/[^""'\\\s<>]+?\.mp4(?:\?[^""'\\\s<>]*)?",
            @"https?:\\?/\\?/[^""'\\\s<>]+?(?:video|play|download)[^""'\\\s<>]*",
            @"https?://[^""'\s<>]+?\.mp4(?:\?[^""'\s<>]*)?"
        };

        foreach (string pattern in patterns)
        {
            Match match =
                Regex.Match(
                    html,
                    pattern,
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string candidate =
                    NormalizeJsonUrl(match.Value);

                if (LooksLikeVideoUrl(candidate))
                    return candidate;
            }
        }

        return null;
    }

    static string NormalizeJsonUrl(
        string value)
    {
        value = value.Trim();

        value = value.Replace("\\/", "/");
        value = value.Replace("\\u0026", "&");

        try
        {
            value = Regex.Unescape(value);
        }
        catch
        {
            // Keep original if malformed escaping is encountered.
        }

        value =
            WebUtility.HtmlDecode(value);

        return value;
    }

    static bool LooksLikeVideoUrl(
        string value)
    {
        if (!Uri.TryCreate(
            value,
            UriKind.Absolute,
            out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string lower =
            value.ToLowerInvariant();

        return
            lower.Contains(".mp4") ||
            lower.Contains(".m3u8") ||
            lower.Contains("video") ||
            lower.Contains("play") ||
            lower.Contains("download");
    }

    // ============================================================
    // YT-DLP PROCESS
    // ============================================================

    static async Task<string> RunProcessWithProgressAsync(
        string executable,
        List<string> arguments,
        long chatId,
        int messageId,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo =
            new()
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process =
            new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

        if (!process.Start())
            throw new Exception(
                $"Could not start {executable}.");

        Task<string> standardOutputTask =
            process.StandardOutput.ReadToEndAsync();

        Task<string> standardErrorTask =
            process.StandardError.ReadToEndAsync();

        using CancellationTokenRegistration registration =
            cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(
                            entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore cancellation cleanup errors.
                }
            });

        DateTime lastUpdate =
            DateTime.MinValue;

        // Read stdout while the process is running.
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(
                250,
                cancellationToken);

            string outputSoFar =
                await ReadTaskSafelyAsync(
                    standardOutputTask);

            string? progress =
                GetLastProgressLine(
                    outputSoFar);

            if (progress is not null &&
                (DateTime.UtcNow - lastUpdate).TotalMilliseconds >=
                ProgressUpdateMilliseconds)
            {
                lastUpdate = DateTime.UtcNow;

                await SafeEditMessageAsync(
                    chatId,
                    messageId,
                    progress,
                    CreateCancelKeyboard(),
                    cancellationToken);
            }
        }

        await process.WaitForExitAsync(
            cancellationToken);

        string output =
            await standardOutputTask;

        string error =
            await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new ProcessFailedException(
                process.ExitCode,
                output + Environment.NewLine + error);
        }

        return output;
    }

    static async Task<string> ReadTaskSafelyAsync(
        Task<string> task)
    {
        if (task.IsCompletedSuccessfully)
            return task.Result;

        return string.Empty;
    }

    static string? GetLastProgressLine(
        string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        string[] lines =
            output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line =
                lines[i].Trim();

            Match match =
                Regex.Match(
                    line,
                    @"(\d+(?:\.\d+)?)%\s+of\s+~?\s*([\d.]+\s*[KMG]?i?B)?(?:\s+at\s+(.+?)(?:\s+ETA\s+(.+))?)?$",
                    RegexOptions.IgnoreCase);

            if (!match.Success)
                continue;

            string percent =
                match.Groups[1].Value;

            string size =
                match.Groups[2].Success
                    ? match.Groups[2].Value
                    : "?";

            string speed =
                match.Groups[3].Success
                    ? match.Groups[3].Value
                    : "?";

            string eta =
                match.Groups[4].Success
                    ? match.Groups[4].Value
                    : "?";

            return
                "⏳ Downloading...\n\n" +
                $"{CreateProgressBar(
                    double.TryParse(
                        percent,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double p)
                        ? p
                        : 0)} {percent}%\n\n" +
                $"📦 {size}\n" +
                $"⚡ {speed}\n" +
                $"⏱ ETA: {eta}";
        }

        return null;
    }

    // ============================================================
    // DIRECT FILE DOWNLOAD WITH PROGRESS
    // ============================================================

    static async Task DownloadFileAsync(
        string mediaUrl,
        string destinationPath,
        long chatId,
        int messageId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Get, mediaUrl);

        request.Headers.Referrer =
            new Uri("https://www.google.com/");

        using HttpResponseMessage response =
            await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        long? totalBytes =
            response.Content.Headers.ContentLength;

        await using Stream input =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        await using FileStream output =
            new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

        byte[] buffer =
            new byte[81920];

        long downloaded = 0;

        DateTime started =
            DateTime.UtcNow;

        DateTime lastUpdate =
            DateTime.MinValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read =
                await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);

            if (read == 0)
                break;

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);

            downloaded += read;

            DateTime now =
                DateTime.UtcNow;

            if ((now - lastUpdate).TotalMilliseconds >=
                ProgressUpdateMilliseconds)
            {
                lastUpdate = now;

                double seconds =
                    Math.Max(
                        0.1,
                        (now - started).TotalSeconds);

                double speed =
                    downloaded / seconds;

                double percent =
                    totalBytes.HasValue &&
                    totalBytes.Value > 0
                        ? downloaded * 100.0 /
                          totalBytes.Value
                        : 0;

                string progressText;

                if (totalBytes.HasValue)
                {
                    progressText =
                        "📌 Downloading...\n\n" +
                        $"{CreateProgressBar(percent)} {percent:F0}%\n\n" +
                        $"📦 {FormatBytes(downloaded)} / " +
                        $"{FormatBytes(totalBytes.Value)}\n" +
                        $"⚡ {FormatBytes((long)speed)}/s";
                }
                else
                {
                    progressText =
                        "📌 Downloading...\n\n" +
                        "██████████░░░░░░░░░░\n\n" +
                        $"📦 {FormatBytes(downloaded)}\n" +
                        $"⚡ {FormatBytes((long)speed)}/s";
                }

                await SafeEditMessageAsync(
                    chatId,
                    messageId,
                    progressText,
                    CreateCancelKeyboard(),
                    cancellationToken);
            }
        }

        await output.FlushAsync(
            cancellationToken);
    }

    // ============================================================
    // FFMPEG MP3
    // ============================================================

    static async Task ConvertToMp3Async(
        string inputFile,
        string outputFile,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo =
            new()
            {
                FileName = FfmpegExecutable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFile);

        startInfo.ArgumentList.Add("-vn");

        startInfo.ArgumentList.Add("-codec:a");
        startInfo.ArgumentList.Add("libmp3lame");

        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("192k");

        startInfo.ArgumentList.Add(outputFile);

        await RunFfmpegAsync(
            startInfo,
            cancellationToken);

        if (!File.Exists(outputFile))
            throw new Exception(
                "FFmpeg did not create the MP3 file.");
    }

    // ============================================================
    // FFMPEG MP4 NORMALIZATION
    // ============================================================

    static async Task NormalizeVideoToMp4Async(
        string inputFile,
        string outputFile,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo =
            new()
            {
                FileName = FfmpegExecutable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFile);

        // H.264 + AAC + faststart gives broad Telegram/player compatibility.
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");

        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0?");

        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");

        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("veryfast");

        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("23");

        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");

        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");

        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("128k");

        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");

        startInfo.ArgumentList.Add("-sn");

        startInfo.ArgumentList.Add(outputFile);

        await RunFfmpegAsync(
            startInfo,
            cancellationToken);

        if (!File.Exists(outputFile))
            throw new Exception(
                "FFmpeg did not create the MP4 file.");

        FileInfo info =
            new(outputFile);

        if (info.Length < 10_000)
            throw new Exception(
                "The generated MP4 is too small to be a valid video.");
    }

    static async Task RunFfmpegAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using Process process =
            new()
            {
                StartInfo = startInfo
            };

        if (!process.Start())
            throw new Exception(
                "Could not start FFmpeg.");

        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();

        using CancellationTokenRegistration registration =
            cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(
                            entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore.
                }
            });

        await process.WaitForExitAsync(
            cancellationToken);

        string error =
            await errorTask;

        _ = await outputTask;

        if (process.ExitCode != 0)
        {
            Console.WriteLine(
                $"FFmpeg error:\n{error}");

            throw new Exception(
                "FFmpeg processing failed.");
        }
    }

    // ============================================================
    // TELEGRAM SEND MEDIA
    // ============================================================

    static async Task SendMediaAsync(
        long chatId,
        string filePath,
        string format,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        FileInfo info =
            new(filePath);

        if (!info.Exists)
            throw new FileNotFoundException(
                "Media file does not exist.",
                filePath);

        await using FileStream stream =
            new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        InputFile file =
            InputFile.FromStream(
                stream,
                info.Name);

        if (format == "MP3")
        {
            await Bot.SendAudio(
                chatId,
                file,
                caption: $"🎧 {info.Name}",
                cancellationToken: cancellationToken);
        }
        else
        {
            await Bot.SendVideo(
                chatId,
                file,
                caption: $"🎬 {info.Name}",
                supportsStreaming: true,
                cancellationToken: cancellationToken);
        }
    }

    // ============================================================
    // CANCEL
    // ============================================================

    static async Task CancelAsync(
        long chatId,
        long userId,
        CancellationToken cancellationToken)
    {
        CancelUserDownload(userId);

        if (Sessions.TryGetValue(
            userId,
            out UserSession? session))
        {
            session.WaitingForUrl = false;
        }

        if (Bot is not null)
        {
            await Bot.SendMessage(
                chatId,
                "❌ Download cancelled.",
                cancellationToken: cancellationToken);
        }
    }

    static void CancelUserDownload(
        long userId)
    {
        if (Sessions.TryGetValue(
            userId,
            out UserSession? session))
        {
            try
            {
                session.DownloadCancellation?.Cancel();
            }
            catch
            {
                // Ignore.
            }
        }
    }

    static InlineKeyboardMarkup CreateCancelKeyboard()
    {
        return new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "❌ Cancel",
                        "cancel")
                }
            });
    }

    // ============================================================
    // URL VALIDATION
    // ============================================================

    static bool IsTikTokUrl(
        string url)
    {
        if (!TryGetHttpUri(
            url,
            out Uri? uri))
        {
            return false;
        }

        string host =
            uri.Host.ToLowerInvariant();

        return
            host == "tiktok.com" ||
            host.EndsWith(".tiktok.com") ||
            host == "vm.tiktok.com" ||
            host == "vt.tiktok.com";
    }

    static bool IsPinterestUrl(
        string url)
    {
        if (!TryGetHttpUri(
            url,
            out Uri? uri))
        {
            return false;
        }

        string host =
            uri.Host.ToLowerInvariant();

        return
            host == "pinterest.com" ||
            host.EndsWith(".pinterest.com") ||
            host == "pin.it";
    }

    static bool TryGetHttpUri(
        string value,
        out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(
            value.Trim(),
            UriKind.Absolute,
            out Uri? parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp &&
            parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        uri = parsed;

        return true;
    }

    // ============================================================
    // TELEGRAM PROGRESS
    // ============================================================

    static async Task SafeEditMessageAsync(
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken cancellationToken)
    {
        if (Bot is null)
            return;

        try
        {
            await Bot.EditMessageText(
                chatId,
                messageId,
                text,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Telegram progress edit: {ex.Message}");
        }
    }

    static string CreateProgressBar(
        double percent)
    {
        const int width = 20;

        percent =
            Math.Clamp(
                percent,
                0,
                100);

        int filled =
            (int)Math.Round(
                width * percent / 100.0);

        filled =
            Math.Clamp(
                filled,
                0,
                width);

        return
            new string('█', filled) +
            new string('░', width - filled);
    }

    // ============================================================
    // FILE UTILITIES
    // ============================================================

    static string? FindLargestMediaFile(
        string directory,
        bool includeMp4Only)
    {
        if (!Directory.Exists(directory))
            return null;

        string[] extensions =
            includeMp4Only
                ? new[] { ".mp4" }
                : new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v" };

        FileInfo? largest = null;

        foreach (string file in
                 Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                FileInfo info = new(file);

                if (!extensions.Contains(
                    info.Extension,
                    StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (info.Length < 10_000)
                    continue;

                if (largest is null ||
                    info.Length > largest.Length)
                {
                    largest = info;
                }
            }
            catch
            {
                // Ignore inaccessible files.
            }
        }

        return largest?.FullName;
    }

    static string FormatBytes(
        long bytes)
    {
        string[] units = new[]
        {
            "B",
            "KB",
            "MB",
            "GB"
        };

        double value = bytes;
        int index = 0;

        while (value >= 1024 &&
               index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return
            $"{value:F1} {units[index]}";
    }

    static void TryDeleteDirectory(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Cleanup error: {ex.Message}");
        }
    }

    static bool IsExecutableAvailable(
        string executable)
    {
        string path =
            Environment.GetEnvironmentVariable("PATH")
            ?? "";

        string[] names =
            OperatingSystem.IsWindows()
                ? [executable + ".exe", executable + ".cmd", executable + ".bat"]
                : [executable];

        foreach (string folder in
                 path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try
                {
                    if (File.Exists(
                        Path.Combine(
                            folder.Trim(),
                            name)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore invalid PATH entries.
                }
            }
        }

        return false;
    }
}

// ============================================================
// USER SESSION
// ============================================================

class UserSession
{
    public string? Platform { get; set; }

    public string? Format { get; set; }

    public bool WaitingForUrl { get; set; }

    public CancellationTokenSource? DownloadCancellation { get; set; }
}

// ============================================================
// CUSTOM EXCEPTIONS
// ============================================================

class MediaNotFoundException : Exception
{
    public MediaNotFoundException(
        string message)
        : base(message)
    {
    }
}

class ProcessFailedException : Exception
{
    public int ExitCode { get; }

    public string Output { get; }

    public ProcessFailedException(
        int exitCode,
        string output)
        : base($"Process exited with code {exitCode}.")
    {
        ExitCode = exitCode;
        Output = output;
    }
}
