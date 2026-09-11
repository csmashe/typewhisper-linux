namespace TypeWhisper.Core.Models;

/// <summary>Regional spelling applied to final German output; only Switzerland rewrites anything today (ß → ss).</summary>
public enum GermanOutputVariant
{
    AsTranscribed,
    Germany,
    Austria,
    Switzerland,
}
