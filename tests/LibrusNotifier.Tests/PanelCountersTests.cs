using LibrusApixNet;
using LibrusNotifier.Librus;

namespace LibrusNotifier.Tests;

public sealed class PanelCountersTests
{
    [Theory]
    [InlineData("/wiadomosci3")]
    [InlineData("/wiadomosci")]
    public void MessagesCounterIsRecognizedAtCurrentAndLegacyAddress(string destination)
    {
        var counters = PanelCounters.From([new NotificationAmount("Wiadomości", destination, 3)]);

        Assert.Equal(3, counters.Counts[ModuleKind.Messages]);
        Assert.Equal([ModuleKind.Messages], counters.NonZero);
    }

    [Fact]
    public void AllModulesOfTheLivePanelMapToTheirKinds()
    {
        var counters = PanelCounters.From(
        [
            new NotificationAmount("Oceny", "/przegladaj_oceny/uczen", 1),
            new NotificationAmount("Frekwencja", "/przegladaj_nb/uczen", 2),
            new NotificationAmount("Wiadomości", "/wiadomosci3", 3),
            new NotificationAmount("Ogłoszenia", "/ogloszenia", 4),
            new NotificationAmount("Terminarz", "/terminarz", 5),
            new NotificationAmount("Zadania domowe", "/moje_zadania", 6)
        ]);

        Assert.Equal(6, counters.Counts.Count);
        Assert.Equal(3, counters.Counts[ModuleKind.Messages]);
    }

    [Fact]
    public void UnknownDestinationIsIgnored()
    {
        Assert.Empty(PanelCounters.From([new NotificationAmount("Inne", "/cos_innego", 9)]).Counts);
    }
}
