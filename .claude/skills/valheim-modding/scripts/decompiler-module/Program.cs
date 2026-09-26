using System;
using System.IO;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

// decompmod <assembly.dll> <out.cs> <out.il> [searchDir ...]
// Decompiles a WHOLE assembly (every type, nested types included) to one C# file and writes its full IL
// disassembly to another. Meant for reading a small plugin end to end; decomp (the sibling tool) is the
// one for single game types and members. searchDirs let referenced assemblies resolve, which gives
// correct member names, overloads and default arguments in the C# output.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: decompmod <assembly.dll> <out.cs> <out.il> [searchDir ...]");
            return 2;
        }

        string asm = args[0];
        var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
        };

        var module = new PEFile(asm);
        var resolver = new UniversalAssemblyResolver(asm, false, module.Metadata.DetectTargetFrameworkId());
        for (int i = 3; i < args.Length; i++)
            resolver.AddSearchDirectory(args[i]);

        var utf8 = new UTF8Encoding(false);
        var decompiler = new CSharpDecompiler(module, resolver, settings);
        File.WriteAllText(args[1], decompiler.DecompileWholeModuleAsString(), utf8);

        using (var w = new StringWriter())
        {
            var dis = new ReflectionDisassembler(new PlainTextOutput(w), default);
            dis.WriteModuleContents(module);
            File.WriteAllText(args[2], w.ToString(), utf8);
        }

        Console.WriteLine("C#: " + args[1]);
        Console.WriteLine("IL: " + args[2]);
        return 0;
    }
}
