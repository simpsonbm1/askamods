using System;
using System.Reflection;
public class Dump {
    public static void Main() {
        var asm = Assembly.LoadFrom(@"D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll");
        var t1 = asm.GetType("SSSGame.WorldObjectiveMarker");
        Console.WriteLine("WorldObjectiveMarker methods:");
        foreach(var m in t1.GetMethods()) Console.WriteLine("  " + m.Name);
        Console.WriteLine("WorldObjectiveMarker props:");
        foreach(var m in t1.GetProperties()) Console.WriteLine("  " + m.Name);
        Console.WriteLine("WorldObjectiveMarker fields:");
        foreach(var m in t1.GetFields()) Console.WriteLine("  " + m.Name);
        
        var t2 = asm.GetType("SSSGame.CompassObjectiveMarker");
        if(t2 != null) {
            Console.WriteLine("CompassObjectiveMarker methods:");
            foreach(var m in t2.GetMethods()) Console.WriteLine("  " + m.Name);
            Console.WriteLine("CompassObjectiveMarker props:");
            foreach(var m in t2.GetProperties()) Console.WriteLine("  " + m.Name);
        }
    }
}
