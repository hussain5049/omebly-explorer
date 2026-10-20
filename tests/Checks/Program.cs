using OmeblyExplorer.Core;

int pass = 0, fail = 0;
void Ok(string name, bool cond) { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}"); if (cond) pass++; else fail++; }

// Terminal here: the folder is the working directory, never part of the command line.
foreach (var folder in new[] { @"D:\work\a;b", @"D:\", @"C:\Users\x\new-tab; calc", @"D:\quote""name" })
{
    var t = Launch.TerminalInfo(folder);
    Ok($"terminal: no path in the arguments for {folder}", t.Arguments == "-d ." && !t.Arguments.Contains(folder));
    Ok($"terminal: the folder is the working directory for {folder}", t.WorkingDirectory == folder);
    Ok($"terminal fallback: no arguments for {folder}", Launch.PowerShellInfo(folder).Arguments == "" && Launch.PowerShellInfo(folder).WorkingDirectory == folder);
}
// Programs are started by full path, never found by name (the folder may hold a program with the same name).
Ok("terminal: Windows Terminal by full path", Path.IsPathRooted(Launch.TerminalInfo(@"D:\x").FileName) && Launch.TerminalInfo(@"D:\x").FileName.EndsWith(@"\Microsoft\WindowsApps\wt.exe", StringComparison.OrdinalIgnoreCase));
Ok("terminal: PowerShell by full path in System32", Launch.PowerShellInfo(@"D:\x").FileName.Equals(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), StringComparison.OrdinalIgnoreCase) && File.Exists(Launch.PowerShellExe));
// The real thing: a folder holding fake wt.exe and powershell.exe (copies of a harmless program) is used, and
// the program that starts must be Windows' own, not a copy from the folder.
var trap = Path.Combine(Path.GetTempPath(), "omebly trap;x");
Directory.CreateDirectory(trap);
var harmless = Path.Combine(Environment.SystemDirectory, "where.exe");
File.Copy(harmless, Path.Combine(trap, "wt.exe"), true);
File.Copy(harmless, Path.Combine(trap, "powershell.exe"), true);
var info = Launch.PowerShellInfo(trap);
info.Arguments = "-NoProfile -Command \"[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName; (Get-Location).Path\"";
info.RedirectStandardOutput = true;
using (var pr = System.Diagnostics.Process.Start(info)!)
{
    var outText = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
    Ok("terminal: the real PowerShell starts, not the copy in the folder", outText.Contains(Launch.PowerShellExe, StringComparison.OrdinalIgnoreCase) && !outText.Contains(Path.Combine(trap, "powershell.exe"), StringComparison.OrdinalIgnoreCase));
    Ok("terminal: it starts in the folder (with ';' in its name)", outText.Contains(trap.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
}

// Pin colours from the settings file.
Ok("colour: #2EF2B3 is accepted", Launch.IsColor("#2EF2B3"));
Ok("colour: #zzzzzz is refused", !Launch.IsColor("#zzzzzz"));
Ok("colour: missing is refused", !Launch.IsColor(null));
Ok("colour: short is refused", !Launch.IsColor("#fff"));
Ok("colour: no # is refused", !Launch.IsColor("2EF2B3x"));

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
