param(
[Parameter(Mandatory = $false)]
[ValidateSet(
        "Gemma4E4B_LMK",
        "Gemma4E4B_Q8_0",
        "Gemma4E4B_Q4_K_M",
        "Gemma4E2B_Q8_0",
        "Gemma3_1b_Q4_K_M",
        "Gemma1_1_2b_Q8_0",
        "Gemma4E4B_Unsloth_Q4_0",
        "Custom"
    )]
    [string]$Preset = "Gemma4E4B_LMK",

    [Parameter(Mandatory = $false)]
    [string]$CustomUrl,

    [Parameter(Mandatory = $false)]
    [switch]$List
)

$presets = @{
    Gemma4E4B_LMK = @{
        Name = "Gemma 4 E4B LM-Kit catalog build (recommended for LM-Kit runtime)"
        ModelPath = "https://huggingface.co/lm-kit/gemma-4-e4b-instruct-lmk/resolve/main/Gemma-4-E4B-It-7.5B-Q4_K_M.lmk"
    }
    Gemma4E4B_Q8_0 = @{
        Name = "Gemma 4 E4B Q8_0 (broader runtime compatibility)"
        ModelPath = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q8_0.gguf"
    }
    Gemma4E4B_Q4_K_M = @{
        Name = "Gemma 4 E4B Q4_K_M (compact, 5.3GB)"
        ModelPath = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf"
    }
    Gemma4E4B_Unsloth_Q4_0 = @{
        Name = "Gemma 4 E4B Unsloth Q4_0 (alternative GGUF pack)"
        ModelPath = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_0.gguf"
    }
    Gemma4E2B_Q8_0 = @{
        Name = "Gemma 4 E2B Q8_0 (smaller / lower memory)"
        ModelPath = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q8_0.gguf"
    }
    Gemma3_1b_Q4_K_M = @{
        Name = "Gemma 3 1B Q4_K_M (tiny/offline-friendly)"
        ModelPath = "https://huggingface.co/ggml-org/gemma-3-1b-it-GGUF/resolve/main/gemma-3-1b-it-Q4_K_M.gguf"
    }
    Gemma1_1_2b_Q8_0 = @{
        Name = "Gemma 1.1 2B Q8_0 (very small / reliable)"
        ModelPath = "https://huggingface.co/ggml-org/gemma-1.1-2b-it-Q8_0-GGUF/resolve/main/gemma-1.1-2b-it.Q8_0.gguf"
    }
}

if ($List)
{
    Write-Host "Available local Gemma presets:"
    foreach ($entry in $presets.GetEnumerator())
    {
        Write-Host (" - {0}: {1}" -f $entry.Key, $entry.Value.Name)
        Write-Host ("   {0}" -f $entry.Value.ModelPath)
    }
    Write-Host ""
    Write-Host "Use -Preset Custom -CustomUrl <url> to define your own GGUF URL."
    return
}

if ($Preset -eq "Custom")
{
    if ([string]::IsNullOrWhiteSpace($CustomUrl))
    {
        throw "Custom preset selected but no -CustomUrl was provided."
    }

    $selected = @{
        Name = "Custom local Gemma model"
        ModelPath = $CustomUrl
    }
}
else
{
    $selected = $presets[$Preset]
}

$env:LlmGateway__LocalInference__ModelPath = $selected.ModelPath
$env:LlmGateway__LocalInference__WarmupEnabled = "true"
$env:LlmGateway__LocalInference__BlockStartupUntilWarm = "true"
$env:LlmGateway__LocalInference__WarmupTimeoutSeconds = "120"

Write-Host "Configured local Gemma preset: $($selected.Name)"
Write-Host "  ModelPath: $($selected.ModelPath)"
Write-Host ""
Write-Host "Run your app in this shell to use the selected preset."
Write-Host ""
