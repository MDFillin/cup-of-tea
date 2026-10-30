using System.Diagnostics;
using System.Text;

namespace PVpn.Core.Cores;

public sealed record ValidationResult(bool Ok, string Output);

/// <summary>Проверка готового конфига самими ядрами: «xray run -test» и «sing-box check».</summary>
public static class CoreValidator
{
    public static Task<ValidationResult> ValidateXrayAsync(CorePaths cores, string configJson, CancellationToken ct = default) =>
        RunAsync(cores.Xray, ["run", "-test", "-c", "stdin:"], configJson, ct);

    public static Task<ValidationResult> ValidateSingBoxAsync(CorePaths cores, string configJson, CancellationToken ct = default) =>
        RunAsync(cores.SingBox, ["check", "-c", "stdin", "--disable-color"], configJson, ct);

    private static async Task<ValidationResult> RunAsync(string exe, string[] args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";

        using var p = new Process { StartInfo = psi };
        ProcessStarter.Start(p);
        await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
        return new ValidationResult(p.ExitCode == 0, output);
    }
}
