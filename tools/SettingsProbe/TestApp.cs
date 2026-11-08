using System.Text;

namespace SettingsProbe;

/// <summary>
/// A stand-in "app" that behaves like a typical desktop program: it saves settings the safe way
/// (write a temp file, then rename over the real one), keeps a profile, fills a cache, writes a
/// log, and leaves a stray temp file behind and deletes it. It only ever writes under the folder
/// it's given, which is the probe's scratch folder.
/// </summary>
internal static class TestApp
{
    public static int Run(string root)
    {
        // Wait for the probe to say it's watching, so ETW sees everything from the start.
        Console.In.ReadLine();

        string app = Path.Combine(root, "Contoso", "Notepadish");
        Directory.CreateDirectory(app);

        AtomicWrite(Path.Combine(app, "settings.json"), "{\n  \"theme\": \"dark\",\n  \"fontSize\": 13\n}\n");
        Directory.CreateDirectory(Path.Combine(app, "Profiles"));
        File.WriteAllText(Path.Combine(app, "Profiles", "default.ini"), "[editor]\nwrap=true\n");

        Directory.CreateDirectory(Path.Combine(app, "Cache"));
        File.WriteAllBytes(Path.Combine(app, "Cache", "thumb-001.bin"), new byte[256 * 1024]);

        Directory.CreateDirectory(Path.Combine(app, "logs"));
        string log = Path.Combine(app, "logs", "app.log");
        File.AppendAllText(log, "started\n");
        File.AppendAllText(log, "saved settings\n");

        File.WriteAllText(Path.Combine(app, "token.json"), "{ \"refresh\": \"not-a-real-token\" }\n");

        string stray = Path.Combine(app, "recover.tmp");
        File.WriteAllText(stray, "x");
        File.Delete(stray);

        // A second save, the in-place way this time.
        File.WriteAllText(Path.Combine(app, "settings.json"), "{\n  \"theme\": \"light\",\n  \"fontSize\": 13\n}\n", Encoding.UTF8);

        Console.Out.WriteLine("done");
        return 0;
    }

    private static void AtomicWrite(string path, string text)
    {
        string temp = path + ".new";
        File.WriteAllText(temp, text, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }
}
