using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PDWSLC.App;

internal static class BundledRuntime
{
    // PowerShell needs physical script files. Extract the executable's own copies
    // into a content-versioned user cache, never alongside the executable.
    public static string GetBridgePath() => GetScriptPath("Invoke-Desktop.ps1");

    public static string GetScriptPath(string scriptName)
    {
        var assembly = typeof(BundledRuntime).Assembly;
        string[] names = ["desktop.ps1", "Invoke-Desktop.ps1", "Requirements.ps1"];
        var scripts = names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream("PDWSLC.Runtime." + name)
                ?? throw new InvalidDataException("Missing bundled desktop script: " + name);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (Name: name, Bytes: buffer.ToArray());
        }).ToArray();
        var fingerprint = string.Join("-", scripts.Select(s => Convert.ToHexString(SHA256.HashData(s.Bytes))));
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDWSLC", "runtime", version);
        Directory.CreateDirectory(directory);
        foreach (var script in scripts)
        {
            var target = Path.Combine(directory, script.Name);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(script.Bytes)) continue;
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, script.Bytes);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return Path.Combine(directory, scriptName);
    }
}
