using System;
using System.Collections.Generic;

namespace Interstellar.Loader;

internal enum ReleaseProduct
{
    Menu,
    Loader
}

internal sealed class ReleaseSection
{
    public string Heading { get; }
    public IReadOnlyList<string> Items { get; }
    public string ItemsText { get; }

    public ReleaseSection(string heading, params string[] items)
    {
        Heading = heading;
        Items = Array.AsReadOnly((string[])items.Clone());
        ItemsText = string.Join(" · ", Items);
    }
}

internal sealed class ReleaseEntry
{
    public string Version { get; }
    public string Title { get; }
    public bool IsPrerelease { get; }
    public IReadOnlyList<ReleaseSection> Sections { get; }

    public ReleaseEntry(string version, string title, bool isPrerelease,
        params ReleaseSection[] sections)
    {
        Version = version;
        Title = title;
        IsPrerelease = isPrerelease;
        Sections = Array.AsReadOnly((ReleaseSection[])sections.Clone());
    }
}

internal static class ReleaseCatalog
{
    private static readonly IReadOnlyList<ReleaseEntry> MenuEntries = Array.AsReadOnly(new[]
    {
        new ReleaseEntry("1.0.1", "Security and privacy update", false,
            new ReleaseSection("Improvements",
                "More robust player name handling",
                "Safer local file storage")),
        new ReleaseEntry("1.0.0", "First release", false,
            new ReleaseSection("Basic Mods",
                "WASD Fly",
                "PC Button Click",
                "Platforms",
                "Fly",
                "Joystick Fly",
                "Mute Gun",
                "Focus Voice",
                "Restart Microphone",
                "Anti AFK"),
            new ReleaseSection("Movement",
                "Speed Boost",
                "Noclip",
                "Uncap Max Velocity",
                "Wall Walk",
                "Pull Mod",
                "Teleport Gun",
                "Teleport to Map",
                "Low Gravity",
                "No Slippery Surfaces",
                "Slide Control"),
            new ReleaseSection("Rig",
                "Ghost",
                "Invis",
                "Jumpscare Gun",
                "Orbit Player Gun",
                "Chase Player Gun",
                "Spin Rig",
                "Upside Down Rig",
                "Stare At Closest",
                "Spin Head"),
            new ReleaseSection("World",
                "Morning",
                "Day",
                "Night",
                "No Rain",
                "Disable Network Triggers",
                "Disable Wind Barriers",
                "Fullbright"),
            new ReleaseSection("Room",
                "Reconnect",
                "Join Random",
                "Join DAISY09",
                "Join PBBV",
                "Join MODS"),
            new ReleaseSection("Infection",
                "Tag All",
                "Tag Gun",
                "No Tag On Join",
                "No Tag Freeze"),
            new ReleaseSection("Guardian",
                "Always Guardian",
                "Guardian Protector",
                "Guardian Fling All",
                "Guardian Fling Gun",
                "Guardian Orbit All",
                "Guardian Orbit Gun",
                "Guardian Freeze Gun"),
            new ReleaseSection("Paintbrawl",
                "Slingshot Aimbot",
                "Projectile Trajectories",
                "Restart Round"),
            new ReleaseSection("Visuals",
                "Name Tags",
                "Voice Indicators",
                "Automatic Tracers"),
            new ReleaseSection("Camera",
                "Camera View",
                "Camera FOV",
                "Free Camera",
                "Spectate Gun"))
    });

    private static readonly IReadOnlyList<ReleaseEntry> LoaderEntries = Array.AsReadOnly(new[]
    {
        new ReleaseEntry("1.0.1", "Security and privacy update", false,
            new ReleaseSection("Privacy",
                "Hide personal folder and network share details in diagnostics and error logs"),
            new ReleaseSection("Menu",
                "Includes Interstellar Menu v1.0.1")),
        new ReleaseEntry("1.0.0", "First release", false,
            new ReleaseSection("Menu",
                "Install and repair Interstellar Menu",
                "Load the installed menu while Gorilla Tag is running",
                "Launch Gorilla Tag with Interstellar enabled",
                "Launch Gorilla Tag without Interstellar",
                "Restore the previous installed build",
                "Remove the installed menu"),
            new ReleaseSection("Game",
                "Detect Gorilla Tag or choose its install folder",
                "Open Gorilla Tag installation and file repair in Steam",
                "Install, repair and uninstall BepInEx",
                "Open the game folder"),
            new ReleaseSection("Loader",
                "Menu and loader release history",
                "Close to tray",
                "Launch preference",
                "Open logs and copy diagnostics",
                "Single instance support"))
    });

    public static IReadOnlyList<ReleaseEntry> GetEntries(ReleaseProduct product) => product switch
    {
        ReleaseProduct.Menu => MenuEntries,
        ReleaseProduct.Loader => LoaderEntries,
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    };

    public static IReadOnlyList<ReleaseEntry> GetEntries(ReleaseProduct product, UpdateCandidate? latest)
    {
        var existing = GetEntries(product);
        if (latest == null) return existing;
        var entries = existing.Select(entry => new ReleaseEntry(entry.Version, entry.Title, false,
            entry.Sections.ToArray())).ToList();
        if (!entries.Any(entry => entry.Version == latest.Version))
        {
            var notes = string.IsNullOrWhiteSpace(latest.ReleaseNotes)
                ? Array.Empty<ReleaseSection>() : new[] { new ReleaseSection("Release notes", latest.ReleaseNotes.Trim()) };
            entries.Insert(0, new ReleaseEntry(latest.Version, "Latest release", false, notes));
        }
        return entries.AsReadOnly();
    }
}
