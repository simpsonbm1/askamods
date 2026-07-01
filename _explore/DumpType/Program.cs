using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        var asm1 = Assembly.LoadFrom(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll");
        var asm2 = Assembly.LoadFrom(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll");
        foreach (var asm in new[] { asm1, asm2 })
        {
            foreach (var t in asm.GetTypes())
            {
                if (t.Name.Contains("HarvestMarker", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{t.Namespace}.{t.Name}");
                }
            }
        }
    }
}
