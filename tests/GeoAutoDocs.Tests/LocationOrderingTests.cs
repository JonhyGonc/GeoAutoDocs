using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class LocationOrderingTests
{
    [Fact]
    public void ApplyUsesRequestedOrderWithoutChangingLocationsOrSections()
    {
        var first = new Location { Name = "PV 01", Order = 0, Sections = [new InspectionSection { Name = "Trecho 01" }] };
        var second = new Location { Name = "PV 02", Order = 1, Sections = [new InspectionSection { Name = "Trecho 02" }] };
        var third = new Location { Name = "PV 03", Order = 2, Sections = [new InspectionSection { Name = "Trecho 03" }] };
        var project = new Project { Locations = [first, second, third] };

        LocationOrdering.Apply(project, [third.Id, first.Id, second.Id]);

        Assert.Equal([third.Id, first.Id, second.Id],
            project.Locations.OrderBy(x => x.Order).Select(x => x.Id).ToArray());
        Assert.Equal(["Trecho 01", "Trecho 02", "Trecho 03"],
            project.Locations.SelectMany(x => x.Sections).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void ApplyRejectsMissingDuplicateOrUnknownLocations()
    {
        var first = new Location { Name = "PV 01" };
        var second = new Location { Name = "PV 02" };
        var project = new Project { Locations = [first, second] };

        Assert.Throws<InvalidDataException>(() => LocationOrdering.Apply(project, [first.Id]));
        Assert.Throws<InvalidDataException>(() => LocationOrdering.Apply(project, [first.Id, first.Id]));
        Assert.Throws<InvalidDataException>(() => LocationOrdering.Apply(project, [first.Id, Guid.NewGuid()]));
    }
}
