namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>Fail-closed field eligibility shared by focus-lock capture and restore.</summary>
internal static class AtSpiEventClientExtensions
{
    /// <summary>
    ///     True only when the element is positively not a password field and positively editable;
    ///     a null (unreadable) answer to either question counts as ineligible.
    /// </summary>
    public static async Task<bool> IsLockableFieldAsync(
        this IAtSpiEventClient client, AtSpiElementRef element, CancellationToken cancellationToken = default)
    {
        return await client.IsPasswordFieldAsync(element).WaitAsync(cancellationToken) == false
            && await client.IsElementEditableAsync(element).WaitAsync(cancellationToken) == true;
    }
}
