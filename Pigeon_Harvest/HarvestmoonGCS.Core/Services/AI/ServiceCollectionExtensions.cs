using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using HarvestmoonGCS.Core.Models.AI;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.AI;
using HarvestmoonGCS.Services;

namespace HarvestmoonGCS.Core.Services.AI;

/// <summary>
/// Extension methods for registering AI services in the dependency injection container
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all AI services as singletons in the dependency injection container.
    /// Includes: GeminiFallbackService, LLMServiceFactory, and ILLMService resolution
    /// </summary>
    /// <param name="services">The service collection to add AI services to</param>
    /// <param name="configureSettings">Optional action to configure AI settings</param>
    /// <returns>The service collection for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    public static IServiceCollection AddAIServices(
        this IServiceCollection services,
        Action<AISettings>? configureSettings = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Configure AI settings from persisted app settings when available.
        services.AddSingleton<AISettings>(sp =>
        {
            var settingsService = sp.GetService<ISettingsService>();
            var settings = settingsService?.Settings?.AI ?? new AISettings();
            var keyStore = sp.GetService<IApiKeyStore>();
            NormalizeProviderSettings(settings);
            var provider = settings.Provider;
            string? secureApiKey;
            try
            {
                secureApiKey = keyStore?.GetApiKey(provider);
            }
            catch
            {
                secureApiKey = null;
            }

            if (!string.IsNullOrWhiteSpace(secureApiKey))
            {
                settings.ApiKey = secureApiKey;
            }
            else if (!string.IsNullOrWhiteSpace(settings.ApiKey) && keyStore != null)
            {
                // One-time migration from old plaintext settings payload to secure store.
                bool migrated;
                try
                {
                    migrated = keyStore.SaveApiKey(provider, settings.ApiKey);
                }
                catch
                {
                    migrated = false;
                }

                if (migrated)
                {
                    try
                    {
                        settingsService?.SaveSettingsAsync().GetAwaiter().GetResult();
                    }
                    catch
                    {
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                settings.ApiKey = ResolveEnvironmentApiKey(provider);
            }

            configureSettings?.Invoke(settings);
            NormalizeProviderSettings(settings);
            return settings;
        });

        // Register HttpClient for AI services
        services.AddSingleton<HttpClient>(sp =>
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            return client;
        });

        services.AddSingleton<OpenRouterService>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var settings = sp.GetRequiredService<AISettings>();
            return new OpenRouterService(
                settings,
                () => GetProviderApiKey(sp, settings, "OpenRouter"),
                ResolveBaseUrl("OpenRouter"),
                httpClient);
        });
        services.AddSingleton<OpenAIService>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var settings = sp.GetRequiredService<AISettings>();
            return new OpenAIService(
                httpClient,
                settings,
                () => GetProviderApiKey(sp, settings, "OpenAI"));
        });
        services.AddSingleton<GrokService>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var settings = sp.GetRequiredService<AISettings>();
            return new GrokService(
                httpClient,
                settings,
                () => GetProviderApiKey(sp, settings, "Grok"));
        });

        // Register GeminiFallbackService as singleton
        services.AddSingleton<GeminiFallbackService>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var settings = sp.GetRequiredService<AISettings>();
            var model = "gemini-2.0-flash";
            return new GeminiFallbackService(
                httpClient,
                () => GetProviderApiKey(sp, settings, "Gemini"),
                model);
        });

        services.AddSingleton<LLMServiceFactory>(sp =>
        {
            var settings = sp.GetRequiredService<AISettings>();
            var openRouter = sp.GetRequiredService<OpenRouterService>();
            var gemini = sp.GetRequiredService<GeminiFallbackService>();
            var openAi = sp.GetRequiredService<OpenAIService>();
            var grok = sp.GetRequiredService<GrokService>();
            var primary = ResolvePrimaryProvider(settings.Provider, openRouter, gemini, openAi, grok);
            var fallback = ResolveConfiguredFallbackProvider(
                settings.FallbackProvider,
                primary,
                openRouter,
                gemini,
                openAi,
                grok);
            return new LLMServiceFactory(primary, fallback);
        });

        services.AddSingleton<ILLMService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            return factory.GetService();
        });

        return services;
    }

    /// <summary>
    /// Registers the LLM service factory with a specific primary service.
    /// Use this when you have a custom primary LLM service implementation.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="primaryService">The primary LLM service to use</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLLMServiceFactory(
        this IServiceCollection services,
        ILLMService primaryService)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));
        if (primaryService == null)
            throw new ArgumentNullException(nameof(primaryService));

        services.AddSingleton<LLMServiceFactory>(sp =>
        {
            return new LLMServiceFactory(primaryService, primaryService);
        });

        return services;
    }

    private static ILLMService ResolvePrimaryProvider(
        string? provider,
        OpenRouterService openRouter,
        GeminiFallbackService gemini,
        OpenAIService openAi,
        GrokService grok)
    {
        var normalized = (provider ?? "OpenRouter").Trim().ToLowerInvariant();
        return normalized switch
        {
            "gemini" => gemini,
            "openai" => openAi,
            "grok" or "xai" => grok,
            _ => openRouter
        };
    }

    private static ILLMService ResolveFallbackProvider(
        ILLMService primary,
        OpenRouterService openRouter,
        GeminiFallbackService gemini,
        OpenAIService openAi,
        GrokService grok)
    {
        var candidates = new ILLMService[] { openRouter, gemini, openAi, grok };
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, primary))
            {
                return candidate;
            }
        }

        return new UnavailableLLMService();
    }

    private static ILLMService ResolveConfiguredFallbackProvider(
        string? configuredFallbackProvider,
        ILLMService primary,
        OpenRouterService openRouter,
        GeminiFallbackService gemini,
        OpenAIService openAi,
        GrokService grok)
    {
        var preferred = ResolveProviderByName(configuredFallbackProvider, openRouter, gemini, openAi, grok);
        if (preferred != null)
        {
            return preferred;
        }

        return ResolveFallbackProvider(primary, openRouter, gemini, openAi, grok);
    }

    private static ILLMService? ResolveProviderByName(
        string? provider,
        OpenRouterService openRouter,
        GeminiFallbackService gemini,
        OpenAIService openAi,
        GrokService grok)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "openrouter" => openRouter,
            "gemini" => gemini,
            "openai" => openAi,
            "grok" or "xai" => grok,
            _ => null
        };
    }

    private static string GetProviderApiKey(IServiceProvider serviceProvider, AISettings settings, string provider)
    {
        var keyStore = serviceProvider.GetService<IApiKeyStore>();

        try
        {
            var secure = keyStore?.GetApiKey(provider);
            if (!string.IsNullOrWhiteSpace(secure))
            {
                return secure;
            }
        }
        catch
        {
        }

        if (string.Equals(settings.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return settings.ApiKey;
        }

        return ResolveEnvironmentApiKey(provider);
    }

    private static string ResolveEnvironmentApiKey(string provider)
    {
        var envName = (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "openrouter" => "OPENROUTER_API_KEY",
            "gemini" => "GEMINI_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "grok" or "xai" => "XAI_API_KEY",
            _ => null
        };

        return string.IsNullOrWhiteSpace(envName)
            ? string.Empty
            : Environment.GetEnvironmentVariable(envName) ?? string.Empty;
    }

    private static void NormalizeProviderSettings(AISettings settings)
    {
        settings.Provider = NormalizeProviderName(settings.Provider);
        settings.FallbackProvider = NormalizeFallbackProvider(settings.Provider, settings.FallbackProvider);
        settings.BaseUrl = ResolveBaseUrl(settings.Provider);
    }

    private static string NormalizeFallbackProvider(string primaryProvider, string? fallbackProvider)
    {
        var fallback = NormalizeProviderName(fallbackProvider);
        if (string.Equals(primaryProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fallback, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini";
        }

        return fallback;
    }

    private static string NormalizeProviderName(string? provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "gemini" => "Gemini",
            "openai" => "OpenAI",
            "grok" or "xai" => "Grok",
            _ => "OpenRouter"
        };
    }

    private static string ResolveBaseUrl(string? provider)
    {
        return NormalizeProviderName(provider) switch
        {
            "Gemini" => "https://generativelanguage.googleapis.com",
            "OpenAI" => "https://api.openai.com/v1",
            "Grok" => "https://api.x.ai/v1",
            _ => "https://openrouter.ai/api/v1"
        };
    }

#if !__WASM__
    /// <summary>
    /// Registers all PIA (Pigeon Intelligent Assistant) AI services.
    /// Must be called after AddAIServices() to ensure AISettings and HttpClient are available.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureAnomalyDetection">Optional action to configure anomaly detection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPIAIntelligence(
        this IServiceCollection services,
        Action<AnomalyDetectionConfig>? configureAnomalyDetection = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Keep 30-minute rolling context to support post-flight summary and evaluation.
        services.AddSingleton<TelemetryBuffer>(sp => new TelemetryBuffer(windowMinutes: 30, maxSnapshots: 3600));
        services.AddSingleton<ITelemetryBufferProvider, TelemetryBufferAdapter>();
        services.AddSingleton<ITelemetrySampler, TelemetrySampler>();
        services.AddSingleton<IAnomalyEvaluationService, AnomalyEvaluationService>();

        services.AddSingleton<IAnomalyDetector, RuleBasedDetector>(sp =>
        {
            var settings = sp.GetRequiredService<AISettings>();
            return new RuleBasedDetector(settings.AnomalyDetection.Thresholds);
        });

        services.AddSingleton<IAnomalyDetector, StatisticalDetector>(sp =>
        {
            var settings = sp.GetRequiredService<AISettings>();
            return new StatisticalDetector(settings);
        });

        services.AddSingleton<IAnomalyDetector, AIAnomalyDetector>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var settings = sp.GetRequiredService<AISettings>();
            return new AIAnomalyDetector(factory.GetService(), settings);
        });

        services.AddSingleton<IAnomalyDetectionService, AnomalyDetectionService>(sp =>
        {
            var alertManager = sp.GetRequiredService<HarvestmoonGCS.Core.Services.AlertManager>();
            var detectors = sp.GetServices<IAnomalyDetector>();
            var ruleBased = detectors.First(d => d.Name == "RuleBased");
            var statistical = detectors.FirstOrDefault(d => d.Name == "Statistical");
            var ai = detectors.FirstOrDefault(d => d.Name == "AI");

            // Use the live AISettings.AnomalyDetection instance — not a detached copy — so the
            // RuleBased/Statistical/AI toggles on the AI Settings page genuinely gate these
            // detection layers instead of only updating a config object nobody reads.
            var settings = sp.GetRequiredService<AISettings>();
            var config = settings.AnomalyDetection;
            configureAnomalyDetection?.Invoke(config);

            return new AnomalyDetectionService(alertManager, ruleBased, statistical, ai, config);
        });

        services.AddSingleton<TelemetryAnalysisService>(sp =>
        {
            var settings = sp.GetRequiredService<AISettings>();
            var bufferProvider = sp.GetRequiredService<ITelemetryBufferProvider>();
            var factory = sp.GetRequiredService<LLMServiceFactory>();

            return new TelemetryAnalysisService(settings, bufferProvider, () => factory.GetService());
        });

        services.AddSingleton<NaturalLanguageService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var buffer = sp.GetRequiredService<TelemetryBuffer>();
            return new NaturalLanguageService(() => factory.GetService(), buffer);
        });

        services.AddSingleton<IVoiceCommandService>(sp =>
        {
            var mavlink = sp.GetService<IMavLinkService>();
            var settings = sp.GetRequiredService<AISettings>();
            var voiceRecognition = sp.GetService<IVoiceRecognitionService>();
            var camera = sp.GetService<ICameraService>();
            var historyStore = sp.GetService<IPIAHistoryStore>();
            var speech = sp.GetService<ISpeechService>();
            return new VoiceCommandService(mavlink, settings, voiceRecognition, camera, historyStore, speech);
        });

        services.AddSingleton<MaintenancePredictionService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var buffer = sp.GetRequiredService<TelemetryBuffer>();
            var settings = sp.GetRequiredService<AISettings>();
            return new MaintenancePredictionService(() => factory.GetService(), buffer, settings);
        });

        services.AddSingleton<BatteryPredictionService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var buffer = sp.GetRequiredService<TelemetryBuffer>();
            return new BatteryPredictionService(() => factory.GetService(), buffer);
        });

        services.AddSingleton<FlightSessionSummaryService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var buffer = sp.GetRequiredService<TelemetryBuffer>();
            return new FlightSessionSummaryService(() => factory.GetService(), buffer);
        });

        services.AddSingleton<PerformanceScoringService>(sp =>
        {
            var factory = sp.GetRequiredService<LLMServiceFactory>();
            var buffer = sp.GetRequiredService<TelemetryBuffer>();
            return new PerformanceScoringService(() => factory.GetService(), buffer);
        });

        return services;
    }
#endif
}
