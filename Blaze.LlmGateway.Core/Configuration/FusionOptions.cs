namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for the <c>fusion</c> virtual model — a MoA-Lite mixture-of-agents mode
/// that fans one prompt out to several proposer models in parallel and synthesizes their
/// answers with a single aggregator model. Binds from <c>LlmGateway:Fusion</c>.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <c>false</c> the fusion client passes every request straight
/// through to the CodebrewRouter single-model pipeline, so a request for <c>model=fusion</c>
/// behaves exactly like <c>auto</c> until fusion is switched on.
/// </remarks>
public class FusionOptions
{
    /// <summary>When false, fusion is inert: all requests delegate to the CodebrewRouter pipeline.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maps each <c>TaskType</c> name to an ordered list of proposer provider DI keys.
    /// The first <see cref="MaxProposers"/> resolvable keys are fanned out. Falls back to the
    /// <c>General</c> entry when the classified task has no dedicated set.
    /// </summary>
    public Dictionary<string, string[]> ProposerSetsByTaskClass { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps each <c>TaskType</c> name to ordered aggregator provider DI keys (strongest first).
    /// The synthesizer tries them in order if an earlier one fails before its first token.
    /// Falls back to the <c>General</c> entry, then to the first successful proposer's model.
    /// </summary>
    public Dictionary<string, string[]> AggregatorByTaskClass { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum number of proposers to fan out to per request.</summary>
    public int MaxProposers { get; set; } = 3;

    /// <summary>Minimum proposer successes required before the straggler grace window starts.</summary>
    public int MinProposers { get; set; } = 2;

    /// <summary>Hard per-proposer timeout.</summary>
    public int ProposerTimeoutSeconds { get; set; } = 30;

    /// <summary>Once <see cref="MinProposers"/> have completed, how long to wait for stragglers before cancelling them.</summary>
    public int StragglerGraceSeconds { get; set; } = 6;

    /// <summary>Proposer sampling temperature (diversity). Applied only when the caller left <c>Temperature</c> unset.</summary>
    public float ProposerTemperature { get; set; } = 0.7f;

    /// <summary>Aggregator sampling temperature (faithful synthesis).</summary>
    public float AggregatorTemperature { get; set; } = 0.4f;

    /// <summary>Caps each proposer's output to bound cost.</summary>
    public int ProposerMaxOutputTokens { get; set; } = 1536;

    /// <summary>Each proposer response is truncated to this many characters before being injected as a reference (rough token guard).</summary>
    public int MaxReferenceChars { get; set; } = 8000;
}
