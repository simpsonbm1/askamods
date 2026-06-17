using System;
using System.Linq;
using Mono.Cecil;

var asmPath = @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop");
resolver.AddSearchDirectory(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core");
resolver.AddSearchDirectory(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs");

var readerParams = new ReaderParameters { AssemblyResolver = resolver };
var asm = AssemblyDefinition.ReadAssembly(asmPath, readerParams);
var types = asm.MainModule.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes)).ToList();

var ssPath = @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll";
var ssAsm = AssemblyDefinition.ReadAssembly(ssPath, readerParams);
var ssTypes = ssAsm.MainModule.Types.SelectMany(t => new[] { t }.Concat(t.NestedTypes)).ToList();

var allTypes = types.Concat(ssTypes).ToList();

void DumpAll(string typeName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == typeName);
    if (t == null) { Console.WriteLine($"\n[NOT FOUND] {typeName}"); return; }
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.FullName}) [ALL members] ===");
    foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("NativeField") && !f.Name.StartsWith("NativeMethod")))
        Console.WriteLine($"  [field]  {f.FieldType.Name} {f.Name} public={f.IsPublic} static={f.IsStatic}");
    foreach (var p in t.Properties)
        Console.WriteLine($"  [prop]   {p.PropertyType.Name} {p.Name}  get={(p.GetMethod != null ? p.GetMethod.IsPublic.ToString() : "none")} set={(p.SetMethod != null ? p.SetMethod.IsPublic.ToString() : "none")}");
    foreach (var m in t.Methods.Where(m => !m.IsSpecialName && !m.Name.StartsWith("Native")))
        Console.WriteLine($"  [method] {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))}) public={m.IsPublic} virtual={m.IsVirtual} static={m.IsStatic}");
}

DumpAll("SSSGame.Structure");

Console.WriteLine("\n=== Searching for ItemInfo/name-like members on Structure ===");
var structType = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.Structure");
if (structType != null)
{
    var bt = structType.BaseType;
    while (bt != null)
    {
        Console.WriteLine($"  base: {bt.FullName}");
        TypeDefinition? resolved = null;
        try { resolved = bt.Resolve(); } catch { }
        if (resolved != null)
        {
            foreach (var p in resolved.Properties.Where(p => p.Name.Contains("Info") || p.Name.Contains("Name")))
                Console.WriteLine($"    [prop] {resolved.FullName}.{p.PropertyType.Name} {p.Name}");
        }
        bt = resolved?.BaseType;
    }
}
