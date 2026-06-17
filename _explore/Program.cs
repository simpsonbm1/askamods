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
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.FullName}) ===");
    foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("NativeField") && !f.Name.StartsWith("NativeMethod")))
        Console.WriteLine($"  [field]  {f.FieldType.Name} {f.Name}");
    foreach (var p in t.Properties)
        Console.WriteLine($"  [prop]   {p.PropertyType.Name} {p.Name}");
    foreach (var m in t.Methods.Where(m => !m.IsSpecialName && !m.Name.StartsWith("Native")))
        Console.WriteLine($"  [method] {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
}

void DumpEnum(string typeName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == typeName);
    if (t == null) { Console.WriteLine($"\n[NOT FOUND] {typeName}"); return; }
    Console.WriteLine($"\n=== ENUM {t.FullName} ===");
    foreach (var f in t.Fields.Where(f => f.IsStatic))
        Console.WriteLine($"  {f.Name} = {f.Constant}");
}

// Dump FireState enum values
DumpEnum("SSSGame.FireStructure/FireState");

// Dump WeatherManager (separate from WeatherSystem)
DumpAll("SSSGame.WeatherManager");

// Dump WeatherEventData (used in weather callbacks)
DumpAll("SSSGame.WeatherEventData");
