using System;
using System.Reflection;

class Program {
    static void Main() {
        var asm = Assembly.LoadFrom(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Fusion.Runtime.dll");
        var type = asm.GetType("Fusion.NetworkRunner");
        if (type == null) {
            Console.WriteLine("Type not found.");
            return;
        }
        foreach(var prop in type.GetProperties()) {
            Console.WriteLine(prop.Name + " - " + prop.PropertyType.Name);
        }
    }
}
