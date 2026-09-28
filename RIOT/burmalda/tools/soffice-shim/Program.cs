using System.Runtime.InteropServices;

static string? ValueAfter(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

var outDir = ValueAfter(args, "--outdir") ?? Directory.GetCurrentDirectory();
var input = args.LastOrDefault(x => x.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
var saveFields = Environment.GetEnvironmentVariable("SOFFICE_SHIM_SAVE_FIELDS") == "1";
if (input is null)
{
    Console.Error.WriteLine("No DOCX input was supplied.");
    return 2;
}

Directory.CreateDirectory(outDir);
var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".pdf");
object? word = null;
object? document = null;
try
{
    var type = Type.GetTypeFromProgID("Word.Application")
        ?? throw new InvalidOperationException("Microsoft Word is not installed.");
    word = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not start Microsoft Word.");
    dynamic app = word;
    app.Visible = false;
    app.DisplayAlerts = 0;
    document = app.Documents.Open(Path.GetFullPath(input), ReadOnly: !saveFields, AddToRecentFiles: false, Visible: false);
    dynamic doc = document;
    doc.Fields.Update();
    if (saveFields)
        doc.Save();
    doc.ExportAsFixedFormat(Path.GetFullPath(output), 17, false, 0, 0, 1, 9999, 0, true, true, 1, true, true, false);
    doc.Close(false);
    document = null;
    app.Quit(false);
    word = null;
    Console.WriteLine($"convert {input} as {output}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    if (document is not null)
    {
        try { ((dynamic)document).Close(false); } catch { }
        try { Marshal.FinalReleaseComObject(document); } catch { }
    }
    if (word is not null)
    {
        try { ((dynamic)word).Quit(false); } catch { }
        try { Marshal.FinalReleaseComObject(word); } catch { }
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
