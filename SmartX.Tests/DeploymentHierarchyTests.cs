using SmartX.Api.Services;

namespace SmartX.Tests;

public sealed class DeploymentHierarchyTests
{
    private readonly DeploymentValidator _validator = new();

    [Fact]
    public void Facility_A_paths_and_categories_stay_as_they_were()
    {
        var gutter = _validator.ValidateNodePath("Facility A -> Zone 1 -> Sub-Zone B", "Environmental");
        Assert.True(gutter.IsValid);
        Assert.Equal("Node is safely nested in the campus tree.", gutter.Reason);
        Assert.Contains("visit[1]: Facility A", gutter.WalkTrace);

        var powerInGutter = _validator.ValidateNodePath("Facility A -> Zone 1 -> Sub-Zone B", "PowerConsumption");
        Assert.False(powerInGutter.IsValid);
        Assert.Contains("not permitted", powerInGutter.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(_validator.ValidateNodePath("Facility A -> Zone 1 -> Sub-Zone Reservoir", "Actuator").IsValid);
        Assert.True(_validator.ValidateNodePath("Facility A -> Zone 2 -> Sub-Zone Canopy", "Environmental").IsValid);
        Assert.Equal(6, Count(node: _validator.Root));
    }

    [Fact]
    public void Seeded_facility_B_and_C_paths_validate_without_joining_the_radio_graph()
    {
        Assert.True(_validator.ValidateNodePath("Facility B -> Zone 1 -> Transformer Bay", "PowerConsumption").IsValid);
        Assert.True(_validator.ValidateNodePath("Facility B -> Zone 1 -> Transformer Bay", "Actuator").IsValid);
        Assert.False(_validator.ValidateNodePath("Facility B -> Zone 1 -> Transformer Bay", "Environmental").IsValid);

        Assert.True(_validator.ValidateNodePath("Facility C -> Zone Lobby -> Stack B", "Environmental").IsValid);
        Assert.True(_validator.ValidateNodePath("Facility C -> Zone Lobby -> Stack B", "Actuator").IsValid);
        Assert.False(_validator.ValidateNodePath("Facility C -> Zone Lobby -> Stack B", "PowerConsumption").IsValid);

        var missing = _validator.ValidateNodePath("Facility D -> Zone 1 -> Nowhere");
        Assert.False(missing.IsValid);
        Assert.Contains("does not exist", missing.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, _validator.Facilities.Count);
        Assert.Equal("Facility A", _validator.Root.Name);
        Assert.Equal(12, _validator.CountNodes());
    }

    private static int Count(SmartX.Shared.Deployment.DeploymentNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += Count(child);
        }

        return count;
    }
}
