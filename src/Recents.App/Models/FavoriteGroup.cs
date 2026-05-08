namespace Recents.App.Models;

public sealed class FavoriteGroup
{
    public const string DefaultGroupId = "default";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsCollapsed { get; set; }

    public static bool IsDefaultGroupId(string? id) =>
        string.Equals(id, DefaultGroupId, StringComparison.OrdinalIgnoreCase);
}
