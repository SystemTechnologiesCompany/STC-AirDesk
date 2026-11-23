using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("STC AirDesk Setup")]
[assembly: AssemblyProduct("STC AirDesk Setup")]
[assembly: AssemblyVersion(AirDeskUpdates.InstalledVersion + ".0")]
[assembly: AssemblyFileVersion(AirDeskUpdates.InstalledVersion + ".0")]
[assembly: AssemblyInformationalVersion(AirDeskUpdates.InstalledVersion)]

internal sealed class SetupWizard : Form
{
    internal static readonly Color Surface = ColorTranslator.FromHtml("#101715");
    internal static readonly Color PanelColor = ColorTranslator.FromHtml("#19241f");
    internal static readonly Color Border = ColorTranslator.FromHtml("#34463c");
    internal static readonly Color Mint = ColorTranslator.FromHtml("#89edb4");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#edf5f0");
    private static readonly Color Muted = ColorTranslator.FromHtml("#adc0b5");

    private readonly Panel options = new Panel();
    private readonly Panel result = new Panel();
    private readonly Label heading = new Label();
    private readonly Label status = new Label();
    private readonly Label details = new Label();
    private readonly TextBox directory = new TextBox();
    private readonly CheckBox desktop = new CheckBox();
    private readonly CheckBox startMenu = new CheckBox();
    private readonly CheckBox repairFiles = new CheckBox();
    private readonly CheckBox removeSettings = new CheckBox();
    private readonly CheckBox launch = new CheckBox();
    private readonly Button next = new Button();
    private readonly Button back = new Button();
    private readonly SetupProgress progressBar = new SetupProgress();
    private readonly BrandPanel brand = new BrandPanel();
    private readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 33 };
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private bool installing;
    private bool complete;
    private string executable;
    private readonly bool uninstalling;
    private readonly bool maintenance;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool uninstall = args.Length > 0 && args[0] == "--uninstall";
        bool repair = args.Length > 0 && args[0] == "--repair";
        try
        {
            if (uninstall && InstallationRegistration.StartUninstallCopy()) return;
            int parentId;
            if (uninstall && args.Length == 2 && Int32.TryParse(args[1], out parentId))
            {
                try
                {
                    using (Process parent = Process.GetProcessById(parentId)) parent.WaitForExit(5000);
                }
                catch (ArgumentException) { }
            }

            using (var session = new Mutex(false, @"Local\STCAirDeskSetup"))
            {
                if (!AirDeskInstallation.TryEnter(session))
                {
                    MessageBox.Show("STC AirDesk Setup is already open.", "STC AirDesk Setup");
                    return;
                }
                try { Application.Run(new SetupWizard(uninstall, repair)); }
                finally { session.ReleaseMutex(); }
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "STC AirDesk Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private SetupWizard(bool uninstall, bool repair)
    {
        uninstalling = uninstall;
        maintenance = uninstall || repair;
        string registered = maintenance ? InstallationRegistration.RegisteredDirectory() : null;
        if (maintenance && String.IsNullOrEmpty(registered))
            throw new InvalidOperationException("No registered STC AirDesk installation was found.");
        SuspendLayout();
        Text = uninstall ? "STC AirDesk Uninstall" : "STC AirDesk Setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(794, 450);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Surface;
        ForeColor = TextColor;
        DoubleBuffered = true;

        brand.SetBounds(0, 0, 252, 450);
        brand.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        heading.SetBounds(284, 30, 482, 48);
        heading.Font = new Font(Font.FontFamily, 23F, FontStyle.Bold);
        heading.Text = ActionLabel + " STC AirDesk";

        options.SetBounds(286, 99, 480, 254);
        Label location = Caption("Installation folder", 0, 0, 350);
        directory.SetBounds(12, 10, 326, 23);
        directory.BorderStyle = BorderStyle.None;
        directory.BackColor = PanelColor;
        directory.ForeColor = TextColor;
        directory.AccessibleName = "Installation folder";
        directory.Text = registered ?? AirDeskInstallation.PreferredDirectory();
        var field = new Panel { BackColor = PanelColor };
        field.SetBounds(0, 37, 349, 42);
        field.Paint += delegate(object sender, PaintEventArgs e) {
            using (var pen = new Pen(directory.Focused ? Mint : Border))
                e.Graphics.DrawRectangle(pen, 0, 0, field.Width - 1, field.Height - 1);
        };
        directory.GotFocus += delegate { field.Invalidate(); };
        directory.LostFocus += delegate { field.Invalidate(); };
        field.Controls.Add(directory);
        var browse = new Button();
        StyleButton(browse, "&Browse...", 361, 37, 116, false);
        browse.Click += BrowseFolder;

        Label shortcuts = Caption("Shortcuts", 0, 103, 350);
        ConfigureCheck(desktop, "Create a Desktop shortcut", 0, 141, false);
        ConfigureCheck(startMenu, "Create a Start menu shortcut", 0, 181, true);
        ConfigureCheck(repairFiles, "Reinstall if already up to date", 0, 214, false);
        repairFiles.Visible = false;
        directory.TextChanged += delegate { RefreshRepairOption(); };
        RefreshRepairOption();
        ConfigureCheck(removeSettings, "Remove saved settings and calibrations", 0, 116, false);
        removeSettings.Visible = uninstall;
        directory.ReadOnly = maintenance;
        browse.Visible = shortcuts.Visible = desktop.Visible = startMenu.Visible = !maintenance;
        options.Controls.AddRange(new Control[] { location, field, browse, shortcuts, desktop, startMenu, repairFiles, removeSettings });

        result.SetBounds(286, 99, 480, 254);
        status.SetBounds(0, 0, 475, 54);
        status.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        details.SetBounds(0, 62, 475, 90);
        details.ForeColor = Muted;
        progressBar.SetBounds(0, 65, 476, 10);
        ConfigureCheck(launch, "Open STC AirDesk", 0, 160, true);
        result.Controls.AddRange(new Control[] { status, details, progressBar, launch });
        result.Visible = false;

        var divider = new Panel { BackColor = Border };
        divider.SetBounds(286, 368, 477, 1);
        StyleButton(back, "Cancel", 514, 388, 111, false);
        StyleButton(next, "&" + ActionLabel, 638, 388, 125, true);
        back.Click += delegate {
            if (result.Visible && !complete) ShowOptions();
            else Close();
        };
        next.Click += ContinueSetup;
        AcceptButton = next;
        CancelButton = back;

        Controls.AddRange(new Control[] { brand, heading, options, result, divider, back, next });
        animation.Tick += delegate {
            if (WindowState == FormWindowState.Minimized) return;
            brand.Frame = elapsed.Elapsed.TotalSeconds;
            brand.Invalidate();
            if (installing)
            {
                progressBar.Frame = brand.Frame;
                progressBar.Invalidate();
            }
        };
        if (SystemInformation.IsMenuAnimationEnabled) animation.Start();
        ResumeLayout(false);
    }

    private string ActionLabel { get { return uninstalling ? "Uninstall" : maintenance ? "Repair" : "Install"; } }

    private void RefreshRepairOption()
    {
        if (maintenance) return;
        try { repairFiles.Visible = File.Exists(AirDeskInstallation.Destination(directory.Text)); }
        catch (Exception error)
        {
            if (!(error is ArgumentException || error is NotSupportedException || error is PathTooLongException))
                throw;
            repairFiles.Visible = false;
        }
        if (!repairFiles.Visible) repairFiles.Checked = false;
    }

    private static Label Caption(string text, int x, int y, int width)
    {
        var label = new Label { Text = text, ForeColor = Mint, Font = new Font("Segoe UI Semibold", 11F) };
        label.SetBounds(x, y, width, 28);
        return label;
    }

    private static void ConfigureCheck(CheckBox check, string text, int x, int y, bool selected)
    {
        check.SetBounds(x, y, 465, 31);
        check.Text = text;
        check.Checked = selected;
        check.FlatStyle = FlatStyle.Flat;
        check.FlatAppearance.BorderColor = Border;
        check.FlatAppearance.CheckedBackColor = PanelColor;
        check.ForeColor = TextColor;
        check.Cursor = Cursors.Hand;
    }

    private static void StyleButton(Button button, string text, int x, int y, int width, bool primary)
    {
        button.SetBounds(x, y, width, 41);
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Mint : PanelColor;
        button.ForeColor = primary ? Surface : TextColor;
        button.FlatAppearance.BorderColor = primary ? Mint : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(166, 248, 200) : Color.FromArgb(49, 70, 58);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(113, 214, 156) : Border;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
    }

    private void BrowseFolder(object sender, EventArgs e)
    {
        using (var picker = new FolderBrowserDialog())
        {
            picker.Description = "Choose the installation folder for STC AirDesk.";
            picker.SelectedPath = directory.Text;
            picker.ShowNewFolderButton = true;
            if (picker.ShowDialog(this) == DialogResult.OK) directory.Text = picker.SelectedPath;
        }
    }

    private void ShowOptions()
    {
        options.Visible = true;
        result.Visible = false;
        heading.Text = ActionLabel + " STC AirDesk";
        next.Text = "&" + ActionLabel;
        back.Text = "Cancel";
        directory.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const int UseImmersiveDarkMode = 20;
        int enabled = 1;
        DwmSetWindowAttribute(Handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }

    private async void ContinueSetup(object sender, EventArgs args)
    {
        if (installing) return;
        if (complete)
        {
            if (launch.Checked)
            {
                try { Process.Start(executable); }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show(this, executable, "Couldn’t open STC AirDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            Close();
            return;
        }

        try { executable = AirDeskInstallation.Destination(directory.Text); }
        catch (Exception error)
        {
            if (!(error is ArgumentException || error is NotSupportedException || error is PathTooLongException))
                throw;
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowOptions();
            return;
        }

        bool desktopShortcut = !maintenance && desktop.Checked;
        bool startMenuShortcut = !maintenance && startMenu.Checked;
        bool repair = maintenance || repairFiles.Checked;
        bool deleteSettings = removeSettings.Checked;
        installing = true;
        heading.Text = uninstalling ? "Uninstalling STC AirDesk" : "Installing STC AirDesk";
        options.Visible = false;
        result.Visible = true;
        next.Enabled = back.Enabled = false;
        launch.Visible = details.Visible = false;
        progressBar.Visible = true;
        ShowStatus(uninstalling ? "Removing AirDesk" : "Checking for updates", -1);
        IProgress<Tuple<string, int>> progress = new Progress<Tuple<string, int>>(value => {
            if (installing) ShowStatus(value.Item1, value.Item2);
        });

        try
        {
            details.Text = await Task.Run(() => uninstalling
                ? AirDeskInstallation.Uninstall(executable, deleteSettings)
                : AirDeskInstallation.Install(executable, desktopShortcut, startMenuShortcut, repair,
                    (message, percent) => progress.Report(Tuple.Create(message, percent))));
            heading.Text = uninstalling ? "Uninstall complete" : "Setup complete";
            status.Text = uninstalling ? "STC AirDesk was removed" : "STC AirDesk is ready";
            launch.Checked = !uninstalling;
            launch.Visible = !uninstalling;
            back.Visible = false;
            next.Text = "&Finish";
            complete = true;
        }
        catch (Exception error)
        {
            heading.Text = "Setup couldn’t finish";
            status.Text = error.Message;
            details.Text = "";
            back.Text = "&Back";
            next.Text = "&Retry";
        }
        finally
        {
            installing = false;
            progressBar.Visible = false;
            details.Visible = true;
            next.Enabled = back.Enabled = true;
            next.Focus();
        }
    }

    private void ShowStatus(string message, int percent)
    {
        status.Text = percent < 0 ? message : message + "  " + percent + "%";
        progressBar.Percent = percent;
        progressBar.Invalidate();
    }
}

internal sealed class BrandPanel : Control
{
    private readonly Bitmap mark;
    private readonly Bitmap wordmark;
    internal double Frame;

    internal BrandPanel()
    {
        DoubleBuffered = true;
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("stc-head.png"))
        using (var image = Image.FromStream(resource)) mark = new Bitmap(image);
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("airdesk-logo.png"))
        using (var image = Image.FromStream(resource)) wordmark = new Bitmap(image);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        if (Width == 0 || Height == 0) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(28, 54, 42), Color.FromArgb(13, 22, 18), 70))
            graphics.FillRectangle(gradient, ClientRectangle);
        using (var line = new Pen(SetupWizard.Border))
            graphics.DrawLine(line, Width - 1, 0, Width - 1, Height);

        int centerX = Width / 2;
        int centerY = (int)(Height * .42);
        double orbit = Frame * .8 - Math.PI / 2;
        int ring = Math.Min(Width, Height) * 39 / 100;
        using (var circles = new Pen(Color.FromArgb(58, 104, 80)))
        {
            graphics.DrawEllipse(circles, centerX - ring, centerY - ring, ring * 2, ring * 2);
            int inner = ring - Width / 16;
            graphics.DrawEllipse(circles, centerX - inner, centerY - inner, inner * 2, inner * 2);
        }

        int markHeight = Width * 47 / 100;
        int markWidth = markHeight * mark.Width / mark.Height;
        int bob = (int)(Math.Sin(Frame * 1.8) * 4);
        graphics.DrawImage(mark, centerX - markWidth / 2, centerY - markHeight / 2 + bob, markWidth, markHeight);
        using (var dot = new SolidBrush(SetupWizard.Mint))
            graphics.FillEllipse(dot, centerX + (float)Math.Cos(orbit) * ring - 3,
                centerY + (float)Math.Sin(orbit) * ring - 3, 6, 6);
        int wordmarkWidth = Width * 86 / 100;
        graphics.DrawImage(wordmark, centerX - wordmarkWidth / 2, Height * 72 / 100,
            wordmarkWidth, wordmarkWidth * wordmark.Height / wordmark.Width);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { mark.Dispose(); wordmark.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class SetupProgress : Control
{
    internal double Frame;
    internal int Percent = -1;

    internal SetupProgress() { DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var track = new SolidBrush(SetupWizard.Border)) e.Graphics.FillRectangle(track, ClientRectangle);
        int segment = Width / 4;
        int x = Percent < 0 ? (int)((Frame * 165 + segment) % (Width + segment + 1)) - segment : 0;
        int length = Percent < 0 ? segment : Width * Percent / 100;
        using (var fill = new SolidBrush(SetupWizard.Mint)) e.Graphics.FillRectangle(fill, x, 0, length, Height);
    }
}
internal static class AirDeskInstallation
{
    private const string ApplicationName = "STC AirDesk";
    private const string AppFileName = "STC-AirDesk.exe";
    private static readonly Version BundledVersion = new Version(AirDeskUpdates.InstalledVersion);

    private const string SetupKey = @"HKEY_CURRENT_USER\Software\STC\AirDesk\Setup";
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ApplicationName);

    internal static string PreferredDirectory()
    {
        try
        {
            string registered = InstallationRegistration.RegisteredDirectory();
            if (!String.IsNullOrWhiteSpace(registered)) return registered;
            string saved = Registry.GetValue(SetupKey, "InstallDirectory", null) as string;
            if (!String.IsNullOrWhiteSpace(saved)) return saved;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return DefaultDirectory;
    }

    internal static string Destination(string folder)
    {
        string expanded = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
        if (expanded.Length < 3 || !Char.IsLetter(expanded[0]) || expanded[1] != ':' ||
            (expanded[2] != '\\' && expanded[2] != '/'))
            throw new ArgumentException("Choose a full folder path on a local drive.");
        return Path.Combine(Path.GetFullPath(expanded), AppFileName);
    }

    internal static string Install(string executable, bool desktopShortcut, bool startMenuShortcut, bool repair,
        Action<string, int> report)
    {
        var updater = new AirDeskUpdates();
        string directory = Path.GetDirectoryName(executable);
        string registered = InstallationRegistration.RegisteredDirectory();
        if (!String.IsNullOrEmpty(registered) && !InstallationRegistration.SamePath(registered, directory))
            throw new IOException("Use the existing installation folder, or uninstall AirDesk first.");
        Version installed = ReadVersion(executable);
        if (installed == null && File.Exists(executable))
            throw new IOException("That folder contains another file named " + AppFileName + ". Choose a different folder.");
        Version minimum = installed != null && installed > BundledVersion ? installed : BundledVersion;
        Version recorded = InstallationRegistration.RegisteredVersion(executable);
        if (recorded != null && recorded > minimum) minimum = recorded;
        bool needSameVersion = repair || (installed == null && minimum > BundledVersion);
        AirDeskUpdates.UpdateStatus update = updater.Check(minimum, needSameVersion);
        string detail = update.state == "current" ? "" : "\r\nGitHub update unavailable.";
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            Version ready = null;
            if (update.state == "available")
            {
                try
                {
                    report("Downloading v" + update.version + "…", 0);
                    DownloadUpdate(updater.GetOffer(update.version), update.sha256, staged, report);
                    ready = new Version(update.version);
                    if (ReadVersion(staged) != ready)
                    {
                        ready = null;
                        throw new InvalidDataException("The executable version didn’t match the release.");
                    }
                    detail = "";
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is InvalidDataException || error is WebException ||
                        error is CryptographicException || error is System.ComponentModel.Win32Exception))
                        throw;
                    ready = null;
                    detail = "\r\nThe GitHub download couldn't be verified or completed.";
                    File.Delete(staged);
                }
            }

            report("Installing STC AirDesk…", -1);
            if (ready == null && minimum == BundledVersion && (repair || installed == null || installed < BundledVersion))
            {
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppFileName))
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                    payload.CopyTo(output);
                ready = BundledVersion;
                if (ReadVersion(staged) != ready)
                    throw new InvalidDataException("The bundled executable could not be verified.");
            }

            if (ready == null && (repair || installed == null || installed < minimum))
                throw new IOException("The installed version is unavailable to download. Use a current setup file to repair AirDesk.");

            Version result = ActivateExecutable(staged, ready, executable, repair);
            InstallationRegistration.Register(executable, result, Assembly.GetExecutingAssembly().Location);
            RememberDirectory(directory);
            if (desktopShortcut && !CreateShortcut(executable, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))
                detail += "\r\nDesktop shortcut unavailable.";
            if (startMenuShortcut && !CreateShortcut(executable, Environment.GetFolderPath(Environment.SpecialFolder.Programs)))
                detail += "\r\nStart menu shortcut unavailable.";
            return "Version " + result.ToString(3) + "\r\n" + directory + detail;
        }
        finally
        {
            File.Delete(staged);
        }
    }

    internal static bool TryEnter(Mutex instance)
    {
        try { return instance.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void RequireClosed(Mutex app)
    {
        if (!TryEnter(app))
            throw new IOException("Close STC AirDesk from its system tray menu, then click Retry.");
    }

    internal static string Uninstall(string executable, bool removeSettings)
    {
        string registered = InstallationRegistration.RegisteredDirectory();
        if (String.IsNullOrEmpty(registered) ||
            !InstallationRegistration.SamePath(Destination(registered), executable))
            throw new IOException("The registered AirDesk installation could not be found.");

        using (var app = new Mutex(false, @"Local\STCAirDesk"))
        {
            RequireClosed(app);
            try { InstallationRegistration.Remove(executable, removeSettings); }
            finally { app.ReleaseMutex(); }
        }
        return removeSettings ? "Saved settings removed." : "Saved settings kept.";
    }

    internal static Version ActivateExecutable(string staged, Version ready, string destination, bool replaceEqual)
    {
        using (var app = new Mutex(false, @"Local\STCAirDesk"))
        {
            RequireClosed(app);

            try
            {
                Version current = ReadVersion(destination);
                if (current == null && File.Exists(destination))
                    throw new IOException("That folder contains another file named " + AppFileName + ". Choose a different folder.");
                if (current != null && (ready == null || current > ready || (current == ready && !replaceEqual)))
                    return current;
                if (ready == null) throw new IOException("The installed copy was removed during setup. Click Retry.");
                if (File.Exists(destination)) File.Replace(staged, destination, null);
                else File.Move(staged, destination);
                return ready;
            }
            finally { app.ReleaseMutex(); }
        }
    }

    internal static Version ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        Version version;
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != ApplicationName || !Version.TryParse(info.ProductVersion, out version) ||
            version.Build < 0 || version.Revision >= 0) return null;
        return version;
    }

    private static void DownloadUpdate(AirDeskUpdates.DownloadOffer offer, string digest, string staged,
        Action<string, int> report)
    {
        using (HttpWebResponse response = AirDeskUpdates.OpenDownload(offer))
        using (Stream input = response.GetResponseStream())
        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
            WriteVerifiedDownload(input, output, offer.Size, digest,
                percent => report("Downloading v" + offer.Version + "…", percent));
    }

    internal static void WriteVerifiedDownload(Stream input, Stream output, long size, string digest, Action<int> progress)
    {
        var elapsed = Stopwatch.StartNew();
        using (SHA256 sha = SHA256.Create())
        {
            byte[] buffer = new byte[65536];
            long received = 0;
            int lastPercent = 0;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                received += count;
                if (received > size || elapsed.Elapsed > TimeSpan.FromMinutes(5))
                    throw new InvalidDataException("The download exceeded its expected size or time limit.");
                output.Write(buffer, 0, count);
                sha.TransformBlock(buffer, 0, count, buffer, 0);
                int percent = (int)(received * 100 / size);
                if (percent != lastPercent) progress(lastPercent = percent);
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
            if (received != size || !String.Equals(actual, digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download didn’t match the GitHub release checksum.");
        }
    }

    private static void RememberDirectory(string directory)
    {
        try { Registry.SetValue(SetupKey, "InstallDirectory", directory); }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static bool CreateShortcut(string executable, string folder)
    {
        try
        {
            if (String.IsNullOrEmpty(folder)) return false;
            Directory.CreateDirectory(folder);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            string link = Path.Combine(folder, ApplicationName + ".lnk");
            dynamic shortcut = shell.CreateShortcut(link);
            string currentTarget = shortcut.TargetPath;
            if (File.Exists(link) && !InstallationRegistration.SamePath(currentTarget, executable)) return false;
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.Save();
            return true;
        }
        catch (Exception) { return false; }
    }
}
