namespace Blaze.LlmGateway.Core;

public enum RouteDestination
{
    LocalGemma,
    
    OllamaRouter,
    LmStudio,

    OpenCodeGo_DeepSeekV4Pro,
    OpenCodeGo_DeepSeekV4Flash,
    OpenCodeGo_Qwen3_5Plus,
    OpenCodeGo_Qwen3_6Plus,
    OpenCodeGo_KimiK2_5,
    OpenCodeGo_KimiK2_6,
    OpenCodeGo_GLM5,
    OpenCodeGo_GLM5_1,
    OpenCodeGo_MiniMaxM2_5,
    OpenCodeGo_MiniMaxM2_7,
    OpenCodeGo_MiMoV2Pro,
    OpenCodeGo_MiMoV2_5,
    OpenCodeGo_MiMoV2_5Pro,
    OpenCodeGo_MiMoV2Omni
}

public static class OpenCodeGoModels
{
    public static readonly IReadOnlyDictionary<RouteDestination, string> ModelNames = new Dictionary<RouteDestination, string>
    {
        [RouteDestination.OpenCodeGo_DeepSeekV4Pro]  = "deepseek-v4-pro",
        [RouteDestination.OpenCodeGo_DeepSeekV4Flash] = "deepseek-v4-flash",
        [RouteDestination.OpenCodeGo_Qwen3_5Plus]     = "qwen3.5-plus",
        [RouteDestination.OpenCodeGo_Qwen3_6Plus]     = "qwen3.6-plus",
        [RouteDestination.OpenCodeGo_KimiK2_5]        = "kimi-k2.5",
        [RouteDestination.OpenCodeGo_KimiK2_6]        = "kimi-k2.6",
        [RouteDestination.OpenCodeGo_GLM5]            = "glm-5",
        [RouteDestination.OpenCodeGo_GLM5_1]          = "glm-5.1",
        [RouteDestination.OpenCodeGo_MiniMaxM2_5]     = "mini-max-m2.5",
        [RouteDestination.OpenCodeGo_MiniMaxM2_7]     = "mini-max-m2.7",
        [RouteDestination.OpenCodeGo_MiMoV2Pro]       = "mimo-v2-pro",
        [RouteDestination.OpenCodeGo_MiMoV2_5]        = "mimo-v2.5",
        [RouteDestination.OpenCodeGo_MiMoV2_5Pro]     = "mimo-v2.5-pro",
        [RouteDestination.OpenCodeGo_MiMoV2Omni]      = "mimo-v2-omni",
    };
}
