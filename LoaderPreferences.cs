using System.IO;
using System.Text;
using System.Xml;

namespace Interstellar.Loader;

public sealed class LoaderPreferences
{
    public string GameDirectory { get; set; } = "";
    public bool CloseToTray { get; set; }
    public bool LaunchWithMenu { get; set; } = true;
    public bool CheckForUpdatesOnOpen { get; set; }
    public bool AutoUpdateMenu { get; set; } = true;
    public bool AutoUpdateLoader { get; set; } = true;
}

public static class LoaderSettingsStore
{
    private static readonly object gate = new();

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Interstellar", "Loader", "settings.xml");

    public static string? LastError { get; private set; }

    public static LoaderPreferences Load() => LoadFromFile(SettingsPath);

    internal static LoaderPreferences LoadFromFile(string path)
    {
        lock (gate)
        {
            LastError = null;
            if (!File.Exists(path)) return new LoaderPreferences();
            try
            {
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 65536
                })) document.Load(reader);
                var root = document.DocumentElement;
                if (root is null || root.Name != "LoaderSettings")
                    throw new XmlException("The settings file does not contain loader settings.");

                var preferences = new LoaderPreferences();
                var directory = root.SelectSingleNode("GameDirectory")?.InnerText ?? "";
                if (directory.Length > 32767) throw new XmlException("The saved game directory is too long.");
                preferences.GameDirectory = directory.Trim();
                preferences.CloseToTray = ReadBoolean(root, "CloseToTray", preferences.CloseToTray);
                preferences.LaunchWithMenu = ReadBoolean(root, "LaunchWithMenu", preferences.LaunchWithMenu);
                preferences.CheckForUpdatesOnOpen = ReadBoolean(root, "CheckForUpdatesOnOpen", preferences.CheckForUpdatesOnOpen);
                preferences.AutoUpdateMenu = ReadUpdateBoolean(root, "AutoUpdateMenu", preferences.AutoUpdateMenu);
                preferences.AutoUpdateLoader = ReadUpdateBoolean(root, "AutoUpdateLoader", preferences.AutoUpdateLoader);
                return preferences;
            }
            catch (Exception exception) when (IsSettingsFailure(exception))
            {
                LastError = "The saved loader settings could not be read. Defaults are being used. " + exception.Message;
                return new LoaderPreferences();
            }
        }
    }

    public static bool Save(LoaderPreferences preferences) => SaveToFile(SettingsPath, preferences);

    internal static bool SaveToFile(string path, LoaderPreferences preferences)
    {
        if (preferences is null) throw new ArgumentNullException(nameof(preferences));
        lock (gate)
        {
            LastError = null;
            string? temporaryPath = null;
            try
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        Indent = true,
                        CloseOutput = false
                    }))
                    {
                        writer.WriteStartDocument();
                        writer.WriteStartElement("LoaderSettings");
                        writer.WriteAttributeString("version", "1");
                        writer.WriteElementString("GameDirectory", (preferences.GameDirectory ?? "").Trim());
                        writer.WriteElementString("CloseToTray", XmlConvert.ToString(preferences.CloseToTray));
                        writer.WriteElementString("LaunchWithMenu", XmlConvert.ToString(preferences.LaunchWithMenu));
                        writer.WriteElementString("CheckForUpdatesOnOpen", XmlConvert.ToString(preferences.CheckForUpdatesOnOpen));
                        writer.WriteElementString("AutoUpdateMenu", XmlConvert.ToString(preferences.AutoUpdateMenu));
                        writer.WriteElementString("AutoUpdateLoader", XmlConvert.ToString(preferences.AutoUpdateLoader));
                        writer.WriteEndElement();
                        writer.WriteEndDocument();
                    }
                    stream.Flush(true);
                }

                if (File.Exists(path)) File.Replace(temporaryPath, path, null, true);
                else File.Move(temporaryPath, path);
                temporaryPath = null;
                return true;
            }
            catch (Exception exception) when (IsSettingsFailure(exception))
            {
                LastError = "The loader settings could not be saved. " + exception.Message;
                return false;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception exception) when (IsSettingsFailure(exception)) { }
                }
            }
        }
    }

    private static bool ReadBoolean(XmlElement root, string name, bool defaultValue)
    {
        var node = root.SelectSingleNode(name);
        if (node is null) return defaultValue;
        if (bool.TryParse(node.InnerText, out var value)) return value;
        throw new XmlException("The " + name + " setting is invalid.");
    }

    private static bool ReadUpdateBoolean(XmlElement root, string name, bool defaultValue)
    {
        var node = root.SelectSingleNode(name);
        if (node is null) return defaultValue;
        if (bool.TryParse(node.InnerText, out var value)) return value;
        string warning = "The " + name + " setting is invalid. Its default is being used.";
        LastError = LastError is null ? warning : LastError + " " + warning;
        return defaultValue;
    }

    private static bool IsSettingsFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException or System.Security.SecurityException;
}
