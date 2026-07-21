namespace TypeWhisper.Core.Models;

/// <summary>How a snippet's trigger is matched: anywhere within the text, or only when it stands alone as an exact phrase.</summary>
public enum SnippetTriggerMode
{
    Anywhere,
    ExactPhrase,
}
