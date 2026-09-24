using System;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// decomp <assembly.dll> <TypeName> [LanguageVersion]   decompiles one type to C# on stdout
// decomp --list <assembly.dll> <text>                   prints the full names of the types containing <text>
//
// Built and run by tools\decompile.ps1; see that script for how to use it.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--list")
        {
            return List(args[1], args[2]);
        }

        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine("usage: decomp <assembly.dll> <TypeName> [LanguageVersion]");
            Console.Error.WriteLine("       decomp --list <assembly.dll> <text>");
            return 2;
        }

        LanguageVersion version = LanguageVersion.CSharp10_0;
        if (args.Length == 3 && !Enum.TryParse(args[2], out version))
        {
            Console.Error.WriteLine("unknown language version: " + args[2]);
            return 2;
        }

        var decompiler = new CSharpDecompiler(args[0], Settings(version));
        string typeName = args[1];

        ITypeDefinition type = decompiler.TypeSystem.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.FullName == typeName || t.ReflectionName == typeName)
            ?? decompiler.TypeSystem.MainModule.TypeDefinitions.FirstOrDefault(t => t.Name == typeName);

        if (type == null)
        {
            Console.Error.WriteLine("type not found: " + typeName);
            return 1;
        }

        Console.WriteLine(decompiler.DecompileTypeAsString(new FullTypeName(type.ReflectionName)));
        return 0;
    }

    private static int List(string assembly, string needle)
    {
        var decompiler = new CSharpDecompiler(assembly, Settings(LanguageVersion.CSharp10_0));
        foreach (ITypeDefinition t in decompiler.TypeSystem.MainModule.TypeDefinitions
                     .Where(t => t.FullName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            Console.WriteLine(t.FullName);
        }

        return 0;
    }

    private static DecompilerSettings Settings(LanguageVersion version)
    {
        return new DecompilerSettings(version)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
        };
    }
}
