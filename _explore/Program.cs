using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Mono.Cecil;

var resolver = new DefaultAssemblyResolver();
var dirs = new[] {
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop",
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core",
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs"
};
foreach (var d in dirs) resolver.AddSearchDirectory(d);
var rp = new ReaderParameters { AssemblyResolver = resolver };

var asms = new List<AssemblyDefinition>();
foreach (var f in Directory.GetFiles(dirs[0], "*.dll"))
{
    try { asms.Add(AssemblyDefinition.ReadAssembly(f, rp)); } catch { }
}

IEnumerable<TypeDefinition> AllTypes()
{
    foreach (var a in asms)
        foreach (var t in a.MainModule.Types)
        {
            yield return t;
            foreach (var n in t.NestedTypes) yield return n;
        }
}
var allTypes = AllTypes().ToList();

// Real game types only — drop interop noise.
bool IsNoise(string fn) =>
    fn.Contains("MethodInfoStoreGeneric") || fn.Contains("Il2Cpp") ||
    fn.Contains("/MethodInfoStore") || fn.Contains("NativeMethodInfoPtr");

string Sig(TypeReference tr) => tr?.Name ?? "?";

string BaseChain(TypeDefinition t)
{
    var bc = new List<string>();
    var bt = t.BaseType;
    int guard = 0;
    while (bt != null && guard++ < 20) { bc.Add(bt.Name); var bd = bt.Resolve(); bt = bd?.BaseType; }
    return string.Join(" -> ", bc);
}

void Dump(string fullName)
{
    var t = allTypes.FirstOrDefault(x => x.FullName == fullName);
    if (t == null) { Console.WriteLine($"\n##### [NOT FOUND] {fullName}"); return; }
    Console.WriteLine($"\n##### {t.FullName}\n  : {BaseChain(t)}");
    foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("NativeFieldInfoPtr") && !f.Name.StartsWith("NativeMethodInfoPtr")).OrderBy(f => f.Name))
        Console.WriteLine($"  F {(f.IsStatic ? "static " : "")}{Sig(f.FieldType)} {f.Name}");
    foreach (var p in t.Properties.OrderBy(p => p.Name))
        Console.WriteLine($"  P {Sig(p.PropertyType)} {p.Name}");
    foreach (var m in t.Methods.Where(m => !m.IsConstructor && !m.IsGetter && !m.IsSetter).OrderBy(m => m.Name))
        Console.WriteLine($"  M {(m.IsStatic ? "static " : "")}{Sig(m.ReturnType)} {m.Name}(" +
            string.Join(", ", m.Parameters.Select(p => $"{Sig(p.ParameterType)} {p.Name}")) + ")");
}

// 1) Subtypes of the instance/descriptor hierarchies
Console.WriteLine("========== SUBTYPES ==========");
void Subtypes(string baseName)
{
    Console.WriteLine($"\n[derives from {baseName}]");
    foreach (var t in allTypes.Where(t => !IsNoise(t.FullName)))
    {
        var bt = t.BaseType; int guard=0;
        while (bt != null && guard++<20)
        {
            if (bt.Name == baseName) { Console.WriteLine($"  {t.FullName}  ({BaseChain(t)})"); break; }
            bt = bt.Resolve()?.BaseType;
        }
    }
}
Subtypes("WorldItemInstance");
Subtypes("WorldItemDescriptor");
Subtypes("Interaction");

// 2) Real game type names containing key words
Console.WriteLine("\n\n========== TYPE NAME SEARCH (game types only) ==========");
string[] typeKeywords = { "Stone", "Harvest", "Mineral", "Resource", "Deposit", "Clump", "Ore", "Vein", "Boulder", "Rock", "Hittable", "Replenish" };
foreach (var kw in typeKeywords)
{
    var hits = allTypes.Where(t => !IsNoise(t.FullName)
            && t.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0
            && (t.Namespace.StartsWith("SSSGame") || t.Namespace == ""))
        .Select(t => t.FullName).Distinct().OrderBy(s => s).ToList();
    if (hits.Count > 0) Console.WriteLine($"\n[type ~ '{kw}']\n  " + string.Join("\n  ", hits));
}

// 3) Member name search in SSSGame, excluding noisy generics
Console.WriteLine("\n\n========== MEMBER SEARCH (SSSGame, real) ==========");
string[] memberKeywords = { "Replenish", "Reactivat", "Restore", "Refill", "Respawn", "Regrow", "Reset", "Activate" };
foreach (var kw in memberKeywords)
{
    Console.WriteLine($"\n[member ~ '{kw}']");
    foreach (var t in allTypes.Where(t => t.Namespace != null && t.Namespace.StartsWith("SSSGame") && !IsNoise(t.FullName)))
    {
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter))
            if (m.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine($"  M {t.Name}.{m.Name}({string.Join(",", m.Parameters.Select(p=>Sig(p.ParameterType)))})");
        foreach (var f in t.Fields.Where(f => !f.Name.StartsWith("Native")))
            if (f.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine($"  F {t.Name}.{f.Name}");
    }
}

// 4) Dump the Resource* family + interfaces
foreach (var n in new[] {
    "SSSGame.Weather.WeatherSystem",
}) Dump(n);
