using System.IO;
using System.Drawing;
using System.Windows.Forms;

// Test target: logs every left click as client-area pixels, closes itself after 30 s.
var log = args.Length > 0 ? args[0] : "clicks.log";
ApplicationConfiguration.Initialize();
var f = new Form
{
    Text = "UTA Target Window",
    StartPosition = FormStartPosition.Manual,
    Location = new Point(300, 260),
    ClientSize = new Size(640, 420),
    TopMost = true,
    BackColor = Color.White
};
f.MouseDown += (_, e) => File.AppendAllText(log, $"{e.X},{e.Y}\n");
var t = new System.Windows.Forms.Timer { Interval = 30000 };
t.Tick += (_, _) => f.Close();
t.Start();
File.AppendAllText(log, "start\n");
Application.Run(f);
