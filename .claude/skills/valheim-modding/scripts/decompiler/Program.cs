using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// decomp <assembly.dll> <TypeName> [MethodName]
// Decompiles a whole type, or just the named method(s), to readable C#.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: decomp <assembly.dll> <TypeName> [MethodName]");
            return 2;
        }

        string assembly = args[0];
        string typeName = args[1];
        string methodName = args.Length > 2 ? args[2] : null;

        var settings = new DecompilerSettings(LanguageVersion.CSharp10_0)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
        };

        var decompiler = new CSharpDecompiler(assembly, settings);

        ITypeDefinition type = decompiler.TypeSystem.MainModule.TypeDefinitions
            .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);

        if (type == null)
        {
            Console.Error.WriteLine("type not found: " + typeName);
            return 1;
        }

        if (methodName == null)
        {
            Console.WriteLine(decompiler.DecompileTypeAsString(new FullTypeName(type.FullTypeName.ToString())));
            return 0;
        }

        var handles = new List<EntityHandle>();
        foreach (var m in type.Methods)
            if (m.Name == methodName)
                handles.Add(m.MetadataToken);

        foreach (var p in type.Properties)
            if (p.Name == methodName)
            {
                if (p.Getter != null) handles.Add(p.Getter.MetadataToken);
                if (p.Setter != null) handles.Add(p.Setter.MetadataToken);
            }

        if (handles.Count == 0)
        {
            Console.Error.WriteLine("member not found: " + typeName + "." + methodName);
            return 1;
        }

        Console.WriteLine(decompiler.DecompileAsString(handles));
        return 0;
    }
}
