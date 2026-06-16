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

void Dump(string typeName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == typeName);
    if (t == null) { Console.WriteLine($"\n[NOT FOUND] {typeName}"); return; }
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.FullName}) ===");
    foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("NativeField") && !f.Name.StartsWith("NativeMethod") && !f.IsStatic))
        Console.WriteLine($"  [field]  {f.FieldType.Name} {f.Name}");
    foreach (var p in t.Properties)
        Console.WriteLine($"  [prop]   {p.PropertyType.Name} {p.Name}");
    foreach (var m in t.Methods.Where(m => !m.IsSpecialName && m.IsPublic))
        Console.WriteLine($"  [method] {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
}

// Check accessor visibility for key Creature properties
var creatureType = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.Creature");
if (creatureType != null)
{
    Console.WriteLine("\n=== Creature property accessors ===");
    foreach (var pname in new[] { "CurrentHealth", "MaxHealth", "IsDead" })
    {
        var p = creatureType.Properties.FirstOrDefault(x => x.Name == pname);
        if (p == null) { Console.WriteLine($"  {pname}: NOT FOUND"); continue; }
        Console.WriteLine($"  {pname}: get={(p.GetMethod != null ? p.GetMethod.IsPublic.ToString() : "none")} set={(p.SetMethod != null ? p.SetMethod.IsPublic.ToString() : "none")}");
    }
}

// Search for Heal-related methods/types across both assemblies
Console.WriteLine("\n=== Searching for Heal-related members ===");
foreach (var t in allTypes)
{
    foreach (var m in t.Methods.Where(m => m.Name.Contains("Heal") && !m.Name.Contains("Health")))
        Console.WriteLine($"  {t.FullName}.{m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
}

// Check whether Character / PlayerCharacter override key Creature methods
void DumpOverrides(string typeName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == typeName);
    if (t == null) { Console.WriteLine($"[NOT FOUND] {typeName}"); return; }
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.FullName}) method overrides ===");
    foreach (var m in t.Methods.Where(m => m.IsVirtual && (m.Name == "TakeDamage" || m.Name == "Spawned" || m.Name == "IsPlayer" || m.Name == "Despawned")))
        Console.WriteLine($"  {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
}
DumpOverrides("SSSGame.Character");
DumpOverrides("SSSGame.PlayerCharacter");

void Dump2(string typeName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == typeName);
    if (t == null) { Console.WriteLine($"\n[NOT FOUND] {typeName}"); return; }
    Console.WriteLine($"\n=== {t.FullName} (base: {t.BaseType?.FullName}) ===");
    foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("NativeField") && !f.Name.StartsWith("NativeMethod") && !f.IsStatic))
        Console.WriteLine($"  [field]  {f.FieldType.Name} {f.Name}");
    foreach (var p in t.Properties)
        Console.WriteLine($"  [prop]   {p.PropertyType.Name} {p.Name}  get={(p.GetMethod != null ? p.GetMethod.IsPublic.ToString() : "none")} set={(p.SetMethod != null ? p.SetMethod.IsPublic.ToString() : "none")}");
    foreach (var m in t.Methods.Where(m => !m.IsSpecialName && m.IsPublic))
        Console.WriteLine($"  [method] {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
}

// Find static members across all types whose type is PlayerManager (how do other classes obtain it?)
Console.WriteLine("\n=== Searching for static PlayerManager accessors ===");
foreach (var t in allTypes)
{
    foreach (var f in t.Fields.Where(f => f.IsStatic && f.FieldType.Name == "PlayerManager"))
        Console.WriteLine($"  [static field] {t.FullName}.{f.Name}");
    foreach (var p in t.Properties.Where(p => p.PropertyType.Name == "PlayerManager" && p.GetMethod != null && p.GetMethod.IsStatic))
        Console.WriteLine($"  [static prop]  {t.FullName}.{p.Name}");
}

// Does PlayerManager itself have an Instance-style static accessor (any static prop/field on it)?
var pmType = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.PlayerManager");
if (pmType != null)
{
    Console.WriteLine("\n=== PlayerManager static members ===");
    foreach (var f in pmType.Fields.Where(f => f.IsStatic))
        Console.WriteLine($"  [static field] {f.FieldType.Name} {f.Name}");
    foreach (var p in pmType.Properties.Where(p => p.GetMethod != null && p.GetMethod.IsStatic))
        Console.WriteLine($"  [static prop]  {p.PropertyType.Name} {p.Name}");
    foreach (var m in pmType.Methods.Where(m => m.IsStatic))
        Console.WriteLine($"  [static method] {m.ReturnType.Name} {m.Name}()");
}

// Check available FindObjectsOfType-style static methods on UnityEngine.Object
var coreModulePath = @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs\UnityEngine.CoreModule.dll";
var coreAsm = AssemblyDefinition.ReadAssembly(coreModulePath, readerParams);
var objType = coreAsm.MainModule.Types.FirstOrDefault(t => t.FullName == "UnityEngine.Object");
if (objType != null)
{
    Console.WriteLine($"\n=== UnityEngine.Object base chain ===");
    var bt = objType.BaseType;
    while (bt != null)
    {
        Console.WriteLine($"  -> {bt.FullName}");
        var resolved = bt.Resolve();
        bt = resolved?.BaseType;
    }
}

// Find TryCast definition across core + interop dlls
var il2cppInteropPath = @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Il2CppInterop.Runtime.dll";
var il2cppInteropAsm = AssemblyDefinition.ReadAssembly(il2cppInteropPath, readerParams);
Console.WriteLine("\n=== Searching for TryCast definitions in Il2CppInterop.Runtime.dll ===");
foreach (var t in il2cppInteropAsm.MainModule.Types)
{
    foreach (var m in t.Methods.Where(m => m.Name == "TryCast"))
        Console.WriteLine($"  {t.FullName}.{m.Name}<{string.Join(",", m.GenericParameters.Select(g => g.Name))}>({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name))}) static={m.IsStatic}");
}

// Check WorldItemInstance's base chain (known-working TryCast caller) for comparison
Console.WriteLine("\n=== SSSGame.WorldItemInstance base chain ===");
var wiiType = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.WorldItemInstance");
var bt2 = wiiType?.BaseType;
while (bt2 != null)
{
    Console.WriteLine($"  -> {bt2.FullName} (scope: {bt2.Scope})");
    TypeDefinition? resolved2 = null;
    try { resolved2 = bt2.Resolve(); } catch { }
    bt2 = resolved2?.BaseType;
}
