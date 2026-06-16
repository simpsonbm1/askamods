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

Dump("SSSGame.BiomeItemAvailabilityData");
Dump("SandSailorStudio.Inventory.AvailabilityProcess");
Dump("SSSGame.Weather.WeatherSystem");
Dump("SSSGame.Weather.WeatherStation");

// Find HarvestInteraction, WorldItemInstance, BiomeItemDescriptor by searching all types
Console.WriteLine("\n=== Searching for Harvest / Biome / WorldItem types ===");
foreach (var t in allTypes.Where(t => t.Name.Contains("Harvest") || t.Name.Contains("WorldItem") || t.Name.Contains("BiomeItem")))
    Console.WriteLine($"  {t.FullName}");

// Dump whichever full names we find above
Dump("SSSGame.HarvestInteraction");
Dump("SSSGame.WorldItemInstance");
Dump("SSSGame.BiomeItemDescriptor");
Dump("SSSGame.BiomeItemInstance");

// Find ResourcePieces / HarvestPiece to know the List<T> element type
Console.WriteLine("\n=== Searching for ResourcePieces / HarvestPiece types ===");
foreach (var t in allTypes.Where(t => t.Name.Contains("ResourcePiece") || t.Name.Contains("HarvestPiece")))
    Console.WriteLine($"  {t.FullName}");

// Confirm the element type of HarvestInteraction.harvestPieces
var hi = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.HarvestInteraction");
if (hi != null)
{
    var prop = hi.Properties.FirstOrDefault(p => p.Name == "harvestPieces");
    if (prop != null) Console.WriteLine($"\nharvestPieces type: {prop.PropertyType.FullName}");
}

Dump("SSSGame.HarvestInteraction/HarvestPiece");
Dump("SSSGame.ResourcePieces");

// Print enum values of ResourcePieces
var rpType = allTypes.FirstOrDefault(t => t.FullName == "SSSGame.ResourcePieces");
if (rpType != null)
{
    Console.WriteLine("\n=== ResourcePieces enum values ===");
    foreach (var f in rpType.Fields.Where(f => f.IsStatic))
        Console.WriteLine($"  {f.Name} = {f.Constant}");
}
