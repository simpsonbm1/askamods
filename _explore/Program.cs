using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

var resolver = new DefaultAssemblyResolver();
var dirs = new[] {
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop",
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core",
    @"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs"
};
foreach (var d in dirs) resolver.AddSearchDirectory(d);
var rp = new ReaderParameters { AssemblyResolver = resolver };

var asm = AssemblyDefinition.ReadAssembly(Path.Combine(dirs[0], "Assembly-CSharp.dll"), rp);
var ss = AssemblyDefinition.ReadAssembly(Path.Combine(dirs[0], "SandSailorStudio.dll"), rp);
var allTypes = asm.MainModule.Types.SelectMany(t => new[]{t}.Concat(t.NestedTypes))
    .Concat(ss.MainModule.Types.SelectMany(t => new[]{t}.Concat(t.NestedTypes))).ToList();

var hi = allTypes.First(t => t.FullName == "SSSGame.HarvestInteraction");

void IL(string mname)
{
    var m = hi.Methods.FirstOrDefault(x => x.Name == mname);
    if (m == null) { Console.WriteLine($"\n[no {mname}]"); return; }
    Console.WriteLine($"\n=== HarvestInteraction.{mname} IL (calls only) ===");
    foreach (var instr in m.Body.Instructions)
        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
        {
            var mr = instr.Operand as MethodReference;
            if (mr != null) Console.WriteLine($"  -> {mr.DeclaringType?.Name}.{mr.Name}");
        }
}
IL("TakeDamage");
IL("_TakeDamageInternal");
IL("_HitFromHittable");
IL("ForceTakeDamage");

// What does the despawn? Find methods that call NetworkSpawner.Despawn or runner.Despawn
Console.WriteLine(""); Console.WriteLine("=== Callers of Despawn (NetworkSpawner/NetworkRunner) ===");
foreach (var t in allTypes)
    foreach (var m in t.Methods.Where(m => m.HasBody))
        foreach (var instr in m.Body.Instructions)
            if (instr.Operand is MethodReference mr && mr.Name == "Despawn"
                && (mr.DeclaringType?.Name == "NetworkSpawner" || mr.DeclaringType?.Name == "NetworkRunner"))
                Console.WriteLine($"  {t.FullName}.{m.Name} -> {mr.DeclaringType.Name}.Despawn");
