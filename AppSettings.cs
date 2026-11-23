using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Microsoft.Win32;

internal sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = Launcher.AppTitle;
    private const string LegacyRunName = Launcher.LegacyProductName;
    public const int MaxBytes = 16384;
    private readonly object gate = new object();
    private readonly string folder;
    private readonly string previousFolder;
    private readonly DataContractJsonSerializer json = new DataContractJsonSerializer(typeof(Preferences),
        new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 1024 });

    [DataContract]
    public sealed class Tuning
    {
        [DataMember(IsRequired = true)] public int sensitivity { get; set; }
        [DataMember(IsRequired = true)] public int smoothing { get; set; }
        [DataMember(IsRequired = true)] public int scrollSpeed { get; set; }
    }

    [DataContract]
    public sealed class Profile
    {
        [DataMember(IsRequired = true)]
        public string name { get; set; }
        [DataMember(IsRequired = true)]
        public double[] values { get; set; }
        [DataMember(EmitDefaultValue = false)] public Tuning tuning { get; set; }
    }

    [DataContract]
    public sealed class Preferences
    {
        [DataMember(IsRequired = true)]
        public string activeProfile { get; set; }
        [DataMember(IsRequired = true)]
        public List<Profile> profiles { get; set; }
        [DataMember(IsRequired = true)]
        public bool keyboard { get; set; }
        [DataMember(IsRequired = true)]
        public int fingerMask { get; set; }
        [DataMember(IsRequired = true)]
        public string drill { get; set; }
        [DataMember(EmitDefaultValue = false)] public Tuning defaultTuning { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool? palmPause { get; set; }

        public Preferences()
        {
            activeProfile = "";
            profiles = new List<Profile>();
            fingerMask = 31;
            drill = "targets";
        }
    }

    public AppSettings(string folder)
        : this(folder, folder)
    {
    }

    public AppSettings(string folder, string previousFolder)
    {
        this.folder = folder;
        this.previousFolder = previousFolder;
    }

    public string Read()
    {
        lock (gate)
        {
            string path = Path.Combine(folder, "settings.json");
            if (!File.Exists(path)) path = Path.Combine(previousFolder, "settings.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxBytes) throw new IOException("Settings file exceeds its size limit.");
                Preferences saved = Decode(File.ReadAllText(path));
                if (saved == null) throw new IOException("Settings file is invalid.");
                return Encode(saved);
            }

            Preferences initial = new Preferences();
            string oldFile = Path.Combine(folder, "mouse-calibration.txt");
            if (!File.Exists(oldFile)) oldFile = Path.Combine(previousFolder, "mouse-calibration.txt");
            if (File.Exists(oldFile) && new FileInfo(oldFile).Length <= 64)
            {
                string[] parts = File.ReadAllText(oldFile).Trim().Split(' ');
                double[] values = new double[9];
                if (parts.Length == values.Length &&
                    parts.Select((part, index) => Double.TryParse(part, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out values[index])).All(success => success) && ValidCalibration(values))
                {
                    initial.activeProfile = "Previous calibration";
                    initial.profiles.Add(new Profile { name = initial.activeProfile, values = values });
                }
            }
            return Encode(initial);
        }
    }

    public bool Save(string source)
    {
        lock (gate)
        {
            Preferences value = Decode(source);
            if (value == null) return false;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "settings.json");
            string pending = path + ".tmp";
            File.WriteAllText(pending, Encode(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
            return true;
        }
    }

    private string Encode(Preferences value)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            json.WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private Preferences Decode(string source)
    {
        Preferences value;
        try
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(source)))
                value = json.ReadObject(stream) as Preferences;
        }
        catch (SerializationException) { return null; }
        catch (XmlException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        if (value == null || value.activeProfile == null || value.profiles == null || value.profiles.Count > 30 ||
            value.fingerMask < 0 || value.fingerMask > 31 || !ValidTuning(value.defaultTuning) ||
            (value.drill != "targets" && value.drill != "typing" && value.drill != "scroll")) return null;

        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Profile profile in value.profiles)
        {
            if (profile == null || String.IsNullOrWhiteSpace(profile.name) || profile.name.Length > 36 ||
                profile.name != profile.name.Trim() || profile.name.Any(Char.IsControl) ||
                !names.Add(profile.name) || !ValidCalibration(profile.values) || !ValidTuning(profile.tuning)) return null;
        }
        return value.activeProfile.Length == 0 || value.profiles.Any(profile => profile.name == value.activeProfile)
            ? value : null;
    }

    private static bool ValidCalibration(double[] values)
    {
        return values != null && values.Length == 9 &&
            values.All(value => value >= 0 && value <= 1) &&
            new[] { 0, 1, 3, 4 }.All(index => values[index + 1] - values[index] >= 0.0649) &&
            values.Skip(6).All(value => value >= 0.25 && value <= 0.7);
    }

    private static bool ValidTuning(Tuning value)
    {
        return value == null || new[] { value.sensitivity, value.smoothing, value.scrollSpeed }
            .All(setting => setting >= 0 && setting <= 100);
    }

    public static bool StartupEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && (key.GetValue(RunName) is string || key.GetValue(LegacyRunName) is string);
    }

    public static void SetStartup(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (key == null) throw new IOException("Windows startup settings are unavailable.");
            if (!enabled)
            {
                key.DeleteValue(RunName, false);
                key.DeleteValue(LegacyRunName, false);
                return;
            }
            string command = "\"" + Assembly.GetExecutingAssembly().Location + "\"";
            if (command.Length > 260) throw new IOException("The app path is too long for Windows startup.");
            key.SetValue(RunName, command, RegistryValueKind.String);
            key.DeleteValue(LegacyRunName, false);
        }
    }

}
