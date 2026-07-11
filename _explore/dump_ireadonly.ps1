$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$base\interop\Assembly-CSharp.dll", $rp)
$t = $asm.MainModule.Types | Where-Object { $_.FullName -eq "SandSailorStudio.Inventory.ItemContainer" }
$m = $t.Methods | Where-Object { $_.Name -eq "GetItems" }
Write-Output "Return type: $($m.ReturnType.FullName)"
