using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AtSpiEventClientFocusTests
{
    [Fact]
    public void FocusLoss_ClearsCurrentFocus()
    {
        using var client = CreateClient();
        var element = new AtSpiElementRef("app-loss", "/field/loss");

        RaiseFocus(client, element, detail1: 1);
        Assert.Equal(element, client.CurrentFocusedElement);

        RaiseFocus(client, element, detail1: 0);
        Assert.Null(client.CurrentFocusedElement);
    }

    [Fact]
    public void GainThenLossOfOldElement_PreservesNewFocus()
    {
        using var client = CreateClient();
        var oldElement = new AtSpiElementRef("app-order-old", "/field/order-old");
        var newElement = new AtSpiElementRef("app-order-new", "/field/order-new");

        RaiseFocus(client, oldElement, detail1: 1);
        RaiseFocus(client, newElement, detail1: 1);
        Assert.Equal(newElement, client.CurrentFocusedElement);

        RaiseFocus(client, oldElement, detail1: 0);
        Assert.Equal(newElement, client.CurrentFocusedElement);

        // Prove the stale-loss assertion did not pass only because every loss was ignored.
        RaiseFocus(client, newElement, detail1: 0);
        Assert.Null(client.CurrentFocusedElement);
    }

    [Fact]
    public void LossThenGainOfNewElement_EndsOnNewFocus()
    {
        using var client = CreateClient();
        var oldElement = new AtSpiElementRef("app-common-old", "/field/common-old");
        var newElement = new AtSpiElementRef("app-common-new", "/field/common-new");

        RaiseFocus(client, oldElement, detail1: 1);
        RaiseFocus(client, oldElement, detail1: 0);
        RaiseFocus(client, newElement, detail1: 1);

        Assert.Equal(newElement, client.CurrentFocusedElement);
    }

    [Fact]
    public void LossOfElementThatWasNeverCurrent_DoesNotChangeCurrentFocus()
    {
        using var client = CreateClient();
        var currentElement = new AtSpiElementRef("app-current", "/field/current");
        var neverCurrentElement = new AtSpiElementRef("app-never-current", "/field/never-current");

        RaiseFocus(client, currentElement, detail1: 1);
        RaiseFocus(client, neverCurrentElement, detail1: 0);

        Assert.Equal(currentElement, client.CurrentFocusedElement);
    }

    [Fact]
    public void FocusChanged_DoesNotFireOnFocusLoss()
    {
        using var client = CreateClient();
        var element = new AtSpiElementRef("app-event", "/field/event");
        var notifications = new List<AtSpiElementRef>();
        client.FocusChanged += notifications.Add;

        RaiseFocus(client, element, detail1: 1);
        Assert.Equal([element], notifications);

        RaiseFocus(client, element, detail1: 0);
        Assert.Equal([element], notifications);
    }

    private static AtSpiEventClient CreateClient()
    {
        return new AtSpiEventClient(new NullErrorLogService());
    }

    private static void RaiseFocus(AtSpiEventClient client, AtSpiElementRef element, int detail1)
    {
        client.HandleStateChanged(
            null,
            new AtSpiEventClient.AtSpiSignal(
                element.BusName,
                element.ObjectPath,
                "focused",
                detail1
            ),
            null,
            null
        );
    }

    private sealed class NullErrorLogService : IErrorLogService
    {
        public IReadOnlyList<ErrorLogEntry> Entries => [];

        public void AddEntry(string message, string category = ErrorCategory.General)
        {
        }

        public void ClearAll()
        {
        }

        public string ExportDiagnostics()
        {
            return string.Empty;
        }

        public event Action? EntriesChanged
        {
            add { }
            remove { }
        }
    }
}
