using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class FloatingKeyboard : Form
{
    private sealed class Key
    {
        public string Value;
        public Rectangle Bounds;
    }

    private readonly List<Key> keys = new List<Key>();
    private readonly Timer dragTimer = new Timer { Interval = 16 };
    private readonly Icon appIcon;
    private Key hovered, pressed;
    private Point? dragOffset;
    private Point? lastPlacement;
    private const int HeaderHeight = 48;
    private bool shift;
    private string feedback = "Point at a key and pinch to type";

    public FloatingKeyboard()
    {
        Text = "STC AirDesk Keyboard";
        using (Stream source = typeof(FloatingKeyboard).Assembly.GetManifestResourceStream("airdesk-icon.ico"))
        using (Icon embedded = new Icon(source, 32, 32))
            appIcon = (Icon)embedded.Clone();
        Icon = appIcon;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.FromArgb(17, 28, 23);
        ForeColor = Color.FromArgb(237, 248, 241);
        Font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Point);
        dragTimer.Tick += (sender, args) =>
        {
            // Keep following the pointer if it moves outside this non-activating window.
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0) MoveKeyboard(System.Windows.Forms.Cursor.Position);
            else EndDrag();
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams settings = base.CreateParams;
            settings.ExStyle |= 0x08000000 | 0x00000080; // No activation or Alt+Tab entry.
            return settings;
        }
    }

    public void RequestVisibility(bool show)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => ApplyVisibility(show))); }
        catch (InvalidOperationException) { }
    }

    private void ApplyVisibility(bool show)
    {
        if (IsDisposed || Visible == show) return;
        hovered = pressed = null;
        shift = false;
        if (!show)
        {
            EndDrag();
            Hide();
            return;
        }
        feedback = "Point at a key and pinch to type";
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        int width = Math.Min(820, work.Width - 20);
        int height = Math.Min(315, work.Height * 45 / 100);
        Size = new Size(width, height);
        Point initial = new Point(work.Left + (work.Width - width) / 2, work.Bottom - height - 10);
        Location = ConstrainPlacement(lastPlacement ?? initial, work);
        BuildKeys();
        Show();
    }

    private void BuildKeys()
    {
        keys.Clear();
        keys.Add(new Key { Value = "Close", Bounds = new Rectangle(ClientSize.Width - 54, 8, 42, 30) });
        int keyTop = HeaderHeight;
        int rowHeight = (ClientSize.Height - keyTop - 10) / 5;
        AddRow("1234567890", keyTop, rowHeight);
        AddRow("QWERTYUIOP", keyTop + rowHeight, rowHeight);
        AddRow("ASDFGHJKL", keyTop + rowHeight * 2, rowHeight);
        AddRow("ZXCVBNM", keyTop + rowHeight * 3, rowHeight);
        string[] bottom = { "Shift", "Space", "Backspace", "Enter" };
        int[] stops = { 0, 2, 6, 8, 10 };
        int available = ClientSize.Width - 24;
        for (int index = 0; index < bottom.Length; index++)
        {
            int left = available * stops[index] / 10;
            int right = available * stops[index + 1] / 10;
            keys.Add(new Key { Value = bottom[index],
                Bounds = new Rectangle(12 + left, keyTop + rowHeight * 4, right - left - 4, rowHeight - 4) });
        }
    }

    private void AddRow(string letters, int top, int height)
    {
        int width = (ClientSize.Width - 24) / 10;
        int left = (ClientSize.Width - width * letters.Length) / 2;
        foreach (char letter in letters)
        {
            keys.Add(new Key { Value = letter.ToString(), Bounds = new Rectangle(left, top, width - 4, height - 4) });
            left += width;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            message.Result = new IntPtr(3); // MA_NOACTIVATE, keep the click.
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Brush header = new SolidBrush(Color.FromArgb(27, 45, 36)))
            e.Graphics.FillRectangle(header, 0, 0, ClientSize.Width, HeaderHeight - 4);
        TextRenderer.DrawText(e.Graphics, "Floating keyboard", Font, new Point(12, 5), ForeColor);
        TextRenderer.DrawText(e.Graphics, feedback + "  |  Drag this bar to move", Font,
            new Rectangle(12, 24, Math.Max(0, ClientSize.Width - 78), 18), Color.FromArgb(165, 192, 179),
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        foreach (Key key in keys)
        {
            bool selected = key == hovered || (shift && key.Value == "Shift");
            Color fill = selected ? Color.FromArgb(143, 229, 181) : Color.FromArgb(39, 59, 49);
            using (Brush brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, key.Bounds);
            string label = key.Value == "Close" ? "\u00d7" :
                key.Value.Length == 1 && !shift ? key.Value.ToLowerInvariant() : key.Value;
            TextRenderer.DrawText(e.Graphics, label, Font, key.Bounds, selected ? BackColor : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private Key Hit(Point point)
    {
        return keys.Find(key => key.Bounds.Contains(point));
    }

    private Point ConstrainPlacement(Point point, Rectangle work)
    {
        return new Point(
            Math.Max(work.Left, Math.Min(point.X, work.Right - Width)),
            Math.Max(work.Top, Math.Min(point.Y, work.Bottom - Height)));
    }

    private void MoveKeyboard(Point cursor)
    {
        if (!dragOffset.HasValue) return;
        Point offset = dragOffset.Value;
        Point location = ConstrainPlacement(new Point(cursor.X - offset.X, cursor.Y - offset.Y),
            Screen.PrimaryScreen.WorkingArea);
        if (location == Location) return;
        Location = location;
        lastPlacement = location;
    }

    private void EndDrag()
    {
        if (!dragOffset.HasValue) return;
        dragOffset = null;
        dragTimer.Stop();
        Cursor = Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragOffset.HasValue)
        {
            MoveKeyboard(System.Windows.Forms.Cursor.Position);
            return;
        }
        Key key = Hit(e.Location);
        Cursor = key == null && e.Y < HeaderHeight ? Cursors.SizeAll : Cursors.Default;
        if (hovered == key) return;
        hovered = key;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        pressed = Hit(e.Location);
        if (pressed == null && e.Y < HeaderHeight)
        {
            dragOffset = e.Location;
            Cursor = Cursors.SizeAll;
            dragTimer.Start();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (dragOffset.HasValue)
        {
            MoveKeyboard(System.Windows.Forms.Cursor.Position);
            EndDrag();
            return;
        }
        Key key = pressed;
        pressed = null;
        if (key == null || key != Hit(e.Location)) return;
        if (key.Value == "Close")
        {
            ApplyVisibility(false);
            return;
        }
        if (key.Value == "Shift") shift = !shift;
        else
        {
            string value = shift && key.Value.Length == 1 ? key.Value : key.Value.ToLowerInvariant();
            bool sent = MouseControl.TypeKey(value);
            feedback = sent ? "Point at a key and pinch to type" : "Windows could not send the key to the active app";
            if (sent && key.Value.Length == 1 && Char.IsLetter(key.Value[0])) shift = false;
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dragTimer.Dispose();
            if (appIcon != null) appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
